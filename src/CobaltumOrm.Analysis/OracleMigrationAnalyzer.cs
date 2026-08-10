using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

/// <summary>Applies supported Oracle DDL to the compile-time database schema.</summary>
public static class OracleSchemaBuilder
{
    public static MigrationAnalysisResult ApplyScript(DatabaseSchema schema, string sql) =>
        OracleMigrationAnalyzer.Analyze(schema, sql);
}

/// <summary>Analyzes Oracle migration scripts without connecting to a database.</summary>
public static class OracleMigrationAnalyzer
{
    public static MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql)
    {
        if (schema is null)
        {
            return new MigrationAnalysisResult(
                new DatabaseSchema(Array.Empty<Table>()),
                new[]
                {
                    new Diagnostic(
                        "DDL000",
                        "A database schema is required.",
                        new SourceSpan(0, 0)),
                });
        }

        if (sql is null)
        {
            return new MigrationAnalysisResult(
                new DatabaseSchema(schema.Tables),
                new[]
                {
                    new Diagnostic(
                        "DDL000",
                        "Migration SQL text is required.",
                        new SourceSpan(0, 0)),
                });
        }

        var current = schema;
        var diagnostics = new List<Diagnostic>();
        var statements = OracleScriptClassifier.SplitAndClassify(sql, out var scriptError);
        if (scriptError is not null)
        {
            diagnostics.Add(new Diagnostic("DDL300", scriptError.Message, scriptError.Span));
        }

        foreach (var statement in statements)
        {
            if (statement.Kind == SqlStatementKind.Empty ||
                statement.Kind == SqlStatementKind.Select ||
                statement.Kind == SqlStatementKind.DataManipulation ||
                statement.Kind == SqlStatementKind.SchemaNeutral)
            {
                continue;
            }

            if (statement.Kind == SqlStatementKind.Unsupported)
            {
                diagnostics.Add(new Diagnostic(
                    "DDL300",
                    "This Oracle statement may change the queryable schema or execute procedural code and is not supported by schema analysis.",
                    statement.Span));
                continue;
            }

            var statementDiagnostics = new List<Diagnostic>();
            var tokens = new OracleDdlLexer(statement.Text, statementDiagnostics).Lex();
            var parsed = new OracleDdlParser(tokens, statementDiagnostics).Parse();
            if (parsed is not null && statementDiagnostics.Count == 0)
            {
                var candidate = new List<Table>(current.Tables);
                new OracleMigrationApplier(statementDiagnostics).Apply(parsed, candidate);
                if (statementDiagnostics.Count == 0)
                {
                    current = new DatabaseSchema(candidate);
                }
            }

            foreach (var diagnostic in statementDiagnostics)
            {
                diagnostics.Add(new Diagnostic(
                    diagnostic.Code,
                    diagnostic.Message,
                    new SourceSpan(
                        statement.Span.Start + diagnostic.Span.Start,
                        diagnostic.Span.Length)));
            }
        }

        return new MigrationAnalysisResult(current, diagnostics);
    }
}

/// <summary>Adapts the Oracle migration analyzer to the dialect service contract.</summary>
public sealed class OracleSchemaMigrationAnalyzer : ISchemaMigrationAnalyzer
{
    public MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        OracleMigrationAnalyzer.Analyze(schema, sql);
}

internal sealed class OracleMigrationApplier
{
    private readonly List<Diagnostic> _diagnostics;
    private readonly OracleTypeMapper _typeMapper = new OracleTypeMapper();

    internal OracleMigrationApplier(List<Diagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
    }

    internal void Apply(OracleDdlStatement statement, List<Table> tables)
    {
        if (statement is OracleDdlCreateTableStatement create)
        {
            ApplyCreate(create, tables);
            return;
        }

        if (statement is OracleDdlDropTableStatement drop)
        {
            ApplyDrop(drop, tables);
            return;
        }

        if (statement is OracleDdlRenameTableStatement rename)
        {
            ApplyRenameTable(rename, tables);
            return;
        }

        if (statement is OracleDdlAlterTableStatement alter)
        {
            ApplyAlter(alter, tables);
            return;
        }

        Report("DDL999", "The Oracle DDL statement could not be applied.", statement.Span);
    }

