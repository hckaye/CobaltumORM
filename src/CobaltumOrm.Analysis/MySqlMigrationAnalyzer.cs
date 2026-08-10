using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

/// <summary>Applies supported MySQL 8 DDL to the compile-time database schema.</summary>
public static class MySqlMigrationAnalyzer
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
            var current = schema;
            string? defaultDatabase = null;
            var statements = MySqlScriptClassifier.SplitAndClassify(sql, out var scriptError);
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
                        "This MySQL statement may change the queryable schema and is not supported by schema analysis.",
                        statement.Span));
                    continue;
                }

                var localDiagnostics = new List<Diagnostic>();
                var tokens = new MySqlDdlLexer(statement.Text, localDiagnostics).Lex();
                var parsed = new MySqlDdlParser(tokens, statement.Text, localDiagnostics).Parse();
                if (localDiagnostics.Count != 0)
                {
                    MySqlAppendDiagnostics(diagnostics, localDiagnostics, statement.Span.Start);
                    continue;
                }

                foreach (var ddlStatement in parsed)
                {
                    var workingTables = current.Tables.ToList();
                    var applier = new MySqlMigrationApplier(workingTables, defaultDatabase, localDiagnostics);
                    applier.Apply(ddlStatement);
                    if (localDiagnostics.Count != 0)
                    {
                        MySqlAppendDiagnostics(diagnostics, localDiagnostics, statement.Span.Start);
                        break;
                    }

                    current = new DatabaseSchema(workingTables);
                    defaultDatabase = applier.DefaultDatabase;
                }
            }

            return new MigrationAnalysisResult(current, diagnostics);
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

    private static void MySqlAppendDiagnostics(
        ICollection<Diagnostic> target,
        IEnumerable<Diagnostic> diagnostics,
        int offset)
    {
        foreach (var diagnostic in diagnostics)
        {
            target.Add(new Diagnostic(
                diagnostic.Code,
                diagnostic.Message,
                new SourceSpan(offset + diagnostic.Span.Start, diagnostic.Span.Length)));
        }
    }
}

/// <summary>Adapts MySQL migration analysis to the dialect service contract.</summary>
public sealed class MySqlSchemaMigrationAnalyzer : ISchemaMigrationAnalyzer
{
    public MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        MySqlMigrationAnalyzer.Analyze(schema, sql);
}

internal sealed class MySqlMigrationApplier
{
    private readonly List<Table> _tables;
    private readonly MySqlTypeMapper _typeMapper = new MySqlTypeMapper();
    private readonly List<Diagnostic> _diagnostics;

    internal MySqlMigrationApplier(
        List<Table> tables,
        string? defaultDatabase,
        List<Diagnostic> diagnostics)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        DefaultDatabase = defaultDatabase;
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    internal string? DefaultDatabase { get; private set; }

    internal void Apply(MySqlDdlStatement statement)
    {
        if (statement is MySqlCreateTableStatement create)
        {
            MySqlApplyCreate(create);
            return;
        }

        if (statement is MySqlDropTableStatement drop)
        {
            MySqlApplyDrop(drop);
            return;
        }

        if (statement is MySqlAlterTableStatement alter)
        {
            MySqlApplyAlter(alter);
            return;
        }

        if (statement is MySqlRenameTableStatement rename)
        {
            MySqlApplyRenameTable(rename);
            return;
        }

        if (statement is MySqlUseStatement use)
        {
            DefaultDatabase = MySqlDeclaredName(use.Database);
            return;
        }

        MySqlReport("DDL101", "The MySQL migration statement is not supported by schema analysis.", statement.Span);
    }

