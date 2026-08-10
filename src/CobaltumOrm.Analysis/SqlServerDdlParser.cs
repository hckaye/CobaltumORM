using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

internal sealed class SqlServerDdlIdentifier
{
    internal SqlServerDdlIdentifier(string name, bool isQuoted, SourceSpan span)
    {
        Name = name;
        IsQuoted = isQuoted;
        Span = span;
    }

    internal string Name { get; }
    internal bool IsQuoted { get; }
    internal SourceSpan Span { get; }
}

internal sealed class SqlServerDdlQualifiedName
{
    internal SqlServerDdlQualifiedName(
        SqlServerDdlIdentifier? schema,
        SqlServerDdlIdentifier name,
        SourceSpan span)
    {
        Schema = schema;
        Name = name;
        Span = span;
    }

    internal SqlServerDdlIdentifier? Schema { get; }
    internal SqlServerDdlIdentifier Name { get; }
    internal SourceSpan Span { get; }
}

internal abstract class SqlServerDdlStatement
{
    protected SqlServerDdlStatement(SourceSpan span, bool isValid)
    {
        Span = span;
        IsValid = isValid;
    }

    internal SourceSpan Span { get; }
    internal bool IsValid { get; }
}

internal sealed class SqlServerCreateTableStatement : SqlServerDdlStatement
{
    internal SqlServerCreateTableStatement(
        SqlServerDdlQualifiedName table,
        IReadOnlyList<SqlServerDdlColumnDefinition> columns,
        IReadOnlyList<IReadOnlyList<SqlServerDdlIdentifier>> primaryKeys,
        IReadOnlyList<SqlServerDdlDefaultConstraint> defaults,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        Columns = columns;
        PrimaryKeys = primaryKeys;
        Defaults = defaults;
    }

    internal SqlServerDdlQualifiedName Table { get; }
    internal IReadOnlyList<SqlServerDdlColumnDefinition> Columns { get; }
    internal IReadOnlyList<IReadOnlyList<SqlServerDdlIdentifier>> PrimaryKeys { get; }
    internal IReadOnlyList<SqlServerDdlDefaultConstraint> Defaults { get; }
}

internal sealed class SqlServerDropTableStatement : SqlServerDdlStatement
{
    internal SqlServerDropTableStatement(
        IReadOnlyList<SqlServerDdlQualifiedName> tables,
        bool ifExists,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Tables = tables;
        IfExists = ifExists;
    }

    internal IReadOnlyList<SqlServerDdlQualifiedName> Tables { get; }
    internal bool IfExists { get; }
}

internal sealed class SqlServerAlterTableStatement : SqlServerDdlStatement
{
    internal SqlServerAlterTableStatement(
        SqlServerDdlQualifiedName table,
        IReadOnlyList<SqlServerDdlAction> actions,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        Actions = actions;
    }

    internal SqlServerDdlQualifiedName Table { get; }
    internal IReadOnlyList<SqlServerDdlAction> Actions { get; }
}

internal sealed class SqlServerRenameStatement : SqlServerDdlStatement
{
    internal SqlServerRenameStatement(
        string objectName,
        string newName,
        string? objectType,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        ObjectName = objectName;
        NewName = newName;
        ObjectType = objectType;
    }

    internal string ObjectName { get; }
    internal string NewName { get; }
    internal string? ObjectType { get; }
}

internal sealed class SqlServerDdlColumnDefinition
{
    internal SqlServerDdlColumnDefinition(
        SqlServerDdlIdentifier name,
        string sqlType,
        bool isNullable,
        bool isPrimaryKey,
        string? defaultExpression,
        bool isIdentity,
        SourceSpan span)
    {
        Name = name;
        SqlType = sqlType;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        DefaultExpression = defaultExpression;
        IsIdentity = isIdentity;
        Span = span;
    }

    internal SqlServerDdlIdentifier Name { get; }
    internal string SqlType { get; }
    internal bool IsNullable { get; }
    internal bool IsPrimaryKey { get; }
    internal string? DefaultExpression { get; }
    internal bool IsIdentity { get; }
    internal SourceSpan Span { get; }
}

internal abstract class SqlServerDdlAction
{
    protected SqlServerDdlAction(SourceSpan span)
    {
        Span = span;
    }

    internal SourceSpan Span { get; }
}

internal sealed class SqlServerAddColumnAction : SqlServerDdlAction
{
    internal SqlServerAddColumnAction(SqlServerDdlColumnDefinition column, SourceSpan span)
        : base(span)
    {
        Column = column;
    }

    internal SqlServerDdlColumnDefinition Column { get; }
}

internal sealed class SqlServerDropColumnAction : SqlServerDdlAction
{
    internal SqlServerDropColumnAction(SqlServerDdlIdentifier column, SourceSpan span)
        : base(span)
    {
        Column = column;
    }

    internal SqlServerDdlIdentifier Column { get; }
}

internal sealed class SqlServerAlterColumnAction : SqlServerDdlAction
{
    internal SqlServerAlterColumnAction(
        SqlServerDdlIdentifier column,
        string sqlType,
        bool? nullable,
        SourceSpan span)
        : base(span)
    {
        Column = column;
        SqlType = sqlType;
        Nullable = nullable;
    }

    internal SqlServerDdlIdentifier Column { get; }
    internal string SqlType { get; }
    internal bool? Nullable { get; }
}

internal sealed class SqlServerPrimaryKeyConstraintAction : SqlServerDdlAction
{
    internal SqlServerPrimaryKeyConstraintAction(
        IReadOnlyList<SqlServerDdlIdentifier> columns,
        SourceSpan span)
        : base(span)
    {
        Columns = columns;
    }