    private void ApplyCreate(OracleDdlCreateTableStatement statement, List<Table> tables)
    {
        if (statement.Columns.Count == 0)
        {
            Report("DDL208", "CREATE TABLE must declare at least one column.", statement.Span);
            return;
        }

        if (FindTableMatches(tables, statement.Table).Count != 0)
        {
            Report("DDL200", $"Table '{DisplayName(statement.Table)}' already exists.", statement.Table.Span);
            return;
        }

        var columns = new List<Column>();
        var valid = true;
        foreach (var definition in statement.Columns)
        {
            if (definition.SqlType is null || !_typeMapper.TryMap(definition.SqlType, out _))
            {
                Report(
                    "DDL205",
                    $"Unsupported Oracle type '{definition.SqlType ?? string.Empty}'.",
                    definition.Span);
                valid = false;
            }

            if (definition.IsIdentity && !IsOracleIdentityType(definition.SqlType))
            {
                Report("DDL101", "Oracle identity columns must use a supported integer NUMBER type.", definition.Span);
                valid = false;
            }

            var declaredName = DeclaredName(definition.Name);
            if (columns.Any(column => string.Equals(column.Name, declaredName, StringComparison.Ordinal)))
            {
                Report("DDL203", $"Column '{definition.Name.Name}' is defined more than once.", definition.Name.Span);
                valid = false;
            }

            columns.Add(new Column(
                declaredName,
                definition.SqlType ?? string.Empty,
                definition.IsNullable,
                definition.IsPrimaryKey,
                definition.DefaultExpression,
                definition.IsIdentity));
        }

        var primaryKeyCount = statement.PrimaryKeys.Count +
            statement.Columns.Count(column => column.IsPrimaryKey);
        if (primaryKeyCount > 1)
        {
            Report("DDL206", "A table can have only one PRIMARY KEY constraint.", statement.Span);
            valid = false;
        }

        foreach (var primaryKey in statement.PrimaryKeys)
        {
            if (!ApplyPrimaryKey(columns, primaryKey, statement.Span))
            {
                valid = false;
            }
        }

        if (!valid)
        {
            return;
        }

        tables.Add(new Table(
            DeclaredName(statement.Table.Name),
            columns,
            statement.Table.Schema is null ? null : DeclaredName(statement.Table.Schema)));
    }

    private void ApplyDrop(OracleDdlDropTableStatement statement, List<Table> tables)
    {
        var index = FindTableIndex(tables, statement.Table, out var ambiguous);
        if (index < 0)
        {
            if (!ambiguous)
            {
                Report("DDL201", $"Cannot drop unknown table '{DisplayName(statement.Table)}'.", statement.Table.Span);
            }

            return;
        }

        tables.RemoveAt(index);
    }

    private void ApplyAlter(OracleDdlAlterTableStatement statement, List<Table> tables)
    {
        var tableIndex = FindTableIndex(tables, statement.Table, out var ambiguous);
        if (tableIndex < 0)
        {
            if (!ambiguous)
            {
                Report("DDL202", $"Cannot alter unknown table '{DisplayName(statement.Table)}'.", statement.Table.Span);
            }

            return;
        }

        foreach (var action in statement.Actions)
        {
            var table = tables[tableIndex];
            if (action is OracleDdlAddColumnAction add)
            {
                ApplyAddColumn(tableIndex, table, add, tables);
            }
            else if (action is OracleDdlModifyColumnAction modify)
            {
                ApplyModifyColumn(tableIndex, table, modify, tables);
            }
            else if (action is OracleDdlDropColumnAction drop)
            {
                ApplyDropColumn(tableIndex, table, drop, tables);
            }
            else if (action is OracleDdlRenameColumnAction renameColumn)
            {
                ApplyRenameColumn(tableIndex, table, renameColumn, tables);
            }
            else if (action is OracleDdlRenameTableAction renameTable)
            {
                ApplyRenameTable(tableIndex, table, renameTable, tables, action.Span);
            }
            else if (action is OracleDdlAddPrimaryKeyAction primaryKey)
            {
                var columns = table.Columns.ToList();
                if (columns.Any(column => column.IsPrimaryKey))
                {
                    Report("DDL206", "A table can have only one PRIMARY KEY constraint.", action.Span);
                    continue;
                }

                if (ApplyPrimaryKey(columns, primaryKey.Columns, action.Span))
                {
                    tables[tableIndex] = new Table(table.Name, columns, table.Schema);
                }
            }
        }
    }

