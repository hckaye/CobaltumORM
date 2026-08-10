using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

/// <summary>Provides SQLite implementations of all compile-time dialect services.</summary>
public sealed class SqliteDatabaseDialect : IDatabaseDialect
{
    /// <summary>Initializes the SQLite dialect services.</summary>
    public SqliteDatabaseDialect()
    {
        var typeMapper = new SqliteTypeMapper();
        QueryAnalyzer = new SqliteQueryAnalyzer(typeMapper);
        SchemaMigrationAnalyzer = new SqliteSchemaMigrationAnalyzer();
        ScriptClassifier = new SqliteScriptClassifierService();
        IdentifierQuoter = new SqliteIdentifierQuoter();
        TypeMapper = typeMapper;
        MigrationSqlWriter = new SqliteMigrationSqlWriter();
        SchemaRules = new SqliteSchemaRules();
    }

    /// <inheritdoc />
    public DatabaseProvider Provider => DatabaseProvider.Sqlite;

    /// <inheritdoc />
    public string Name => "Sqlite";

    /// <inheritdoc />
    public IQueryAnalyzer QueryAnalyzer { get; }

    /// <inheritdoc />
    public ISchemaMigrationAnalyzer SchemaMigrationAnalyzer { get; }

    /// <inheritdoc />
    public ISqlScriptClassifier ScriptClassifier { get; }

    /// <inheritdoc />
    public ISqlIdentifierQuoter IdentifierQuoter { get; }

    /// <inheritdoc />
    public ISqlTypeMapper TypeMapper { get; }

    /// <inheritdoc />
    public ISqlMigrationWriter MigrationSqlWriter { get; }

    /// <inheritdoc />
    public ISchemaRules SchemaRules { get; }
}

/// <summary>Quotes identifiers using SQLite's ANSI double-quote form.</summary>
public sealed class SqliteIdentifierQuoter : ISqlIdentifierQuoter
{
    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A SQLite identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "SQLite identifiers cannot contain a null character.",
                nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    /// <inheritdoc />
    public string QuoteQualifiedName(string? schema, string name)
    {
        SqliteRejectSchema(schema);
        return QuoteIdentifier(name);
    }

    private static void SqliteRejectSchema(string? schema)
    {
        if (!string.IsNullOrEmpty(schema))
        {
            throw new NotSupportedException(
                "SQLite compile-time analysis does not support non-empty schema names.");
        }
    }
}

/// <summary>Describes SQLite's single-schema and case-insensitive identifier rules.</summary>
public sealed class SqliteSchemaRules : ISchemaRules
{
    /// <inheritdoc />
    public bool SupportsSchemas => false;

    /// <inheritdoc />
    public string? DefaultSchema => null;

    /// <inheritdoc />
    public bool IsDefaultSchema(string? schema) => string.IsNullOrEmpty(schema);

    /// <inheritdoc />
    public string NormalizeUnquotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

    /// <inheritdoc />
    public string NormalizeQuotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

    /// <inheritdoc />
    public bool AreIdentifiersEqual(string reference, bool referenceIsQuoted, string declared)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (declared is null)
        {
            throw new ArgumentNullException(nameof(declared));
        }