    internal IReadOnlyList<SqlServerDdlIdentifier> Columns { get; }
}

internal sealed class SqlServerDefaultConstraintAction : SqlServerDdlAction
{
    internal SqlServerDefaultConstraintAction(
        SqlServerDdlIdentifier column,
        string expression,
        SourceSpan span)
        : base(span)
    {
        Column = column;
        Expression = expression;
    }

    internal SqlServerDdlIdentifier Column { get; }
    internal string Expression { get; }
}

internal sealed class SqlServerDropConstraintAction : SqlServerDdlAction
{
    internal SqlServerDropConstraintAction(SqlServerDdlIdentifier constraint, SourceSpan span)
        : base(span)
    {
        Constraint = constraint;
    }

    internal SqlServerDdlIdentifier Constraint { get; }
}

internal sealed class SqlServerSchemaNeutralConstraintAction : SqlServerDdlAction
{
    internal SqlServerSchemaNeutralConstraintAction(SourceSpan span)
        : base(span)
    {
    }
}

internal sealed class SqlServerDdlDefaultConstraint
{
    internal SqlServerDdlDefaultConstraint(
        SqlServerDdlIdentifier column,
        string expression,
        SourceSpan span)
    {
        Column = column;
        Expression = expression;
        Span = span;
    }

    internal SqlServerDdlIdentifier Column { get; }
    internal string Expression { get; }
    internal SourceSpan Span { get; }
}

