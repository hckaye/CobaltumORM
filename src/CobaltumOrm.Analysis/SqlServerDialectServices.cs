using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

/// <summary>Provides SQL Server services for compile-time analysis.</summary>
public sealed class SqlServerDatabaseDialect : IDatabaseDialect
{
    public SqlServerDatabaseDialect()
    {
        var mapper = new SqlServerTypeMapper();
        QueryAnalyzer = new SqlServerQueryAnalyzer(mapper);
        SchemaMigrationAnalyzer = new SqlServerSchemaMigrationAnalyzer();
        ScriptClassifier = new SqlServerScriptClassifierService();
        IdentifierQuoter = new SqlServerIdentifierQuoter();
        TypeMapper = mapper;
        MigrationSqlWriter = new SqlServerMigrationSqlWriter();
        SchemaRules = new SqlServerSchemaRules();
    }

    public DatabaseProvider Provider => DatabaseProvider.SqlServer;
    public string Name => "SqlServer";
    public IQueryAnalyzer QueryAnalyzer { get; }
    public ISchemaMigrationAnalyzer SchemaMigrationAnalyzer { get; }
    public ISqlScriptClassifier ScriptClassifier { get; }
    public ISqlIdentifierQuoter IdentifierQuoter { get; }
    public ISqlTypeMapper TypeMapper { get; }
    public ISqlMigrationWriter MigrationSqlWriter { get; }
    public ISchemaRules SchemaRules { get; }
}

/// <summary>Quotes SQL Server identifiers with brackets.</summary>
public sealed class SqlServerIdentifierQuoter : ISqlIdentifierQuoter
{
    public string QuoteIdentifier(string identifier)
    {
        SqlServerValidateIdentifier(identifier, nameof(identifier));
        return "[" + identifier.Replace("]", "]]") + "]";
    }

    public string QuoteQualifiedName(string? schema, string name)
    {
        SqlServerValidateIdentifier(name, nameof(name));
        var effectiveSchema = string.IsNullOrEmpty(schema) ? "dbo" : schema!;
        SqlServerValidateIdentifier(effectiveSchema, nameof(schema));
        return QuoteIdentifier(effectiveSchema) + "." + QuoteIdentifier(name);
    }

    private static void SqlServerValidateIdentifier(string? identifier, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A SQL Server identifier is required.", argumentName);
        }

        if (identifier!.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("SQL Server identifiers cannot contain a null character.", argumentName);
        }
    }
}

/// <summary>Applies SQL Server's case-insensitive schema and identifier rules.</summary>
public sealed class SqlServerSchemaRules : ISchemaRules
{
    public bool SupportsSchemas => true;
    public string? DefaultSchema => "dbo";

    public bool IsDefaultSchema(string? schema) =>
        string.IsNullOrEmpty(schema) || string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase);

    public string NormalizeUnquotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

    public string NormalizeQuotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

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

        return string.Equals(reference, declared, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Adapts the SQL Server script classifier to the dialect service contract.</summary>
public sealed class SqlServerScriptClassifierService : ISqlScriptClassifier
{
    public IReadOnlyList<SqlScriptStatement> SplitAndClassify(string sql, out SqlScriptError? error)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        return SqlServerScriptClassifier.SplitAndClassify(sql, out error);
    }
}

