using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

/// <summary>Identifies a database provider supported by CobaltumORM configuration.</summary>
public enum DatabaseProvider
{
    PostgreSql,
    MySql,
    Sqlite,
    SqlServer,
    Oracle,
}

/// <summary>Provides the provider-specific services used by compile-time SQL analysis.</summary>
public interface IDatabaseDialect
{
    DatabaseProvider Provider { get; }
    string Name { get; }
    IQueryAnalyzer QueryAnalyzer { get; }
    ISchemaMigrationAnalyzer SchemaMigrationAnalyzer { get; }
    ISqlScriptClassifier ScriptClassifier { get; }
    ISqlIdentifierQuoter IdentifierQuoter { get; }
    ISqlTypeMapper TypeMapper { get; }
    ISqlMigrationWriter MigrationSqlWriter { get; }
    ISchemaRules SchemaRules { get; }
}

/// <summary>Splits SQL scripts and classifies statements by their compile-time effect.</summary>
public interface ISqlScriptClassifier
{
    IReadOnlyList<SqlScriptStatement> SplitAndClassify(string sql, out SqlScriptError? error);
}

/// <summary>Quotes identifiers according to one database provider's SQL rules.</summary>
public interface ISqlIdentifierQuoter
{
    string QuoteIdentifier(string identifier);
    string QuoteQualifiedName(string? schema, string name);
}

/// <summary>Maps provider SQL types to the common analysis type model.</summary>
public interface ISqlTypeMapper
{
    bool TryMap(string sqlType, out SqlValueKind kind);
    string ToClrTypeName(SqlValueKind kind, bool nullable);
    string? ToDatabaseTypeName(SqlValueKind kind);
    string MapMigrationType(string logicalType, int? length = null, int? precision = null, int? scale = null);
}

/// <summary>Formats migration DDL using one database provider's SQL syntax.</summary>
public interface ISqlMigrationWriter
{
    string FormatColumn(string quotedName, string sqlType, bool? nullable, bool primaryKey, bool identity);
    string CreateTable(string qualifiedTable, IReadOnlyList<string> columns);
    string AddColumn(string qualifiedTable, string column);
    /// <summary>
    /// Tries to format provider-specific ALTER COLUMN SQL for the requested target properties.
    /// </summary>
    /// <param name="qualifiedTable">The already quoted table name.</param>
    /// <param name="quotedColumn">The already quoted column name.</param>
    /// <param name="sqlType">The target SQL type, or <see langword="null"/> when it is unchanged.</param>
    /// <param name="nullable">The target nullability, or <see langword="null"/> when it is unchanged.</param>
    /// <param name="sql">The generated SQL when the operation is supported; otherwise <see langword="null"/>.</param>
    /// <param name="error">An explanation when the operation cannot be represented; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when SQL was generated; otherwise <see langword="false"/>.</returns>
    /// <remarks>Implementations report unsupported operations through the return value and error instead of throwing.</remarks>
    bool TryAlterColumn(
        string qualifiedTable,
        string quotedColumn,
        string? sqlType,
        bool? nullable,
        out string? sql,
        out string? error);
    string DropTable(string qualifiedTable);
    string DropColumn(string qualifiedTable, string quotedColumn);
    string RenameTable(string qualifiedTable, string quotedNewName);
    string RenameColumn(string qualifiedTable, string quotedOldName, string quotedNewName);
}

/// <summary>Describes schema support and identifier case behavior for a provider.</summary>
public interface ISchemaRules
{
    bool SupportsSchemas { get; }
    string? DefaultSchema { get; }
    bool IsDefaultSchema(string? schema);
    string NormalizeUnquotedIdentifier(string identifier);
    string NormalizeQuotedIdentifier(string identifier);
    bool AreIdentifiersEqual(string reference, bool referenceIsQuoted, string declared);
}

/// <summary>Resolves the configured database provider to its analysis services.</summary>
public static class DatabaseDialects
{
    public const string DefaultProviderName = "PostgreSql";
    public const string ConfigurationPropertyName = "CobaltumOrmDatabaseProvider";

    private static readonly IDatabaseDialect PostgreSql = new PostgreSqlDatabaseDialect();
    private static readonly IDatabaseDialect MySql = new MySqlDatabaseDialect();
    private static readonly IDatabaseDialect Sqlite = new SqliteDatabaseDialect();
    private static readonly IDatabaseDialect SqlServer = new SqlServerDatabaseDialect();
    private static readonly IDatabaseDialect Oracle = new OracleDatabaseDialect();

