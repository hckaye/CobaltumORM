using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

public static class PostgreSqlMigrationAnalyzer
{
    public static MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql)
    {
        var diagnostics = new List<Diagnostic>();
        if (schema is null)
        {
            diagnostics.Add(new Diagnostic("DDL000", "A database schema is required.", new SourceSpan(0, 0)));
            return new MigrationAnalysisResult(new DatabaseSchema(Array.Empty<Table>()), diagnostics);
        }

        if (sql is null)
        {
            diagnostics.Add(new Diagnostic("DDL000", "Migration SQL text is required.", new SourceSpan(0, 0)));
            return new MigrationAnalysisResult(new DatabaseSchema(schema.Tables), diagnostics);
        }

        try
        {
            var tokens = new PostgreSqlDdlLexer(sql, diagnostics).Lex();
            var statements = new PostgreSqlDdlParser(tokens, sql, diagnostics).Parse();
            var tables = new List<Table>(schema.Tables);
            var applier = new PostgreSqlMigrationApplier(diagnostics);
            foreach (var statement in statements)
            {
                if (statement.IsValid)
                {
                    applier.Apply(statement, tables);
                }
            }

            return new MigrationAnalysisResult(new DatabaseSchema(tables), diagnostics);
        }
        catch (Exception)
        {
            diagnostics.Add(new Diagnostic(
                "DDL999",
                "The migration could not be analyzed because of an internal analysis error.",
                new SourceSpan(0, sql.Length)));
            return new MigrationAnalysisResult(new DatabaseSchema(schema.Tables), diagnostics);
        }
    }
}

public sealed class PostgreSqlSchemaMigrationAnalyzer : ISchemaMigrationAnalyzer
{
    public MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        PostgreSqlMigrationAnalyzer.Analyze(schema, sql);
}

internal sealed class PostgreSqlMigrationApplier
{
    private readonly List<Diagnostic> _diagnostics;

    internal PostgreSqlMigrationApplier(List<Diagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
    }

    internal void Apply(DdlStatement statement, List<Table> tables)
    {
        var create = statement as CreateTableStatement;
        if (create != null)
        {
            ApplyCreate(create, tables);
            return;
        }

        var drop = statement as DropTableStatement;
        if (drop != null)
        {
            ApplyDrop(drop, tables);
            return;
        }

        var alter = statement as AlterTableStatement;
        if (alter != null)
        {
            ApplyAlter(alter, tables);
        }
    }

