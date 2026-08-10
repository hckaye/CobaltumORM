using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

/// <summary>Analyzes SQLite table-shape changes without executing SQL.</summary>
public static class SqliteMigrationAnalyzer
{
    /// <summary>Applies supported SQLite DDL to a copy of the supplied schema.</summary>
    public static MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql)
    {
        var diagnostics = new List<Diagnostic>();
        if (schema is null)
        {
            diagnostics.Add(new Diagnostic(
                "DDL000",
                "A database schema is required.",
                new SourceSpan(0, 0)));
            return new MigrationAnalysisResult(
                new DatabaseSchema(Array.Empty<Table>()),
                diagnostics);
        }

        if (sql is null)
        {
            diagnostics.Add(new Diagnostic(
                "DDL000",
                "Migration SQL text is required.",
                new SourceSpan(0, 0)));
            return new MigrationAnalysisResult(
                new DatabaseSchema(schema.Tables),
                diagnostics);
        }

        try
        {
            var tokens = new SqliteDdlLexer(sql, diagnostics).Lex();
            var statements = new SqliteDdlParser(tokens, sql, diagnostics).Parse();
            var tables = new List<Table>(schema.Tables);
            var applier = new SqliteMigrationApplier(diagnostics);
            foreach (var statement in statements)
            {
                applier.Apply(statement, tables);
            }

            return new MigrationAnalysisResult(new DatabaseSchema(tables), diagnostics);
        }
        catch (Exception)
        {
            diagnostics.Add(new Diagnostic(
                "DDL999",
                "The SQLite migration could not be analyzed because of an internal analysis error.",
                new SourceSpan(0, sql.Length)));
            return new MigrationAnalysisResult(new DatabaseSchema(schema.Tables), diagnostics);
        }
    }
}

/// <summary>Adapts the SQLite migration analyzer to the dialect service contract.</summary>
public sealed class SqliteSchemaMigrationAnalyzer : ISchemaMigrationAnalyzer
{
    /// <inheritdoc />
    public MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        SqliteMigrationAnalyzer.Analyze(schema, sql);
}

internal sealed class SqliteMigrationApplier
{
    private readonly List<Diagnostic> _diagnostics;

    internal SqliteMigrationApplier(List<Diagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
    }

    internal void Apply(SqliteDdlStatement statement, List<Table> tables)
    {
        var create = statement as SqliteCreateTableStatement;
        if (create != null)
        {
            SqliteApplyCreate(create, tables);
            return;
        }

        var drop = statement as SqliteDropTableStatement;
        if (drop != null)
        {
            SqliteApplyDrop(drop, tables);
            return;
        }

        var alter = statement as SqliteAlterTableStatement;
        if (alter != null)
        {
            SqliteApplyAlter(alter, tables);
        }
    }

