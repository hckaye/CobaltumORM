using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

/// <summary>Analyzes SQL Server table-shape changes without connecting to a server.</summary>
public static class SqlServerMigrationAnalyzer
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
            var tables = schema.Tables.ToList();
            var statements = SqlServerScriptClassifier.SplitAndClassify(sql, out var scriptError);
            if (scriptError != null)
            {
                diagnostics.Add(new Diagnostic("DDL300", scriptError.Message, scriptError.Span));
            }

            foreach (var scriptStatement in statements)
            {
                if (scriptStatement.Kind == SqlStatementKind.Empty ||
                    scriptStatement.Kind == SqlStatementKind.Select ||
                    scriptStatement.Kind == SqlStatementKind.DataManipulation ||
                    scriptStatement.Kind == SqlStatementKind.SchemaNeutral)
                {
                    continue;
                }

                if (scriptStatement.Kind == SqlStatementKind.Unsupported)
                {
                    diagnostics.Add(new Diagnostic(
                        "DDL300",
                        "This SQL Server migration statement may change the table schema and is not supported by schema analysis.",
                        scriptStatement.Span));
                    continue;
                }

                var statementDiagnostics = new List<Diagnostic>();
                var tokens = new SqlServerDdlLexer(scriptStatement.Text, statementDiagnostics).Lex();
                var parsed = new SqlServerDdlParser(tokens, scriptStatement.Text, statementDiagnostics).Parse();
                SqlServerAddDiagnostics(diagnostics, statementDiagnostics, scriptStatement.Span.Start);
                if (statementDiagnostics.Count != 0)
                {
                    continue;
                }

                var applier = new SqlServerMigrationApplier(diagnostics);
                foreach (var statement in parsed)
                {
                    if (statement.IsValid)
                    {
                        applier.Apply(statement, tables);
                    }
                }
            }

            return new MigrationAnalysisResult(new DatabaseSchema(tables), diagnostics);
        }
        catch (Exception)
        {
            diagnostics.Add(new Diagnostic(
                "DDL999",
                "The SQL Server migration could not be analyzed because of an internal analysis error.",
                new SourceSpan(0, sql.Length)));
            return new MigrationAnalysisResult(new DatabaseSchema(schema.Tables), diagnostics);
        }
    }

    private static void SqlServerAddDiagnostics(
        ICollection<Diagnostic> diagnostics,
        IEnumerable<Diagnostic> source,
        int offset)
    {
        foreach (var diagnostic in source)
        {
            diagnostics.Add(new Diagnostic(
                diagnostic.Code,
                diagnostic.Message,
                new SourceSpan(offset + diagnostic.Span.Start, diagnostic.Span.Length)));
        }
    }
}

/// <summary>Provides SQL Server migration analysis through the common dialect contract.</summary>
public sealed class SqlServerSchemaMigrationAnalyzer : ISchemaMigrationAnalyzer
{
    public MigrationAnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        SqlServerMigrationAnalyzer.Analyze(schema, sql);
}

/// <summary>Applies the supported SQL Server DDL operations to a compile-time schema.</summary>
internal sealed class SqlServerMigrationApplier
{
    private readonly List<Diagnostic> _sqlServerDiagnostics;
    private readonly SqlServerTypeMapper _sqlServerTypes = new SqlServerTypeMapper();

    internal SqlServerMigrationApplier(List<Diagnostic> diagnostics)
    {
        _sqlServerDiagnostics = diagnostics;
    }

    internal void Apply(SqlServerDdlStatement statement, List<Table> tables)
    {
        if (statement is SqlServerCreateTableStatement create)
        {
            SqlServerApplyCreate(create, tables);
            return;
        }

        if (statement is SqlServerDropTableStatement drop)
        {
            SqlServerApplyDrop(drop, tables);
            return;
        }

        if (statement is SqlServerAlterTableStatement alter)
        {
            SqlServerApplyAlter(alter, tables);
            return;
        }

        if (statement is SqlServerRenameStatement rename)
        {
            SqlServerApplyRename(rename, tables);
        }
    }

