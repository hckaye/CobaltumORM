using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CobaltumOrm.Migrations;

internal enum AdvancedMigrationProvider
{
    PostgreSql,
    MySql,
    Sqlite,
    SqlServer,
    Oracle,
}

internal static class AdvancedMigrationSqlGenerator
{
    internal static bool TryGenerateConditional(
        MigrationOperation operation,
        AdvancedMigrationProvider provider,
        Func<MigrationOperation, IReadOnlyList<MigrationCommand>> generate,
        out IReadOnlyList<MigrationCommand> commands)
    {
        if (!(operation is ConditionalMigrationOperation conditional))
        {
            commands = Array.Empty<MigrationCommand>();
            return false;
        }

        string canonical;
        string[] aliases;
        switch (provider)
        {
            case AdvancedMigrationProvider.PostgreSql:
                canonical = "PostgreSQL";
                aliases = new[] { "Postgres", "PostgreSql", "Npgsql" };
                break;
            case AdvancedMigrationProvider.MySql:
                canonical = "MySQL";
                aliases = new[] { "MySql" };
                break;
            case AdvancedMigrationProvider.Sqlite:
                canonical = "SQLite";
                aliases = new[] { "Sqlite" };
                break;
            case AdvancedMigrationProvider.SqlServer:
                canonical = "SqlServer";
                aliases = new[] { "SQL Server", "MSSQL" };
                break;
            default:
                canonical = "Oracle";
                aliases = Array.Empty<string>();
                break;
        }

        commands = conditional.Matches(canonical, aliases)
            ? generate(conditional.Operation)
            : Array.Empty<MigrationCommand>();
        return true;
    }

    internal static bool TryGenerate(
        MigrationOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify,
        out IReadOnlyList<MigrationCommand> commands)
    {
        if (operation is CreateSchemaOperation createSchema)
            commands = One(NonShape(CreateSchema(createSchema, provider, quote)));
        else if (operation is DeleteSchemaOperation deleteSchema)
            commands = One(NonShape(DeleteSchema(deleteSchema, provider, quote)));
        else if (operation is MoveTableOperation moveTable)
            commands = One(NonShape(MoveTable(moveTable, provider, quote, qualify)));
        else if (operation is AlterTableDescriptionOperation tableDescription)
            commands = GenerateDescriptionCommands(
                tableDescription.TableName,
                tableDescription.SchemaName,
                tableDescription.Description,
                Array.Empty<ColumnDefinition>(),
                provider,
                quote,
                qualify);
        else if (operation is CreateIndexOperation createIndex)
            commands = One(NonShape(CreateIndex(createIndex, provider, quote, qualify)));
        else if (operation is DeleteIndexOperation deleteIndex)
            commands = One(NonShape(DeleteIndex(deleteIndex, provider, quote, qualify)));
        else if (operation is CreateForeignKeyOperation createForeignKey)
            commands = One(NonShape(CreateForeignKey(createForeignKey.ForeignKey, provider, quote, qualify)));
        else if (operation is DeleteForeignKeyOperation deleteForeignKey)
            commands = One(NonShape(DeleteForeignKey(deleteForeignKey.ForeignKey, provider, quote, qualify)));
        else if (operation is CreateConstraintOperation createConstraint)
            commands = One(NonShape(CreateConstraint(createConstraint, provider, quote, qualify)));
        else if (operation is DeleteConstraintOperation deleteConstraint)
            commands = One(NonShape(DeleteConstraint(deleteConstraint, provider, quote, qualify)));
        else if (operation is CreateSequenceOperation createSequence)
            commands = One(NonShape(CreateSequence(createSequence, provider, qualify)));
        else if (operation is DeleteSequenceOperation deleteSequence)
            commands = One(NonShape(DeleteSequence(deleteSequence, provider, qualify)));
        else if (operation is DeleteDefaultConstraintOperation deleteDefault)
            commands = One(NonShape(DeleteDefault(deleteDefault, provider, quote, qualify)));
        else if (operation is InsertDataOperation insert)
            commands = GenerateInsert(insert, provider, quote, qualify);
        else if (operation is UpdateDataOperation update)
            commands = One(GenerateUpdate(update, provider, quote, qualify));
        else if (operation is DeleteDataOperation deleteData)
            commands = GenerateDelete(deleteData, provider, quote, qualify);
        else if (operation is ExecuteScriptOperation script)
            commands = One(new MigrationCommand(ReadScript(script)));
        else if (operation is ExecuteWithConnectionOperation withConnection)
            commands = One(NonShape("-- Execute.WithConnection: " +
                (string.IsNullOrWhiteSpace(withConnection.Description) ? "custom callback" : withConnection.Description!)));
        else
        {
            commands = Array.Empty<MigrationCommand>();
            return false;
        }

        if (provider == AdvancedMigrationProvider.Oracle && !(operation is ExecuteScriptOperation))
        {
            commands = commands.Select(RemoveOracleTerminator).ToArray();
        }

        return true;
    }

