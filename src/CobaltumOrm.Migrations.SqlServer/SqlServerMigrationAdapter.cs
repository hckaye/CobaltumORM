using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.SqlServer;

/// <summary>
/// Generates SQL Server commands for CobaltumORM migration operations and history
/// actions. Object names are treated as literal names and are always bracket quoted.
/// </summary>
public sealed class SqlServerMigrationAdapter : IMigrationDatabaseAdapter, IMigrationDryRunDatabaseAdapter
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
                $"DROP TABLE {Qualify(deleteTable.SchemaName, deleteTable.TableName)};"));
        }

        if (operation is DeleteColumnOperation deleteColumn)
        {
            return One(new MigrationCommand(
                $"ALTER TABLE {Qualify(deleteColumn.SchemaName, deleteColumn.TableName)} " +
                $"DROP COLUMN {QuoteIdentifier(deleteColumn.ColumnName)};"));
        }

        if (operation is RenameTableOperation renameTable)
        {
            return One(CreateRenameCommand(
                renameTable.SchemaName,
                renameTable.OldName,
                renameTable.NewName,
                "OBJECT"));
        }

        if (operation is RenameColumnOperation renameColumn)
        {
            return One(CreateRenameCommand(
                renameColumn.SchemaName,
                renameColumn.TableName,
                renameColumn.OldName,
                renameColumn.NewName,
                "COLUMN"));
        }

        if (operation is ExecuteSqlOperation executeSql)
        {
            return One(new MigrationCommand(executeSql.Sql));
        }

        throw new NotSupportedException(
            $"Operation type '{operation.GetType().FullName}' is not supported by the SQL Server adapter.");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateEnsureHistoryTableCommand(string? schemaName, string tableName)
    {
        var schema = ResolveSchema(schemaName);
        var table = Qualify(schema, tableName);
        return new MigrationCommand(
            "IF NOT EXISTS (" +
            "SELECT 1 FROM sys.tables AS t " +
            "INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "WHERE s.name = " + SqlStringLiteral(schema) +
            " AND t.name = " + SqlStringLiteral(tableName) +
            ") BEGIN " +
            $"CREATE TABLE {table} (" +
            "[version] bigint NOT NULL PRIMARY KEY, " +
            "[description] nvarchar(max) NOT NULL, " +
            "[applied_utc] datetimeoffset NOT NULL); " +
            "END;");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateReadHistoryCommand(string? schemaName, string tableName)
    {
        return new MigrationCommand(
            $"SELECT [version] FROM {Qualify(schemaName, tableName)} ORDER BY [version];");
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
            "([version], [description], [applied_utc]) " +
            "VALUES (@version, @description, @applied_utc);",
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
            $"DELETE FROM {Qualify(schemaName, tableName)} WHERE [version] = @version;",
            new[] { new MigrationCommandParameter("version", version) });
    }

    /// <inheritdoc/>
    public MigrationCommand CreateHistoryTableExistsCommand(string? schemaName, string tableName)
    {
        var schema = ResolveSchema(schemaName);
        return new MigrationCommand(
            "SELECT CONVERT(bit, CASE WHEN EXISTS (" +
            "SELECT 1 FROM sys.tables AS t " +
            "INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "WHERE s.name = @schema_name AND t.name = @table_name" +
            ") THEN 1 ELSE 0 END);",
            new[]
            {
                new MigrationCommandParameter("schema_name", schema),
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

        var schema = SqlServerSchemaBuilder.CreateEmpty();
        foreach (var command in commands)
        {
            if (command is null)
            {
                throw new MigrationValidationException("The schema preview command collection contains null.");
            }

            schema = SqlServerSchemaBuilder.ApplyScript(schema, command);
        }

        return SqlServerSchemaBuilder.ToMigrationSchema(schema);
    }

    /// <summary>
    /// Quotes one SQL Server identifier. A closing bracket in the name is escaped
    /// by doubling it; dots in a name have no qualification meaning.
    /// </summary>
    /// <param name="identifier">One literal identifier, without qualification.</param>
    /// <returns>The bracket-quoted identifier.</returns>
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A SQL Server identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("SQL Server identifiers cannot contain a null character.", nameof(identifier));
        }

        return "[" + identifier.Replace("]", "]]") + "]";
    }

    private MigrationCommand GenerateCreateTable(CreateTableOperation operation)
    {
        if (operation.Columns.Count == 0)
        {
            throw new MigrationValidationException(
                $"Create.Table('{operation.TableName}') must declare at least one column.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            $"({string.Join(", ", columns)});");
    }

    private MigrationCommand GenerateAddColumn(AddColumnOperation operation)
    {
        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"ADD {GenerateColumnDefinition(operation.Column)};");
    }

    private MigrationCommand GenerateAlterColumn(AlterColumnOperation operation)
    {
        if (operation.Column.Type == MigrationColumnType.Unspecified)
        {
            throw new MigrationValidationException(
                $"AlterColumn('{operation.Column.Name}') must declare a type because SQL Server requires a type in ALTER COLUMN.");
        }

        var type = GenerateType(operation.Column);
        var nullability = operation.Column.IsNullable.HasValue
            ? (operation.Column.IsNullable.Value ? " NULL" : " NOT NULL")
            : string.Empty;
        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"ALTER COLUMN {QuoteIdentifier(operation.Column.Name)} {type}{nullability};");
    }

    private MigrationCommand CreateRenameCommand(
        string? schemaName,
        string oldName,
        string newName,
        string objectType)
    {
        return new MigrationCommand(
            "EXEC sys.sp_rename @objname = @old_name, @newname = @new_name, " +
            $"@objtype = N'{objectType}';",
            new[]
            {
                new MigrationCommandParameter("old_name", Qualify(schemaName, oldName)),
                new MigrationCommandParameter("new_name", newName),
            });
    }

    private MigrationCommand CreateRenameCommand(
        string? schemaName,
        string tableName,
        string oldColumnName,
        string newColumnName,
        string objectType)
    {
        return new MigrationCommand(
            "EXEC sys.sp_rename @objname = @old_name, @newname = @new_name, " +
            $"@objtype = N'{objectType}';",
            new[]
            {
                new MigrationCommandParameter(
                    "old_name",
                    QualifyColumn(schemaName, tableName, oldColumnName)),
                new MigrationCommandParameter("new_name", newColumnName),
            });
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

            builder.Append(" IDENTITY(1,1)");
        }

        if (column.IsNullable == false)
        {
            builder.Append(" NOT NULL");
        }
        else if (column.IsNullable == true)
        {
            builder.Append(" NULL");
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
                return "smallint";
            case MigrationColumnType.Int32:
                return "int";
            case MigrationColumnType.Int64:
                return "bigint";
            case MigrationColumnType.Boolean:
                return "bit";
            case MigrationColumnType.Decimal:
                if (!column.Precision.HasValue)
                {
                    return "decimal";
                }

                if (column.Precision.Value > 38)
                {
                    throw new MigrationValidationException(
                        $"Decimal precision for column '{column.Name}' cannot exceed SQL Server's maximum of 38.");
                }

                return "decimal(" +
                    column.Precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                    column.Scale!.Value.ToString(CultureInfo.InvariantCulture) + ")";
            case MigrationColumnType.Single:
                return "real";
            case MigrationColumnType.Double:
                return "float";
            case MigrationColumnType.String:
                if (!column.Length.HasValue)
                {
                    return "nvarchar(max)";
                }

                if (column.Length.Value > 4000)
                {
                    throw new MigrationValidationException(
                        $"String length for column '{column.Name}' cannot exceed SQL Server's nvarchar limit of 4000.");
                }

                return "nvarchar(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")";
            case MigrationColumnType.Text:
            case MigrationColumnType.Json:
            case MigrationColumnType.JsonBinary:
                return "nvarchar(max)";
            case MigrationColumnType.Date:
                return "date";
            case MigrationColumnType.DateTime:
                return "datetime2";
            case MigrationColumnType.DateTimeOffset:
                return "datetimeoffset";
            case MigrationColumnType.Time:
                return "time";
            case MigrationColumnType.Guid:
                return "uniqueidentifier";
            case MigrationColumnType.Binary:
                return "varbinary(max)";
            case MigrationColumnType.Unspecified:
            default:
                throw new MigrationValidationException($"Column '{column.Name}' must declare a type.");
        }
    }

    private string Qualify(string? schemaName, string objectName)
    {
        return QuoteIdentifier(ResolveSchema(schemaName)) + "." + QuoteIdentifier(objectName);
    }

    private string QualifyColumn(string? schemaName, string tableName, string columnName)
    {
        return Qualify(schemaName, tableName) + "." + QuoteIdentifier(columnName);
    }

    private string ResolveSchema(string? schemaName)
    {
        return schemaName ?? "dbo";
    }

    private static string SqlStringLiteral(string value)
    {
        return "N'" + value.Replace("'", "''") + "'";
    }

    private static IReadOnlyList<MigrationCommand> One(MigrationCommand command) =>
        new[] { command };
}
