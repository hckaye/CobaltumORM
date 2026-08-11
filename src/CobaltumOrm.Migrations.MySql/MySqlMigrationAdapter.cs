using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.MySql;

/// <summary>
/// Generates MySQL 8 migration SQL and reconstructs the schema for migration dry runs.
/// Identifiers are treated as unquoted input and are quoted with MySQL backticks.
/// </summary>
public sealed class MySqlMigrationAdapter : IMigrationDatabaseAdapter, IMigrationDryRunDatabaseAdapter
{
    /// <inheritdoc/>
    public IReadOnlyList<MigrationCommand> GenerateCommands(MigrationOperation operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (AdvancedMigrationSqlGenerator.TryGenerateConditional(
            operation, AdvancedMigrationProvider.MySql, GenerateCommands, out var conditionalCommands))
            return conditionalCommands;

        if (operation is CreateTableOperation createTable)
        {
            return WithColumnIndexes(
                GenerateCreateTable(createTable),
                createTable.Columns,
                createTable.TableName,
                createTable.SchemaName,
                createTable.Description);
        }

        if (operation is AddColumnOperation addColumn)
        {
            return WithColumnIndexes(
                GenerateAddColumn(addColumn),
                new[] { addColumn.Column },
                addColumn.TableName,
                addColumn.SchemaName,
                null);
        }

        if (operation is AlterColumnOperation alterColumn)
        {
            var commands = new List<MigrationCommand> { GenerateAlterColumn(alterColumn) };
            commands.AddRange(AdvancedMigrationSqlGenerator.GenerateColumnIndexCommands(
                new[] { alterColumn.Column },
                alterColumn.TableName,
                alterColumn.SchemaName,
                AdvancedMigrationProvider.MySql,
                QuoteIdentifier,
                Qualify));
            commands.AddRange(AdvancedMigrationSqlGenerator.GenerateReferencedByCommands(
                new[] { alterColumn.Column },
                AdvancedMigrationProvider.MySql,
                QuoteIdentifier,
                Qualify));
            return commands.AsReadOnly();
        }

        if (operation is DeleteTableOperation deleteTable)
        {
            return One(new MigrationCommand(
                $"DROP TABLE{(deleteTable.IfExists ? " IF EXISTS" : string.Empty)} " +
                $"{Qualify(deleteTable.SchemaName, deleteTable.TableName)};"));
        }

        if (operation is DeleteColumnOperation deleteColumn)
        {
            return One(new MigrationCommand(
                $"ALTER TABLE {Qualify(deleteColumn.SchemaName, deleteColumn.TableName)} " +
                $"DROP COLUMN {QuoteIdentifier(deleteColumn.ColumnName)};"));
        }

        if (operation is RenameTableOperation renameTable)
        {
            var oldTable = Qualify(renameTable.SchemaName, renameTable.OldName);
            var newTable = Qualify(renameTable.SchemaName, renameTable.NewName);
            return One(new MigrationCommand($"RENAME TABLE {oldTable} TO {newTable};"));
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

        if (AdvancedMigrationSqlGenerator.TryGenerate(
            operation,
            AdvancedMigrationProvider.MySql,
            QuoteIdentifier,
            Qualify,
            out var advancedCommands))
        {
            return advancedCommands;
        }

        throw new NotSupportedException(
            $"Operation type '{operation.GetType().FullName}' is not supported by the MySQL adapter.");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateEnsureHistoryTableCommand(string? schemaName, string tableName)
    {
        return new MigrationCommand(
            $"CREATE TABLE IF NOT EXISTS {Qualify(schemaName, tableName)} (" +
            $"{QuoteIdentifier("version")} bigint NOT NULL PRIMARY KEY, " +
            $"{QuoteIdentifier("description")} text NOT NULL, " +
            $"{QuoteIdentifier("applied_utc")} datetime(6) NOT NULL);");
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
            $"DELETE FROM {Qualify(schemaName, tableName)} " +
            $"WHERE {QuoteIdentifier("version")} = @version;",
            new[] { new MigrationCommandParameter("version", version) });
    }

    /// <inheritdoc/>
    public MigrationCommand CreateHistoryTableExistsCommand(string? schemaName, string tableName)
    {
        return new MigrationCommand(
            "SELECT EXISTS (" +
            "SELECT 1 FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_SCHEMA = COALESCE(@schema_name, DATABASE()) " +
            "AND TABLE_NAME = @table_name AND TABLE_TYPE = 'BASE TABLE');",
            new[]
            {
                new MigrationCommandParameter("schema_name", schemaName),
                new MigrationCommandParameter("table_name", tableName),
            });
    }

    /// <inheritdoc/>
    public MigrationSchema BuildSchema(IReadOnlyList<MigrationCommand> commands)
    {
        if (commands is null) throw new ArgumentNullException(nameof(commands));
        var schemaCommands = new List<MigrationCommand>();
        foreach (var command in commands)
        {
            if (command is null)
                throw new MigrationValidationException("The schema preview command collection contains null.");
            if (command.AnalyzeForSchema) schemaCommands.Add(command);
        }
        return MySqlSchemaBuilder.Build(schemaCommands);
    }

    /// <summary>Quotes one MySQL identifier by doubling embedded backticks.</summary>
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A MySQL identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("MySQL identifiers cannot contain a null character.", nameof(identifier));
        }