    private static MigrationCommand RemoveOracleTerminator(MigrationCommand command)
    {
        var sql = command.CommandText.TrimEnd();
        if (sql.EndsWith(";", StringComparison.Ordinal)) sql = sql.Substring(0, sql.Length - 1);
        return sql == command.CommandText
            ? command
            : new MigrationCommand(sql, command.Parameters, command.AnalyzeForSchema);
    }

    internal static string GenerateDefaultValue(object? value, AdvancedMigrationProvider provider)
    {
        if (value is RawSql raw) return raw.Sql;
        if (value is SystemMethods method) return GenerateSystemMethod(method, provider);
        if (value is null || value == DBNull.Value) return "NULL";
        if (value is string text) return StringLiteral(text, provider == AdvancedMigrationProvider.SqlServer);
        if (value is char character) return StringLiteral(character.ToString(), provider == AdvancedMigrationProvider.SqlServer);
        if (value is bool boolean)
        {
            return provider == AdvancedMigrationProvider.PostgreSql || provider == AdvancedMigrationProvider.MySql
                ? (boolean ? "TRUE" : "FALSE")
                : (boolean ? "1" : "0");
        }
        if (value is Guid guid) return StringLiteral(guid.ToString("D", CultureInfo.InvariantCulture), false);
        if (value is DateTime dateTime)
            return StringLiteral(dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), false);
        if (value is DateTimeOffset dateTimeOffset)
            return StringLiteral(dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture), false);
        if (value is TimeSpan timeSpan) return StringLiteral(timeSpan.ToString("c", CultureInfo.InvariantCulture), false);
        if (value is byte[] bytes) return BinaryLiteral(bytes, provider);
        if (value.GetType().IsEnum) return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "NULL";
        throw new MigrationValidationException(
            $"Default value type '{value.GetType().FullName}' cannot be represented as a SQL literal. Use RawSql.Insert for a provider-specific expression.");
    }

    internal static string GenerateRule(Rule rule, AdvancedMigrationProvider provider, bool update)
    {
        if (rule == Rule.None) return string.Empty;
        if (provider == AdvancedMigrationProvider.Oracle && update)
            throw new NotSupportedException("Oracle does not support ON UPDATE actions on foreign keys.");

        switch (rule)
        {
            case Rule.Cascade: return "CASCADE";
            case Rule.SetNull: return "SET NULL";
            case Rule.SetDefault:
                if (provider == AdvancedMigrationProvider.Oracle)
                    throw new NotSupportedException("Oracle does not support SET DEFAULT on foreign keys.");
                return "SET DEFAULT";
            default: return "NO ACTION";
        }
    }

    internal static string ConventionalIndexName(string tableName, IEnumerable<string> columns) =>
        "IX_" + tableName + "_" + string.Join("_", columns);

    internal static string ConventionalForeignKeyName(ForeignKeyDefinition foreignKey) =>
        "FK_" + foreignKey.ForeignTableName + "_" + foreignKey.PrimaryTableName + "_" +
        string.Join("_", foreignKey.ForeignColumns);

    internal static string ConventionalConstraintName(
        MigrationConstraintType type,
        string tableName,
        IEnumerable<string> columns) =>
        (type == MigrationConstraintType.PrimaryKey ? "PK_" : "UC_") + tableName +
        (type == MigrationConstraintType.Unique ? "_" + string.Join("_", columns) : string.Empty);

    internal static string GenerateColumnOptions(
        ColumnDefinition column,
        string tableName,
        string? tableSchema,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        var sql = new StringBuilder();
        if (column.CollationName != null)
        {
            sql.Append(" COLLATE ").Append(quote(column.CollationName));
        }

        if (column.ComputedExpression != null)
        {
            if (provider == AdvancedMigrationProvider.SqlServer || provider == AdvancedMigrationProvider.Oracle)
            {
                throw new NotSupportedException(
                    $"Computed column '{column.Name}' requires provider-specific type omission that is not available in this fluent chain. Use Execute.Sql for this provider.");
            }

            sql.Append(" GENERATED ALWAYS AS (").Append(column.ComputedExpression).Append(')');
            if (provider == AdvancedMigrationProvider.PostgreSql || column.IsComputedStored)
                sql.Append(" STORED");
            else
                sql.Append(" VIRTUAL");
        }
        else if (column.HasDefaultValue)
        {
            sql.Append(" DEFAULT ").Append(GenerateDefaultValue(column.DefaultValue, provider));
        }

        if (column.IsUnique)
        {
            if (column.UniqueIndexName != null)
                sql.Append(" CONSTRAINT ").Append(quote(column.UniqueIndexName));
            sql.Append(" UNIQUE");
        }

        if (column.ForeignKey != null)
        {
            var foreignKey = column.ForeignKey;
            if (!string.IsNullOrEmpty(tableName)) foreignKey.ForeignTableName = tableName;
            if (tableSchema != null) foreignKey.ForeignTableSchema = tableSchema;
            ValidateForeignKey(foreignKey);
            if (foreignKey.Name != null) sql.Append(" CONSTRAINT ").Append(quote(foreignKey.Name));
            sql.Append(" REFERENCES ")
                .Append(qualify(foreignKey.PrimaryTableSchema, foreignKey.PrimaryTableName))
                .Append(" (").Append(QuoteList(foreignKey.PrimaryColumns, quote)).Append(')');
            AppendForeignKeyRules(sql, foreignKey, provider);
        }

        return sql.ToString();
    }

    internal static IReadOnlyList<MigrationCommand> GenerateColumnIndexCommands(
        IEnumerable<ColumnDefinition> columns,
        string tableName,
        string? schemaName,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        var commands = new List<MigrationCommand>();
        foreach (var column in columns)
        {
            if (!column.IsIndexed) continue;
            var operation = new CreateIndexOperation(column.IndexName)
            {
                TableName = tableName,
                SchemaName = schemaName,
            };
            operation.AddColumn(column.Name);
            commands.Add(NonShape(CreateIndex(operation, provider, quote, qualify)));
        }
        return commands.AsReadOnly();
    }

    internal static IReadOnlyList<MigrationCommand> GenerateDescriptionCommands(
        string tableName,
        string? schemaName,
        string? tableDescription,
        IEnumerable<ColumnDefinition> columns,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        var commands = new List<MigrationCommand>();
        if (provider == AdvancedMigrationProvider.Sqlite)
        {
            if (tableDescription != null || columns.Any(column =>
                column.Description != null || column.AdditionalDescriptions.Count != 0))
                throw new NotSupportedException("SQLite does not store table or column descriptions.");
            return commands.AsReadOnly();
        }
        var table = qualify(schemaName, tableName);
        if (tableDescription != null)
        {
            if (provider == AdvancedMigrationProvider.PostgreSql || provider == AdvancedMigrationProvider.Oracle)
                commands.Add(NonShape("COMMENT ON TABLE " + table + " IS " + StringLiteral(tableDescription, false) + ";"));
            else if (provider == AdvancedMigrationProvider.MySql)
                commands.Add(NonShape("ALTER TABLE " + table + " COMMENT = " + StringLiteral(tableDescription, false) + ";"));
            else
                commands.Add(SqlServerDescription(
                    schemaName ?? "dbo", tableName, null, "MS_Description", tableDescription));
        }

        if (provider == AdvancedMigrationProvider.MySql) return commands.AsReadOnly();
        foreach (var column in columns)
        {
            if (column.Description is null) continue;
            if (provider == AdvancedMigrationProvider.PostgreSql || provider == AdvancedMigrationProvider.Oracle)
            {
                commands.Add(NonShape(
                    "COMMENT ON COLUMN " + table + "." + quote(column.Name) + " IS " +
                    StringLiteral(CombinedDescription(column), false) + ";"));
            }
            else
            {
                commands.Add(SqlServerDescription(
                    schemaName ?? "dbo", tableName, column.Name, "MS_Description", column.Description));
                foreach (var description in column.AdditionalDescriptions)
                {
                    commands.Add(SqlServerDescription(
                        schemaName ?? "dbo", tableName, column.Name, description.Key, description.Value));
                }
            }
        }
        return commands.AsReadOnly();
    }

    internal static IReadOnlyList<MigrationCommand> GenerateReferencedByCommands(
        IEnumerable<ColumnDefinition> columns,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        var commands = new List<MigrationCommand>();
        foreach (var foreignKey in columns.SelectMany(column => column.ReferencedByForeignKeys))
        {
            commands.Add(NonShape(CreateForeignKey(foreignKey, provider, quote, qualify)));
        }
        return commands.AsReadOnly();
    }

    internal static IReadOnlyList<MigrationCommand> GenerateAlterColumnAuxiliaryCommands(
        ColumnDefinition column,
        string tableName,
        string? schemaName,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        var commands = new List<MigrationCommand>();
        if (column.IsIndexed || column.IsUnique)
        {
            var index = new CreateIndexOperation(column.IsUnique ? column.UniqueIndexName : column.IndexName)
            {
                TableName = tableName,
                SchemaName = schemaName,
                IsUnique = column.IsUnique,
            };
            index.AddColumn(column.Name);
            commands.Add(NonShape(CreateIndex(index, provider, quote, qualify)));
        }
        if (column.ForeignKey != null)
        {
            column.ForeignKey.ForeignTableName = tableName;
            column.ForeignKey.ForeignTableSchema = schemaName;
            commands.Add(NonShape(CreateForeignKey(column.ForeignKey, provider, quote, qualify)));
        }
        commands.AddRange(GenerateReferencedByCommands(new[] { column }, provider, quote, qualify));
        commands.AddRange(GenerateDescriptionCommands(
            tableName, schemaName, null, new[] { column }, provider, quote, qualify));
        return commands.AsReadOnly();
    }

    private static MigrationCommand SqlServerDescription(
        string schemaName,
        string tableName,
        string? columnName,
        string descriptionName,
        string description)
    {
        var levels = "@level0type=N'SCHEMA', @level0name=@schema, " +
            "@level1type=N'TABLE', @level1name=@table";
        var minorId = "0";
        var parameters = new List<MigrationCommandParameter>
        {
            new MigrationCommandParameter("description_name", descriptionName),
            new MigrationCommandParameter("description", description),
            new MigrationCommandParameter("schema", schemaName),
            new MigrationCommandParameter("table", tableName),
        };
        if (columnName != null)
        {
            levels += ", @level2type=N'COLUMN', @level2name=@column";
            minorId = "COLUMNPROPERTY(OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@table)), @column, 'ColumnId')";
            parameters.Add(new MigrationCommandParameter("column", columnName));
        }
        var sql =
            "IF EXISTS (SELECT 1 FROM sys.extended_properties AS ep " +
            "INNER JOIN sys.tables AS t ON t.object_id = ep.major_id " +
            "INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
            "WHERE s.name = @schema AND t.name = @table AND ep.name = @description_name " +
            "AND ep.minor_id = " + minorId + ")\n" +
            "    EXEC sys.sp_updateextendedproperty @name=@description_name, @value=@description, " + levels + ";\n" +
            "ELSE\n" +
            "    EXEC sys.sp_addextendedproperty @name=@description_name, @value=@description, " + levels + ";";
        return new MigrationCommand(sql, parameters, false);
    }

    internal static string CombinedDescription(ColumnDefinition column)
    {
        if (column.Description is null) return string.Empty;
        if (column.AdditionalDescriptions.Count == 0) return column.Description;
        return string.Join(
            Environment.NewLine,
            new[] { "Description:" + column.Description }.Concat(
                column.AdditionalDescriptions.Select(item => item.Key + ":" + item.Value)));
    }

    private static string CreateSchema(CreateSchemaOperation operation, AdvancedMigrationProvider provider, Func<string, string> quote)
    {
        RejectSchemaDdl(provider);
        return "CREATE SCHEMA " + quote(operation.SchemaName) + ";";
    }

    private static string DeleteSchema(DeleteSchemaOperation operation, AdvancedMigrationProvider provider, Func<string, string> quote)
    {
        RejectSchemaDdl(provider);
        return "DROP SCHEMA " + quote(operation.SchemaName) + ";";
    }

    private static void RejectSchemaDdl(AdvancedMigrationProvider provider)
    {
        if (provider == AdvancedMigrationProvider.Sqlite)
            throw new NotSupportedException("SQLite does not support named schemas.");
        if (provider == AdvancedMigrationProvider.Oracle)
            throw new NotSupportedException("Oracle users own schemas; CREATE SCHEMA and DROP SCHEMA are not portable migration operations.");
    }

    private static string MoveTable(
        MoveTableOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        switch (provider)
        {
            case AdvancedMigrationProvider.PostgreSql:
                return "ALTER TABLE " + qualify(operation.OldSchemaName, operation.TableName) +
                    " SET SCHEMA " + quote(operation.NewSchemaName) + ";";
            case AdvancedMigrationProvider.MySql:
                return "RENAME TABLE " + qualify(operation.OldSchemaName, operation.TableName) +
                    " TO " + qualify(operation.NewSchemaName, operation.TableName) + ";";
            case AdvancedMigrationProvider.SqlServer:
                return "ALTER SCHEMA " + quote(operation.NewSchemaName) + " TRANSFER " +
                    qualify(operation.OldSchemaName, operation.TableName) + ";";
            case AdvancedMigrationProvider.Sqlite:
                throw new NotSupportedException("SQLite does not support moving tables between schemas.");
            default:
                throw new NotSupportedException("Oracle cannot move a table to another user's schema with ALTER TABLE.");
        }
    }

    private static string CreateIndex(
        CreateIndexOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        RequireTableAndColumns(operation.TableName, operation.Columns.Select(column => column.Name), "Create.Index");
        if (operation.IsClustered == true && provider != AdvancedMigrationProvider.SqlServer)
            throw new NotSupportedException("Clustered indexes are supported only by the SQL Server adapter.");

        var name = operation.IndexName ?? ConventionalIndexName(operation.TableName, operation.Columns.Select(column => column.Name));
        var columns = string.Join(", ", operation.Columns.Select(column =>
            quote(column.Name) + (column.IsDescending ? " DESC" : " ASC")));
        var clustered = provider == AdvancedMigrationProvider.SqlServer && operation.IsClustered.HasValue
            ? (operation.IsClustered.Value ? " CLUSTERED" : " NONCLUSTERED")
            : string.Empty;
        return "CREATE" + (operation.IsUnique ? " UNIQUE" : string.Empty) + clustered +
            " INDEX " + quote(name) + " ON " + qualify(operation.SchemaName, operation.TableName) +
            " (" + columns + ");";
    }

    private static string DeleteIndex(
        DeleteIndexOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (string.IsNullOrEmpty(operation.TableName))
            throw new MigrationValidationException("Delete.Index must call OnTable.");
        var name = operation.IndexName ?? ConventionalIndexName(operation.TableName, operation.Columns);
        if (provider == AdvancedMigrationProvider.PostgreSql ||
            provider == AdvancedMigrationProvider.Sqlite ||
            provider == AdvancedMigrationProvider.Oracle)
        {
            return "DROP INDEX " + qualify(operation.SchemaName, name) + ";";
        }

        return "DROP INDEX " + quote(name) + " ON " + qualify(operation.SchemaName, operation.TableName) + ";";
    }

    private static string CreateForeignKey(
        ForeignKeyDefinition foreignKey,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (provider == AdvancedMigrationProvider.Sqlite)
            throw new NotSupportedException("SQLite cannot add a foreign key to an existing table without rebuilding it.");
        ValidateForeignKey(foreignKey);
        var name = foreignKey.Name ?? ConventionalForeignKeyName(foreignKey);
        var sql = new StringBuilder()
            .Append("ALTER TABLE ").Append(qualify(foreignKey.ForeignTableSchema, foreignKey.ForeignTableName))
            .Append(" ADD CONSTRAINT ").Append(quote(name))
            .Append(" FOREIGN KEY (").Append(QuoteList(foreignKey.ForeignColumns, quote)).Append(')')
            .Append(" REFERENCES ").Append(qualify(foreignKey.PrimaryTableSchema, foreignKey.PrimaryTableName))
            .Append(" (").Append(QuoteList(foreignKey.PrimaryColumns, quote)).Append(')');
        AppendForeignKeyRules(sql, foreignKey, provider);
        return FixRuleTerminator(sql.ToString());
    }

    private static string DeleteForeignKey(
        ForeignKeyDefinition foreignKey,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (provider == AdvancedMigrationProvider.Sqlite)
            throw new NotSupportedException("SQLite cannot drop a foreign key without rebuilding its table.");
        if (string.IsNullOrEmpty(foreignKey.ForeignTableName))
            throw new MigrationValidationException("Delete.ForeignKey must select a table.");
        var name = foreignKey.Name ?? ConventionalForeignKeyName(foreignKey);
        return "ALTER TABLE " + qualify(foreignKey.ForeignTableSchema, foreignKey.ForeignTableName) +
            (provider == AdvancedMigrationProvider.MySql ? " DROP FOREIGN KEY " : " DROP CONSTRAINT ") +
            quote(name) + ";";
    }

    private static string CreateConstraint(
        CreateConstraintOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (provider == AdvancedMigrationProvider.Sqlite)
            throw new NotSupportedException("SQLite cannot add a table constraint without rebuilding the table.");
        RequireTableAndColumns(operation.TableName, operation.Columns, "Create constraint");
        var name = operation.ConstraintName ?? ConventionalConstraintName(operation.ConstraintType, operation.TableName, operation.Columns);
        var kind = operation.ConstraintType == MigrationConstraintType.PrimaryKey ? "PRIMARY KEY" : "UNIQUE";
        return "ALTER TABLE " + qualify(operation.SchemaName, operation.TableName) + " ADD CONSTRAINT " +
            quote(name) + " " + kind + " (" + QuoteList(operation.Columns, quote) + ");";
    }

    private static string DeleteConstraint(
        DeleteConstraintOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (provider == AdvancedMigrationProvider.Sqlite)
            throw new NotSupportedException("SQLite cannot drop a table constraint without rebuilding the table.");
        if (string.IsNullOrEmpty(operation.TableName))
            throw new MigrationValidationException("Delete constraint must select a table.");
        var name = operation.ConstraintName ?? ConventionalConstraintName(operation.ConstraintType, operation.TableName, operation.Columns);
        var table = qualify(operation.SchemaName, operation.TableName);
        if (provider == AdvancedMigrationProvider.MySql)
        {
            return operation.ConstraintType == MigrationConstraintType.PrimaryKey
                ? "ALTER TABLE " + table + " DROP PRIMARY KEY;"
                : "ALTER TABLE " + table + " DROP INDEX " + quote(name) + ";";
        }
        return "ALTER TABLE " + table + " DROP CONSTRAINT " + quote(name) + ";";
    }

    private static string CreateSequence(
        CreateSequenceOperation operation,
        AdvancedMigrationProvider provider,
        Func<string?, string, string> qualify)
    {
        RejectSequence(provider);
        var sql = new StringBuilder("CREATE SEQUENCE ").Append(qualify(operation.SchemaName, operation.SequenceName));
        if (operation.StartValue.HasValue) sql.Append(" START WITH ").Append(Invariant(operation.StartValue.Value));
        if (operation.Increment.HasValue) sql.Append(" INCREMENT BY ").Append(Invariant(operation.Increment.Value));
        if (operation.MinimumValue.HasValue) sql.Append(" MINVALUE ").Append(Invariant(operation.MinimumValue.Value));
        if (operation.MaximumValue.HasValue) sql.Append(" MAXVALUE ").Append(Invariant(operation.MaximumValue.Value));
        if (operation.CacheSize.HasValue) sql.Append(" CACHE ").Append(Invariant(operation.CacheSize.Value));
        if (operation.IsCyclic) sql.Append(" CYCLE");
        return sql.Append(';').ToString();
    }

    private static string DeleteSequence(
        DeleteSequenceOperation operation,
        AdvancedMigrationProvider provider,
        Func<string?, string, string> qualify)
    {
        RejectSequence(provider);
        return "DROP SEQUENCE " + qualify(operation.SchemaName, operation.SequenceName) + ";";
    }

    private static void RejectSequence(AdvancedMigrationProvider provider)
    {
        if (provider == AdvancedMigrationProvider.MySql)
            throw new NotSupportedException("MySQL does not support standalone sequences.");
        if (provider == AdvancedMigrationProvider.Sqlite)
            throw new NotSupportedException("SQLite does not support standalone sequences.");
    }

    private static string DeleteDefault(
        DeleteDefaultConstraintOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        var table = qualify(operation.SchemaName, operation.TableName);
        var column = quote(operation.ColumnName);
        if (provider == AdvancedMigrationProvider.Sqlite)
            throw new NotSupportedException("SQLite cannot drop a column default without rebuilding its table.");
        if (provider == AdvancedMigrationProvider.Oracle)
            return "ALTER TABLE " + table + " MODIFY (" + column + " DEFAULT NULL);";
        if (provider != AdvancedMigrationProvider.SqlServer)
            return "ALTER TABLE " + table + " ALTER COLUMN " + column + " DROP DEFAULT;";

        var schema = operation.SchemaName ?? "dbo";
        return "DECLARE @constraint sysname; " +
            "SELECT @constraint = dc.name FROM sys.default_constraints dc " +
            "JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
            "WHERE dc.parent_object_id = OBJECT_ID(" + StringLiteral(schema + "." + operation.TableName, true) + ") " +
            "AND c.name = " + StringLiteral(operation.ColumnName, true) + "; " +
            "IF @constraint IS NOT NULL EXEC(N'ALTER TABLE " + table.Replace("'", "''") +
            " DROP CONSTRAINT ' + QUOTENAME(@constraint));";
    }

    private static IReadOnlyList<MigrationCommand> GenerateInsert(
        InsertDataOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (operation.Rows.Count == 0)
            throw new MigrationValidationException("Insert.IntoTable must add at least one Row or Rows value.");
        var commands = new List<MigrationCommand>();
        foreach (var row in operation.Rows)
        {
            var parameters = new List<MigrationCommandParameter>();
            var valueSql = row.Values.Select(value => ValueSql(value.Value, provider, parameters)).ToArray();
            commands.Add(new MigrationCommand(
                "INSERT INTO " + qualify(operation.SchemaName, operation.TableName) + " (" +
                QuoteList(row.Values.Select(value => value.Key), quote) + ") VALUES (" +
                string.Join(", ", valueSql) + ");",
                parameters,
                false));
        }
        return commands.AsReadOnly();
    }

    private static MigrationCommand GenerateUpdate(
        UpdateDataOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (operation.Values is null) throw new MigrationValidationException("Update.Table must call Set.");
        if (!operation.AllRows && operation.Criteria is null)
            throw new MigrationValidationException("Update.Table must call Where or AllRows.");
        var parameters = new List<MigrationCommandParameter>();
        var set = string.Join(", ", operation.Values.Values.Select(value =>
            quote(value.Key) + " = " + ValueSql(value.Value, provider, parameters)));
        var where = operation.AllRows ? string.Empty : " WHERE " + CriteriaSql(operation.Criteria!, provider, quote, parameters);
        return new MigrationCommand(
            "UPDATE " + qualify(operation.SchemaName, operation.TableName) + " SET " + set + where + ";",
            parameters,
            false);
    }

    private static IReadOnlyList<MigrationCommand> GenerateDelete(
        DeleteDataOperation operation,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        Func<string?, string, string> qualify)
    {
        if (operation.AllRows)
            return One(new MigrationCommand(
                "DELETE FROM " + qualify(operation.SchemaName, operation.TableName) + ";",
                Array.Empty<MigrationCommandParameter>(),
                false));
        if (operation.Criteria.Count == 0)
            throw new MigrationValidationException("Delete.FromTable must call Row, Where, IsNull, or AllRows.");
        var commands = new List<MigrationCommand>();
        foreach (var criteria in operation.Criteria)
        {
            var parameters = new List<MigrationCommandParameter>();
            commands.Add(new MigrationCommand(
                "DELETE FROM " + qualify(operation.SchemaName, operation.TableName) + " WHERE " +
                CriteriaSql(criteria, provider, quote, parameters) + ";",
                parameters,
                false));
        }
        return commands.AsReadOnly();
    }

    private static string CriteriaSql(
        MigrationDataRow criteria,
        AdvancedMigrationProvider provider,
        Func<string, string> quote,
        List<MigrationCommandParameter> parameters) =>
        string.Join(" AND ", criteria.Values.Select(value =>
        {
            if (value.Value is null || value.Value == DBNull.Value) return quote(value.Key) + " IS NULL";
            if (value.Value is RawSql raw) return quote(value.Key) + " = " + raw.Sql;
            return quote(value.Key) + " = " + ValueSql(value.Value, provider, parameters);
        }));

    private static string ValueSql(
        object? value,
        AdvancedMigrationProvider provider,
        List<MigrationCommandParameter> parameters)
    {
        if (value is RawSql || value is SystemMethods) return GenerateDefaultValue(value, provider);
        var name = "p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters.Add(new MigrationCommandParameter(name, value));
        return (provider == AdvancedMigrationProvider.Oracle ? ":" : "@") + name;
    }

    private static string ReadScript(ExecuteScriptOperation operation)
    {
        string sql;
        if (operation.IsEmbedded)
        {
            var assembly = operation.MigrationType.Assembly;
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name =>
                string.Equals(name, operation.ScriptName, StringComparison.Ordinal) ||
                name.EndsWith("." + operation.ScriptName, StringComparison.Ordinal));
            if (resourceName is null)
                throw new FileNotFoundException($"Embedded SQL script '{operation.ScriptName}' was not found in '{assembly.FullName}'.");
            using (var stream = assembly.GetManifestResourceStream(resourceName) ??
                throw new FileNotFoundException($"Embedded SQL script '{resourceName}' could not be opened."))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                sql = reader.ReadToEnd();
            }
        }
        else
        {
            sql = File.ReadAllText(operation.ScriptName);
        }

        foreach (var parameter in operation.Parameters)
        {
            sql = sql.Replace(
                "$(" + parameter.Key + ")",
                Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
        return ExpressionValidation.Sql(sql);
    }

    private static string GenerateSystemMethod(SystemMethods method, AdvancedMigrationProvider provider)
    {
        switch (method)
        {
            case SystemMethods.NewGuid:
                if (provider == AdvancedMigrationProvider.PostgreSql) return "gen_random_uuid()";
                if (provider == AdvancedMigrationProvider.MySql) return "UUID()";
                if (provider == AdvancedMigrationProvider.SqlServer) return "NEWID()";
                if (provider == AdvancedMigrationProvider.Oracle) return "SYS_GUID()";
                return "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-' || substr('89ab',abs(random()) % 4 + 1,1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6)))";
            case SystemMethods.NewSequentialId:
                if (provider == AdvancedMigrationProvider.SqlServer) return "NEWSEQUENTIALID()";
                if (provider == AdvancedMigrationProvider.PostgreSql) return "uuid_generate_v1()";
                if (provider == AdvancedMigrationProvider.MySql) return "UUID()";
                if (provider == AdvancedMigrationProvider.Oracle) return "SYS_GUID()";
                throw new NotSupportedException("SQLite does not provide a sequential GUID function.");
            case SystemMethods.CurrentDateTime:
                if (provider == AdvancedMigrationProvider.SqlServer) return "GETDATE()";
                if (provider == AdvancedMigrationProvider.Oracle) return "LOCALTIMESTAMP";
                return "CURRENT_TIMESTAMP";
            case SystemMethods.CurrentDateTimeOffset:
                if (provider == AdvancedMigrationProvider.SqlServer) return "SYSDATETIMEOFFSET()";
                if (provider == AdvancedMigrationProvider.Oracle) return "SYSTIMESTAMP";
                return "CURRENT_TIMESTAMP";
            case SystemMethods.CurrentUTCDateTime:
                if (provider == AdvancedMigrationProvider.PostgreSql) return "CURRENT_TIMESTAMP AT TIME ZONE 'UTC'";
                if (provider == AdvancedMigrationProvider.MySql) return "UTC_TIMESTAMP()";
                if (provider == AdvancedMigrationProvider.SqlServer) return "SYSUTCDATETIME()";
                if (provider == AdvancedMigrationProvider.Oracle) return "SYS_EXTRACT_UTC(SYSTIMESTAMP)";
                return "CURRENT_TIMESTAMP";
            case SystemMethods.CurrentUser:
                if (provider == AdvancedMigrationProvider.Sqlite)
                    throw new NotSupportedException("SQLite does not expose a current database user.");
                return provider == AdvancedMigrationProvider.Oracle ? "USER" : "CURRENT_USER";
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }
    }

    private static void ValidateForeignKey(ForeignKeyDefinition foreignKey)
    {
        RequireTableAndColumns(foreignKey.ForeignTableName, foreignKey.ForeignColumns, "Foreign key");
        RequireTableAndColumns(foreignKey.PrimaryTableName, foreignKey.PrimaryColumns, "Foreign key reference");
        if (foreignKey.ForeignColumns.Count != foreignKey.PrimaryColumns.Count)
            throw new MigrationValidationException("A foreign key must have the same number of foreign and referenced columns.");
    }

    private static void RequireTableAndColumns(string tableName, IEnumerable<string> columns, string operation)
    {
        if (string.IsNullOrEmpty(tableName)) throw new MigrationValidationException(operation + " must select a table.");
        if (!columns.Any()) throw new MigrationValidationException(operation + " must select at least one column.");
    }

    private static void AppendForeignKeyRules(StringBuilder builder, ForeignKeyDefinition foreignKey, AdvancedMigrationProvider provider)
    {
        var deleteRule = GenerateRule(foreignKey.OnDelete, provider, false);
        var updateRule = GenerateRule(foreignKey.OnUpdate, provider, true);
        if (deleteRule.Length > 0) builder.Append(" ON DELETE ").Append(deleteRule);
        if (updateRule.Length > 0) builder.Append(" ON UPDATE ").Append(updateRule);
    }

    private static string FixRuleTerminator(string sql)
    {
        var semicolon = sql.IndexOf(';');
        return semicolon < 0 ? sql + ";" : sql.Remove(semicolon, 1) + ";";
    }

    private static string QuoteList(IEnumerable<string> names, Func<string, string> quote) =>
        string.Join(", ", names.Select(quote));

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string StringLiteral(string value, bool unicode) =>
        (unicode ? "N" : string.Empty) + "'" + value.Replace("'", "''") + "'";

    private static string BinaryLiteral(byte[] value, AdvancedMigrationProvider provider)
    {
        var hex = BitConverter.ToString(value).Replace("-", string.Empty);
        if (provider == AdvancedMigrationProvider.PostgreSql) return "decode('" + hex + "', 'hex')";
        if (provider == AdvancedMigrationProvider.Oracle) return "HEXTORAW('" + hex + "')";
        if (provider == AdvancedMigrationProvider.Sqlite) return "X'" + hex + "'";
        return "0x" + hex;
    }

    private static MigrationCommand NonShape(string sql) =>
        new MigrationCommand(sql, Array.Empty<MigrationCommandParameter>(), false);

    private static IReadOnlyList<MigrationCommand> One(MigrationCommand command) => new[] { command };
}