    private void SqlServerApplyCreate(SqlServerCreateTableStatement statement, List<Table> tables)
    {
        var schema = SqlServerDeclaredSchema(statement.Table.Schema);
        if (SqlServerFindTable(tables, schema, statement.Table.Name.Name) != null)
        {
            SqlServerReport("DDL200", $"Table '{SqlServerTableDisplay(statement.Table)}' already exists.", statement.Table.Span);
            return;
        }

        if (statement.Columns.Count == 0)
        {
            SqlServerReport("DDL208", "CREATE TABLE must declare at least one column.", statement.Span);
            return;
        }

        var columns = new List<Column>();
        var valid = true;
        foreach (var definition in statement.Columns)
        {
            if (!_sqlServerTypes.TryMap(definition.SqlType, out _))
            {
                SqlServerReport(
                    "DDL205",
                    $"Unsupported SQL Server type '{definition.SqlType}'.",
                    definition.Span);
                valid = false;
            }

            if (SqlServerFindColumn(columns, definition.Name.Name) != null)
            {
                SqlServerReport(
                    "DDL203",
                    $"Column '{definition.Name.Name}' is defined more than once.",
                    definition.Name.Span);
                valid = false;
            }

            if (definition.IsIdentity && !SqlServerIsSupportedIdentityType(definition.SqlType))
            {
                SqlServerReport(
                    "DDL206",
                    $"Identity column '{definition.Name.Name}' must use smallint, int, or bigint.",
                    definition.Name.Span);
                valid = false;
            }

            columns.Add(new Column(
                definition.Name.Name,
                definition.SqlType,
                definition.IsPrimaryKey ? false : definition.IsNullable,
                definition.IsPrimaryKey,
                definition.DefaultExpression,
                definition.IsIdentity));
        }

        if (statement.PrimaryKeys.Count > 1 ||
            statement.PrimaryKeys.Count != 0 && columns.Any(item => item.IsPrimaryKey))
        {
            SqlServerReport(
                "DDL206",
                "A CREATE TABLE statement cannot declare more than one PRIMARY KEY constraint.",
                statement.Span);
            valid = false;
        }

        foreach (var primaryKey in statement.PrimaryKeys)
        {
            if (primaryKey.Count == 0)
            {
                SqlServerReport("DDL204", "A PRIMARY KEY must contain at least one column.", statement.Span);
                valid = false;
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var identifier in primaryKey)
            {
                var index = SqlServerFindColumnIndex(columns, identifier.Name);
                if (index < 0)
                {
                    SqlServerReport(
                        "DDL204",
                        $"PRIMARY KEY refers to unknown column '{identifier.Name}'.",
                        identifier.Span);
                    valid = false;
                    continue;
                }

                if (!seen.Add(columns[index].Name))
                {
                    SqlServerReport(
                        "DDL206",
                        "A PRIMARY KEY cannot list the same column more than once.",
                        identifier.Span);
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

        foreach (var defaultConstraint in statement.Defaults)
        {
            var index = SqlServerFindColumnIndex(columns, defaultConstraint.Column.Name);
            if (index < 0)
            {
                SqlServerReport(
                    "DDL204",
                    $"DEFAULT refers to unknown column '{defaultConstraint.Column.Name}'.",
                    defaultConstraint.Column.Span);
                valid = false;
                continue;
            }

            var column = columns[index];
            columns[index] = new Column(
                column.Name,
                column.SqlType,
                column.IsNullable,
                column.IsPrimaryKey,
                defaultConstraint.Expression,
                column.IsIdentity);
        }

        if (valid)
        {
            tables.Add(new Table(statement.Table.Name.Name, columns, schema));
        }
    }

    private void SqlServerApplyDrop(SqlServerDropTableStatement statement, List<Table> tables)
    {
        foreach (var tableName in statement.Tables)
        {
            var schema = SqlServerDeclaredSchema(tableName.Schema);
            var table = SqlServerFindTable(tables, schema, tableName.Name.Name);
            if (table == null)
            {
                if (!statement.IfExists)
                {
                    SqlServerReport(
                        "DDL201",
                        $"Cannot drop unknown table '{SqlServerTableDisplay(tableName)}'.",
                        tableName.Span);
                }

                continue;
            }

            tables.Remove(table);
        }
    }

    private void SqlServerApplyAlter(SqlServerAlterTableStatement statement, List<Table> tables)
    {
        var schema = SqlServerDeclaredSchema(statement.Table.Schema);
        var table = SqlServerFindTable(tables, schema, statement.Table.Name.Name);
        if (table == null)
        {
            SqlServerReport(
                "DDL202",
                $"Cannot alter unknown table '{SqlServerTableDisplay(statement.Table)}'.",
                statement.Table.Span);
            return;
        }

        var tableIndex = tables.IndexOf(table);
        foreach (var action in statement.Actions)
        {
            table = tables[tableIndex];
            if (action is SqlServerAddColumnAction add)
            {
                SqlServerApplyAddColumn(tableIndex, table, add, tables);
            }
            else if (action is SqlServerDropColumnAction drop)
            {
                SqlServerApplyDropColumn(tableIndex, table, drop, tables);
            }
            else if (action is SqlServerAlterColumnAction alterColumn)
            {
                SqlServerApplyAlterColumn(tableIndex, table, alterColumn, tables);
            }
            else if (action is SqlServerPrimaryKeyConstraintAction primaryKey)
            {
                SqlServerApplyPrimaryKey(tableIndex, table, primaryKey, tables);
            }
            else if (action is SqlServerDefaultConstraintAction defaultConstraint)
            {
                SqlServerApplyDefault(tableIndex, table, defaultConstraint, tables);
            }
            else if (action is SqlServerDropConstraintAction ||
                     action is SqlServerSchemaNeutralConstraintAction)
            {
                // Constraint names are not part of DatabaseSchema. The table shape is unchanged.
            }
        }
    }

    private void SqlServerApplyAddColumn(
        int tableIndex,
        Table table,
        SqlServerAddColumnAction action,
        List<Table> tables)
    {
        var column = action.Column;
        if (!_sqlServerTypes.TryMap(column.SqlType, out _))
        {
            SqlServerReport("DDL205", $"Unsupported SQL Server type '{column.SqlType}'.", column.Span);
            return;
        }

        if (SqlServerFindColumn(table.Columns, column.Name.Name) != null)
        {
            SqlServerReport(
                "DDL203",
                $"Column '{column.Name.Name}' already exists on table '{SqlServerTableDisplay(table)}'.",
                column.Name.Span);
            return;
        }

        if (column.IsIdentity && !SqlServerIsSupportedIdentityType(column.SqlType))
        {
            SqlServerReport(
                "DDL206",
                $"Identity column '{column.Name.Name}' must use smallint, int, or bigint.",
                column.Name.Span);
            return;
        }

        if (column.IsPrimaryKey && table.Columns.Any(item => item.IsPrimaryKey))
        {
            SqlServerReport("DDL206", "A table can have only one PRIMARY KEY constraint.", column.Span);
            return;
        }

        var columns = table.Columns.ToList();
        columns.Add(new Column(
            column.Name.Name,
            column.SqlType,
            column.IsPrimaryKey ? false : column.IsNullable,
            column.IsPrimaryKey,
            column.DefaultExpression,
            column.IsIdentity));
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqlServerApplyDropColumn(
        int tableIndex,
        Table table,
        SqlServerDropColumnAction action,
        List<Table> tables)
    {
        var index = SqlServerFindColumnIndex(table.Columns, action.Column.Name);
        if (index < 0)
        {
            SqlServerReport(
                "DDL204",
                $"Cannot drop unknown column '{action.Column.Name}' from table '{SqlServerTableDisplay(table)}'.",
                action.Column.Span);
            return;
        }

        var columns = table.Columns.Where((_, columnIndex) => columnIndex != index).ToList();
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqlServerApplyAlterColumn(
        int tableIndex,
        Table table,
        SqlServerAlterColumnAction action,
        List<Table> tables)
    {
        if (!_sqlServerTypes.TryMap(action.SqlType, out _))
        {
            SqlServerReport("DDL205", $"Unsupported SQL Server type '{action.SqlType}'.", action.Span);
            return;
        }

        var index = SqlServerFindColumnIndex(table.Columns, action.Column.Name);
        if (index < 0)
        {
            SqlServerReport(
                "DDL204",
                $"Cannot alter unknown column '{action.Column.Name}' on table '{SqlServerTableDisplay(table)}'.",
                action.Column.Span);
            return;
        }

        var oldColumn = table.Columns[index];
        if (oldColumn.IsPrimaryKey && action.Nullable == true)
        {
            SqlServerReport("DDL206", "A PRIMARY KEY column cannot be made nullable.", action.Column.Span);
            return;
        }

        var columns = table.Columns.ToList();
        columns[index] = new Column(
            oldColumn.Name,
            action.SqlType,
            action.Nullable ?? oldColumn.IsNullable,
            oldColumn.IsPrimaryKey,
            oldColumn.DefaultExpression,
            oldColumn.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqlServerApplyPrimaryKey(
        int tableIndex,
        Table table,
        SqlServerPrimaryKeyConstraintAction action,
        List<Table> tables)
    {
        if (table.Columns.Any(item => item.IsPrimaryKey))
        {
            SqlServerReport("DDL206", "A table can have only one PRIMARY KEY constraint.", action.Span);
            return;
        }

        var columns = table.Columns.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = action.Columns.Count != 0;
        foreach (var identifier in action.Columns)
        {
            var index = SqlServerFindColumnIndex(columns, identifier.Name);
            if (index < 0)
            {
                SqlServerReport(
                    "DDL204",
                    $"PRIMARY KEY refers to unknown column '{identifier.Name}'.",
                    identifier.Span);
                valid = false;
            }
            else if (!seen.Add(columns[index].Name))
            {
                SqlServerReport("DDL206", "A PRIMARY KEY cannot list the same column more than once.", identifier.Span);
                valid = false;
            }
            else
            {
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
            if (action.Columns.Count == 0)
            {
                SqlServerReport("DDL204", "A PRIMARY KEY must contain at least one column.", action.Span);
            }

            return;
        }

        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqlServerApplyDefault(
        int tableIndex,
        Table table,
        SqlServerDefaultConstraintAction action,
        List<Table> tables)
    {
        var index = SqlServerFindColumnIndex(table.Columns, action.Column.Name);
        if (index < 0)
        {
            SqlServerReport(
                "DDL204",
                $"DEFAULT refers to unknown column '{action.Column.Name}'.",
                action.Column.Span);
            return;
        }

        var column = table.Columns[index];
        var columns = table.Columns.ToList();
        columns[index] = new Column(
            column.Name,
            column.SqlType,
            column.IsNullable,
            column.IsPrimaryKey,
            action.Expression,
            column.IsIdentity);
        tables[tableIndex] = new Table(table.Name, columns, table.Schema);
    }

    private void SqlServerApplyRename(SqlServerRenameStatement statement, List<Table> tables)
    {
        var nameParts = SqlServerParseRenameParts(statement.ObjectName, statement.Span);
        if (nameParts == null)
        {
            return;
        }

        var objectType = statement.ObjectType;
        if (string.IsNullOrEmpty(objectType))
        {
            objectType = nameParts.Count == 3 ? "COLUMN" : "OBJECT";
        }

        if (string.Equals(objectType, "OBJECT", StringComparison.OrdinalIgnoreCase))
        {
            if (nameParts.Count == 1)
            {
                nameParts.Insert(0, "dbo");
            }

            if (nameParts.Count != 2)
            {
                SqlServerReport("DDL300", "SQL Server sp_rename object names must contain a schema and object name.", statement.Span);
                return;
            }

            var table = SqlServerFindTable(tables, nameParts[0], nameParts[1]);
            if (table == null)
            {
                SqlServerReport("DDL204", $"Cannot rename unknown table '{nameParts[0]}.{nameParts[1]}'.", statement.Span);
                return;
            }

            if (SqlServerFindTable(tables, SqlServerDeclaredSchema(table.Schema), statement.NewName) != null)
            {
                SqlServerReport("DDL203", $"Table '{statement.NewName}' already exists.", statement.Span);
                return;
            }

            var index = tables.IndexOf(table);
            tables[index] = new Table(statement.NewName, table.Columns, table.Schema);
            return;
        }

        if (string.Equals(objectType, "COLUMN", StringComparison.OrdinalIgnoreCase))
        {
            if (nameParts.Count == 2)
            {
                nameParts.Insert(0, "dbo");
            }

            if (nameParts.Count != 3)
            {
                SqlServerReport(
                    "DDL300",
                    "SQL Server sp_rename column names must contain a schema, table, and column name.",
                    statement.Span);
                return;
            }

            var table = SqlServerFindTable(tables, nameParts[0], nameParts[1]);
            if (table == null)
            {
                SqlServerReport("DDL204", $"Cannot rename an unknown table '{nameParts[0]}.{nameParts[1]}'.", statement.Span);
                return;
            }

            var columnIndex = SqlServerFindColumnIndex(table.Columns, nameParts[2]);
            if (columnIndex < 0)
            {
                SqlServerReport("DDL204", $"Cannot rename unknown column '{nameParts[2]}'.", statement.Span);
                return;
            }

            if (SqlServerFindColumn(table.Columns, statement.NewName) != null)
            {
                SqlServerReport("DDL203", $"Column '{statement.NewName}' already exists.", statement.Span);
                return;
            }

            var columns = table.Columns.ToList();
            var oldColumn = columns[columnIndex];
            columns[columnIndex] = new Column(
                statement.NewName,
                oldColumn.SqlType,
                oldColumn.IsNullable,
                oldColumn.IsPrimaryKey,
                oldColumn.DefaultExpression,
                oldColumn.IsIdentity);
            tables[tables.IndexOf(table)] = new Table(table.Name, columns, table.Schema);
            return;
        }

        SqlServerReport(
            "DDL300",
            $"SQL Server sp_rename object type '{objectType}' is not supported by schema analysis.",
            statement.Span);
    }

    private List<string>? SqlServerParseRenameParts(string value, SourceSpan span)
    {
        var diagnostics = new List<Diagnostic>();
        var tokens = new SqlServerDdlLexer(value, diagnostics).Lex();
        if (diagnostics.Count != 0)
        {
            SqlServerReport("DDL100", "sp_rename contains an invalid qualified object name.", span);
            return null;
        }

        var parts = new List<string>();
        var position = 0;
        while (tokens[position].Kind != SqlServerDdlTokenKind.End)
        {
            var token = tokens[position];
            if (!token.SqlServerIsIdentifier())
            {
                SqlServerReport("DDL100", "sp_rename object names must be qualified identifiers.", span);
                return null;
            }

            parts.Add(token.Value ?? token.Text);
            position++;
            if (tokens[position].Kind == SqlServerDdlTokenKind.End)
            {
                break;
            }

            if (tokens[position].Kind != SqlServerDdlTokenKind.Symbol || tokens[position].Text != ".")
            {
                SqlServerReport("DDL100", "sp_rename object names must be qualified identifiers.", span);
                return null;
            }

            position++;
        }

        if (parts.Count == 0 || parts.Count > 3)
        {
            SqlServerReport("DDL300", "sp_rename accepts at most a schema, table, and column name.", span);
            return null;
        }

        return parts;
    }

    private static Table? SqlServerFindTable(List<Table> tables, string schema, string name) =>
        tables.FirstOrDefault(table =>
            string.Equals(SqlServerDeclaredSchema(table.Schema), schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Column? SqlServerFindColumn(IEnumerable<Column> columns, string name) =>
        columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int SqlServerFindColumnIndex(IReadOnlyList<Column> columns, string name)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string SqlServerDeclaredSchema(SqlServerDdlIdentifier? identifier) =>
        identifier == null || string.IsNullOrEmpty(identifier.Name) ? "dbo" : identifier.Name;

    private static string SqlServerDeclaredSchema(string? schema) =>
        string.IsNullOrEmpty(schema) ? "dbo" : schema!;

    private static string SqlServerTableDisplay(SqlServerDdlQualifiedName table) =>
        (table.Schema == null ? "dbo" : table.Schema.Name) + "." + table.Name.Name;

    private static string SqlServerTableDisplay(Table table) =>
        SqlServerDeclaredSchema(table.Schema) + "." + table.Name;

    private static bool SqlServerIsSupportedIdentityType(string sqlType)
    {
        var normalized = sqlType.Trim().ToLowerInvariant();
        var open = normalized.IndexOf('(');
        var baseType = open < 0 ? normalized : normalized.Substring(0, open).Trim();
        return baseType == "smallint" || baseType == "int" || baseType == "integer" || baseType == "bigint";
    }

    private void SqlServerReport(string code, string message, SourceSpan span) =>
        _sqlServerDiagnostics.Add(new Diagnostic(code, message, span));
}

/// <summary>Applies SQL Server schema-changing statements one script at a time.</summary>
public static class SqlServerSchemaBuilder
{
    public static MigrationAnalysisResult ApplyScript(DatabaseSchema schema, string sql) =>
        SqlServerMigrationAnalyzer.Analyze(schema, sql);
}