internal sealed class SqlServerDdlParser
{
    private static readonly HashSet<string> SqlServerColumnConstraintKeywords = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "COLLATE",
        "CONSTRAINT",
        "DEFAULT",
        "FILESTREAM",
        "GENERATED",
        "IDENTITY",
        "MASKED",
        "NOT",
        "NULL",
        "PRIMARY",
        "REFERENCES",
        "ROWGUIDCOL",
        "SPARSE",
        "UNIQUE",
        "CHECK",
        "WITH",
    };

    private readonly IReadOnlyList<SqlServerDdlToken> _sqlServerTokens;
    private readonly string _sqlServerText;
    private readonly List<Diagnostic> _sqlServerDiagnostics;
    private int _sqlServerPosition;

    internal SqlServerDdlParser(
        IReadOnlyList<SqlServerDdlToken> tokens,
        string sql,
        List<Diagnostic> diagnostics)
    {
        _sqlServerTokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _sqlServerText = sql ?? throw new ArgumentNullException(nameof(sql));
        _sqlServerDiagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    internal IReadOnlyList<SqlServerDdlStatement> Parse()
    {
        var statements = new List<SqlServerDdlStatement>();
        while (!SqlServerAtEnd)
        {
            if (SqlServerMatchSymbol(";"))
            {
                continue;
            }

            var diagnosticCount = _sqlServerDiagnostics.Count;
            var start = SqlServerCurrent.Span.Start;
            SqlServerDdlStatement? statement;
            if (SqlServerIs("CREATE"))
            {
                statement = SqlServerParseCreateTable(start, diagnosticCount);
            }
            else if (SqlServerIs("DROP"))
            {
                statement = SqlServerParseDropTable(start, diagnosticCount);
            }
            else if (SqlServerIs("ALTER"))
            {
                statement = SqlServerParseAlterTable(start, diagnosticCount);
            }
            else if (SqlServerIs("EXEC") || SqlServerIs("EXECUTE"))
            {
                statement = SqlServerParseRename(start, diagnosticCount);
            }
            else
            {
                SqlServerReport(
                    "DDL300",
                    "This SQL Server migration statement is not supported by schema analysis.",
                    SqlServerCurrent.Span);
                SqlServerSkipToStatementEnd();
                statement = null;
            }

            if (statement != null)
            {
                statements.Add(statement);
            }

            SqlServerMatchSymbol(";");
        }

        return statements;
    }

    private SqlServerCreateTableStatement SqlServerParseCreateTable(int start, int diagnosticCount)
    {
        SqlServerAdvance();
        SqlServerExpectKeyword("TABLE", "Expected TABLE after CREATE.");
        if (SqlServerMatchKeyword("IF"))
        {
            SqlServerReport(
                "DDL300",
                "SQL Server does not support CREATE TABLE IF NOT EXISTS syntax in migration analysis.",
                SqlServerPrevious.Span);
            SqlServerExpectKeyword("NOT", "Expected NOT after IF in CREATE TABLE.");
            SqlServerExpectKeyword("EXISTS", "Expected EXISTS after IF NOT in CREATE TABLE.");
        }

        var table = SqlServerParseQualifiedName("Expected a table name after CREATE TABLE.");
        SqlServerExpectSymbol("(", "Expected '(' after the CREATE TABLE name.");
        var columns = new List<SqlServerDdlColumnDefinition>();
        var primaryKeys = new List<IReadOnlyList<SqlServerDdlIdentifier>>();
        var defaults = new List<SqlServerDdlDefaultConstraint>();
        while (!SqlServerAtEnd && !SqlServerIsSymbol(")"))
        {
            if (SqlServerMatchSymbol(","))
            {
                continue;
            }

            if (SqlServerIsTableConstraintStart())
            {
                SqlServerParseTableConstraint(primaryKeys, defaults);
            }
            else
            {
                var column = SqlServerParseColumnDefinition();
                if (column != null)
                {
                    columns.Add(column);
                }
            }

            if (!SqlServerMatchSymbol(",") && !SqlServerIsSymbol(")") && !SqlServerAtEnd)
            {
                SqlServerReport(
                    "DDL100",
                    "Expected ',' or ')' after a SQL Server table definition.",
                    SqlServerCurrent.Span);
                SqlServerSkipToTableItemEnd();
            }
        }

        var close = SqlServerExpectSymbol(")", "Expected ')' after CREATE TABLE definitions.");
        SqlServerSkipCreateTableOptions();
        var span = SqlServerFromBounds(start, SqlServerEndOf(close.Span));
        var isValid = _sqlServerDiagnostics.Count == diagnosticCount;
        return new SqlServerCreateTableStatement(table, columns, primaryKeys, defaults, span, isValid);
    }

    private SqlServerDropTableStatement SqlServerParseDropTable(int start, int diagnosticCount)
    {
        SqlServerAdvance();
        if (SqlServerMatchKeyword("INDEX"))
        {
            SqlServerSkipToStatementEnd();
            return new SqlServerDropTableStatement(
                Array.Empty<SqlServerDdlQualifiedName>(),
                false,
                SqlServerFromBounds(start, SqlServerEndOf(SqlServerPrevious.Span)),
                false);
        }

        SqlServerExpectKeyword("TABLE", "Expected TABLE after DROP.");
        var ifExists = false;
        if (SqlServerMatchKeyword("IF"))
        {
            SqlServerExpectKeyword("EXISTS", "Expected EXISTS after IF in DROP TABLE.");
            ifExists = true;
        }

        var tables = new List<SqlServerDdlQualifiedName>();
        while (!SqlServerAtEnd && !SqlServerIsSymbol(";"))
        {
            tables.Add(SqlServerParseQualifiedName("Expected a table name after DROP TABLE."));
            if (!SqlServerMatchSymbol(","))
            {
                break;
            }
        }

        SqlServerSkipToStatementEnd();
        var span = SqlServerFromBounds(start, SqlServerStatementEnd(start));
        return new SqlServerDropTableStatement(
            tables,
            ifExists,
            span,
            _sqlServerDiagnostics.Count == diagnosticCount);
    }

    private SqlServerAlterTableStatement SqlServerParseAlterTable(int start, int diagnosticCount)
    {
        SqlServerAdvance();
        SqlServerExpectKeyword("TABLE", "Expected TABLE after ALTER.");
        var table = SqlServerParseQualifiedName("Expected a table name after ALTER TABLE.");
        var actions = new List<SqlServerDdlAction>();
        while (!SqlServerAtEnd && !SqlServerIsSymbol(";"))
        {
            if (SqlServerMatchSymbol(","))
            {
                continue;
            }

            if (SqlServerMatchKeyword("ADD"))
            {
                SqlServerParseAlterAdd(actions);
            }
            else if (SqlServerMatchKeyword("ALTER"))
            {
                SqlServerParseAlterColumn(actions);
            }
            else if (SqlServerMatchKeyword("DROP"))
            {
                SqlServerParseAlterDrop(actions);
            }
            else
            {
                SqlServerReport(
                    "DDL300",
                    "This ALTER TABLE action is not supported by SQL Server schema analysis.",
                    SqlServerCurrent.Span);
                SqlServerSkipToStatementEnd();
            }

            if (!SqlServerIsSymbol(",") && !SqlServerAtEnd && !SqlServerIsSymbol(";"))
            {
                SqlServerReport(
                    "DDL100",
                    "Expected ',' or the end of an ALTER TABLE statement.",
                    SqlServerCurrent.Span);
                SqlServerSkipToStatementEnd();
            }
        }

        var span = SqlServerFromBounds(start, SqlServerStatementEnd(start));
        return new SqlServerAlterTableStatement(
            table,
            actions,
            span,
            _sqlServerDiagnostics.Count == diagnosticCount);
    }

    private void SqlServerParseAlterAdd(List<SqlServerDdlAction> actions)
    {
        var start = SqlServerCurrent.Span.Start;
        if (SqlServerIs("CONSTRAINT") || SqlServerIs("PRIMARY") || SqlServerIs("UNIQUE") ||
            SqlServerIs("FOREIGN") || SqlServerIs("CHECK") || SqlServerIs("DEFAULT"))
        {
            actions.Add(SqlServerParseConstraintAction(start));
            return;
        }

        SqlServerMatchKeyword("COLUMN");
        if (SqlServerMatchSymbol("("))
        {
            while (!SqlServerAtEnd && !SqlServerIsSymbol(")"))
            {
                if (SqlServerMatchSymbol(","))
                {
                    continue;
                }

                var column = SqlServerParseColumnDefinition();
                if (column != null)
                {
                    actions.Add(new SqlServerAddColumnAction(column, column.Span));
                }
            }

            SqlServerExpectSymbol(")", "Expected ')' after ALTER TABLE ADD columns.");
            return;
        }

        var single = SqlServerParseColumnDefinition();
        if (single != null)
        {
            actions.Add(new SqlServerAddColumnAction(single, single.Span));
        }
    }

    private SqlServerDdlAction SqlServerParseConstraintAction(int start)
    {
        if (SqlServerMatchKeyword("CONSTRAINT") && SqlServerCurrent.SqlServerIsIdentifier())
        {
            SqlServerAdvance();
        }

        if (SqlServerIs("PRIMARY"))
        {
            var columns = SqlServerParsePrimaryKeyColumns();
            SqlServerSkipPrimaryKeyOptions();
            return new SqlServerPrimaryKeyConstraintAction(
                columns,
                SqlServerFromBounds(start, SqlServerStatementEnd(start)));
        }

        if (SqlServerIs("DEFAULT"))
        {
            var defaultConstraint = SqlServerParseDefaultConstraint();
            return new SqlServerDefaultConstraintAction(
                defaultConstraint.Column,
                defaultConstraint.Expression,
                SqlServerFromBounds(start, SqlServerStatementEnd(start)));
        }

        if (SqlServerIs("UNIQUE") || SqlServerIs("FOREIGN") || SqlServerIs("CHECK"))
        {
            SqlServerSkipToActionEnd();
            return new SqlServerSchemaNeutralConstraintAction(
                SqlServerFromBounds(start, SqlServerStatementEnd(start)));
        }

        SqlServerReport(
            "DDL300",
            "This SQL Server table constraint is not supported by schema analysis.",
            SqlServerCurrent.Span);
        SqlServerSkipToActionEnd();
        return new SqlServerSchemaNeutralConstraintAction(
            SqlServerFromBounds(start, SqlServerStatementEnd(start)));
    }

    private void SqlServerParseAlterColumn(List<SqlServerDdlAction> actions)
    {
        var start = SqlServerCurrent.Span.Start;
        SqlServerMatchKeyword("COLUMN");
        var column = SqlServerParseIdentifier("Expected a column name after ALTER COLUMN.");
        var type = SqlServerReadType();
        bool? nullable = null;
        while (!SqlServerAtEnd && !SqlServerIsSymbol(",") && !SqlServerIsSymbol(";"))
        {
            if (SqlServerMatchKeyword("NULL"))
            {
                nullable = true;
            }
            else if (SqlServerMatchKeyword("NOT"))
            {
                SqlServerExpectKeyword("NULL", "Expected NULL after NOT in ALTER COLUMN.");
                nullable = false;
            }
            else
            {
                SqlServerSkipConstraintTail();
            }
        }

        actions.Add(new SqlServerAlterColumnAction(
            column,
            type,
            nullable,
            SqlServerFromBounds(start, SqlServerStatementEnd(start))));
    }

    private void SqlServerParseAlterDrop(List<SqlServerDdlAction> actions)
    {
        var start = SqlServerCurrent.Span.Start;
        if (SqlServerMatchKeyword("CONSTRAINT"))
        {
            var constraint = SqlServerParseIdentifier("Expected a constraint name after DROP CONSTRAINT.");
            actions.Add(new SqlServerDropConstraintAction(
                constraint,
                SqlServerFromBounds(start, SqlServerEndOf(constraint.Span))));
            return;
        }

        SqlServerMatchKeyword("COLUMN");
        while (!SqlServerAtEnd && !SqlServerIsSymbol(",") && !SqlServerIsSymbol(";"))
        {
            var column = SqlServerParseIdentifier("Expected a column name after DROP COLUMN.");
            actions.Add(new SqlServerDropColumnAction(
                column,
                SqlServerFromBounds(start, SqlServerEndOf(column.Span))));
            if (!SqlServerMatchSymbol(","))
            {
                break;
            }
        }
    }

    private SqlServerRenameStatement SqlServerParseRename(int start, int diagnosticCount)
    {
        SqlServerAdvance();
        var procedureStart = SqlServerParseIdentifier("Expected a procedure name after EXEC.");
        var procedure = procedureStart.Name;
        if (SqlServerMatchSymbol("."))
        {
            var procedureName = SqlServerParseIdentifier("Expected a procedure name after '.'.");
            procedure = procedureName.Name;
        }

        if (!string.Equals(procedure, "sp_rename", StringComparison.OrdinalIgnoreCase))
        {
            SqlServerReport(
                "DDL300",
                "Only sp_rename is supported among SQL Server executable schema statements.",
                procedureStart.Span);
            SqlServerSkipToStatementEnd();
            return new SqlServerRenameStatement(
                string.Empty,
                string.Empty,
                null,
                SqlServerFromBounds(start, SqlServerStatementEnd(start)),
                false);
        }

        string? objectName = null;
        string? newName = null;
        string? objectType = null;
        var positional = 0;
        while (!SqlServerAtEnd && !SqlServerIsSymbol(";"))
        {
            if (SqlServerMatchSymbol(","))
            {
                continue;
            }

            string? parameterName = null;
            if (SqlServerCurrent.Kind == SqlServerDdlTokenKind.Parameter)
            {
                parameterName = SqlServerCurrent.Value;
                SqlServerAdvance();
                if (!SqlServerMatchSymbol("="))
                {
                    positional++;
                    parameterName = SqlServerRenamePositionalParameter(positional);
                }
            }

            var value = SqlServerReadRenameValue();
            if (value == null)
            {
                SqlServerSkipToStatementEnd();
                break;
            }

            if (parameterName == null)
            {
                positional++;
                parameterName = SqlServerRenamePositionalParameter(positional);
            }

            if (string.Equals(parameterName, "objname", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parameterName, "old_name", StringComparison.OrdinalIgnoreCase))
            {
                objectName = value;
            }
            else if (string.Equals(parameterName, "newname", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parameterName, "new_name", StringComparison.OrdinalIgnoreCase))
            {
                newName = value;
            }
            else if (string.Equals(parameterName, "objtype", StringComparison.OrdinalIgnoreCase))
            {
                objectType = value;
            }
            else
            {
                SqlServerReport("DDL300", "sp_rename contains an unsupported argument.", SqlServerPrevious.Span);
            }
        }

        if (objectName == null || newName == null)
        {
            SqlServerReport(
                "DDL100",
                "sp_rename requires an object name and a new name.",
                SqlServerCurrent.Span);
        }

        return new SqlServerRenameStatement(
            objectName ?? string.Empty,
            newName ?? string.Empty,
            objectType,
            SqlServerFromBounds(start, SqlServerStatementEnd(start)),
            _sqlServerDiagnostics.Count == diagnosticCount);
    }

    private string? SqlServerReadRenameValue()
    {
        if (SqlServerCurrent.Kind == SqlServerDdlTokenKind.Identifier &&
            string.Equals(SqlServerCurrent.Value, "N", StringComparison.OrdinalIgnoreCase) &&
            SqlServerPeek(1).Kind == SqlServerDdlTokenKind.String)
        {
            SqlServerAdvance();
        }

        if (SqlServerCurrent.Kind == SqlServerDdlTokenKind.String ||
            SqlServerCurrent.Kind == SqlServerDdlTokenKind.Identifier ||
            SqlServerCurrent.Kind == SqlServerDdlTokenKind.BracketIdentifier ||
            SqlServerCurrent.Kind == SqlServerDdlTokenKind.QuotedIdentifier)
        {
            var value = SqlServerCurrent.Value ?? SqlServerCurrent.Text;
            SqlServerAdvance();
            return value;
        }

        SqlServerReport("DDL100", "Expected a literal value in sp_rename.", SqlServerCurrent.Span);
        return null;
    }

    private SqlServerDdlColumnDefinition? SqlServerParseColumnDefinition()
    {
        var start = SqlServerCurrent.Span.Start;
        if (!SqlServerCurrent.SqlServerIsIdentifier())
        {
            SqlServerReport("DDL100", "Expected a SQL Server column name.", SqlServerCurrent.Span);
            SqlServerSkipToTableItemEnd();
            return null;
        }

        var name = SqlServerParseIdentifier("Expected a SQL Server column name.");
        var type = SqlServerReadType();
        var nullable = false;
        var primaryKey = false;
        var identity = false;
        string? defaultExpression = null;
        while (!SqlServerAtEnd && !SqlServerIsSymbol(",") && !SqlServerIsSymbol(")") &&
               !SqlServerIsSymbol(";"))
        {
            if (SqlServerMatchKeyword("IDENTITY"))
            {
                identity = true;
                SqlServerSkipBalancedParenthesesIfPresent();
            }
            else if (SqlServerMatchKeyword("NULL"))
            {
                nullable = true;
            }
            else if (SqlServerMatchKeyword("NOT"))
            {
                SqlServerExpectKeyword("NULL", "Expected NULL after NOT in a SQL Server column definition.");
                nullable = false;
            }
            else if (SqlServerMatchKeyword("PRIMARY"))
            {
                SqlServerExpectKeyword("KEY", "Expected KEY after PRIMARY in a SQL Server column definition.");
                primaryKey = true;
                nullable = false;
                SqlServerMatchKeyword("CLUSTERED");
                SqlServerMatchKeyword("NONCLUSTERED");
            }
            else if (SqlServerMatchKeyword("DEFAULT"))
            {
                defaultExpression = SqlServerReadExpression(false);
            }
            else if (SqlServerMatchKeyword("CONSTRAINT"))
            {
                if (SqlServerCurrent.SqlServerIsIdentifier())
                {
                    SqlServerAdvance();
                }

                if (SqlServerMatchKeyword("DEFAULT"))
                {
                    defaultExpression = SqlServerReadExpression(false);
                }
                else if (SqlServerMatchKeyword("PRIMARY"))
                {
                    SqlServerExpectKeyword("KEY", "Expected KEY after PRIMARY in a SQL Server constraint.");
                    primaryKey = true;
                    nullable = false;
                }
                else
                {
                    SqlServerSkipToTableItemEnd();
                }
            }
            else if (SqlServerMatchKeyword("COLLATE"))
            {
                if (SqlServerCurrent.SqlServerIsIdentifier())
                {
                    SqlServerAdvance();
                }
            }
            else if (SqlServerIs("UNIQUE") || SqlServerIs("REFERENCES") || SqlServerIs("CHECK") ||
                     SqlServerIs("GENERATED"))
            {
                SqlServerAdvance();
                SqlServerSkipToTableItemEnd();
            }
            else if (SqlServerIs("ROWGUIDCOL") || SqlServerIs("SPARSE") || SqlServerIs("FILESTREAM"))
            {
                SqlServerAdvance();
            }
            else if (SqlServerIs("MASKED"))
            {
                SqlServerAdvance();
                SqlServerSkipConstraintTail();
            }
            else
            {
                SqlServerReport(
                    "DDL100",
                    "Unsupported SQL Server column constraint or definition syntax near '" +
                    SqlServerCurrent.Text + "' for column '" + name.Name + "'.",
                    SqlServerCurrent.Span);
                SqlServerSkipToTableItemEnd();
                break;
            }
        }

        var span = SqlServerFromBounds(start, SqlServerStatementEnd(start));
        return new SqlServerDdlColumnDefinition(
            name,
            type,
            nullable,
            primaryKey,
            defaultExpression,
            identity,
            span);
    }

    private void SqlServerParseTableConstraint(
        List<IReadOnlyList<SqlServerDdlIdentifier>> primaryKeys,
        List<SqlServerDdlDefaultConstraint> defaults)
    {
        if (SqlServerMatchKeyword("CONSTRAINT"))
        {
            if (SqlServerCurrent.SqlServerIsIdentifier())
            {
                SqlServerAdvance();
            }
        }

        if (SqlServerIs("PRIMARY"))
        {
            primaryKeys.Add(SqlServerParsePrimaryKeyColumns());
            SqlServerSkipPrimaryKeyOptions();
            return;
        }

        if (SqlServerIs("DEFAULT"))
        {
            defaults.Add(SqlServerParseDefaultConstraint());
            return;
        }

        if (SqlServerIs("UNIQUE"))
        {
            SqlServerAdvance();
            SqlServerSkipToTableItemEnd();
            return;
        }

        if (SqlServerIs("FOREIGN") || SqlServerIs("CHECK"))
        {
            SqlServerAdvance();
            SqlServerSkipToTableItemEnd();
            return;
        }

        SqlServerReport(
            "DDL300",
            "This SQL Server table constraint is not supported by schema analysis.",
            SqlServerCurrent.Span);
        SqlServerSkipToTableItemEnd();
    }

    private IReadOnlyList<SqlServerDdlIdentifier> SqlServerParsePrimaryKeyColumns()
    {
        SqlServerExpectKeyword("PRIMARY", "Expected PRIMARY KEY.");
        SqlServerExpectKeyword("KEY", "Expected KEY after PRIMARY.");
        SqlServerMatchKeyword("CLUSTERED");
        SqlServerMatchKeyword("NONCLUSTERED");
        SqlServerMatchKeyword("WITH");
        if (SqlServerPrevious.SqlServerIs("WITH"))
        {
            SqlServerSkipBalancedParenthesesIfPresent();
        }

        var columns = new List<SqlServerDdlIdentifier>();
        SqlServerExpectSymbol("(", "Expected a column list after PRIMARY KEY.");
        while (!SqlServerAtEnd && !SqlServerIsSymbol(")"))
        {
            if (SqlServerMatchSymbol(","))
            {
                continue;
            }

            columns.Add(SqlServerParseIdentifier("Expected a column name in PRIMARY KEY."));
            if (SqlServerIs("ASC") || SqlServerIs("DESC"))
            {
                SqlServerAdvance();
            }
        }

        SqlServerExpectSymbol(")", "Expected ')' after PRIMARY KEY columns.");
        return columns;
    }

    private SqlServerDdlDefaultConstraint SqlServerParseDefaultConstraint()
    {
        var start = SqlServerCurrent.Span.Start;
        SqlServerExpectKeyword("DEFAULT", "Expected DEFAULT.");
        var expression = SqlServerReadDefaultConstraintExpression();
        SqlServerExpectKeyword("FOR", "Expected FOR after a table DEFAULT constraint.");
        var column = SqlServerParseIdentifier("Expected a column name after DEFAULT ... FOR.");
        return new SqlServerDdlDefaultConstraint(
            column,
            expression,
            SqlServerFromBounds(start, SqlServerEndOf(column.Span)));
    }

    private string SqlServerReadDefaultConstraintExpression()
    {
        var start = SqlServerCurrent.Span.Start;
        var end = start;
        var depth = 0;
        var forPosition = -1;
        var forEnd = start;
        while (!SqlServerAtEnd)
        {
            if (depth == 0 && (SqlServerIsSymbol(",") || SqlServerIsSymbol(")") || SqlServerIsSymbol(";")))
            {
                break;
            }

            if (depth == 0 && SqlServerIs("FOR"))
            {
                forPosition = _sqlServerPosition;
                forEnd = SqlServerEndOf(SqlServerCurrent.Span);
            }

            if (SqlServerIsSymbol("("))
            {
                depth++;
            }
            else if (SqlServerIsSymbol(")") && depth > 0)
            {
                depth--;
            }

            end = SqlServerEndOf(SqlServerCurrent.Span);
            SqlServerAdvance();
        }

        if (forPosition >= 0)
        {
            _sqlServerPosition = forPosition;
            end = forEnd - SqlServerCurrent.Span.Length;
        }

        if (end == start)
        {
            SqlServerReport("DDL100", "A SQL Server DEFAULT constraint requires an expression.", SqlServerCurrent.Span);
            return string.Empty;
        }

        return _sqlServerText.Substring(start, end - start).Trim();
    }

    private string SqlServerReadType()
    {
        if (!SqlServerCurrent.SqlServerIsIdentifier())
        {
            SqlServerReport("DDL100", "A SQL Server column type is required.", SqlServerCurrent.Span);
            return string.Empty;
        }

        var start = SqlServerCurrent.Span.Start;
        var end = SqlServerEndOf(SqlServerCurrent.Span);
        var depth = 0;
        SqlServerAdvance();
        while (!SqlServerAtEnd)
        {
            if (depth == 0 && SqlServerIsColumnConstraintBoundary(SqlServerCurrent))
            {
                break;
            }

            if (depth == 0 && (SqlServerIsSymbol(",") || SqlServerIsSymbol(")") || SqlServerIsSymbol(";")))
            {
                break;
            }

            if (SqlServerIsSymbol("("))
            {
                depth++;
            }
            else if (SqlServerIsSymbol(")"))
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }

            end = SqlServerEndOf(SqlServerCurrent.Span);
            SqlServerAdvance();
        }

        return SqlServerNormalizeTypeText(_sqlServerText.Substring(start, Math.Max(0, end - start)));
    }

    private static string SqlServerNormalizeTypeText(string value)
    {
        var builder = new System.Text.StringBuilder();
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (character == '(' || character == ')' || character == ',' || character == '.')
            {
                while (builder.Length > 0 && builder[builder.Length - 1] == ' ')
                {
                    builder.Length--;
                }

                builder.Append(character);
                pendingSpace = false;
                continue;
            }

            if (pendingSpace && builder.Length != 0 && builder[builder.Length - 1] != '(' &&
                builder[builder.Length - 1] != '.' && builder[builder.Length - 1] != ',')
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(character));
            pendingSpace = false;
        }

        return builder.ToString();
    }

    private string SqlServerReadExpression(bool stopsAtFor)
    {
        var start = SqlServerCurrent.Span.Start;
        var end = start;
        var depth = 0;
        while (!SqlServerAtEnd)
        {
            if (depth == 0 && (SqlServerIsSymbol(",") || SqlServerIsSymbol(")") || SqlServerIsSymbol(";")))
            {
                break;
            }

            if (depth == 0 && SqlServerIsExpressionBoundary(SqlServerCurrent, stopsAtFor))
            {
                break;
            }

            if (SqlServerIsSymbol("("))
            {
                depth++;
            }
            else if (SqlServerIsSymbol(")") && depth > 0)
            {
                depth--;
            }

            end = SqlServerEndOf(SqlServerCurrent.Span);
            SqlServerAdvance();
        }

        if (end == start)
        {
            SqlServerReport("DDL100", "A SQL Server DEFAULT constraint requires an expression.", SqlServerCurrent.Span);
            return string.Empty;
        }

        return _sqlServerText.Substring(start, end - start).Trim();
    }

    private bool SqlServerIsColumnConstraintBoundary(SqlServerDdlToken token) =>
        token.Kind == SqlServerDdlTokenKind.Identifier &&
        SqlServerColumnConstraintKeywords.Contains(token.Value ?? token.Text);

    private static bool SqlServerIsExpressionBoundary(SqlServerDdlToken token, bool stopsAtFor)
    {
        if (token.Kind != SqlServerDdlTokenKind.Identifier)
        {
            return false;
        }

        return token.SqlServerIs("NOT") || token.SqlServerIs("NULL") || token.SqlServerIs("PRIMARY") ||
            token.SqlServerIs("CONSTRAINT") || token.SqlServerIs("UNIQUE") || token.SqlServerIs("REFERENCES") ||
            token.SqlServerIs("CHECK") || token.SqlServerIs("COLLATE") || stopsAtFor && token.SqlServerIs("FOR");
    }

    private SqlServerDdlQualifiedName SqlServerParseQualifiedName(string message)
    {
        var first = SqlServerParseIdentifier(message);
        if (!SqlServerMatchSymbol("."))
        {
            return new SqlServerDdlQualifiedName(null, first, first.Span);
        }

        var second = SqlServerParseIdentifier("Expected an object name after '.'.");
        if (SqlServerIsSymbol("."))
        {
            SqlServerReport(
                "DDL300",
                "Four-part SQL Server names are not supported by compile-time schema analysis.",
                SqlServerCurrent.Span);
            SqlServerSkipToStatementEnd();
        }

        return new SqlServerDdlQualifiedName(
            first,
            second,
            SqlServerFromBounds(first.Span.Start, SqlServerEndOf(second.Span)));
    }

    private SqlServerDdlIdentifier SqlServerParseIdentifier(string message)
    {
        if (SqlServerCurrent.SqlServerIsIdentifier())
        {
            var token = SqlServerAdvance();
            return new SqlServerDdlIdentifier(
                token.Value ?? token.Text,
                token.Kind != SqlServerDdlTokenKind.Identifier,
                token.Span);
        }

        SqlServerReport("DDL100", message, SqlServerCurrent.Span);
        var fallback = SqlServerAdvance();
        return new SqlServerDdlIdentifier(
            fallback.Value ?? fallback.Text,
            false,
            fallback.Span);
    }

    private void SqlServerSkipCreateTableOptions()
    {
        while (!SqlServerAtEnd && !SqlServerIsSymbol(";"))
        {
            if (SqlServerMatchKeyword("ON"))
            {
                if (SqlServerCurrent.SqlServerIsIdentifier())
                {
                    SqlServerAdvance();
                }
                else
                {
                    SqlServerReport(
                        "DDL100",
                        "Expected a filegroup name after ON in CREATE TABLE.",
                        SqlServerCurrent.Span);
                    SqlServerSkipToStatementEnd();
                    return;
                }

                continue;
            }

            if (SqlServerMatchKeyword("TEXTIMAGE_ON"))
            {
                if (SqlServerCurrent.SqlServerIsIdentifier())
                {
                    SqlServerAdvance();
                }
                else
                {
                    SqlServerReport(
                        "DDL100",
                        "Expected a filegroup name after TEXTIMAGE_ON in CREATE TABLE.",
                        SqlServerCurrent.Span);
                    SqlServerSkipToStatementEnd();
                    return;
                }

                continue;
            }

            if (SqlServerMatchKeyword("WITH"))
            {
                if (!SqlServerIsSymbol("("))
                {
                    SqlServerReport(
                        "DDL100",
                        "Expected a parenthesized option list after WITH in CREATE TABLE.",
                        SqlServerCurrent.Span);
                    SqlServerSkipToStatementEnd();
                    return;
                }

                SqlServerSkipBalancedParenthesesIfPresent();
                continue;
            }

            SqlServerReport(
                "DDL300",
                "This SQL Server CREATE TABLE option is not supported by schema analysis.",
                SqlServerCurrent.Span);
            SqlServerSkipToStatementEnd();
            return;
        }
    }

    private void SqlServerSkipToStatementEnd()
    {
        while (!SqlServerAtEnd && !SqlServerIsSymbol(";"))
        {
            SqlServerAdvance();
        }
    }

    private void SqlServerSkipToTableItemEnd()
    {
        var depth = 0;
        while (!SqlServerAtEnd)
        {
            if (depth == 0 && (SqlServerIsSymbol(",") || SqlServerIsSymbol(")") || SqlServerIsSymbol(";")))
            {
                return;
            }

            if (SqlServerMatchSymbol("("))
            {
                depth++;
            }
            else if (SqlServerMatchSymbol(")") && depth > 0)
            {
                depth--;
            }
            else
            {
                SqlServerAdvance();
            }
        }
    }

    private void SqlServerSkipToActionEnd()
    {
        var depth = 0;
        while (!SqlServerAtEnd && !SqlServerIsSymbol(";"))
        {
            if (depth == 0 && SqlServerIsSymbol(","))
            {
                return;
            }

            if (SqlServerMatchSymbol("("))
            {
                depth++;
            }
            else if (SqlServerMatchSymbol(")") && depth > 0)
            {
                depth--;
            }
            else
            {
                SqlServerAdvance();
            }
        }
    }

    private void SqlServerSkipConstraintTail()
    {
        if (SqlServerIsSymbol("("))
        {
            SqlServerSkipBalancedParenthesesIfPresent();
        }
        else if (SqlServerCurrent.SqlServerIsIdentifier())
        {
            SqlServerAdvance();
        }
    }

    private void SqlServerSkipPrimaryKeyOptions()
    {
        if (SqlServerMatchKeyword("WITH"))
        {
            SqlServerSkipBalancedParenthesesIfPresent();
        }

        if (SqlServerMatchKeyword("ON") && SqlServerCurrent.SqlServerIsIdentifier())
        {
            SqlServerAdvance();
        }
    }

    private void SqlServerSkipBalancedParenthesesIfPresent()
    {
        if (!SqlServerMatchSymbol("("))
        {
            return;
        }

        var depth = 1;
        while (!SqlServerAtEnd && depth > 0)
        {
            if (SqlServerMatchSymbol("("))
            {
                depth++;
            }
            else if (SqlServerMatchSymbol(")"))
            {
                depth--;
            }
            else
            {
                SqlServerAdvance();
            }
        }

        if (depth != 0)
        {
            SqlServerReport(
                "DDL100",
                "Unterminated parenthesized SQL Server expression.",
                SqlServerCurrent.Span);
        }
    }

    private bool SqlServerIsTableConstraintStart() =>
        SqlServerIs("CONSTRAINT") || SqlServerIs("PRIMARY") || SqlServerIs("UNIQUE") ||
        SqlServerIs("FOREIGN") || SqlServerIs("CHECK") || SqlServerIs("DEFAULT");

    private void SqlServerExpectKeyword(string keyword, string message)
    {
        if (!SqlServerMatchKeyword(keyword))
        {
            SqlServerReport("DDL100", message, SqlServerCurrent.Span);
        }
    }

    private SqlServerDdlToken SqlServerExpectSymbol(string symbol, string message)
    {
        if (SqlServerMatchSymbol(symbol))
        {
            return SqlServerPrevious;
        }

        SqlServerReport("DDL100", message, SqlServerCurrent.Span);
        return new SqlServerDdlToken(
            SqlServerDdlTokenKind.Symbol,
            symbol,
            symbol,
            new SourceSpan(SqlServerCurrent.Span.Start, 0));
    }

    private bool SqlServerMatchKeyword(string keyword)
    {
        if (!SqlServerIs(keyword))
        {
            return false;
        }

        SqlServerAdvance();
        return true;
    }

    private bool SqlServerMatchSymbol(string symbol)
    {
        if (!SqlServerIsSymbol(symbol))
        {
            return false;
        }

        SqlServerAdvance();
        return true;
    }

    private bool SqlServerIs(string keyword) => SqlServerCurrent.SqlServerIs(keyword);

    private bool SqlServerIsSymbol(string symbol) =>
        SqlServerCurrent.Kind == SqlServerDdlTokenKind.Symbol &&
        string.Equals(SqlServerCurrent.Text, symbol, StringComparison.Ordinal);

    private SqlServerDdlToken SqlServerAdvance()
    {
        var token = SqlServerCurrent;
        if (_sqlServerPosition < _sqlServerTokens.Count - 1)
        {
            _sqlServerPosition++;
        }

        return token;
    }

    private SqlServerDdlToken SqlServerPeek(int offset)
    {
        var index = _sqlServerPosition + offset;
        return index >= 0 && index < _sqlServerTokens.Count
            ? _sqlServerTokens[index]
            : _sqlServerTokens[_sqlServerTokens.Count - 1];
    }

    private SqlServerDdlToken SqlServerCurrent => _sqlServerTokens[_sqlServerPosition];

    private SqlServerDdlToken SqlServerPrevious =>
        _sqlServerPosition > 0 ? _sqlServerTokens[_sqlServerPosition - 1] : SqlServerCurrent;

    private bool SqlServerAtEnd => SqlServerCurrent.Kind == SqlServerDdlTokenKind.End;

    private int SqlServerStatementEnd(int start)
    {
        if (_sqlServerPosition > 0)
        {
            return Math.Max(start, SqlServerEndOf(SqlServerPrevious.Span));
        }

        return start;
    }

    private void SqlServerReport(string code, string message, SourceSpan span) =>
        _sqlServerDiagnostics.Add(new Diagnostic(code, message, span));

    private static string SqlServerRenamePositionalParameter(int position)
    {
        switch (position)
        {
            case 1: return "objname";
            case 2: return "newname";
            case 3: return "objtype";
            default: return "argument" + position.ToString();
        }
    }

    private static int SqlServerEndOf(SourceSpan span) => span.Start + span.Length;

    private static SourceSpan SqlServerFromBounds(int start, int end) =>
        new SourceSpan(start, Math.Max(0, end - start));
}