        // SQLite compares object names without regard to case, including names
        // written with one of its identifier delimiters.
        return string.Equals(reference, declared, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Maps SQLite declared types and migration logical types.</summary>
public sealed class SqliteTypeMapper : ISqlTypeMapper
{
    /// <inheritdoc />
    public bool TryMap(string sqlType, out SqlValueKind kind)
    {
        if (sqlType is null)
        {
            kind = SqlValueKind.Error;
            return false;
        }

        var normalized = SqliteNormalizeType(sqlType);
        // SQLite applies these tests in this order. In particular, a name such
        // as CHARINT has INTEGER affinity because it contains INT.
        if (SqliteHasAffinityMarker(normalized, "INT"))
        {
            kind = SqlValueKind.Int64;
            return true;
        }

        if (SqliteHasAffinityMarker(normalized, "CHAR") ||
            SqliteHasAffinityMarker(normalized, "CLOB") ||
            SqliteHasAffinityMarker(normalized, "TEXT"))
        {
            kind = SqlValueKind.String;
            return true;
        }

        if (normalized.Length == 0 || SqliteHasAffinityMarker(normalized, "BLOB"))
        {
            kind = SqlValueKind.Bytes;
            return true;
        }

        if (SqliteHasAffinityMarker(normalized, "REAL") ||
            SqliteHasAffinityMarker(normalized, "FLOA") ||
            SqliteHasAffinityMarker(normalized, "DOUB"))
        {
            kind = SqlValueKind.Double;
            return true;
        }

        // BOOLEAN, DATE, DATETIME, NUMERIC, DECIMAL and all other declared
        // names have SQLite NUMERIC affinity.
        kind = SqlValueKind.Decimal;
        return true;
    }

    /// <inheritdoc />
    public string ToClrTypeName(SqlValueKind kind, bool nullable) =>
        SqlTypeMapper.ToClrName(kind, nullable);

    /// <inheritdoc />
    public string? ToDatabaseTypeName(SqlValueKind kind)
    {
        switch (kind)
        {
            case SqlValueKind.Bool:
            case SqlValueKind.Int16:
            case SqlValueKind.Int32:
            case SqlValueKind.Int64:
                return "INTEGER";
            case SqlValueKind.Float:
            case SqlValueKind.Double:
                return "REAL";
            case SqlValueKind.Decimal:
                return "NUMERIC";
            case SqlValueKind.String:
            case SqlValueKind.Json:
            case SqlValueKind.Guid:
            case SqlValueKind.DateOnly:
            case SqlValueKind.TimeOnly:
            case SqlValueKind.DateTime:
            case SqlValueKind.DateTimeOffset:
                return "TEXT";
            case SqlValueKind.Bytes:
            case SqlValueKind.JsonBinary:
                return "BLOB";
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public string MapMigrationType(
        string logicalType,
        int? length = null,
        int? precision = null,
        int? scale = null)
    {
        if (logicalType is null)
        {
            throw new ArgumentNullException(nameof(logicalType));
        }

        switch (logicalType.Trim().ToLowerInvariant())
        {
            case "int16":
            case "int32":
            case "int64":
            case "boolean":
                return "INTEGER";
            case "decimal":
                return "NUMERIC";
            case "float":
            case "double":
                return "REAL";
            case "string":
            case "text":
            case "date":
            case "datetime":
            case "datetimeoffset":
            case "time":
            case "guid":
            case "json":
                return "TEXT";
            case "binary":
            case "jsonb":
                return "BLOB";
            default:
                throw new ArgumentException(
                    "Unknown SQLite migration type '" + logicalType + "'.",
                    nameof(logicalType));
        }
    }

    private static string SqliteNormalizeType(string value)
    {
        var parts = value.Trim().ToUpperInvariant().Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    private static bool SqliteHasAffinityMarker(string value, string marker) =>
        value.IndexOf(marker, StringComparison.Ordinal) >= 0;
}

/// <summary>Formats SQLite migration DDL without emitting unsupported ALTER COLUMN SQL.</summary>
public sealed class SqliteMigrationSqlWriter : ISqlMigrationWriter
{
    /// <inheritdoc />
    public string FormatColumn(
        string quotedName,
        string sqlType,
        bool? nullable,
        bool primaryKey,
        bool identity)
    {
        if (string.IsNullOrWhiteSpace(quotedName))
        {
            throw new ArgumentException("A quoted SQLite column name is required.", nameof(quotedName));
        }

        if (string.IsNullOrWhiteSpace(sqlType))
        {
            throw new ArgumentException("A SQLite column type is required.", nameof(sqlType));
        }

        if (identity)
        {
            if (!primaryKey || !string.Equals(sqlType.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "SQLite identity columns must use INTEGER PRIMARY KEY.",
                    nameof(identity));
            }

            return quotedName + " INTEGER PRIMARY KEY AUTOINCREMENT";
        }

        var builder = new StringBuilder();
        builder.Append(quotedName).Append(' ').Append(sqlType);
        if (nullable == false || primaryKey)
        {
            builder.Append(" NOT NULL");
        }

        if (primaryKey)
        {
            builder.Append(" PRIMARY KEY");
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public string CreateTable(string qualifiedTable, IReadOnlyList<string> columns)
    {
        SqliteRequireTable(qualifiedTable);
        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("SQLite CREATE TABLE requires at least one column.", nameof(columns));
        }

        return "CREATE TABLE " + qualifiedTable + " (" + string.Join(", ", columns) + ");";
    }

    /// <inheritdoc />
    public string AddColumn(string qualifiedTable, string column)
    {
        SqliteRequireTable(qualifiedTable);
        if (string.IsNullOrWhiteSpace(column))
        {
            throw new ArgumentException("A SQLite column definition is required.", nameof(column));
        }

        return "ALTER TABLE " + qualifiedTable + " ADD COLUMN " + column + ";";
    }

    /// <inheritdoc />
    public bool TryAlterColumn(
        string qualifiedTable,
        string quotedColumn,
        string? sqlType,
        bool? nullable,
        out string? sql,
        out string? error)
    {
        sql = null;
        error =
            "SQLite does not support ALTER COLUMN. Changing a column type or nullability requires a table " +
            "rebuild, but the provider-neutral operation does not include enough existing table metadata to " +
            "rebuild it safely.";
        return false;
    }

    /// <inheritdoc />
    public string DropTable(string qualifiedTable)
    {
        SqliteRequireTable(qualifiedTable);
        return "DROP TABLE " + qualifiedTable + ";";
    }

    /// <inheritdoc />
    public string DropColumn(string qualifiedTable, string quotedColumn)
    {
        SqliteRequireTable(qualifiedTable);
        SqliteRequireColumn(quotedColumn);
        return "ALTER TABLE " + qualifiedTable + " DROP COLUMN " + quotedColumn + ";";
    }

    /// <inheritdoc />
    public string RenameTable(string qualifiedTable, string quotedNewName)
    {
        SqliteRequireTable(qualifiedTable);
        SqliteRequireColumn(quotedNewName);
        return "ALTER TABLE " + qualifiedTable + " RENAME TO " + quotedNewName + ";";
    }

    /// <inheritdoc />
    public string RenameColumn(string qualifiedTable, string quotedOldName, string quotedNewName)
    {
        SqliteRequireTable(qualifiedTable);
        SqliteRequireColumn(quotedOldName);
        SqliteRequireColumn(quotedNewName);
        return "ALTER TABLE " + qualifiedTable + " RENAME COLUMN " + quotedOldName + " TO " + quotedNewName + ";";
    }

    private static void SqliteRequireTable(string table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            throw new ArgumentException("A quoted SQLite table name is required.", nameof(table));
        }

        if (SqliteContainsUnquotedDot(table))
        {
            throw new NotSupportedException(
                "SQLite compile-time migration SQL cannot use qualified schema names.");
        }
    }

    private static void SqliteRequireColumn(string column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            throw new ArgumentException("A quoted SQLite identifier is required.", nameof(column));
        }
    }

    private static bool SqliteContainsUnquotedDot(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (quote == '\0')
            {
                if (current == '"' || current == '`')
                {
                    quote = current;
                    continue;
                }

                if (current == '[')
                {
                    quote = ']';
                    continue;
                }

                if (current == '.')
                {
                    return true;
                }

                continue;
            }

            if (current == quote)
            {
                if ((quote == '"' || quote == '`') && index + 1 < value.Length && value[index + 1] == quote)
                {
                    index++;
                    continue;
                }

                quote = '\0';
            }
        }

        return false;
    }
}
