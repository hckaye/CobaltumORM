using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        if (AdvancedMigrationSqlGenerator.TryGenerateConditional(
            operation, AdvancedMigrationProvider.Oracle, GenerateCommands, out var conditionalCommands))
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
            commands.AddRange(AdvancedMigrationSqlGenerator.GenerateAlterColumnAuxiliaryCommands(
                alterColumn.Column,
                alterColumn.TableName,
                alterColumn.SchemaName,
                AdvancedMigrationProvider.Oracle,
                QuoteIdentifier,
                Qualify));
            return commands.AsReadOnly();
        }

        if (operation is DeleteTableOperation deleteTable)
        {
            if (deleteTable.IfExists)
                throw new NotSupportedException("Oracle does not provide portable DROP TABLE IF EXISTS syntax.");
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

        if (AdvancedMigrationSqlGenerator.TryGenerate(
            operation,
            AdvancedMigrationProvider.Oracle,
            QuoteIdentifier,
            Qualify,
            out var advancedCommands))
        {
            return advancedCommands;
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

        var schemaCommands = new List<MigrationCommand>();
        foreach (var command in commands)
        {
            if (command is null)
                throw new MigrationValidationException("The schema preview command collection contains null.");
            if (command.AnalyzeForSchema) schemaCommands.Add(command);
        }
        return OracleSchemaBuilder.Build(schemaCommands);
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
        if (operation.IfNotExists)
            throw new NotSupportedException("Oracle does not provide portable CREATE TABLE IF NOT EXISTS syntax.");
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
        if (operation.Column.ComputedExpression != null)
            throw new NotSupportedException("Oracle cannot change a regular column into a virtual column with MODIFY. Drop and recreate the column.");
        if (operation.Column.Type == MigrationColumnType.Unspecified &&
            !operation.Column.IsNullable.HasValue &&
            !operation.Column.HasDefaultValue)
        {
            throw new MigrationValidationException(
                $"AlterColumn('{operation.Column.Name}') must change its type, nullability, or default value.");
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

        if (operation.Column.HasDefaultValue)
        {
            definition.Append(" DEFAULT ").Append(
                AdvancedMigrationSqlGenerator.GenerateDefaultValue(
                    operation.Column.DefaultValue,
                    AdvancedMigrationProvider.Oracle));
        }

        return new MigrationCommand(
            $"ALTER TABLE {Qualify(operation.SchemaName, operation.TableName)} " +
            $"MODIFY ({definition})");
    }

    private string GenerateColumnDefinition(ColumnDefinition column)
    {
        if (column.ComputedExpression != null)
            return GenerateComputedColumnDefinition(column);

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
            AdvancedMigrationProvider.Oracle,
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

    private string GenerateComputedColumnDefinition(ColumnDefinition column)
    {
        if (column.IsComputedStored)
            throw new NotSupportedException("Oracle does not support stored computed columns.");
        if (column.IsIdentity || column.HasDefaultValue || column.CollationName != null || column.ForeignKey != null)
            throw new MigrationValidationException(
                $"Virtual column '{column.Name}' cannot also use identity, a default, a collation, or a foreign key.");

        var builder = new StringBuilder()
            .Append(QuoteIdentifier(column.Name))
            .Append(" GENERATED ALWAYS AS (").Append(column.ComputedExpression).Append(')');
        if (column.IsNullable == false) builder.Append(" NOT NULL");
        if (column.IsUnique)
        {
            if (column.UniqueIndexName != null)
                builder.Append(" CONSTRAINT ").Append(QuoteIdentifier(column.UniqueIndexName));
            builder.Append(" UNIQUE");
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
                return "NUMBER(5,0)";
            case MigrationColumnType.Byte:
                return "NUMBER(3,0)";
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
            case MigrationColumnType.Currency:
                return "NUMBER(19,4)";
            case MigrationColumnType.Single:
                return "BINARY_FLOAT";
            case MigrationColumnType.Double:
                return "BINARY_DOUBLE";
            case MigrationColumnType.String:
                return column.Length.HasValue
                    ? "VARCHAR2(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "CLOB";
            case MigrationColumnType.AnsiString:
                return column.Length.HasValue
                    ? "VARCHAR2(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "CLOB";
            case MigrationColumnType.FixedString:
                return "NCHAR(" + column.Length!.Value.ToString(CultureInfo.InvariantCulture) + ")";
            case MigrationColumnType.FixedAnsiString:
                return "CHAR(" + column.Length!.Value.ToString(CultureInfo.InvariantCulture) + ")";
            case MigrationColumnType.Text:
                return "CLOB";
            case MigrationColumnType.Date:
                return "DATE";
            case MigrationColumnType.DateTime:
                return "TIMESTAMP";
            case MigrationColumnType.DateTimeOffset:
                return column.DateTimePrecision.HasValue
                    ? "TIMESTAMP(" + column.DateTimePrecision.Value.ToString(CultureInfo.InvariantCulture) + ") WITH TIME ZONE"
                    : "TIMESTAMP WITH TIME ZONE";
            case MigrationColumnType.Time:
                return "TIMESTAMP";
            case MigrationColumnType.Guid:
                return "RAW(16)";
            case MigrationColumnType.Binary:
                return column.Length.HasValue && column.Length.Value <= 2000
                    ? "RAW(" + column.Length.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "BLOB";
            case MigrationColumnType.Xml:
                return "XMLTYPE";
            case MigrationColumnType.Custom:
                return column.CustomType!;
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
            AdvancedMigrationProvider.Oracle,
            QuoteIdentifier,
            Qualify));
        commands.AddRange(AdvancedMigrationSqlGenerator.GenerateReferencedByCommands(
            columns,
            AdvancedMigrationProvider.Oracle,
            QuoteIdentifier,
            Qualify));
        commands.AddRange(AdvancedMigrationSqlGenerator.GenerateDescriptionCommands(
            tableName,
            schemaName,
            tableDescription,
            columns,
            AdvancedMigrationProvider.Oracle,
            QuoteIdentifier,
            Qualify));
        return commands.AsReadOnly();
    }
}