        return "`" + identifier.Replace("`", "``") + "`";
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
            if (column is null)
            {
                throw new MigrationValidationException(
                    $"Create.Table('{operation.TableName}') contains a null column definition.");
            }

            if (!names.Add(column.Name))
            {
                throw new MigrationValidationException(
                    $"Table '{operation.TableName}' declares column '{column.Name}' more than once.");
            }

            if (column.IsPrimaryKey)
            {
                primaryKeyCount++;
            }

            columns[index] = GenerateColumnDefinition(column, includeExplicitNullable: false);
        }

        if (primaryKeyCount > 1)
        {
            throw new MigrationValidationException(
                "Inline PrimaryKey supports one column. Use raw SQL for a composite primary key.");
        }

        return new MigrationCommand(
            $"CREATE TABLE{(operation.IfNotExists ? " IF NOT EXISTS" : string.Empty)} " +
            $"{Qualify(operation.SchemaName, operation.TableName)} " +
            $"({string.Join(", ", columns)});");
    }

    private MigrationCommand GenerateAddColumn(AddColumnOperation operation)
    {
        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"ADD COLUMN {GenerateColumnDefinition(operation.Column, includeExplicitNullable: false)};");
    }

    private MigrationCommand GenerateAlterColumn(AlterColumnOperation operation)
    {
        if (operation.Column.Type == MigrationColumnType.Unspecified)
        {
            throw new NotSupportedException(
                $"MySQL ALTER COLUMN '{operation.Column.Name}' requires a complete column type and explicit nullability. " +
                "MODIFY COLUMN replaces the full column definition, so the provider-neutral operation " +
                "must specify both the target type and whether the column is nullable.");
        }

        if (!operation.Column.IsNullable.HasValue)
        {
            throw new NotSupportedException(
                $"MySQL ALTER COLUMN '{operation.Column.Name}' requires explicit nullability. " +
                "MODIFY COLUMN replaces the full column definition, so omitting NULL or NOT NULL " +
                "could change the existing column's nullability.");
        }

        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"MODIFY COLUMN {GenerateColumnDefinition(operation.Column, includeExplicitNullable: true)};");
    }

    private string GenerateColumnDefinition(ColumnDefinition column, bool includeExplicitNullable)
    {
        if (column is null)
        {
            throw new MigrationValidationException("A column definition is required.");
        }

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
                    $"Identity column '{column.Name}' must use AsInt16, AsInt32, or AsInt64 for MySQL AUTO_INCREMENT.");
            }

            builder.Append(" AUTO_INCREMENT");
        }

        builder.Append(AdvancedMigrationSqlGenerator.GenerateColumnOptions(
            column,
            string.Empty,
            null,
            AdvancedMigrationProvider.MySql,
            QuoteIdentifier,
            Qualify));

        if (column.IsNullable == false)
        {
            builder.Append(" NOT NULL");
        }
        else if (includeExplicitNullable && column.IsNullable == true)
        {
            builder.Append(" NULL");
        }

        if (column.IsPrimaryKey)
        {
            if (column.PrimaryKeyName != null)
                builder.Append(" CONSTRAINT ").Append(QuoteIdentifier(column.PrimaryKeyName));
            builder.Append(" PRIMARY KEY");
        }

        if (column.Description != null)
        {
            builder.Append(" COMMENT '")
                .Append(AdvancedMigrationSqlGenerator.CombinedDescription(column).Replace("'", "''"))
                .Append('\'');
        }

        return builder.ToString();
    }

    private static string GenerateType(ColumnDefinition column)
    {
        switch (column.Type)
        {
            case MigrationColumnType.Int16:
                return "smallint";
            case MigrationColumnType.Byte:
                return "tinyint unsigned";
            case MigrationColumnType.Int32:
                return "int";
            case MigrationColumnType.Int64:
                return "bigint";
            case MigrationColumnType.Boolean:
                return "tinyint(1)";
            case MigrationColumnType.Decimal:
                if (column.Precision.HasValue != column.Scale.HasValue)
                {
                    throw new MigrationValidationException(
                        $"Decimal column '{column.Name}' must specify both precision and scale, or neither.");
                }

                return column.Precision.HasValue
                    ? "decimal(" + column.Precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                      column.Scale!.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "decimal";
            case MigrationColumnType.Currency:
                return "decimal(19,4)";
            case MigrationColumnType.Single:
                return "float";
            case MigrationColumnType.Double:
                return "double";
            case MigrationColumnType.String:
            case MigrationColumnType.AnsiString:
                return column.Length.HasValue
                    ? "varchar(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "text";
            case MigrationColumnType.FixedString:
            case MigrationColumnType.FixedAnsiString:
                return "char(" + column.Length!.Value.ToString(CultureInfo.InvariantCulture) + ")";
            case MigrationColumnType.Text:
                return "text";
            case MigrationColumnType.Date:
                return "date";
            case MigrationColumnType.DateTime:
                return "datetime";
            case MigrationColumnType.DateTimeOffset:
                return column.DateTimePrecision.HasValue
                    ? "datetime(" + column.DateTimePrecision.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "datetime";
            case MigrationColumnType.Time:
                return "time";
            case MigrationColumnType.Guid:
                return "char(36)";
            case MigrationColumnType.Binary:
                return column.Length.HasValue
                    ? "varbinary(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "longblob";
            case MigrationColumnType.Xml:
                return "longtext";
            case MigrationColumnType.Custom:
                return column.CustomType!;
            case MigrationColumnType.Json:
            case MigrationColumnType.JsonBinary:
                return "json";
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

    private static IReadOnlyList<MigrationCommand> One(MigrationCommand command) =>
        new[] { command };

    private IReadOnlyList<MigrationCommand> WithColumnIndexes(
        MigrationCommand command,
        IEnumerable<ColumnDefinition> columns,
        string tableName,
        string? schemaName,
        string? tableDescription)
    {
        var commands = new List<MigrationCommand> { command };
        commands.AddRange(AdvancedMigrationSqlGenerator.GenerateColumnIndexCommands(
            columns,
            tableName,
            schemaName,
            AdvancedMigrationProvider.MySql,
            QuoteIdentifier,
            Qualify));
        commands.AddRange(AdvancedMigrationSqlGenerator.GenerateReferencedByCommands(
            columns,
            AdvancedMigrationProvider.MySql,
            QuoteIdentifier,
            Qualify));
        commands.AddRange(AdvancedMigrationSqlGenerator.GenerateDescriptionCommands(
            tableName,
            schemaName,
            tableDescription,
            columns,
            AdvancedMigrationProvider.MySql,
            QuoteIdentifier,
            Qualify));
        return commands.AsReadOnly();
    }
}