/// <summary>Formats SQL Server migration DDL.</summary>
public sealed class SqlServerMigrationSqlWriter : ISqlMigrationWriter
{
    public string FormatColumn(string quotedName, string sqlType, bool? nullable, bool primaryKey, bool identity)
    {
        if (string.IsNullOrWhiteSpace(quotedName))
        {
            throw new ArgumentException("A quoted SQL Server column name is required.", nameof(quotedName));
        }

        if (string.IsNullOrWhiteSpace(sqlType))
        {
            throw new ArgumentException("A SQL Server column type is required.", nameof(sqlType));
        }

        var builder = new StringBuilder();
        builder.Append(quotedName).Append(' ').Append(sqlType);
        if (identity)
        {
            if (!SqlServerIsIdentityType(sqlType))
            {
                throw new ArgumentException(
                    "SQL Server identity columns must use smallint, int, or bigint.",
                    nameof(sqlType));
            }

            builder.Append(" IDENTITY(1,1)");
        }

        if (nullable == false || primaryKey)
        {
            builder.Append(" NOT NULL");
        }
        else if (nullable == true)
        {
            builder.Append(" NULL");
        }

        if (primaryKey)
        {
            builder.Append(" PRIMARY KEY");
        }

        return builder.ToString();
    }

    public string CreateTable(string qualifiedTable, IReadOnlyList<string> columns) =>
        "CREATE TABLE " + qualifiedTable + " (" + string.Join(", ", columns) + ");";

    public string AddColumn(string qualifiedTable, string column) =>
        "ALTER TABLE " + qualifiedTable + " ADD " + column + ";";

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
            error = "ALTER COLUMN requires a qualified SQL Server table name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(quotedColumn))
        {
            error = "ALTER COLUMN requires a quoted SQL Server column name.";
            return false;
        }

        if (sqlType != null && string.IsNullOrWhiteSpace(sqlType))
        {
            error = "ALTER COLUMN target SQL type cannot be empty.";
            return false;
        }

        if (sqlType == null)
        {
            error = "SQL Server ALTER COLUMN requires a target SQL type; nullability-only changes are not supported.";
            return false;
        }

        var builder = new StringBuilder();
        builder.Append("ALTER TABLE ")
            .Append(qualifiedTable)
            .Append(" ALTER COLUMN ")
            .Append(quotedColumn)
            .Append(' ')
            .Append(sqlType);
        if (nullable.HasValue)
        {
            builder.Append(nullable.Value ? " NULL" : " NOT NULL");
        }

        sql = builder.Append(';').ToString();
        return true;
    }

    public string DropTable(string qualifiedTable) => "DROP TABLE " + qualifiedTable + ";";

    public string DropColumn(string qualifiedTable, string quotedColumn) =>
        "ALTER TABLE " + qualifiedTable + " DROP COLUMN " + quotedColumn + ";";

    public string RenameTable(string qualifiedTable, string quotedNewName) =>
        SqlServerRenameSql(qualifiedTable, quotedNewName, "OBJECT");

    public string RenameColumn(string qualifiedTable, string quotedOldName, string quotedNewName) =>
        SqlServerRenameSql(qualifiedTable + "." + quotedOldName, quotedNewName, "COLUMN");

    private static bool SqlServerIsIdentityType(string sqlType)
    {
        var normalized = sqlType.Trim().ToLowerInvariant();
        var open = normalized.IndexOf('(');
        return (open < 0 ? normalized : normalized.Substring(0, open).Trim()) == "smallint" ||
            (open < 0 ? normalized : normalized.Substring(0, open).Trim()) == "int" ||
            (open < 0 ? normalized : normalized.Substring(0, open).Trim()) == "integer" ||
            (open < 0 ? normalized : normalized.Substring(0, open).Trim()) == "bigint";
    }

    private static string SqlServerRenameSql(string oldName, string newName, string objectType)
    {
        return "EXEC sys.sp_rename @objname = " + SqlServerStringLiteral(oldName) +
            ", @newname = " + SqlServerStringLiteral(SqlServerUnquoteSingleIdentifier(newName)) +
            ", @objtype = N'" + objectType + "';";
    }

    private static string SqlServerStringLiteral(string value) =>
        "N'" + value.Replace("'", "''") + "'";

    private static string SqlServerUnquoteSingleIdentifier(string value)
    {
        if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
        {
            return value.Substring(1, value.Length - 2).Replace("]]", "]");
        }

        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            return value.Substring(1, value.Length - 2).Replace("\"\"", "\"");
        }

        return value;
    }
}
