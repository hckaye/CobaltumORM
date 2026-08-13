using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

#pragma warning disable RS1035 // Persistent analysis caching intentionally performs local file IO.

namespace CobaltumOrm.Analysis;

internal sealed class SemanticMigrationInput
{
    internal SemanticMigrationInput(long version, string description, IEnumerable<string> sqlSteps)
    {
        Version = version;
        Description = description ?? throw new ArgumentNullException(nameof(description));
        SqlSteps = (sqlSteps ?? throw new ArgumentNullException(nameof(sqlSteps))).ToArray();
    }

    internal long Version { get; }
    internal string Description { get; }
    internal IReadOnlyList<string> SqlSteps { get; }
}

internal readonly struct CacheComputation<T>
{
    internal CacheComputation(T value, bool isSuccessful)
    {
        Value = value;
        IsSuccessful = isSuccessful;
    }

    internal T Value { get; }
    internal bool IsSuccessful { get; }
}

/// <summary>
/// Stores only successful semantic analysis results. All failures in this class are deliberately
/// ignored because the cache is an optional optimization.
/// </summary>
internal sealed class AnalysisCache
{
    private const string FormatVersion = "1";
    private const string AnalysisVersion = "1";
    private const long MaxDocumentCharacters = 16L * 1024L * 1024L;
    private const int MaxTables = 10_000;
    private const int MaxColumns = 100_000;
    private const int MaxParameters = 100_000;
    private const int MaxValueLength = 1024 * 1024;

    private readonly string? _directory;
    private readonly string _provider;
    private readonly bool _enabled;

