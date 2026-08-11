using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CobaltumOrm.Analysis;

namespace CobaltumOrm.Migrations.PostgreSql;

/// <summary>
/// Generates PostgreSQL SQL for CobaltumORM migration operations and history actions.
/// Identifiers are always treated as unquoted input and safely double-quoted; callers
/// should not include quote characters merely to request quoting.
/// </summary>
public sealed class PostgreSqlMigrationAdapter : IMigrationDatabaseAdapter, IMigrationDryRunDatabaseAdapter
{
    /// <inheritdoc/>
    public IReadOnlyList<MigrationCommand> GenerateCommands(MigrationOperation operation)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (AdvancedMigrationSqlGenerator.TryGenerateConditional(
            operation, AdvancedMigrationProvider.PostgreSql, GenerateCommands, out var conditionalCommands))
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
            var commands = new List<MigrationCommand>(GenerateAlterColumn(alterColumn));
            commands.AddRange(AdvancedMigrationSqlGenerator.GenerateAlterColumnAuxiliaryCommands(
                alterColumn.Column,
                alterColumn.TableName,
                alterColumn.SchemaName,
                AdvancedMigrationProvider.PostgreSql,
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

        if (AdvancedMigrationSqlGenerator.TryGenerate(
            operation,
            AdvancedMigrationProvider.PostgreSql,
            QuoteIdentifier,
            Qualify,
            out var advancedCommands))
        {
            return advancedCommands;
        }

        throw new NotSupportedException(
            $"Operation type '{operation.GetType().FullName}' is not supported by the PostgreSQL adapter.");
    }

    /// <inheritdoc/>
    public MigrationCommand CreateEnsureHistoryTableCommand(string? schemaName, string tableName)
    {
        var table = Qualify(schemaName, tableName);
        return new MigrationCommand(
            $"CREATE TABLE IF NOT EXISTS {table} (" +
            $"{QuoteIdentifier("version")} bigint NOT NULL PRIMARY KEY, " +
            $"{QuoteIdentifier("description")} text NOT NULL, " +
            $"{QuoteIdentifier("applied_utc")} timestamp with time zone NOT NULL);" );
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
        return new MigrationCommand(
            "SELECT EXISTS (" +
            "SELECT 1 FROM pg_catalog.pg_class AS c " +
            "INNER JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = COALESCE(@schema_name, current_schema()) " +
            "AND c.relname = @table_name AND c.relkind IN ('r', 'p'));",
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

        var schema = new DatabaseSchema(Array.Empty<Table>());
        foreach (var command in commands)
        {
            if (command is null)
            {
                throw new MigrationValidationException("The schema preview command collection contains null.");
            }

            if (!command.AnalyzeForSchema)
            {
                continue;
            }

            var result = PostgreSqlSchemaBuilder.ApplyScript(schema, command.CommandText);
            if (result.HasErrors)
            {
                var diagnostic = result.Diagnostics[0];
                throw new MigrationValidationException(
                    $"Final schema could not be determined from migration SQL: {diagnostic.Code} {diagnostic.Message}");
            }

            schema = result.Schema;
        }

        return new MigrationSchema(schema.Tables.Select(table =>
            new MigrationSchemaTable(
                table.Schema,
                table.Name,
                table.Columns.Select(column =>
                    new MigrationSchemaColumn(
                        column.Name,
                        column.SqlType,
                        column.IsNullable,
                        column.IsPrimaryKey,
                        column.DefaultExpression,
                        column.IsIdentity)))));
    }

    /// <summary>
    /// Quotes one PostgreSQL identifier by doubling embedded quote characters.
    /// Dots have no special meaning here; schema and table names are quoted separately.
    /// </summary>
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A PostgreSQL identifier is required.", nameof(identifier));
        }

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("PostgreSQL identifiers cannot contain a null character.", nameof(identifier));
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
            $"CREATE TABLE{(operation.IfNotExists ? " IF NOT EXISTS" : string.Empty)} " +
            $"{Qualify(operation.SchemaName, operation.TableName)} " +
            $"({string.Join(", ", columns)});");
    }

    private MigrationCommand GenerateAddColumn(AddColumnOperation operation)
    {
        return new MigrationCommand(
            $"ALTER TABLE{(operation.IfTableExists ? " IF EXISTS" : string.Empty)} " +
            $"{Qualify(operation.SchemaName, operation.TableName)} " +
            $"ADD COLUMN {GenerateColumnDefinition(operation.Column)};");
    }

    private IReadOnlyList<MigrationCommand> GenerateAlterColumn(AlterColumnOperation operation)
    {
        var commands = new List<MigrationCommand>();
        var table = (operation.IfTableExists ? "IF EXISTS " : string.Empty) +
            Qualify(operation.SchemaName, operation.TableName);
        var column = QuoteIdentifier(operation.Column.Name);
        if (operation.Column.Type != MigrationColumnType.Unspecified)
        {
            commands.Add(new MigrationCommand(
                $"ALTER TABLE {table} ALTER COLUMN {column} TYPE {GenerateType(operation.Column)};"));
        }

        if (operation.Column.IsNullable.HasValue)
        {
            var nullability = operation.Column.IsNullable.Value ? "DROP NOT NULL" : "SET NOT NULL";
            commands.Add(new MigrationCommand(
                $"ALTER TABLE {table} ALTER COLUMN {column} {nullability};"));
        }

        if (operation.Column.HasDefaultValue)
        {
            commands.Add(new MigrationCommand(
                $"ALTER TABLE {table} ALTER COLUMN {column} SET DEFAULT " +
                AdvancedMigrationSqlGenerator.GenerateDefaultValue(
                    operation.Column.DefaultValue,
                    AdvancedMigrationProvider.PostgreSql) + ";"));
        }

        if (commands.Count == 0)
        {
            throw new MigrationValidationException(
                $"AlterColumn('{operation.Column.Name}') must change its type, nullability, or default value.");
        }

        return commands;
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

        builder.Append(AdvancedMigrationSqlGenerator.GenerateColumnOptions(
            column,
            string.Empty,
            null,
            AdvancedMigrationProvider.PostgreSql,
            QuoteIdentifier,
            Qualify));

        if (column.IsNullable == false)
        {
            builder.Append(" NOT NULL");
        }

        if (column.IsPrimaryKey)
        {
            if (column.PrimaryKeyName != null)
                builder.Append(" CONSTRAINT ").Append(QuoteIdentifier(column.PrimaryKeyName));
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
            case MigrationColumnType.Byte:
                return "smallint";
            case MigrationColumnType.Int32:
                return "integer";
            case MigrationColumnType.Int64:
                return "bigint";
            case MigrationColumnType.Boolean:
                return "boolean";
            case MigrationColumnType.Decimal:
                return column.Precision.HasValue
                    ? "numeric(" + column.Precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                      column.Scale!.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "numeric";
            case MigrationColumnType.Currency:
                return "money";
            case MigrationColumnType.Single:
                return "real";
            case MigrationColumnType.Double:
                return "double precision";
            case MigrationColumnType.String:
            case MigrationColumnType.AnsiString:
                return column.Length.HasValue
                    ? "character varying(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "text";
            case MigrationColumnType.FixedString:
            case MigrationColumnType.FixedAnsiString:
                return "character(" + column.Length!.Value.ToString(CultureInfo.InvariantCulture) + ")";
            case MigrationColumnType.Text:
                return "text";
            case MigrationColumnType.Date:
                return "date";
            case MigrationColumnType.DateTime:
                return "timestamp without time zone";
            case MigrationColumnType.DateTimeOffset:
                return column.DateTimePrecision.HasValue
                    ? "timestamp(" + column.DateTimePrecision.Value.ToString(CultureInfo.InvariantCulture) + ") with time zone"
                    : "timestamp with time zone";
            case MigrationColumnType.Time:
                return "time without time zone";
            case MigrationColumnType.Guid:
                return "uuid";
            case MigrationColumnType.Binary:
                return "bytea";
            case MigrationColumnType.Xml:
                return "xml";
            case MigrationColumnType.Custom:
                return column.CustomType!;
            case MigrationColumnType.Json:
                return "json";
            case MigrationColumnType.JsonBinary:
                return "jsonb";
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
            AdvancedMigrationProvider.PostgreSql,
            QuoteIdentifier,
            Qualify));
        commands.AddRange(AdvancedMigrationSqlGenerator.GenerateReferencedByCommands(
            columns,
            AdvancedMigrationProvider.PostgreSql,
            QuoteIdentifier,
            Qualify));
        commands.AddRange(AdvancedMigrationSqlGenerator.GenerateDescriptionCommands(
            tableName,
            schemaName,
            tableDescription,
            columns,
            AdvancedMigrationProvider.PostgreSql,
            QuoteIdentifier,
            Qualify));
        return commands.AsReadOnly();
    }
}
