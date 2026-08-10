using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.Oracle;

/// <summary>
/// Generates Oracle Database SQL for CobaltumORM migration operations and history
/// actions. Migration object names are always treated as unquoted input and are
/// safely double-quoted by the adapter.
/// </summary>
public sealed class OracleMigrationAdapter : IMigrationDatabaseAdapter, IMigrationDryRunDatabaseAdapter
{
    /// <inheritdoc/>
    public IReadOnlyList<MigrationCommand> GenerateCommands(MigrationOperation operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (operation is CreateTableOperation createTable)
        {
            return One(GenerateCreateTable(createTable));
        }

        if (operation is AddColumnOperation addColumn)
        {
            return One(GenerateAddColumn(addColumn));
        }

        if (operation is AlterColumnOperation alterColumn)
        {
            return One(GenerateAlterColumn(alterColumn));
        }

        if (operation is DeleteTableOperation deleteTable)
        {
            return One(new MigrationCommand(
                $"DROP TABLE {Qualify(deleteTable.SchemaName, deleteTable.TableName)}"));
        }

        if (operation is DeleteColumnOperation deleteColumn)
        {
            return One(new MigrationCommand(
                $"ALTER TABLE {Qualify(deleteColumn.SchemaName, deleteColumn.TableName)} " +
                $"DROP COLUMN {QuoteIdentifier(deleteColumn.ColumnName)}"));
        }

        if (operation is RenameTableOperation renameTable)
        {
            return One(new MigrationCommand(
                $"ALTER TABLE {Qualify(renameTable.SchemaName, renameTable.OldName)} " +
                $"RENAME TO {QuoteIdentifier(renameTable.NewName)}"));
        }

        if (operation is RenameColumnOperation renameColumn)
        {
            return One(new MigrationCommand(
                $"ALTER TABLE {Qualify(renameColumn.SchemaName, renameColumn.TableName)} " +
                $"RENAME COLUMN {QuoteIdentifier(renameColumn.OldName)} " +
                $"TO {QuoteIdentifier(renameColumn.NewName)}"));
        }

        if (operation is ExecuteSqlOperation executeSql)
        {
            return One(new MigrationCommand(executeSql.Sql));
        }

        throw new NotSupportedException(
            $"Operation type '{operation.GetType().FullName}' is not supported by the Oracle adapter.");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateEnsureHistoryTableCommand(string? schemaName, string tableName)
    {
        var table = Qualify(schemaName, tableName);
        var createTable =
            $"CREATE TABLE {table} (" +
            $"{QuoteIdentifier("version")} NUMBER(19,0) NOT NULL PRIMARY KEY, " +
            $"{QuoteIdentifier("description")} CLOB NOT NULL, " +
            $"{QuoteIdentifier("applied_utc")} TIMESTAMP WITH TIME ZONE NOT NULL)";

        // Oracle does not support CREATE TABLE IF NOT EXISTS. ORA-00955 is the
        // table-already-exists error used here; every other error is re-raised.
        return new MigrationCommand(
            "BEGIN\n" +
            "  EXECUTE IMMEDIATE '" + EscapeSqlLiteral(createTable) + "';\n" +
            "EXCEPTION\n" +
            "  WHEN OTHERS THEN\n" +
            "    IF SQLCODE <> -955 THEN\n" +
            "      RAISE;\n" +
            "    END IF;\n" +
            "END;");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateReadHistoryCommand(string? schemaName, string tableName)
    {
        return new MigrationCommand(
            $"SELECT {QuoteIdentifier("version")} FROM {Qualify(schemaName, tableName)} " +
            $"ORDER BY {QuoteIdentifier("version")}");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateInsertHistoryCommand(
        string? schemaName,
        string tableName,
        long version,
        string description,
        DateTimeOffset appliedUtc)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A migration history description is required.", nameof(description));
        }

        return new MigrationCommand(
            $"INSERT INTO {Qualify(schemaName, tableName)} " +
            $"({QuoteIdentifier("version")}, {QuoteIdentifier("description")}, " +
            $"{QuoteIdentifier("applied_utc")}) " +
            "VALUES (:version, :description, :applied_utc)",
            new[]
            {
                new MigrationCommandParameter("version", version),
                new MigrationCommandParameter("description", description),
                new MigrationCommandParameter("applied_utc", appliedUtc.ToUniversalTime()),
            });
    }

    /// <inheritdoc/>
    public MigrationCommand CreateDeleteHistoryCommand(string? schemaName, string tableName, long version)
    {
        return new MigrationCommand(
            $"DELETE FROM {Qualify(schemaName, tableName)} " +
            $"WHERE {QuoteIdentifier("version")} = :version",
            new[] { new MigrationCommandParameter("version", version) });
    }

    /// <inheritdoc/>
    public MigrationCommand CreateHistoryTableExistsCommand(string? schemaName, string tableName)
    {
        return new MigrationCommand(
            "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END " +
            "FROM ALL_TABLES " +
            "WHERE OWNER = COALESCE(:schema_name, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) " +
            "AND TABLE_NAME = :table_name",
            new[]
            {
                new MigrationCommandParameter("schema_name", schemaName),
                new MigrationCommandParameter("table_name", tableName),
            });
    }

    /// <inheritdoc/>
    public MigrationSchema BuildSchema(IReadOnlyList<MigrationCommand> commands)
    {
        if (commands is null)
        {
            throw new ArgumentNullException(nameof(commands));
        }

        return OracleSchemaBuilder.Build(commands);
    }

    /// <summary>Quotes one Oracle identifier by doubling embedded quote characters.</summary>
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("An Oracle identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Oracle identifiers cannot contain a null character.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Quotes an optional schema and an object name independently.</summary>
    public string QuoteQualifiedName(string? schemaName, string objectName) =>
        Qualify(schemaName, objectName);

    private MigrationCommand GenerateCreateTable(CreateTableOperation operation)
    {
        if (operation.Columns.Count == 0)
        {
            throw new MigrationValidationException(
                $"Create.Table('{operation.TableName}') must declare at least one column.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var primaryKeyCount = 0;
        var columns = new string[operation.Columns.Count];
        for (var index = 0; index < operation.Columns.Count; index++)
        {
            var column = operation.Columns[index];
            if (!names.Add(column.Name))
            {
                throw new MigrationValidationException(
                    $"Table '{operation.TableName}' declares column '{column.Name}' more than once.");
            }

            if (column.IsPrimaryKey)
            {
                primaryKeyCount++;
            }

            columns[index] = GenerateColumnDefinition(column);
        }

        if (primaryKeyCount > 1)
        {
            throw new MigrationValidationException(
                "Inline PrimaryKey supports one column. Use raw SQL for a composite primary key.");
        }

        return new MigrationCommand(
            $"CREATE TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"({string.Join(", ", columns)})");
    }

    private MigrationCommand GenerateAddColumn(AddColumnOperation operation)
    {
        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"ADD ({GenerateColumnDefinition(operation.Column)})");
    }

    private MigrationCommand GenerateAlterColumn(AlterColumnOperation operation)
    {
        if (operation.Column.Type == MigrationColumnType.Unspecified &&
            !operation.Column.IsNullable.HasValue)
        {
            throw new MigrationValidationException(
                $"AlterColumn('{operation.Column.Name}') must change its type or nullability.");
        }

        var definition = new StringBuilder(QuoteIdentifier(operation.Column.Name));
        if (operation.Column.Type != MigrationColumnType.Unspecified)
        {
            definition.Append(' ').Append(GenerateType(operation.Column));
        }

        if (operation.Column.IsNullable.HasValue)
        {
            definition.Append(operation.Column.IsNullable.Value ? " NULL" : " NOT NULL");
        }

        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"MODIFY ({definition})");
    }

    private string GenerateColumnDefinition(ColumnDefinition column)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteIdentifier(column.Name));
        builder.Append(' ');
        builder.Append(GenerateType(column));
        if (column.IsIdentity)
        {
            if (column.Type != MigrationColumnType.Int16 &&
                column.Type != MigrationColumnType.Int32 &&
                column.Type != MigrationColumnType.Int64)
            {
                throw new MigrationValidationException(
                    $"Identity column '{column.Name}' must use AsInt16, AsInt32, or AsInt64.");
            }

            builder.Append(" GENERATED BY DEFAULT AS IDENTITY");
        }

        if (column.IsNullable == false)
        {
            builder.Append(" NOT NULL");
        }

        if (column.IsPrimaryKey)
        {
            builder.Append(" PRIMARY KEY");
        }

        return builder.ToString();
    }

    private static string GenerateType(ColumnDefinition column)
    {
        switch (column.Type)
        {
            case MigrationColumnType.Int16:
                return "NUMBER(5,0)";
            case MigrationColumnType.Int32:
                return "NUMBER(10,0)";
            case MigrationColumnType.Int64:
                return "NUMBER(19,0)";
            case MigrationColumnType.Boolean:
                return "NUMBER(1,0)";
            case MigrationColumnType.Decimal:
                return column.Precision.HasValue
                    ? "NUMBER(" + column.Precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                      column.Scale!.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "NUMBER";
            case MigrationColumnType.Single:
                return "BINARY_FLOAT";
            case MigrationColumnType.Double:
                return "BINARY_DOUBLE";
            case MigrationColumnType.String:
                return column.Length.HasValue
                    ? "VARCHAR2(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "CLOB";
            case MigrationColumnType.Text:
                return "CLOB";
            case MigrationColumnType.Date:
                return "DATE";
            case MigrationColumnType.DateTime:
                return "TIMESTAMP";
            case MigrationColumnType.DateTimeOffset:
                return "TIMESTAMP WITH TIME ZONE";
            case MigrationColumnType.Time:
                return "TIMESTAMP";
            case MigrationColumnType.Guid:
                return "RAW(16)";
            case MigrationColumnType.Binary:
                return "BLOB";
            case MigrationColumnType.Json:
                return "CLOB";
            case MigrationColumnType.JsonBinary:
                return "BLOB";
            case MigrationColumnType.Unspecified:
            default:
                throw new MigrationValidationException($"Column '{column.Name}' must declare a type.");
        }
    }

    private string Qualify(string? schemaName, string objectName)
    {
        return schemaName is null
            ? QuoteIdentifier(objectName)
            : QuoteIdentifier(schemaName) + "." + QuoteIdentifier(objectName);
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static IReadOnlyList<MigrationCommand> One(MigrationCommand command) => new[] { command };
}