    internal AnalysisCache(string? directory, DatabaseProvider provider, bool enabled)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory!);
        _provider = provider.ToString();
        _enabled = enabled && _directory != null;
    }

    internal DatabaseSchema GetOrAnalyzeSchema(
        IReadOnlyList<SemanticMigrationInput> migrations,
        Func<CacheComputation<DatabaseSchema>> analyze,
        out bool cacheHit)
    {
        if (migrations is null)
        {
            throw new ArgumentNullException(nameof(migrations));
        }

        if (analyze is null)
        {
            throw new ArgumentNullException(nameof(analyze));
        }

        cacheHit = false;
        string? path = null;
        if (_enabled && TrySchemaPath(migrations, out path) && TryReadSchema(path!, out var cached))
        {
            cacheHit = true;
            return cached!;
        }

        var computation = analyze();
        if (_enabled && computation.IsSuccessful && path != null)
        {
            TryWriteSchema(path, computation.Value);
        }

        return computation.Value;
    }

    internal AnalysisResult AnalyzeQuery(
        DatabaseSchema schema,
        string sql,
        IQueryAnalyzer analyzer,
        out bool cacheHit)
    {
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        if (analyzer is null)
        {
            throw new ArgumentNullException(nameof(analyzer));
        }

        cacheHit = false;
        string? path = null;
        if (_enabled && TryQueryPath(schema, sql, out path) && TryReadQuery(path!, out var cached))
        {
            cacheHit = true;
            return cached!;
        }

        var result = analyzer.Analyze(schema, sql);
        if (_enabled && !result.HasErrors && path != null)
        {
            TryWriteQuery(path, result);
        }

        return result;
    }

    internal AnalysisResult AnalyzeQuery(DatabaseSchema schema, string sql, IQueryAnalyzer analyzer) =>
        AnalyzeQuery(schema, sql, analyzer, out _);

    private bool TrySchemaPath(IReadOnlyList<SemanticMigrationInput> migrations, out string? path)
    {
        try
        {
            var key = CreateKey(writer =>
            {
                WriteKeyHeader(writer, "schema");
                writer.Write(migrations.Count);
                foreach (var migration in migrations)
                {
                    writer.Write(migration.Version);
                    WriteKeyString(writer, migration.Description);
                    writer.Write(migration.SqlSteps.Count);
                    foreach (var sql in migration.SqlSteps)
                    {
                        WriteKeyString(writer, sql);
                    }
                }
            });
            path = Path.Combine(_directory!, "schema-" + key + ".xml");
            return true;
        }
        catch (Exception)
        {
            path = null;
            return false;
        }
    }

    private bool TryQueryPath(DatabaseSchema schema, string sql, out string? path)
    {
        try
        {
            var key = CreateKey(writer =>
            {
                WriteKeyHeader(writer, "query");
                var tables = schema.Tables
                    .OrderBy(table => table.Schema ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(table => table.Name, StringComparer.Ordinal)
                    .ToArray();
                writer.Write(tables.Length);
                foreach (var table in tables)
                {
                    WriteNullableKeyString(writer, table.Schema);
                    WriteKeyString(writer, table.Name);
                    writer.Write(table.Columns.Count);
                    foreach (var column in table.Columns)
                    {
                        WriteKeyString(writer, column.Name);
                        WriteKeyString(writer, column.SqlType);
                        writer.Write(column.IsNullable);
                        writer.Write(column.IsPrimaryKey);
                        WriteNullableKeyString(writer, column.DefaultExpression);
                        writer.Write(column.IsIdentity);
                    }
                }

                WriteKeyString(writer, sql);
            });
            path = Path.Combine(_directory!, "query-" + key + ".xml");
            return true;
        }
        catch (Exception)
        {
            path = null;
            return false;
        }
    }

    private void WriteKeyHeader(BinaryWriter writer, string kind)
    {
        WriteKeyString(writer, FormatVersion);
        WriteKeyString(writer, AnalysisVersion);
        WriteKeyString(writer, kind);
        WriteKeyString(writer, _provider);
    }

    // SHA-256 is used only to turn semantic cache inputs into a bounded lookup file name.
    private static string CreateKey(Action<BinaryWriter> write)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
        {
            write(writer);
            writer.Flush();
            stream.Position = 0;
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(stream);
                var text = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes)
                {
                    text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return text.ToString();
            }
        }
    }

    private static void WriteKeyString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteNullableKeyString(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null)
        {
            WriteKeyString(writer, value);
        }
    }

    private bool TryReadSchema(string path, out DatabaseSchema? schema)
    {
        schema = null;
        try
        {
            var root = LoadAndValidateRoot(path, "schema");
            if (root == null || root.Elements().Count() != 1 || HasNonWhitespaceText(root))
            {
                return false;
            }

            var schemaElement = root.Element("schema");
            if (schemaElement == null || schemaElement.HasAttributes || HasNonWhitespaceText(schemaElement))
            {
                return false;
            }

            var tableElements = schemaElement.Elements().ToArray();
            if (tableElements.Length > MaxTables || tableElements.Any(element => element.Name != "table"))
            {
                return false;
            }

            var tables = new List<Table>(tableElements.Length);
            var totalColumns = 0;
            foreach (var tableElement in tableElements)
            {
                if (!HasOnlyAttributes(tableElement, "name", "schema-present", "schema") ||
                    HasNonWhitespaceText(tableElement) ||
                    !TryRequiredString(tableElement, "name", out var name) ||
                    !TryBoolean(tableElement, "schema-present", out var schemaPresent) ||
                    !TryOptionalString(tableElement, "schema", schemaPresent, out var tableSchema))
                {
                    return false;
                }

                var columnElements = tableElement.Elements().ToArray();
                totalColumns += columnElements.Length;
                if (totalColumns > MaxColumns || columnElements.Any(element => element.Name != "column"))
                {
                    return false;
                }

                var columns = new List<Column>(columnElements.Length);
                foreach (var columnElement in columnElements)
                {
                    if (!HasOnlyAttributes(
                            columnElement,
                            "name",
                            "sql-type",
                            "nullable",
                            "primary-key",
                            "default-present",
                            "default",
                            "identity") ||
                        (columnElement.HasElements || HasNonWhitespaceText(columnElement)) ||
                        !TryRequiredString(columnElement, "name", out var columnName) ||
                        !TryRequiredString(columnElement, "sql-type", out var sqlType) ||
                        !TryBoolean(columnElement, "nullable", out var nullable) ||
                        !TryBoolean(columnElement, "primary-key", out var primaryKey) ||
                        !TryBoolean(columnElement, "default-present", out var defaultPresent) ||
                        !TryOptionalString(columnElement, "default", defaultPresent, out var defaultExpression) ||
                        !TryBoolean(columnElement, "identity", out var identity))
                    {
                        return false;
                    }

                    columns.Add(new Column(
                        columnName!,
                        sqlType!,
                        nullable,
                        primaryKey,
                        defaultExpression,
                        identity));
                }

                tables.Add(new Table(name!, columns, tableSchema));
            }

            schema = new DatabaseSchema(tables);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryReadQuery(string path, out AnalysisResult? result)
    {
        result = null;
        try
        {
            var root = LoadAndValidateRoot(path, "query");
            if (root == null || HasNonWhitespaceText(root))
            {
                return false;
            }

            var children = root.Elements().ToArray();
            if (children.Length != 2 || children[0].Name != "columns" || children[1].Name != "parameters" ||
                children[0].HasAttributes || children[1].HasAttributes ||
                HasNonWhitespaceText(children[0]) || HasNonWhitespaceText(children[1]))
            {
                return false;
            }

            var columnElements = children[0].Elements().ToArray();
            var parameterElements = children[1].Elements().ToArray();
            if (columnElements.Length > MaxColumns || parameterElements.Length > MaxParameters ||
                columnElements.Any(element => element.Name != "column") ||
                parameterElements.Any(element => element.Name != "parameter"))
            {
                return false;
            }

            var columns = new List<ResultColumn>(columnElements.Length);
            foreach (var element in columnElements)
            {
                if (!HasOnlyAttributes(element, "name", "clr-type") || element.HasElements ||
                    HasNonWhitespaceText(element) ||
                    !TryRequiredString(element, "name", out var name) ||
                    !TryRequiredString(element, "clr-type", out var clrType))
                {
                    return false;
                }

                columns.Add(new ResultColumn(name!, clrType!));
            }

            var parameters = new List<QueryParameter>(parameterElements.Length);
            foreach (var element in parameterElements)
            {
                if (!HasOnlyAttributes(element, "name", "clr-type", "database-type-present", "database-type") ||
                    (element.HasElements || HasNonWhitespaceText(element)) ||
                    !TryRequiredString(element, "name", out var name) ||
                    !TryRequiredString(element, "clr-type", out var clrType) ||
                    !TryBoolean(element, "database-type-present", out var databaseTypePresent) ||
                    !TryOptionalString(element, "database-type", databaseTypePresent, out var databaseType))
                {
                    return false;
                }

                parameters.Add(new QueryParameter(name!, clrType!, databaseType));
            }

            result = new AnalysisResult(columns, parameters, Array.Empty<Diagnostic>());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private XElement? LoadAndValidateRoot(string path, string kind)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaxDocumentCharacters)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxDocumentCharacters,
        };
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = XmlReader.Create(stream, settings))
        {
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root == null || root.Name != "cobaltum-analysis-cache" ||
                !HasOnlyAttributes(root, "kind", "format", "analysis", "provider") ||
                !string.Equals((string?)root.Attribute("kind"), kind, StringComparison.Ordinal) ||
                !string.Equals((string?)root.Attribute("format"), FormatVersion, StringComparison.Ordinal) ||
                !string.Equals((string?)root.Attribute("analysis"), AnalysisVersion, StringComparison.Ordinal) ||
                !string.Equals((string?)root.Attribute("provider"), _provider, StringComparison.Ordinal))
            {
                return null;
            }

            return root;
        }
    }

    private void TryWriteSchema(string path, DatabaseSchema schema)
    {
        if (schema.Tables.Count > MaxTables || schema.Tables.Sum(table => (long)table.Columns.Count) > MaxColumns ||
            !CanSerializeSchema(schema))
        {
            return;
        }

        TryPublish(path, writer =>
        {
            WriteRootStart(writer, "schema");
            writer.WriteStartElement("schema");
            foreach (var table in schema.Tables)
            {
                writer.WriteStartElement("table");
                writer.WriteAttributeString("name", table.Name);
                WriteOptionalAttribute(writer, "schema", table.Schema);
                foreach (var column in table.Columns)
                {
                    writer.WriteStartElement("column");
                    writer.WriteAttributeString("name", column.Name);
                    writer.WriteAttributeString("sql-type", column.SqlType);
                    writer.WriteAttributeString("nullable", XmlConvert.ToString(column.IsNullable));
                    writer.WriteAttributeString("primary-key", XmlConvert.ToString(column.IsPrimaryKey));
                    WriteOptionalAttribute(writer, "default", column.DefaultExpression);
                    writer.WriteAttributeString("identity", XmlConvert.ToString(column.IsIdentity));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private void TryWriteQuery(string path, AnalysisResult result)
    {
        if (result.Columns.Count > MaxColumns || result.Parameters.Count > MaxParameters ||
            result.Columns.Any(column => !CanSerialize(column.Name) || !CanSerialize(column.ClrType)) ||
            result.Parameters.Any(parameter =>
                !CanSerialize(parameter.Name) || !CanSerialize(parameter.ClrType) || !CanSerialize(parameter.DatabaseTypeName)))
        {
            return;
        }

        TryPublish(path, writer =>
        {
            WriteRootStart(writer, "query");
            writer.WriteStartElement("columns");
            foreach (var column in result.Columns)
            {
                writer.WriteStartElement("column");
                writer.WriteAttributeString("name", column.Name);
                writer.WriteAttributeString("clr-type", column.ClrType);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteStartElement("parameters");
            foreach (var parameter in result.Parameters)
            {
                writer.WriteStartElement("parameter");
                writer.WriteAttributeString("name", parameter.Name);
                writer.WriteAttributeString("clr-type", parameter.ClrType);
                WriteOptionalAttribute(writer, "database-type", parameter.DatabaseTypeName);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private void WriteRootStart(XmlWriter writer, string kind)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("cobaltum-analysis-cache");
        writer.WriteAttributeString("kind", kind);
        writer.WriteAttributeString("format", FormatVersion);
        writer.WriteAttributeString("analysis", AnalysisVersion);
        writer.WriteAttributeString("provider", _provider);
    }

    private static void WriteOptionalAttribute(XmlWriter writer, string name, string? value)
    {
        writer.WriteAttributeString(name + "-present", XmlConvert.ToString(value != null));
        if (value != null)
        {
            writer.WriteAttributeString(name, value);
        }
    }

    private void TryPublish(string path, Action<XmlWriter> write)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_directory!);
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                CloseOutput = false,
            };
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = XmlWriter.Create(stream, settings))
                {
                    write(writer);
                }

                stream.Flush(true);
            }

            if (new FileInfo(temporaryPath).Length > MaxDocumentCharacters)
            {
                return;
            }

            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException)
            {
                File.Replace(temporaryPath, path, null);
            }

            temporaryPath = null;
        }
        catch (Exception)
        {
        }
        finally
        {
            if (temporaryPath != null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static bool CanSerializeSchema(DatabaseSchema schema) =>
        schema.Tables.All(table =>
            CanSerialize(table.Name) &&
            CanSerialize(table.Schema) &&
            table.Columns.All(column =>
                CanSerialize(column.Name) &&
                CanSerialize(column.SqlType) &&
                CanSerialize(column.DefaultExpression)));

    private static bool CanSerialize(string? value) => value == null || value.Length <= MaxValueLength;

    private static bool HasOnlyAttributes(XElement element, params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        return element.Attributes().All(attribute =>
            !attribute.IsNamespaceDeclaration &&
            attribute.Name.Namespace == XNamespace.None &&
            allowed.Contains(attribute.Name.LocalName));
    }

    private static bool HasNonWhitespaceText(XElement element) => element
        .Nodes()
        .OfType<XText>()
        .Any(text => !string.IsNullOrWhiteSpace(text.Value));

    private static bool TryRequiredString(XElement element, string name, out string? value)
    {
        var attribute = element.Attribute(name);
        value = attribute?.Value;
        return value != null && value.Length != 0 && value.Length <= MaxValueLength;
    }

    private static bool TryOptionalString(XElement element, string name, bool present, out string? value)
    {
        var attribute = element.Attribute(name);
        if (present)
        {
            value = attribute?.Value;
            return value != null && value.Length <= MaxValueLength;
        }

        value = null;
        return attribute == null;
    }

    private static bool TryBoolean(XElement element, string name, out bool value)
    {
        var text = (string?)element.Attribute(name);
        if (string.Equals(text, "true", StringComparison.Ordinal))
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "false", StringComparison.Ordinal))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }
}

#pragma warning restore RS1035