    private void ApplyCreate(CreateTableStatement statement, List<Table> tables)
    {
        if (statement.Columns.Count == 0)
        {
            Report("DDL208", "CREATE TABLE must declare at least one column.", statement.Span);
            return;
        }

        if (FindTableMatches(tables, statement.Table).Count != 0)
        {
            if (!statement.IfNotExists)
            {
                Report("DDL200", $"Table '{TableDisplayName(statement.Table)}' already exists.", statement.Table.Span);
            }

            return;
        }

        var columns = new List<Column>();
        var valid = true;
        foreach (var definition in statement.Columns)
        {
            if (!QueryDialectProfiles.PostgreSql.Types.TryMapType(definition.SqlType, out _))
            {
                Report("DDL205", $"Unsupported PostgreSQL type '{definition.SqlType}'.", definition.Span);
                valid = false;
            }

            if (FindColumn(columns, definition.Name) != null)
            {
                Report("DDL203", $"Column '{definition.Name.Name}' is defined more than once.", definition.Name.Span);
                valid = false;
            }

            columns.Add(new Column(
                DeclaredName(definition.Name),
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
            Report("DDL206", "A table can have only one PRIMARY KEY constraint.", statement.Span);
            valid = false;
        }

        foreach (var primaryKey in statement.PrimaryKeys)
        {
            if (primaryKey.Count == 0)
            {
                Report("DDL204", "A PRIMARY KEY must contain at least one column.", statement.Span);
                valid = false;
                continue;
            }

            foreach (var identifier in primaryKey)
            {
                var index = FindColumnIndex(columns, identifier);
                if (index < 0)
                {
                    Report("DDL204", $"PRIMARY KEY refers to unknown column '{identifier.Name}'.", identifier.Span);
                    valid = false;
                    continue;
                }

                var column = columns[index];
                if (primaryKey.Count(item => IdentifiersMatch(item, identifier)) > 1)
                {
                    Report("DDL206", "A PRIMARY KEY cannot list the same column more than once.", identifier.Span);
                    valid = false;
                }

                columns[index] = new Column(
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

        tables.Add(new Table(
            DeclaredName(statement.Table.Name),
            columns,
            DeclaredSchema(statement.Table.Schema)));
    }

    private void ApplyDrop(DropTableStatement statement, List<Table> tables)
    {
        foreach (var identifier in statement.Tables)
        {
            var index = FindTableIndex(tables, identifier, out var ambiguous);
            if (index < 0)
            {
                if (!statement.IfExists && !ambiguous)
                {
                    Report("DDL201", $"Cannot drop unknown table '{TableDisplayName(identifier)}'.", identifier.Span);
                }

                continue;
            }

            tables.RemoveAt(index);
        }
    }

    private void ApplyAlter(AlterTableStatement statement, List<Table> tables)
    {
        var tableIndex = FindTableIndex(tables, statement.Table, out var ambiguous);
        if (tableIndex < 0)
        {
            if (!statement.IfExists && !ambiguous)
            {
                Report("DDL202", $"Cannot alter unknown table '{TableDisplayName(statement.Table)}'.", statement.Table.Span);
            }

            return;
        }

        foreach (var action in statement.Actions)
        {
            var table = tables[tableIndex];
            var add = action as AddColumnAction;
            if (add != null)
            {
                ApplyAdd(tableIndex, table, add, tables);
                continue;
            }

            var drop = action as DropColumnAction;
            if (drop != null)
            {
                ApplyDropColumn(tableIndex, table, drop, tables);
                continue;
            }

            var renameColumn = action as RenameColumnAction;
            if (renameColumn != null)
            {
                ApplyRenameColumn(tableIndex, table, renameColumn, tables);
                continue;
            }

            var renameTable = action as RenameTableAction;
            if (renameTable != null)
            {
                ApplyRenameTable(tableIndex, table, renameTable, tables);
                continue;
            }

            var alterType = action as AlterColumnTypeAction;
            if (alterType != null)
            {
                ApplyAlterType(tableIndex, table, alterType, tables);
                continue;
            }

            var nullability = action as SetColumnNullabilityAction;
            if (nullability != null)
            {
                ApplyNullability(tableIndex, table, nullability, tables);
                continue;
            }

            var defaultValue = action as SetColumnDefaultAction;
            if (defaultValue != null)
            {
                ApplyDefault(tableIndex, table, defaultValue, tables);
            }
        }
    }

    private void ApplyAdd(int tableIndex, Table table, AddColumnAction action, List<Table> tables)
    {
        if (!QueryDialectProfiles.PostgreSql.Types.TryMapType(action.Column.SqlType, out _))
        {
            Report("DDL205", $"Unsupported PostgreSQL type '{action.Column.SqlType}'.", action.Column.Span);
            return;
        }

        if (FindColumn(table.Columns, action.Column.Name) != null)
        {
            if (!action.IfNotExists)
            {
                Report("DDL203", $"Column '{action.Column.Name.Name}' already exists on table '{TableDisplayName(table)}'.", action.Column.Name.Span);
            }

            return;
        }

        if (action.Column.IsPrimaryKey && table.Columns.Any(column => column.IsPrimaryKey))
        {
            Report("DDL206", "A table can have only one PRIMARY KEY constraint.", action.Column.Span);
            return;
        }

        var columns = table.Columns.ToList();
        columns.Add(new Column(
            DeclaredName(action.Column.Name),
            action.Column.SqlType,
            action.Column.IsNullable,
            action.Column.IsPrimaryKey,
            action.Column.DefaultExpression,
            action.Column.IsIdentity));
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyDropColumn(int tableIndex, Table table, DropColumnAction action, List<Table> tables)
    {
        var index = FindColumnIndex(table.Columns, action.Column);
        if (index < 0)
        {
            if (!action.IfExists)
            {
                Report("DDL204", $"Cannot drop unknown column '{action.Column.Name}' from table '{TableDisplayName(table)}'.", action.Column.Span);
            }

            return;
        }

        var columns = table.Columns.Where((_, columnIndex) => columnIndex != index).ToList();
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyRenameColumn(int tableIndex, Table table, RenameColumnAction action, List<Table> tables)
    {
        var oldIndex = FindColumnIndex(table.Columns, action.OldName);
        if (oldIndex < 0)
        {
            Report("DDL204", $"Cannot rename unknown column '{action.OldName.Name}' on table '{TableDisplayName(table)}'.", action.OldName.Span);
            return;
        }

        if (FindColumn(table.Columns, action.NewName) != null)
        {
            Report("DDL203", $"Column '{action.NewName.Name}' already exists on table '{TableDisplayName(table)}'.", action.NewName.Span);
            return;
        }

        var oldColumn = table.Columns[oldIndex];
        var columns = table.Columns.ToList();
        columns[oldIndex] = new Column(
            DeclaredName(action.NewName),
            oldColumn.SqlType,
            oldColumn.IsNullable,
            oldColumn.IsPrimaryKey,
            oldColumn.DefaultExpression,
            oldColumn.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyRenameTable(int tableIndex, Table table, RenameTableAction action, List<Table> tables)
    {
        if (TableExistsInSchema(tables, action.NewName, table.Schema, tableIndex))
        {
            Report("DDL200", $"Table '{TableDisplayName(action.NewName)}' already exists.", action.NewName.Span);
            return;
        }

        tables[tableIndex] = new Table(DeclaredName(action.NewName), table.Columns, table.Schema);
    }

    private void ApplyAlterType(int tableIndex, Table table, AlterColumnTypeAction action, List<Table> tables)
    {
        if (!QueryDialectProfiles.PostgreSql.Types.TryMapType(action.SqlType, out _))
        {
            Report("DDL205", $"Unsupported PostgreSQL type '{action.SqlType}'.", action.Span);
            return;
        }

        var index = FindColumnIndex(table.Columns, action.Column);
        if (index < 0)
        {
            Report("DDL204", $"Cannot alter unknown column '{action.Column.Name}' on table '{TableDisplayName(table)}'.", action.Column.Span);
            return;
        }

        var oldColumn = table.Columns[index];
        var columns = table.Columns.ToList();
        columns[index] = new Column(
            oldColumn.Name,
            action.SqlType,
            oldColumn.IsNullable,
            oldColumn.IsPrimaryKey,
            oldColumn.DefaultExpression,
            oldColumn.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyNullability(int tableIndex, Table table, SetColumnNullabilityAction action, List<Table> tables)
    {
        var index = FindColumnIndex(table.Columns, action.Column);
        if (index < 0)
        {
            Report("DDL204", $"Cannot alter unknown column '{action.Column.Name}' on table '{TableDisplayName(table)}'.", action.Column.Span);
            return;
        }

        var oldColumn = table.Columns[index];
        if (oldColumn.IsPrimaryKey && action.IsNullable)
        {
            Report("DDL206", "A PRIMARY KEY column cannot be made nullable.", action.Column.Span);
            return;
        }

        var columns = table.Columns.ToList();
        columns[index] = new Column(
            oldColumn.Name,
            oldColumn.SqlType,
            action.IsNullable,
            oldColumn.IsPrimaryKey,
            oldColumn.DefaultExpression,
            oldColumn.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyDefault(int tableIndex, Table table, SetColumnDefaultAction action, List<Table> tables)
    {
        var index = FindColumnIndex(table.Columns, action.Column);
        if (index < 0)
        {
            Report("DDL204", $"Cannot alter unknown column '{action.Column.Name}' on table '{TableDisplayName(table)}'.", action.Column.Span);
            return;
        }

        var oldColumn = table.Columns[index];
        var columns = table.Columns.ToList();
        columns[index] = new Column(
            oldColumn.Name,
            oldColumn.SqlType,
            oldColumn.IsNullable,
            oldColumn.IsPrimaryKey,
            action.DefaultExpression,
            oldColumn.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private static Column? FindColumn(IEnumerable<Column> columns, SqlIdentifier identifier)
    {
        return columns.FirstOrDefault(column => Matches(identifier, column.Name));
    }

    private static int FindColumnIndex(IReadOnlyList<Column> columns, SqlIdentifier identifier)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (Matches(identifier, columns[index].Name))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Matches(SqlIdentifier identifier, string declaredName) =>
        identifier.IsQuoted
            ? string.Equals(identifier.Name, declaredName, StringComparison.Ordinal)
            : string.Equals(identifier.Name, declaredName, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSchema(SqlIdentifier identifier, string? declaredSchema) =>
        declaredSchema != null && Matches(identifier, declaredSchema);

    private static List<int> FindTableMatches(IReadOnlyList<Table> tables, SqlQualifiedName identifier)
    {
        var matches = new List<int>();
        for (var index = 0; index < tables.Count; index++)
        {
            var table = tables[index];
            if (!Matches(identifier.Name, table.Name) ||
                identifier.Schema != null && !MatchesSchema(identifier.Schema, table.Schema))
            {
                continue;
            }

            matches.Add(index);
        }

        return matches;
    }

    private int FindTableIndex(IReadOnlyList<Table> tables, SqlQualifiedName identifier, out bool ambiguous)
    {
        var matches = FindTableMatches(tables, identifier);
        ambiguous = matches.Count > 1;
        if (ambiguous)
        {
            Report(
                "DDL207",
                $"Table '{TableDisplayName(identifier)}' is ambiguous; qualify it with a schema.",
                identifier.Span);
            return -1;
        }

        return matches.Count == 0 ? -1 : matches[0];
    }

    private static bool TableExistsInSchema(
        IReadOnlyList<Table> tables,
        SqlIdentifier name,
        string? schema,
        int ignoredIndex)
    {
        for (var index = 0; index < tables.Count; index++)
        {
            if (index == ignoredIndex || !Matches(name, tables[index].Name))
            {
                continue;
            }

            if (string.Equals(schema, tables[index].Schema, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string DeclaredName(SqlIdentifier identifier) =>
        identifier.IsQuoted ? identifier.Name : identifier.Name.ToLowerInvariant();

    private static string? DeclaredSchema(SqlIdentifier? identifier) =>
        identifier == null ? null : DeclaredName(identifier);

    private static string TableDisplayName(SqlQualifiedName identifier) =>
        identifier.Schema == null
            ? identifier.Name.Name
            : identifier.Schema.Name + "." + identifier.Name.Name;

    private static string TableDisplayName(SqlIdentifier identifier) => identifier.Name;

    private static string TableDisplayName(Table table) =>
        table.Schema == null ? table.Name : table.Schema + "." + table.Name;

    private static bool IdentifiersMatch(SqlIdentifier left, SqlIdentifier right) =>
        string.Equals(DeclaredName(left), DeclaredName(right), StringComparison.Ordinal);

    private void Report(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));
}