    private void SqliteApplyCreate(SqliteCreateTableStatement statement, List<Table> tables)
    {
        if (statement.Columns.Count == 0)
        {
            SqliteReport("DDL208", "CREATE TABLE must declare at least one column.", statement.Span);
            return;
        }

        if (SqliteFindTableIndex(tables, statement.Table.Name) >= 0)
        {
            if (!statement.IfNotExists)
            {
                SqliteReport(
                    "DDL200",
                    "Table '" + statement.Table.Name.Name + "' already exists.",
                    statement.Table.Name.Span);
            }

            return;
        }

        var columns = new List<Column>();
        var valid = true;
        foreach (var definition in statement.Columns)
        {
            if (SqliteFindColumnIndex(columns, definition.Name) >= 0)
            {
                SqliteReport(
                    "DDL203",
                    "Column '" + definition.Name.Name + "' is defined more than once.",
                    definition.Name.Span);
                valid = false;
                continue;
            }

            if (definition.IsIdentity &&
                (!definition.IsPrimaryKey ||
                 !string.Equals(definition.SqlType, "INTEGER", StringComparison.OrdinalIgnoreCase)))
            {
                SqliteReport(
                    "DDL101",
                    "SQLite AUTOINCREMENT requires an INTEGER PRIMARY KEY column.",
                    definition.Span);
                valid = false;
            }

            columns.Add(new Column(
                definition.Name.Name,
                definition.SqlType,
                definition.IsNullable,
                definition.IsPrimaryKey,
                definition.DefaultExpression,
                definition.IsIdentity));
        }

        var primaryKeyCount = statement.PrimaryKeys.Count +
            statement.Columns.Count(item => item.IsPrimaryKey);
        if (primaryKeyCount > 1)
        {
            SqliteReport(
                "DDL206",
                "A SQLite table can have only one PRIMARY KEY constraint.",
                statement.Span);
            valid = false;
        }

        foreach (var primaryKey in statement.PrimaryKeys)
        {
            if (primaryKey.Count == 0)
            {
                SqliteReport(
                    "DDL204",
                    "A PRIMARY KEY must contain at least one column.",
                    statement.Span);
                valid = false;
                continue;
            }

            var primaryKeyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var identifier in primaryKey)
            {
                var columnIndex = SqliteFindColumnIndex(columns, identifier);
                if (columnIndex < 0)
                {
                    SqliteReport(
                        "DDL204",
                        "PRIMARY KEY refers to unknown column '" + identifier.Name + "'.",
                        identifier.Span);
                    valid = false;
                    continue;
                }

                if (!primaryKeyNames.Add(identifier.Name))
                {
                    SqliteReport(
                        "DDL206",
                        "A PRIMARY KEY cannot list the same column more than once.",
                        identifier.Span);
                    valid = false;
                    continue;
                }

                var column = columns[columnIndex];
                columns[columnIndex] = new Column(
                    column.Name,
                    column.SqlType,
                    false,
                    true,
                    column.DefaultExpression,
                    column.IsIdentity);
            }
        }

        if (!valid)
        {
            return;
        }

