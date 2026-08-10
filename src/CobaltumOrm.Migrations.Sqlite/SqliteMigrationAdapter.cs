using System;
using System.Collections.Generic;
using System.Text;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.Sqlite;

/// <summary>
/// Generates SQLite migration SQL and reconstructs schemas for migration dry runs.
/// SQLite has one user schema, so a non-empty schema name is rejected explicitly.
/// </summary>
public sealed class SqliteMigrationAdapter : IMigrationDatabaseAdapter, IMigrationDryRunDatabaseAdapter
{
    /// <summary>Generates SQLite commands for one migration operation.</summary>
    /// <param name="operation">The operation to translate.</param>
    /// <returns>The commands in execution order.</returns>
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
            return GenerateAlterColumn(alterColumn);
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
            return One(new MigrationCommand(
                $"ALTER TABLE {Qualify(renameTable.SchemaName, renameTable.OldName)} " +
                $"RENAME TO {QuoteIdentifier(renameTable.NewName)};"));
        }

        if (operation is RenameColumnOperation renameColumn)
        {
            return One(new MigrationCommand(
                $"ALTER TABLE {Qualify(renameColumn.SchemaName, renameColumn.TableName)} " +
                $"RENAME COLUMN {QuoteIdentifier(renameColumn.OldName)} " +
                $"TO {QuoteIdentifier(renameColumn.NewName)};"));
        }

        if (operation is ExecuteSqlOperation executeSql)
        {
            return One(new MigrationCommand(executeSql.Sql));
        }

        throw new NotSupportedException(
            $"Operation type '{operation.GetType().FullName}' is not supported by the SQLite adapter.");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateEnsureHistoryTableCommand(string? schemaName, string tableName)
    {
        return new MigrationCommand(
            $"CREATE TABLE IF NOT EXISTS {Qualify(schemaName, tableName)} (" +
            $"{QuoteIdentifier("version")} INTEGER NOT NULL PRIMARY KEY, " +
            $"{QuoteIdentifier("description")} TEXT NOT NULL, " +
            $"{QuoteIdentifier("applied_utc")} TEXT NOT NULL);");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateReadHistoryCommand(string? schemaName, string tableName)
    {
        return new MigrationCommand(
            $"SELECT {QuoteIdentifier("version")} FROM {Qualify(schemaName, tableName)} " +
            $"ORDER BY {QuoteIdentifier("version")};");
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
            $"({QuoteIdentifier("version")}, {QuoteIdentifier("description")}, {QuoteIdentifier("applied_utc")}) " +
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
            $"DELETE FROM {Qualify(schemaName, tableName)} WHERE {QuoteIdentifier("version")} = @version;",
            new[] { new MigrationCommandParameter("version", version) });
    }

    /// <inheritdoc/>
    public MigrationCommand CreateHistoryTableExistsCommand(string? schemaName, string tableName)
    {
        RejectSchema(schemaName);
        return new MigrationCommand(
            "SELECT EXISTS (" +
            "SELECT 1 FROM sqlite_master " +
            "WHERE type = 'table' AND name = @table_name);",
            new[] { new MigrationCommandParameter("table_name", tableName) });
    }

    /// <inheritdoc/>
    public MigrationSchema BuildSchema(IReadOnlyList<MigrationCommand> commands)
    {
        if (commands is null)
        {
            throw new ArgumentNullException(nameof(commands));
        }

        var builder = new SqliteSchemaBuilder();
        foreach (var command in commands)
        {
            if (command is null)
            {
                throw new MigrationValidationException("The schema preview command collection contains null.");
            }

            builder.Apply(command.CommandText);
        }

        return builder.ToMigrationSchema();
    }

    /// <summary>Quotes a SQLite identifier by doubling embedded quote characters.</summary>
    /// <param name="identifier">The unquoted identifier.</param>
    /// <returns>The quoted identifier.</returns>
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A SQLite identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("SQLite identifiers cannot contain a null character.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
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
        RejectUnsupportedAddedColumn(operation.Column);
        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"ADD COLUMN {GenerateColumnDefinition(operation.Column)};");
    }

    private IReadOnlyList<MigrationCommand> GenerateAlterColumn(AlterColumnOperation operation)
    {
        RejectSchema(operation.SchemaName);
        if (operation.Column.Type == MigrationColumnType.Unspecified &&
            !operation.Column.IsNullable.HasValue)
        {
            throw new MigrationValidationException(
                $"AlterColumn('{operation.Column.Name}') must change its type or nullability.");
        }

        throw new NotSupportedException(
            $"SQLite does not support ALTER COLUMN for '{operation.TableName}.{operation.Column.Name}'. " +
            "A type or nullability change requires a table rebuild, but the migration adapter does not have " +
            "enough existing table metadata to rebuild it safely.");
    }

    private void RejectUnsupportedAddedColumn(ColumnDefinition column)
    {
        if (column.IsPrimaryKey || column.IsIdentity)
        {
            throw new NotSupportedException(
                $"SQLite ALTER TABLE ADD COLUMN cannot add primary-key or identity column '{column.Name}'.");
        }
    }

    private string GenerateColumnDefinition(ColumnDefinition column)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteIdentifier(column.Name));
        builder.Append(' ');
        builder.Append(GenerateType(column));

        if (column.IsIdentity)
        {
            if (column.Type != MigrationColumnType.Int64 || !column.IsPrimaryKey)
            {
                throw new MigrationValidationException(
                    $"SQLite identity column '{column.Name}' must be an Int64 primary key.");
            }

            builder.Append(" PRIMARY KEY AUTOINCREMENT");
            return builder.ToString();
        }

        if (column.IsNullable == false || column.IsPrimaryKey)
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
            case MigrationColumnType.Int32:
            case MigrationColumnType.Int64:
            case MigrationColumnType.Boolean:
                return "INTEGER";
            case MigrationColumnType.Decimal:
                return "NUMERIC";
            case MigrationColumnType.Single:
            case MigrationColumnType.Double:
                return "REAL";
            case MigrationColumnType.String:
            case MigrationColumnType.Text:
            case MigrationColumnType.Date:
            case MigrationColumnType.DateTime:
            case MigrationColumnType.DateTimeOffset:
            case MigrationColumnType.Time:
            case MigrationColumnType.Guid:
            case MigrationColumnType.Json:
                return "TEXT";
            case MigrationColumnType.Binary:
            case MigrationColumnType.JsonBinary:
                return "BLOB";
            case MigrationColumnType.Unspecified:
            default:
                throw new MigrationValidationException($"Column '{column.Name}' must declare a type.");
        }
    }

    private string Qualify(string? schemaName, string objectName)
    {
        RejectSchema(schemaName);
        return QuoteIdentifier(objectName);
    }

    private static void RejectSchema(string? schemaName)
    {
        if (!string.IsNullOrEmpty(schemaName))
        {
            throw new NotSupportedException(
                "SQLite migrations do not support non-empty schema names.");
        }
    }

    private static IReadOnlyList<MigrationCommand> One(MigrationCommand command) => new[] { command };
}