    /// <summary>Gets the built-in PostgreSQL dialect.</summary>
    public static IDatabaseDialect PostgreSqlDialect => PostgreSql;

    /// <summary>Gets the built-in MySQL dialect.</summary>
    public static IDatabaseDialect MySqlDialect => MySql;

    /// <summary>Gets the built-in SQLite dialect.</summary>
    public static IDatabaseDialect SqliteDialect => Sqlite;

    /// <summary>Gets the built-in SQL Server dialect.</summary>
    public static IDatabaseDialect SqlServerDialect => SqlServer;

    /// <summary>Gets the built-in Oracle dialect.</summary>
    public static IDatabaseDialect OracleDialect => Oracle;

    /// <summary>
    /// Resolves a provider name. An omitted or blank value selects PostgreSQL.
    /// </summary>
    public static bool TryResolve(
        string? providerName,
        out IDatabaseDialect dialect,
        out string? error)
    {
        var value = string.IsNullOrWhiteSpace(providerName)
            ? DefaultProviderName
            : providerName!.Trim();

        if (string.Equals(value, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            dialect = PostgreSql;
        }
        else if (string.Equals(value, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            dialect = MySql;
        }
        else if (string.Equals(value, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            dialect = Sqlite;
        }
        else if (string.Equals(value, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            dialect = SqlServer;
        }
        else if (string.Equals(value, "Oracle", StringComparison.OrdinalIgnoreCase))
        {
            dialect = Oracle;
        }
        else
        {
            dialect = null!;
            error = $"CobaltumOrmDatabaseProvider must be PostgreSql, MySql, Sqlite, SqlServer, or Oracle; received '{value}'.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>Provides the PostgreSQL implementation of the dialect services.</summary>
public sealed class PostgreSqlDatabaseDialect : IDatabaseDialect
{
    public PostgreSqlDatabaseDialect()
    {
        QueryAnalyzer = new PostgreSqlQueryAnalyzer();
        SchemaMigrationAnalyzer = new PostgreSqlSchemaMigrationAnalyzer();
        ScriptClassifier = new PostgreSqlScriptClassifierService();
        IdentifierQuoter = new PostgreSqlIdentifierQuoter();
        TypeMapper = new PostgreSqlTypeMapper();
        MigrationSqlWriter = new PostgreSqlMigrationSqlWriter();
        SchemaRules = new PostgreSqlSchemaRules();
    }

    public DatabaseProvider Provider => DatabaseProvider.PostgreSql;
    public string Name => "PostgreSql";
    public IQueryAnalyzer QueryAnalyzer { get; }
    public ISchemaMigrationAnalyzer SchemaMigrationAnalyzer { get; }
    public ISqlScriptClassifier ScriptClassifier { get; }
    public ISqlIdentifierQuoter IdentifierQuoter { get; }
    public ISqlTypeMapper TypeMapper { get; }
    public ISqlMigrationWriter MigrationSqlWriter { get; }
    public ISchemaRules SchemaRules { get; }
}

/// <summary>Applies PostgreSQL identifier quoting rules.</summary>
public sealed class PostgreSqlIdentifierQuoter : ISqlIdentifierQuoter
{
    public string QuoteIdentifier(string identifier)
    {
        if (identifier is null)
        {
            throw new ArgumentNullException(nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public string QuoteQualifiedName(string? schema, string name)
    {
        return string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(name)
            : QuoteIdentifier(schema!) + "." + QuoteIdentifier(name);
    }
}

/// <summary>Applies PostgreSQL schema and identifier case rules.</summary>
public sealed class PostgreSqlSchemaRules : ISchemaRules
{
    public bool SupportsSchemas => true;
    public string DefaultSchema => "public";

    public bool IsDefaultSchema(string? schema) =>
        string.IsNullOrEmpty(schema) || string.Equals(schema, DefaultSchema, StringComparison.Ordinal);

    public string NormalizeUnquotedIdentifier(string identifier) =>
        (identifier ?? throw new ArgumentNullException(nameof(identifier))).ToLowerInvariant();

    public string NormalizeQuotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

    public bool AreIdentifiersEqual(string reference, bool referenceIsQuoted, string declared)
    {
        return referenceIsQuoted
            ? string.Equals(reference, declared, StringComparison.Ordinal)
            : string.Equals(reference, declared, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Adapts the PostgreSQL script splitter to the dialect service contract.</summary>
public sealed class PostgreSqlScriptClassifierService : ISqlScriptClassifier
{
    public IReadOnlyList<SqlScriptStatement> SplitAndClassify(string sql, out SqlScriptError? error)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        return PostgreSqlScriptClassifier.SplitAndClassify(sql, out error);
    }
}

/// <summary>Adapts PostgreSQL SQL type mapping to the dialect service contract.</summary>
public sealed class PostgreSqlTypeMapper : ISqlTypeMapper
{
    public bool TryMap(string sqlType, out SqlValueKind kind)
    {
        var normalized = Normalize(sqlType);
        var baseType = RemoveTypeModifiers(normalized);
        if (normalized.IndexOf('(') >= 0 && !AreTypeModifiersValid(normalized, baseType))
        {
            kind = SqlValueKind.Error;
            return false;
        }

        switch (baseType)
        {
            case "boolean":
            case "bool": kind = SqlValueKind.Bool; return true;
            case "smallint":
            case "int2":
            case "smallserial": kind = SqlValueKind.Int16; return true;
            case "integer":
            case "int":
            case "int4":
            case "serial": kind = SqlValueKind.Int32; return true;
            case "bigint":
            case "int8":
            case "bigserial": kind = SqlValueKind.Int64; return true;
            case "real": kind = SqlValueKind.Float; return true;
            case "float4": kind = SqlValueKind.Float; return true;
            case "double precision": kind = SqlValueKind.Double; return true;
            case "float8": kind = SqlValueKind.Double; return true;
            case "numeric":
            case "decimal": kind = SqlValueKind.Decimal; return true;
            case "text":
            case "varchar":
            case "character varying":
            case "character":
            case "char":
            case "bpchar":
            case "name":
            case "xml":
            case "jsonpath": kind = SqlValueKind.String; return true;
            case "json": kind = SqlValueKind.Json; return true;
            case "jsonb": kind = SqlValueKind.JsonBinary; return true;
            case "uuid": kind = SqlValueKind.Guid; return true;
            case "date": kind = SqlValueKind.DateOnly; return true;
            case "time":
            case "time without time zone": kind = SqlValueKind.TimeOnly; return true;
            case "timestamp":
            case "timestamp without time zone": kind = SqlValueKind.DateTime; return true;
            case "timestamptz":
            case "timestamp with time zone": kind = SqlValueKind.DateTimeOffset; return true;
            case "interval": kind = SqlValueKind.Interval; return true;
            case "bytea": kind = SqlValueKind.Bytes; return true;
            default:
                kind = SqlValueKind.Error;
                return false;
        }
    }

    public string ToClrTypeName(SqlValueKind kind, bool nullable) => SqlTypeMapper.ToClrName(kind, nullable);
    public string? ToDatabaseTypeName(SqlValueKind kind)
    {
        switch (kind)
        {
            case SqlValueKind.Json: return "json";
            case SqlValueKind.JsonBinary: return "jsonb";
            case SqlValueKind.Interval: return "interval";
            default: return null;
        }
    }

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

        switch (logicalType)
        {
            case "int16": return "smallint";
            case "int32": return "integer";
            case "int64": return "bigint";
            case "boolean": return "boolean";
            case "float": return "real";
            case "double": return "double precision";
            case "text": return "text";
            case "date": return "date";
            case "datetime": return "timestamp without time zone";
            case "datetimeoffset": return "timestamp with time zone";
            case "time": return "time without time zone";
            case "interval": return "interval";
            case "guid": return "uuid";
            case "binary": return "bytea";
            case "json": return "json";
            case "jsonb": return "jsonb";
            case "string":
                return length.HasValue
                    ? "character varying(" + length.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
                    : "text";
            case "decimal":
                return precision.HasValue
                    ? "numeric(" + precision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                      scale!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
                    : "numeric";
            default:
                throw new ArgumentException($"Unknown PostgreSQL migration type '{logicalType}'.", nameof(logicalType));
        }
    }

    private static string Normalize(string value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var parts = value.Trim().ToLowerInvariant().Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    private static string RemoveTypeModifiers(string value)
    {
        var builder = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var character in value)
        {
            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth == 0)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    private static bool AreTypeModifiersValid(string value, string baseType)
    {
        var open = value.IndexOf('(');
        var close = value.LastIndexOf(')');
        if (open < 0 || close <= open || value.IndexOf('(', close + 1) >= 0 ||
            value.IndexOf(')', close + 1) >= 0)
        {
            return false;
        }

        var modifiers = value.Substring(open + 1, close - open - 1).Split(',');
        var isStringLength = baseType == "varchar" || baseType == "char" ||
            baseType == "character" || baseType == "character varying";
        var isNumericPrecision = baseType == "numeric" || baseType == "decimal";
        var isTemporalPrecision = baseType == "time" ||
            baseType == "time without time zone" || baseType == "timestamp" ||
            baseType == "timestamp without time zone" || baseType == "timestamp with time zone";
        if (modifiers.Length == 0 ||
            isStringLength && modifiers.Length != 1 ||
            isNumericPrecision && modifiers.Length > 2 ||
            isTemporalPrecision && modifiers.Length != 1 ||
            !isStringLength && !isNumericPrecision && !isTemporalPrecision)
        {
            return false;
        }

        var parsed = new int[modifiers.Length];
        for (var index = 0; index < modifiers.Length; index++)
        {
            var modifier = modifiers[index];
            int number;
            if (!int.TryParse(modifier.Trim(), out number) || number < 0 ||
                isStringLength && number == 0)
            {
                return false;
            }

            parsed[index] = number;
        }

        if (isNumericPrecision && (parsed[0] == 0 || parsed.Length == 2 && parsed[1] > parsed[0]))
        {
            return false;
        }

        return true;
    }
}

/// <summary>Formats PostgreSQL migration DDL used by compile-time migration analysis.</summary>
public sealed class PostgreSqlMigrationSqlWriter : ISqlMigrationWriter
{
    public string FormatColumn(string quotedName, string sqlType, bool? nullable, bool primaryKey, bool identity)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(quotedName).Append(' ').Append(sqlType);
        if (nullable == false || primaryKey)
        {
            builder.Append(" NOT NULL");
        }

        if (primaryKey)
        {
            builder.Append(" PRIMARY KEY");
        }

        // Keep the existing compile-time migration output unchanged. Runtime
        // migration adapters remain responsible for provider-specific identity SQL.
        return builder.ToString();
    }

    public string CreateTable(string qualifiedTable, IReadOnlyList<string> columns) =>
        "CREATE TABLE " + qualifiedTable + " (" + string.Join(", ", columns) + ");";

    public string AddColumn(string qualifiedTable, string column) =>
        "ALTER TABLE " + qualifiedTable + " ADD COLUMN " + column + ";";

    public bool TryAlterColumn(
        string qualifiedTable,
        string quotedColumn,
        string? sqlType,
        bool? nullable,
        out string? sql,
        out string? error)
    {
        sql = null;
        error = null;
        if (string.IsNullOrWhiteSpace(qualifiedTable))
        {
            error = "ALTER COLUMN requires a qualified table name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(quotedColumn))
        {
            error = "ALTER COLUMN requires a quoted column name.";
            return false;
        }

        if (sqlType is not null && string.IsNullOrWhiteSpace(sqlType))
        {
            error = "ALTER COLUMN target SQL type cannot be empty.";
            return false;
        }

        if (sqlType is null && !nullable.HasValue)
        {
            error = "ALTER COLUMN requires a target SQL type, target nullability, or both.";
            return false;
        }

        var builder = new System.Text.StringBuilder();
        if (sqlType is not null)
        {
            builder.Append("ALTER TABLE ")
                .Append(qualifiedTable)
                .Append(" ALTER COLUMN ")
                .Append(quotedColumn)
                .Append(" TYPE ")
                .Append(sqlType)
                .Append(';');
        }

        if (nullable.HasValue)
        {
            if (builder.Length != 0)
            {
                builder.Append('\n');
            }

            builder.Append("ALTER TABLE ")
                .Append(qualifiedTable)
                .Append(" ALTER COLUMN ")
                .Append(quotedColumn)
                .Append(nullable.Value ? " DROP NOT NULL;" : " SET NOT NULL;");
        }

        sql = builder.ToString();
        return true;
    }

    public string DropTable(string qualifiedTable) => "DROP TABLE " + qualifiedTable + ";";

    public string DropColumn(string qualifiedTable, string quotedColumn) =>
        "ALTER TABLE " + qualifiedTable + " DROP COLUMN " + quotedColumn + ";";

    public string RenameTable(string qualifiedTable, string quotedNewName) =>
        "ALTER TABLE " + qualifiedTable + " RENAME TO " + quotedNewName + ";";

    public string RenameColumn(string qualifiedTable, string quotedOldName, string quotedNewName) =>
        "ALTER TABLE " + qualifiedTable + " RENAME COLUMN " + quotedOldName + " TO " + quotedNewName + ";";
}