    private void MySqlApplyCreate(MySqlCreateTableStatement statement)
    {
        if (statement.Columns.Count == 0)
        {
            MySqlReport("DDL208", "CREATE TABLE must declare at least one column.", statement.Span);
            return;
        }

        var tableSchema = MySqlEffectiveSchema(statement.Table.Schema);
        if (MySqlFindTableMatches(statement.Table, tableSchema).Count != 0)
        {
            if (!statement.IfNotExists)
            {
                MySqlReport("DDL200", "Table '" + MySqlTableDisplayName(statement.Table, tableSchema) + "' already exists.", statement.Table.Span);
            }

            return;
        }

        var columns = new List<Column>();
        var valid = true;
        foreach (var definition in statement.Columns)
        {
            if (!_typeMapper.TryMap(definition.SqlType, out _))
            {
                MySqlReport("DDL205", "Unsupported MySQL type '" + definition.SqlType + "'.", definition.Span);
                valid = false;
            }

            if (MySqlFindColumnIndex(columns, definition.Name) >= 0)
            {
                MySqlReport("DDL203", "Column '" + definition.Name.Name + "' is defined more than once.", definition.Name.Span);
                valid = false;
            }

            if (definition.IsIdentity &&
                (!_typeMapper.TryMap(definition.SqlType, out var identityKind) || !SqlTypeMapper.IsInteger(identityKind)))
            {
                MySqlReport("DDL205", "AUTO_INCREMENT column '" + definition.Name.Name + "' must use an integer MySQL type.", definition.Span);
                valid = false;
            }

            columns.Add(MySqlCreateColumn(definition));
        }

        if (statement.PrimaryKeys.Count + columns.Count(column => column.IsPrimaryKey) > 1)
        {
            MySqlReport("DDL206", "A table can have only one PRIMARY KEY constraint.", statement.Span);
            valid = false;
        }

        foreach (var primaryKey in statement.PrimaryKeys)
        {
            if (primaryKey.Count == 0)
            {
                MySqlReport("DDL206", "A PRIMARY KEY must contain at least one column.", statement.Span);
                valid = false;
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identifier in primaryKey)
            {
                if (!seen.Add(MySqlIdentifierKey(identifier)))
                {
                    MySqlReport("DDL206", "A PRIMARY KEY cannot list the same column more than once.", identifier.Span);
                    valid = false;
                    continue;
                }

                var index = MySqlFindColumnIndex(columns, identifier);
                if (index < 0)
                {
                    MySqlReport("DDL204", "PRIMARY KEY refers to unknown column '" + identifier.Name + "'.", identifier.Span);
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
        }

        if (!valid)
        {
            return;
        }

        _tables.Add(new Table(
            MySqlDeclaredName(statement.Table.Name),
            columns,
            tableSchema));
    }

    private void MySqlApplyDrop(MySqlDropTableStatement statement)
    {
        foreach (var identifier in statement.Tables)
        {
            var schema = MySqlEffectiveSchema(identifier.Schema);
            var matches = MySqlFindTableMatches(identifier, schema);
            if (matches.Count == 0)
            {
                if (!statement.IfExists)
                {
                    MySqlReport("DDL201", "Cannot drop unknown table '" + MySqlTableDisplayName(identifier, schema) + "'.", identifier.Span);
                }

                continue;
            }

            if (matches.Count > 1)
            {
                MySqlReport("DDL207", "Table '" + MySqlTableDisplayName(identifier, schema) + "' is ambiguous; qualify it with a database.", identifier.Span);
                continue;
            }

            _tables.RemoveAt(matches[0]);
        }
    }

    private void MySqlApplyAlter(MySqlAlterTableStatement statement)
    {
        var schema = MySqlEffectiveSchema(statement.Table.Schema);
        var matches = MySqlFindTableMatches(statement.Table, schema);
        if (matches.Count == 0)
        {
            if (!statement.IfExists)
            {
                MySqlReport("DDL202", "Cannot alter unknown table '" + MySqlTableDisplayName(statement.Table, schema) + "'.", statement.Table.Span);
            }

            return;
        }

        if (matches.Count > 1)
        {
            MySqlReport("DDL207", "Table '" + MySqlTableDisplayName(statement.Table, schema) + "' is ambiguous; qualify it with a database.", statement.Table.Span);
            return;
        }

        var tableIndex = matches[0];
        foreach (var action in statement.Actions)
        {
            if (action is MySqlSchemaNeutralAlterAction)
            {
                continue;
            }

            if (action is MySqlAddColumnAction add)
            {
                MySqlApplyAdd(tableIndex, add);
                continue;
            }

            if (action is MySqlDropColumnAction drop)
            {
                MySqlApplyDropColumn(tableIndex, drop);
                continue;
            }

            if (action is MySqlModifyColumnAction modify)
            {
                MySqlApplyModify(tableIndex, modify);
                continue;
            }

            if (action is MySqlChangeColumnAction change)
            {
                MySqlApplyChange(tableIndex, change);
                continue;
            }

            if (action is MySqlRenameColumnAction renameColumn)
            {
                MySqlApplyRenameColumn(tableIndex, renameColumn);
                continue;
            }

            if (action is MySqlRenameTableAction renameTable)
            {
                MySqlApplyRenameTableAction(tableIndex, schema, renameTable);
                continue;
            }

            if (action is MySqlAlterDefaultAction alterDefault)
            {
                MySqlApplyAlterDefault(tableIndex, alterDefault);
                continue;
            }

            if (action is MySqlPrimaryKeyAction primaryKey)
            {
                MySqlApplyPrimaryKey(tableIndex, primaryKey);
            }
        }
    }

    private void MySqlApplyRenameTable(MySqlRenameTableStatement statement)
    {
        foreach (var pair in statement.Pairs)
        {
            var oldSchema = MySqlEffectiveSchema(pair.OldName.Schema);
            var oldMatches = MySqlFindTableMatches(pair.OldName, oldSchema);
            if (oldMatches.Count == 0)
            {
                MySqlReport("DDL204", "Cannot rename unknown table '" + MySqlTableDisplayName(pair.OldName, oldSchema) + "'.", pair.OldName.Span);
                continue;
            }

            if (oldMatches.Count > 1)
            {
                MySqlReport("DDL207", "Table '" + MySqlTableDisplayName(pair.OldName, oldSchema) + "' is ambiguous; qualify it with a database.", pair.OldName.Span);
                continue;
            }

            var oldIndex = oldMatches[0];
            var newSchema = pair.NewName.Schema == null ? _tables[oldIndex].Schema : MySqlEffectiveSchema(pair.NewName.Schema);
            MySqlRenameTable(oldIndex, newSchema, pair.NewName);
        }
    }

    private void MySqlApplyRenameTableAction(int tableIndex, string? oldSchema, MySqlRenameTableAction action)
    {
        var newSchema = action.NewName.Schema == null ? oldSchema : MySqlEffectiveSchema(action.NewName.Schema);
        MySqlRenameTable(tableIndex, newSchema, action.NewName);
    }

    private void MySqlRenameTable(int tableIndex, string? newSchema, SqlQualifiedName identifier)
    {
        var existing = MySqlFindTableByName(newSchema, identifier.Name, tableIndex);
        if (existing >= 0)
        {
            MySqlReport("DDL200", "Table '" + MySqlTableDisplayName(identifier, newSchema) + "' already exists.", identifier.Span);
            return;
        }

        var table = _tables[tableIndex];
        _tables[tableIndex] = new Table(MySqlDeclaredName(identifier.Name), table.Columns, newSchema);
    }

    private void MySqlApplyAdd(int tableIndex, MySqlAddColumnAction action)
    {
        var table = _tables[tableIndex];
        if (!_typeMapper.TryMap(action.Column.SqlType, out _))
        {
            MySqlReport("DDL205", "Unsupported MySQL type '" + action.Column.SqlType + "'.", action.Column.Span);
            return;
        }

        if (action.Column.IsIdentity &&
            (!_typeMapper.TryMap(action.Column.SqlType, out var identityKind) || !SqlTypeMapper.IsInteger(identityKind)))
        {
            MySqlReport("DDL205", "AUTO_INCREMENT column '" + action.Column.Name.Name + "' must use an integer MySQL type.", action.Column.Span);
            return;
        }

        if (MySqlFindColumnIndex(table.Columns, action.Column.Name) >= 0)
        {
            if (!action.IfNotExists)
            {
                MySqlReport("DDL203", "Column '" + action.Column.Name.Name + "' already exists.", action.Column.Name.Span);
            }

            return;
        }

        if (action.Column.IsPrimaryKey && table.Columns.Any(column => column.IsPrimaryKey))
        {
            MySqlReport("DDL206", "A table can have only one PRIMARY KEY constraint.", action.Column.Span);
            return;
        }

        var columns = table.Columns.ToList();
        MySqlInsertColumn(columns, MySqlCreateColumn(action.Column), action.Column.Position, table.Name);
        MySqlSetTableColumns(tableIndex, columns);
    }

    private void MySqlApplyDropColumn(int tableIndex, MySqlDropColumnAction action)
    {
        var table = _tables[tableIndex];
        var index = MySqlFindColumnIndex(table.Columns, action.Column);
        if (index < 0)
        {
            if (!action.IfExists)
            {
                MySqlReport("DDL204", "Cannot drop unknown column '" + action.Column.Name + "'.", action.Column.Span);
            }

            return;
        }

        MySqlSetTableColumns(
            tableIndex,
            table.Columns.Where((_, current) => current != index).ToArray());
    }

    private void MySqlApplyModify(int tableIndex, MySqlModifyColumnAction action)
    {
        var table = _tables[tableIndex];
        if (!_typeMapper.TryMap(action.Column.SqlType, out _))
        {
            MySqlReport("DDL205", "Unsupported MySQL type '" + action.Column.SqlType + "'.", action.Column.Span);
            return;
        }

        if (action.Column.IsIdentity &&
            (!_typeMapper.TryMap(action.Column.SqlType, out var modifyIdentityKind) ||
             !SqlTypeMapper.IsInteger(modifyIdentityKind)))
        {
            MySqlReport("DDL205", "AUTO_INCREMENT column '" + action.Column.Name.Name + "' must use an integer MySQL type.", action.Column.Span);
            return;
        }

        var index = MySqlFindColumnIndex(table.Columns, action.Column.Name);
        if (index < 0)
        {
            MySqlReport("DDL204", "Cannot modify unknown column '" + action.Column.Name.Name + "'.", action.Column.Name.Span);
            return;
        }

        var old = table.Columns[index];
        if (old.IsPrimaryKey && action.Column.IsNullable)
        {
            MySqlReport("DDL206", "A PRIMARY KEY column cannot be made nullable.", action.Column.Name.Span);
            return;
        }

        var replacement = new Column(
            old.Name,
            action.Column.SqlType,
            old.IsPrimaryKey ? false : action.Column.IsNullable,
            old.IsPrimaryKey || action.Column.IsPrimaryKey,
            action.Column.DefaultExpression,
            action.Column.IsIdentity);
        var columns = table.Columns.ToList();
        columns.RemoveAt(index);
        MySqlInsertColumn(columns, replacement, action.Column.Position, table.Name, index);
        MySqlSetTableColumns(tableIndex, columns);
    }

    private void MySqlApplyChange(int tableIndex, MySqlChangeColumnAction action)
    {
        var table = _tables[tableIndex];
        if (!_typeMapper.TryMap(action.Column.SqlType, out _))
        {
            MySqlReport("DDL205", "Unsupported MySQL type '" + action.Column.SqlType + "'.", action.Column.Span);
            return;
        }

        if (action.Column.IsIdentity &&
            (!_typeMapper.TryMap(action.Column.SqlType, out var changeIdentityKind) ||
             !SqlTypeMapper.IsInteger(changeIdentityKind)))
        {
            MySqlReport("DDL205", "AUTO_INCREMENT column '" + action.Column.Name.Name + "' must use an integer MySQL type.", action.Column.Span);
            return;
        }

        var oldIndex = MySqlFindColumnIndex(table.Columns, action.OldName);
        if (oldIndex < 0)
        {
            MySqlReport("DDL204", "Cannot change unknown column '" + action.OldName.Name + "'.", action.OldName.Span);
            return;
        }

        var duplicate = MySqlFindColumnIndex(table.Columns, action.Column.Name);
        if (duplicate >= 0 && duplicate != oldIndex)
        {
            MySqlReport("DDL203", "Column '" + action.Column.Name.Name + "' already exists.", action.Column.Name.Span);
            return;
        }

        var old = table.Columns[oldIndex];
        if (old.IsPrimaryKey && action.Column.IsNullable)
        {
            MySqlReport("DDL206", "A PRIMARY KEY column cannot be made nullable.", action.Column.Name.Span);
            return;
        }

        var replacement = new Column(
            MySqlDeclaredName(action.Column.Name),
            action.Column.SqlType,
            old.IsPrimaryKey ? false : action.Column.IsNullable,
            old.IsPrimaryKey || action.Column.IsPrimaryKey,
            action.Column.DefaultExpression,
            action.Column.IsIdentity);
        var columns = table.Columns.ToList();
        columns.RemoveAt(oldIndex);
        MySqlInsertColumn(columns, replacement, action.Column.Position, table.Name, oldIndex);
        MySqlSetTableColumns(tableIndex, columns);
    }

    private void MySqlApplyRenameColumn(int tableIndex, MySqlRenameColumnAction action)
    {
        var table = _tables[tableIndex];
        var oldIndex = MySqlFindColumnIndex(table.Columns, action.OldName);
        if (oldIndex < 0)
        {
            MySqlReport("DDL204", "Cannot rename unknown column '" + action.OldName.Name + "'.", action.OldName.Span);
            return;
        }

        var duplicate = MySqlFindColumnIndex(table.Columns, action.NewName);
        if (duplicate >= 0 && duplicate != oldIndex)
        {
            MySqlReport("DDL203", "Column '" + action.NewName.Name + "' already exists.", action.NewName.Span);
            return;
        }

        var old = table.Columns[oldIndex];
        var columns = table.Columns.ToList();
        columns[oldIndex] = new Column(
            MySqlDeclaredName(action.NewName),
            old.SqlType,
            old.IsNullable,
            old.IsPrimaryKey,
            old.DefaultExpression,
            old.IsIdentity);
        MySqlSetTableColumns(tableIndex, columns);
    }

    private void MySqlApplyAlterDefault(int tableIndex, MySqlAlterDefaultAction action)
    {
        var table = _tables[tableIndex];
        var index = MySqlFindColumnIndex(table.Columns, action.Column);
        if (index < 0)
        {
            MySqlReport("DDL204", "Cannot alter the default of unknown column '" + action.Column.Name + "'.", action.Column.Span);
            return;
        }

        var old = table.Columns[index];
        var columns = table.Columns.ToList();
        columns[index] = new Column(
            old.Name,
            old.SqlType,
            old.IsNullable,
            old.IsPrimaryKey,
            action.Drop ? null : action.DefaultExpression,
            old.IsIdentity);
        MySqlSetTableColumns(tableIndex, columns);
    }

    private void MySqlApplyPrimaryKey(int tableIndex, MySqlPrimaryKeyAction action)
    {
        var table = _tables[tableIndex];
        if (action.Drop)
        {
            MySqlSetTableColumns(
                tableIndex,
                table.Columns.Select(column => new Column(
                    column.Name,
                    column.SqlType,
                    column.IsNullable,
                    false,
                    column.DefaultExpression,
                    column.IsIdentity)).ToArray());
            return;
        }

        if (action.Columns.Count == 0)
        {
            MySqlReport("DDL206", "A PRIMARY KEY must contain at least one column.", action.Span);
            return;
        }

        if (table.Columns.Any(column => column.IsPrimaryKey))
        {
            MySqlReport("DDL206", "A table can have only one PRIMARY KEY constraint.", action.Span);
            return;
        }

        var columns = table.Columns.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in action.Columns)
        {
            if (!seen.Add(MySqlIdentifierKey(identifier)))
            {
                MySqlReport("DDL206", "A PRIMARY KEY cannot list the same column more than once.", identifier.Span);
                return;
            }

            var index = MySqlFindColumnIndex(columns, identifier);
            if (index < 0)
            {
                MySqlReport("DDL204", "PRIMARY KEY refers to unknown column '" + identifier.Name + "'.", identifier.Span);
                return;
            }

            var old = columns[index];
            columns[index] = new Column(
                old.Name,
                old.SqlType,
                false,
                true,
                old.DefaultExpression,
                old.IsIdentity);
        }

        MySqlSetTableColumns(tableIndex, columns);
    }

    private Column MySqlCreateColumn(MySqlDdlColumnDefinition definition) =>
        new Column(
            MySqlDeclaredName(definition.Name),
            definition.SqlType,
            definition.IsPrimaryKey ? false : definition.IsNullable,
            definition.IsPrimaryKey,
            definition.DefaultExpression,
            definition.IsIdentity);

    private void MySqlInsertColumn(
        List<Column> columns,
        Column column,
        MySqlColumnPosition position,
        string tableName,
        int? originalIndex = null)
    {
        if (!position.IsSpecified)
        {
            if (originalIndex.HasValue)
            {
                columns.Insert(Math.Min(originalIndex.Value, columns.Count), column);
            }
            else
            {
                columns.Add(column);
            }

            return;
        }

        if (position.IsFirst)
        {
            columns.Insert(0, column);
            return;
        }

        if (position.After is null)
        {
            MySqlReport("DDL204", "AFTER requires an existing column name.", new SourceSpan(0, 0));
            return;
        }

        var afterIndex = MySqlFindColumnIndex(columns, position.After);
        if (afterIndex < 0)
        {
            MySqlReport("DDL204", "AFTER refers to unknown column '" + position.After.Name + "' on table '" + tableName + "'.", position.After.Span);
            return;
        }

        columns.Insert(afterIndex + 1, column);
    }

    private void MySqlSetTableColumns(int tableIndex, IEnumerable<Column> columns)
    {
        var table = _tables[tableIndex];
        _tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private List<int> MySqlFindTableMatches(SqlQualifiedName identifier, string? schema)
    {
        var matches = new List<int>();
        for (var index = 0; index < _tables.Count; index++)
        {
            var table = _tables[index];
            if (!MySqlMatchesIdentifier(identifier.Name, table.Name) ||
                !string.Equals(schema, table.Schema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(index);
        }

        return matches;
    }

    private int MySqlFindTableByName(string? schema, SqlIdentifier name, int ignoredIndex)
    {
        for (var index = 0; index < _tables.Count; index++)
        {
            if (index != ignoredIndex &&
                string.Equals(schema, _tables[index].Schema, StringComparison.OrdinalIgnoreCase) &&
                MySqlMatchesIdentifier(name, _tables[index].Name))
            {
                return index;
            }
        }

        return -1;
    }

    private int MySqlFindColumnIndex(IReadOnlyList<Column> columns, SqlIdentifier identifier)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (MySqlMatchesIdentifier(identifier, columns[index].Name))
            {
                return index;
            }
        }

        return -1;
    }

    private string? MySqlEffectiveSchema(SqlIdentifier? identifier) =>
        identifier is null ? DefaultDatabase : MySqlDeclaredName(identifier);

    private static bool MySqlMatchesIdentifier(SqlIdentifier identifier, string declared) =>
        identifier.IsQuoted
            ? string.Equals(identifier.Name, declared, StringComparison.Ordinal)
            : string.Equals(identifier.Name, declared, StringComparison.OrdinalIgnoreCase);

    private static string MySqlDeclaredName(SqlIdentifier identifier) =>
        identifier.IsQuoted ? identifier.Name : identifier.Name.ToLowerInvariant();

    private static string MySqlDeclaredName(string name) => name.ToLowerInvariant();

    private static string MySqlIdentifierKey(SqlIdentifier identifier) =>
        identifier.IsQuoted ? "Q:" + identifier.Name : "U:" + identifier.Name.ToLowerInvariant();

    private static string MySqlTableDisplayName(SqlQualifiedName identifier, string? schema) =>
        schema is null ? identifier.Name.Name : schema + "." + identifier.Name.Name;

    private void MySqlReport(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));
}