        tables.Add(new Table(statement.Table.Name.Name, columns));
    }

    private void SqliteApplyDrop(SqliteDropTableStatement statement, List<Table> tables)
    {
        var index = SqliteFindTableIndex(tables, statement.Table.Name);
        if (index < 0)
        {
            if (!statement.IfExists)
            {
                SqliteReport(
                    "DDL201",
                    "Cannot drop unknown SQLite table '" + statement.Table.Name.Name + "'.",
                    statement.Table.Name.Span);
            }

            return;
        }

        tables.RemoveAt(index);
    }

    private void SqliteApplyAlter(SqliteAlterTableStatement statement, List<Table> tables)
    {
        var tableIndex = SqliteFindTableIndex(tables, statement.Table.Name);
        if (tableIndex < 0)
        {
            if (!statement.IfExists)
            {
                SqliteReport(
                    "DDL202",
                    "Cannot alter unknown SQLite table '" + statement.Table.Name.Name + "'.",
                    statement.Table.Name.Span);
            }

            return;
        }

        if (statement.Action == null)
        {
            return;
        }

        var add = statement.Action as SqliteAddColumnAction;
        if (add != null)
        {
            SqliteApplyAdd(tableIndex, tables[tableIndex], add, tables);
            return;
        }

        var drop = statement.Action as SqliteDropColumnAction;
        if (drop != null)
        {
            SqliteApplyDropColumn(tableIndex, tables[tableIndex], drop, tables);
            return;
        }

        var renameColumn = statement.Action as SqliteRenameColumnAction;
        if (renameColumn != null)
        {
            SqliteApplyRenameColumn(tableIndex, tables[tableIndex], renameColumn, tables);
            return;
        }

        var renameTable = statement.Action as SqliteRenameTableAction;
        if (renameTable != null)
        {
            SqliteApplyRenameTable(tableIndex, tables[tableIndex], renameTable, tables);
        }
    }

    private void SqliteApplyAdd(
        int tableIndex,
        Table table,
        SqliteAddColumnAction action,
        List<Table> tables)
    {
        if (SqliteFindColumnIndex(table.Columns, action.Column.Name) >= 0)
        {
            if (!action.IfNotExists)
            {
                SqliteReport(
                    "DDL203",
                    "Column '" + action.Column.Name.Name + "' already exists on table '" +
                    table.Name + "'.",
                    action.Column.Name.Span);
            }

            return;
        }

        var columns = table.Columns.ToList();
        columns.Add(new Column(
            action.Column.Name.Name,
            action.Column.SqlType,
            action.Column.IsNullable,
            action.Column.IsPrimaryKey,
            action.Column.DefaultExpression,
            action.Column.IsIdentity));
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqliteApplyDropColumn(
        int tableIndex,
        Table table,
        SqliteDropColumnAction action,
        List<Table> tables)
    {
        var columnIndex = SqliteFindColumnIndex(table.Columns, action.Column);
        if (columnIndex < 0)
        {
            if (!action.IfExists)
            {
                SqliteReport(
                    "DDL204",
                    "Cannot drop unknown SQLite column '" + action.Column.Name + "' from table '" +
                    table.Name + "'.",
                    action.Column.Span);
            }

            return;
        }

        if (table.Columns.Count == 1)
        {
            SqliteReport(
                "DDL101",
                "SQLite cannot drop the only column from a table.",
                action.Column.Span);
            return;
        }

        var columns = table.Columns.Where((column, index) => index != columnIndex).ToList();
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqliteApplyRenameColumn(
        int tableIndex,
        Table table,
        SqliteRenameColumnAction action,
        List<Table> tables)
    {
        var oldIndex = SqliteFindColumnIndex(table.Columns, action.OldName);
        if (oldIndex < 0)
        {
            SqliteReport(
                "DDL204",
                "Cannot rename unknown SQLite column '" + action.OldName.Name + "' on table '" +
                table.Name + "'.",
                action.OldName.Span);
            return;
        }

        var newIndex = SqliteFindColumnIndex(table.Columns, action.NewName);
        if (newIndex >= 0 && newIndex != oldIndex)
        {
            SqliteReport(
                "DDL203",
                "Column '" + action.NewName.Name + "' already exists on table '" + table.Name + "'.",
                action.NewName.Span);
            return;
        }

        var columns = table.Columns.ToList();
        var old = columns[oldIndex];
        columns[oldIndex] = new Column(
            action.NewName.Name,
            old.SqlType,
            old.IsNullable,
            old.IsPrimaryKey,
            old.DefaultExpression,
            old.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqliteApplyRenameTable(
        int tableIndex,
        Table table,
        SqliteRenameTableAction action,
        List<Table> tables)
    {
        var otherIndex = SqliteFindTableIndex(tables, action.NewName);
        if (otherIndex >= 0 && otherIndex != tableIndex)
        {
            SqliteReport(
                "DDL200",
                "Table '" + action.NewName.Name + "' already exists.",
                action.NewName.Span);
            return;
        }

        tables[tableIndex] = new Table(action.NewName.Name,
            table.Columns,
            table.Schema);
    }

    private static int SqliteFindTableIndex(
        IReadOnlyList<Table> tables,
        SqliteDdlIdentifier identifier) =>
        tables
            .Select((table, index) => new { table, index })
            .Where(item => string.IsNullOrEmpty(item.table.Schema))
            .Where(item => string.Equals(item.table.Name, identifier.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

    private static int SqliteFindColumnIndex(
        IReadOnlyList<Column> columns,
        SqliteDdlIdentifier identifier) =>
        columns
            .Select((column, index) => new { column, index })
            .Where(item => string.Equals(item.column.Name, identifier.Name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

    private static int SqliteFindColumnIndex(
        IReadOnlyList<Column> columns,
        string identifier) =>
        columns
            .Select((column, index) => new { column, index })
            .Where(item => string.Equals(item.column.Name, identifier, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

    private void SqliteReport(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));
}