    private void ApplyAddColumn(
        int tableIndex,
        Table table,
        OracleDdlAddColumnAction action,
        List<Table> tables)
    {
        var definition = action.Column;
        if (definition.SqlType is null || !_typeMapper.TryMap(definition.SqlType, out _))
        {
            Report("DDL205", $"Unsupported Oracle type '{definition.SqlType ?? string.Empty}'.", definition.Span);
            return;
        }

        if (definition.IsIdentity && !IsOracleIdentityType(definition.SqlType))
        {
            Report("DDL101", "Oracle identity columns must use a supported integer NUMBER type.", definition.Span);
            return;
        }

        var name = DeclaredName(definition.Name);
        if (FindColumn(table.Columns, definition.Name) is not null)
        {
            Report("DDL203", $"Column '{definition.Name.Name}' already exists on table '{DisplayName(table)}'.", definition.Name.Span);
            return;
        }

        if (definition.IsPrimaryKey && table.Columns.Any(column => column.IsPrimaryKey))
        {
            Report("DDL206", "A table can have only one PRIMARY KEY constraint.", definition.Span);
            return;
        }

        var columns = table.Columns.ToList();
        columns.Add(new Column(
            name,
            definition.SqlType,
            definition.IsNullable && !definition.IsPrimaryKey,
            definition.IsPrimaryKey,
            definition.DefaultExpression,
            definition.IsIdentity));
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyModifyColumn(
        int tableIndex,
        Table table,
        OracleDdlModifyColumnAction action,
        List<Table> tables)
    {
        var definition = action.Column;
        var index = FindColumnIndex(table.Columns, definition.Name);
        if (index < 0)
        {
            Report("DDL204", $"Cannot modify unknown column '{definition.Name.Name}' on table '{DisplayName(table)}'.", definition.Name.Span);
            return;
        }

        if (definition.IsIdentity || definition.IsPrimaryKey)
        {
            Report("DDL101", "ALTER TABLE MODIFY cannot change identity or primary-key metadata in compile-time schema analysis.", definition.Span);
            return;
        }

        if (definition.SqlType is not null && !_typeMapper.TryMap(definition.SqlType, out _))
        {
            Report("DDL205", $"Unsupported Oracle type '{definition.SqlType}'.", definition.Span);
            return;
        }

        if (!definition.IsNullableSpecified && definition.SqlType is null && !definition.IsDefaultSpecified)
        {
            Report("DDL101", "ALTER TABLE MODIFY must change a type, nullability, or default.", definition.Span);
            return;
        }

        var existing = table.Columns[index];
        var nullable = definition.IsNullableSpecified ? definition.IsNullable : existing.IsNullable;
        if (existing.IsPrimaryKey && nullable)
        {
            Report("DDL206", "A PRIMARY KEY column cannot be made nullable.", definition.Name.Span);
            return;
        }

        var columns = table.Columns.ToList();
        columns[index] = new Column(
            existing.Name,
            definition.SqlType ?? existing.SqlType,
            nullable,
            existing.IsPrimaryKey,
            definition.IsDefaultSpecified ? definition.DefaultExpression : existing.DefaultExpression,
            existing.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyDropColumn(
        int tableIndex,
        Table table,
        OracleDdlDropColumnAction action,
        List<Table> tables)
    {
        var index = FindColumnIndex(table.Columns, action.Column);
        if (index < 0)
        {
            Report("DDL204", $"Cannot drop unknown column '{action.Column.Name}' from table '{DisplayName(table)}'.", action.Column.Span);
            return;
        }

        var columns = table.Columns.Where((_, columnIndex) => columnIndex != index).ToList();
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyRenameColumn(
        int tableIndex,
        Table table,
        OracleDdlRenameColumnAction action,
        List<Table> tables)
    {
        var oldIndex = FindColumnIndex(table.Columns, action.OldName);
        if (oldIndex < 0)
        {
            Report("DDL204", $"Cannot rename unknown column '{action.OldName.Name}' on table '{DisplayName(table)}'.", action.OldName.Span);
            return;
        }

        if (FindColumn(table.Columns, action.NewName) is not null)
        {
            Report("DDL203", $"Column '{action.NewName.Name}' already exists on table '{DisplayName(table)}'.", action.NewName.Span);
            return;
        }

        var old = table.Columns[oldIndex];
        var columns = table.Columns.ToList();
        columns[oldIndex] = new Column(
            DeclaredName(action.NewName),
            old.SqlType,
            old.IsNullable,
            old.IsPrimaryKey,
            old.DefaultExpression,
            old.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void ApplyRenameTable(
        int tableIndex,
        Table table,
        OracleDdlRenameTableAction action,
        List<Table> tables,
        SourceSpan span)
    {
        var newName = DeclaredName(action.NewName);
        if (tables.Where((candidate, index) => index != tableIndex).Any(candidate =>
                string.Equals(candidate.Name, newName, StringComparison.Ordinal) &&
                string.Equals(candidate.Schema, table.Schema, StringComparison.Ordinal)))
        {
            Report("DDL200", $"Table '{newName}' already exists.", action.NewName.Span);
            return;
        }

        tables[tableIndex] = new Table(newName, table.Columns, table.Schema);
    }

    private void ApplyRenameTable(OracleDdlRenameTableStatement statement, List<Table> tables)
    {
        var tableIndex = FindTableIndex(tables, statement.OldName, out var ambiguous);
        if (tableIndex < 0)
        {
            if (!ambiguous)
            {
                Report("DDL201", $"Cannot rename unknown table '{DisplayName(statement.OldName)}'.", statement.OldName.Span);
            }

            return;
        }

        ApplyRenameTable(
            tableIndex,
            tables[tableIndex],
            new OracleDdlRenameTableAction(statement.NewName, statement.NewName.Span),
            tables,
            statement.Span);
    }

    private bool ApplyPrimaryKey(
        List<Column> columns,
        IReadOnlyList<OracleDdlIdentifier> primaryKey,
        SourceSpan span)
    {
        if (primaryKey.Count == 0)
        {
            Report("DDL204", "A PRIMARY KEY must contain at least one column.", span);
            return false;
        }

        var valid = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in primaryKey)
        {
            var declaredName = DeclaredName(identifier);
            if (!seen.Add(declaredName))
            {
                Report("DDL206", "A PRIMARY KEY cannot list the same column more than once.", identifier.Span);
                valid = false;
                continue;
            }

            var index = columns.FindIndex(column => string.Equals(column.Name, declaredName, StringComparison.Ordinal));
            if (index < 0)
            {
                Report("DDL204", $"PRIMARY KEY refers to unknown column '{identifier.Name}'.", identifier.Span);
                valid = false;
                continue;
            }

            var column = columns[index];
            columns[index] = new Column(
                column.Name,
                column.SqlType,
                false,
                true,
                column.DefaultExpression,
                column.IsIdentity);
        }

        return valid;
    }

    private int FindTableIndex(
        IReadOnlyList<Table> tables,
        OracleDdlQualifiedName identifier,
        out bool ambiguous)
    {
        var matches = FindTableMatches(tables, identifier);
        ambiguous = matches.Count > 1;
        if (ambiguous)
        {
            Report("DDL207", $"Table '{DisplayName(identifier)}' is ambiguous; qualify it with a schema.", identifier.Span);
            return -1;
        }

        return matches.Count == 0 ? -1 : matches[0];
    }

    private static List<int> FindTableMatches(
        IReadOnlyList<Table> tables,
        OracleDdlQualifiedName identifier)
    {
        var matches = new List<int>();
        for (var index = 0; index < tables.Count; index++)
        {
            var table = tables[index];
            if (!Matches(identifier.Name, table.Name))
            {
                continue;
            }

            if (identifier.Schema is not null &&
                (table.Schema is null || !Matches(identifier.Schema, table.Schema)))
            {
                continue;
            }

            matches.Add(index);
        }

        return matches;
    }

    private static Column? FindColumn(IEnumerable<Column> columns, OracleDdlIdentifier identifier) =>
        columns.FirstOrDefault(column => Matches(identifier, column.Name));

    private static int FindColumnIndex(IReadOnlyList<Column> columns, OracleDdlIdentifier identifier)
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

    private static bool Matches(OracleDdlIdentifier identifier, string declaredName) =>
        identifier.IsQuoted
            ? string.Equals(identifier.Name, declaredName, StringComparison.Ordinal)
            : string.Equals(identifier.Name.ToUpperInvariant(), declaredName, StringComparison.Ordinal);

    private static string DeclaredName(OracleDdlIdentifier identifier) =>
        identifier.IsQuoted ? identifier.Name : identifier.Name.ToUpperInvariant();

    private static string DisplayName(OracleDdlQualifiedName identifier) =>
        identifier.Schema is null
            ? identifier.Name.Name
            : identifier.Schema.Name + "." + identifier.Name.Name;

    private static string DisplayName(Table table) =>
        table.Schema is null ? table.Name : table.Schema + "." + table.Name;

    private bool IsOracleIdentityType(string? sqlType)
    {
        if (sqlType is null || !_typeMapper.TryMap(sqlType, out var kind))
        {
            return false;
        }

        return kind == SqlValueKind.Int16 || kind == SqlValueKind.Int32 || kind == SqlValueKind.Int64;
    }

    private void Report(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));
}
