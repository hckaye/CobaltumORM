using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CobaltumOrm.Analysis;

internal abstract class SqliteDdlStatement
{
    protected SqliteDdlStatement(SourceSpan span, bool isValid)
    {
        Span = span;
        IsValid = isValid;
    }

    internal SourceSpan Span { get; }
    internal bool IsValid { get; }
}

internal sealed class SqliteCreateTableStatement : SqliteDdlStatement
{
    internal SqliteCreateTableStatement(
        SqliteDdlQualifiedName table,
        bool ifNotExists,
        IReadOnlyList<SqliteDdlColumnDefinition> columns,
        IReadOnlyList<IReadOnlyList<SqliteDdlIdentifier>> primaryKeys,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        IfNotExists = ifNotExists;
        Columns = columns;
        PrimaryKeys = primaryKeys;
    }

    internal SqliteDdlQualifiedName Table { get; }
    internal bool IfNotExists { get; }
    internal IReadOnlyList<SqliteDdlColumnDefinition> Columns { get; }
    internal IReadOnlyList<IReadOnlyList<SqliteDdlIdentifier>> PrimaryKeys { get; }
}

internal sealed class SqliteDropTableStatement : SqliteDdlStatement
{
    internal SqliteDropTableStatement(
        SqliteDdlQualifiedName table,
        bool ifExists,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        IfExists = ifExists;
    }

    internal SqliteDdlQualifiedName Table { get; }
    internal bool IfExists { get; }
}

internal sealed class SqliteAlterTableStatement : SqliteDdlStatement
{
    internal SqliteAlterTableStatement(
        SqliteDdlQualifiedName table,
        bool ifExists,
        SqliteDdlAlterAction? action,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        IfExists = ifExists;
        Action = action;
    }

    internal SqliteDdlQualifiedName Table { get; }
    internal bool IfExists { get; }
    internal SqliteDdlAlterAction? Action { get; }
}

internal sealed class SqliteDdlColumnDefinition
{
    internal SqliteDdlColumnDefinition(
        SqliteDdlIdentifier name,
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

    internal SqliteDdlIdentifier Name { get; }
    internal string SqlType { get; }
    internal bool IsNullable { get; }
    internal bool IsPrimaryKey { get; }
    internal string? DefaultExpression { get; }
    internal bool IsIdentity { get; }
    internal SourceSpan Span { get; }
}

internal abstract class SqliteDdlAlterAction
{
    protected SqliteDdlAlterAction(SourceSpan span)
    {
        Span = span;
    }

    internal SourceSpan Span { get; }
}

internal sealed class SqliteAddColumnAction : SqliteDdlAlterAction
{
    internal SqliteAddColumnAction(
        SqliteDdlColumnDefinition column,
        bool ifNotExists,
        SourceSpan span)
        : base(span)
    {
        Column = column;
        IfNotExists = ifNotExists;
    }

    internal SqliteDdlColumnDefinition Column { get; }
    internal bool IfNotExists { get; }
}

internal sealed class SqliteDropColumnAction : SqliteDdlAlterAction
{
    internal SqliteDropColumnAction(
        SqliteDdlIdentifier column,
        bool ifExists,
        SourceSpan span)
        : base(span)
    {
        Column = column;
        IfExists = ifExists;
    }

    internal SqliteDdlIdentifier Column { get; }
    internal bool IfExists { get; }
}

internal sealed class SqliteRenameColumnAction : SqliteDdlAlterAction
{
    internal SqliteRenameColumnAction(
        SqliteDdlIdentifier oldName,
        SqliteDdlIdentifier newName,
        SourceSpan span)
        : base(span)
    {
        OldName = oldName;
        NewName = newName;
    }

    internal SqliteDdlIdentifier OldName { get; }
    internal SqliteDdlIdentifier NewName { get; }
}

internal sealed class SqliteRenameTableAction : SqliteDdlAlterAction
{
    internal SqliteRenameTableAction(SqliteDdlIdentifier newName, SourceSpan span)
        : base(span)
    {
        NewName = newName;
    }

    internal SqliteDdlIdentifier NewName { get; }
}

internal sealed class SqliteDdlIdentifier
{
    internal SqliteDdlIdentifier(string name, bool isQuoted, SourceSpan span)
    {
        Name = name;
        IsQuoted = isQuoted;
        Span = span;
    }

    internal string Name { get; }
    internal bool IsQuoted { get; }
    internal SourceSpan Span { get; }
}

internal sealed class SqliteDdlQualifiedName
{
    internal SqliteDdlQualifiedName(
        SqliteDdlIdentifier? schema,
        SqliteDdlIdentifier name,
        SourceSpan span)
    {
        Schema = schema;
        Name = name;
        Span = span;
    }

    internal SqliteDdlIdentifier? Schema { get; }
    internal SqliteDdlIdentifier Name { get; }
    internal SourceSpan Span { get; }
}

internal sealed class SqliteDdlParser
{
    private readonly IReadOnlyList<SqliteDdlToken> _tokens;
    private readonly List<Diagnostic> _diagnostics;
    private readonly string _sql;
    private int _position;

    internal SqliteDdlParser(
        IReadOnlyList<SqliteDdlToken> tokens,
        string sql,
        List<Diagnostic> diagnostics)
    {
        _tokens = tokens;
        _sql = sql;
        _diagnostics = diagnostics;
    }

    internal IReadOnlyList<SqliteDdlStatement> Parse()
    {
        var statements = new List<SqliteDdlStatement>();
        while (Current.Kind != SqliteDdlTokenKind.End)
        {
            if (SqliteMatch(SqliteDdlTokenKind.Semicolon))
            {
                continue;
            }

            var start = Current.Span.Start;
            var diagnosticCount = _diagnostics.Count;
            SqliteDdlStatement? statement;
            if (SqliteIsSchemaNeutralStatement())
            {
                SqliteValidateSchemaNeutralQualification();
                SqliteSkipToStatementEnd();
                statement = null;
            }
            else if (SqliteMatchKeyword("CREATE"))
            {
                statement = SqliteParseCreateTable(start, diagnosticCount);
            }
            else if (SqliteMatchKeyword("DROP"))
            {
                statement = SqliteParseDropTable(start, diagnosticCount);
            }
            else if (SqliteMatchKeyword("ALTER"))
            {
                statement = SqliteParseAlterTable(start, diagnosticCount);
            }
            else
            {
                SqliteReport(
                    "DDL101",
                    "This SQLite statement is not supported by the compile-time schema analyzer.",
                    Current.Span);
                SqliteSkipToStatementEnd();
                statement = null;
            }

            if (Current.Kind == SqliteDdlTokenKind.Semicolon)
            {
                SqliteAdvance();
            }
            else if (Current.Kind != SqliteDdlTokenKind.End)
            {
                SqliteReport(
                    "DDL100",
                    "Expected a semicolon between SQLite migration statements.",
                    Current.Span);
                SqliteSkipToStatementEnd();
            }

            if (statement != null && statement.IsValid && _diagnostics.Count == diagnosticCount)
            {
                statements.Add(statement);
            }
        }

        return statements;
    }

    private SqliteDdlStatement? SqliteParseCreateTable(int start, int diagnosticCount)
    {
        var temporary = SqliteMatchKeyword("TEMP") || SqliteMatchKeyword("TEMPORARY");
        if (temporary)
        {
            SqliteReport(
                "DDL101",
                "Temporary SQLite tables are not represented by DatabaseSchema.",
                Current.Span);
        }

        if (!SqliteMatchKeyword("TABLE"))
        {
            SqliteReport("DDL101", "Expected TABLE after CREATE.", Current.Span);
            SqliteSkipToStatementEnd();
            return null;
        }

        var ifNotExists = SqliteMatchIfNotExists();
        var table = SqliteParseTableName("Expected a table name after CREATE TABLE.");
        if (!SqliteMatch(SqliteDdlTokenKind.OpenParen))
        {
            SqliteReport(
                "DDL101",
                "SQLite CREATE TABLE AS SELECT and other non-parenthesized forms are not represented by DatabaseSchema.",
                Current.Span);
            SqliteSkipToStatementEnd();
            return null;
        }

        var body = SqliteReadBalancedBody();
        if (body.Any(item => item.Kind == SqliteDdlTokenKind.Invalid))
        {
            SqliteReport(
                "DDL100",
                "The CREATE TABLE statement contains an invalid SQLite token.",
                body.First(item => item.Kind == SqliteDdlTokenKind.Invalid).Span);
        }

        var columns = new List<SqliteDdlColumnDefinition>();
        var primaryKeys = new List<IReadOnlyList<SqliteDdlIdentifier>>();
        foreach (var item in SqliteSplitTopLevel(body))
        {
            if (item.Count == 0)
            {
                continue;
            }

            if (SqliteIsTableConstraint(item))
            {
                SqliteParseTableConstraint(item, primaryKeys);
                continue;
            }

            var column = SqliteParseColumnDefinition(item);
            if (column != null)
            {
                columns.Add(column);
            }
        }

        while (Current.Kind != SqliteDdlTokenKind.Semicolon && Current.Kind != SqliteDdlTokenKind.End)
        {
            if (SqliteMatchKeyword("WITHOUT"))
            {
                SqliteExpectKeyword("ROWID", "Expected ROWID after WITHOUT in CREATE TABLE.");
                continue;
            }

            if (SqliteMatchKeyword("STRICT"))
            {
                continue;
            }

            SqliteReport(
                "DDL101",
                "This CREATE TABLE option is not represented by DatabaseSchema.",
                Current.Span);
            SqliteSkipToStatementEnd();
            break;
        }

        var end = Current.Span.Start;
        if (Current.Kind == SqliteDdlTokenKind.Semicolon)
        {
            end = Current.Span.Start;
        }

        return new SqliteCreateTableStatement(
            table,
            ifNotExists,
            columns,
            primaryKeys,
            SqliteFromBounds(start, end),
            !temporary && _diagnostics.Count == diagnosticCount);
    }

    private SqliteDdlStatement? SqliteParseDropTable(int start, int diagnosticCount)
    {
        if (!SqliteMatchKeyword("TABLE"))
        {
            SqliteReport("DDL101", "Expected TABLE after DROP.", Current.Span);
            SqliteSkipToStatementEnd();
            return null;
        }

        var ifExists = SqliteMatchIfExists();
        var table = SqliteParseTableName("Expected a table name after DROP TABLE.");
        if (Current.Kind != SqliteDdlTokenKind.Semicolon && Current.Kind != SqliteDdlTokenKind.End)
        {
            SqliteReport(
                "DDL101",
                "SQLite DROP TABLE accepts one table name and no schema qualifier.",
                Current.Span);
            SqliteSkipToStatementEnd();
        }

        return new SqliteDropTableStatement(
            table,
            ifExists,
            SqliteFromBounds(start, SqliteEndOf(table.Span)),
            _diagnostics.Count == diagnosticCount);
    }

    private SqliteDdlStatement? SqliteParseAlterTable(int start, int diagnosticCount)
    {
        if (!SqliteMatchKeyword("TABLE"))
        {
            SqliteReport("DDL101", "Expected TABLE after ALTER.", Current.Span);
            SqliteSkipToStatementEnd();
            return null;
        }

        var ifExists = SqliteMatchIfExists();
        var table = SqliteParseTableName("Expected a table name after ALTER TABLE.");
        var action = SqliteParseAlterAction();
        if (action == null)
        {
            SqliteReport(
                "DDL101",
                "ALTER TABLE requires ADD COLUMN, DROP COLUMN, RENAME COLUMN, or RENAME TO in SQLite.",
                Current.Span);
        }

        if (Current.Kind == SqliteDdlTokenKind.Comma)
        {
            SqliteReport(
                "DDL101",
                "SQLite ALTER TABLE accepts one table change per statement.",
                Current.Span);
            SqliteSkipToStatementEnd();
        }
        else if (Current.Kind != SqliteDdlTokenKind.Semicolon && Current.Kind != SqliteDdlTokenKind.End)
        {
            SqliteReport("DDL101", "Unsupported SQLite ALTER TABLE syntax.", Current.Span);
            SqliteSkipToStatementEnd();
        }

        var end = action == null ? SqliteEndOf(table.Span) : SqliteEndOf(action.Span);
        return new SqliteAlterTableStatement(
            table,
            ifExists,
            action,
            SqliteFromBounds(start, end),
            _diagnostics.Count == diagnosticCount);
    }

    private SqliteDdlAlterAction? SqliteParseAlterAction()
    {
        var start = Current.Span.Start;
        if (SqliteMatchKeyword("ADD"))
        {
            SqliteMatchKeyword("COLUMN");
            var ifNotExists = SqliteMatchIfNotExists();
            var tokens = SqliteReadToStatementEnd();
            if (tokens.Count == 0)
            {
                SqliteReport(
                    "DDL100",
                    "ALTER TABLE ADD COLUMN requires a column definition.",
                    Current.Span);
                return null;
            }

            var column = SqliteParseColumnDefinition(tokens);
            if (column == null)
            {
                return null;
            }

            if (column.IsPrimaryKey || column.IsIdentity)
            {
                SqliteReport(
                    "DDL101",
                    "SQLite ALTER TABLE ADD COLUMN cannot add a primary-key or AUTOINCREMENT column.",
                    column.Span);
            }

            return new SqliteAddColumnAction(
                column,
                ifNotExists,
                SqliteFromBounds(start, SqliteEndOf(column.Span)));
        }

        if (SqliteMatchKeyword("DROP"))
        {
            SqliteMatchKeyword("COLUMN");
            var ifExists = SqliteMatchIfExists();
            var column = SqliteParseIdentifier(
                "Expected a column name after ALTER TABLE DROP COLUMN.");
            if (SqliteMatchKeyword("CASCADE") || SqliteMatchKeyword("RESTRICT"))
            {
                SqliteReport(
                    "DDL101",
                    "SQLite ALTER TABLE DROP COLUMN does not accept CASCADE or RESTRICT.",
                    Current.Span);
            }

            return new SqliteDropColumnAction(
                column,
                ifExists,
                SqliteFromBounds(start, SqliteEndOf(column.Span)));
        }

        if (SqliteMatchKeyword("RENAME"))
        {
            if (SqliteMatchKeyword("COLUMN"))
            {
                var oldName = SqliteParseIdentifier(
                    "Expected the old column name after ALTER TABLE RENAME COLUMN.");
                SqliteExpectKeyword("TO", "Expected TO in ALTER TABLE RENAME COLUMN.");
                var newName = SqliteParseIdentifier(
                    "Expected the new column name in ALTER TABLE RENAME COLUMN.");
                return new SqliteRenameColumnAction(
                    oldName,
                    newName,
                    SqliteFromBounds(start, SqliteEndOf(newName.Span)));
            }

            if (SqliteMatchKeyword("TO"))
            {
                var newName = SqliteParseIdentifier(
                    "Expected the new table name after ALTER TABLE RENAME TO.");
                return new SqliteRenameTableAction(
                    newName,
                    SqliteFromBounds(start, SqliteEndOf(newName.Span)));
            }

            SqliteReport(
                "DDL101",
                "SQLite ALTER TABLE RENAME requires COLUMN or TO.",
                Current.Span);
            SqliteSkipToStatementEnd();
            return null;
        }

        if (SqliteMatchKeyword("ALTER"))
        {
            SqliteMatchKeyword("COLUMN");
            SqliteReport(
                "DDL101",
                "SQLite does not support ALTER COLUMN. A type or nullability change requires a table rebuild.",
                Current.Span);
            SqliteSkipToStatementEnd();
            return null;
        }

        SqliteReport("DDL101", "Unsupported SQLite ALTER TABLE action.", Current.Span);
        SqliteSkipToStatementEnd();
        return null;
    }

    private SqliteDdlColumnDefinition? SqliteParseColumnDefinition(
        IReadOnlyList<SqliteDdlToken> tokens)
    {
        try
        {
            var index = 0;
            if (tokens.Count == 0)
            {
                return null;
            }

            var invalid = tokens.FirstOrDefault(item => item.Kind == SqliteDdlTokenKind.Invalid);
            if (invalid.Kind == SqliteDdlTokenKind.Invalid)
            {
                throw new SqliteDdlLocalParseException(
                    "The SQLite column definition contains an invalid token.",
                    invalid.Span);
            }

            var start = tokens[0].Span.Start;
            var name = SqliteReadIdentifier(tokens, ref index, "Expected a column name.");
            var typeStart = index;
            var depth = 0;
            while (index < tokens.Count)
            {
                var token = tokens[index];
                if (token.Kind == SqliteDdlTokenKind.OpenParen)
                {
                    depth++;
                }
                else if (token.Kind == SqliteDdlTokenKind.CloseParen && depth > 0)
                {
                    depth--;
                }

                if (depth == 0 && SqliteIsColumnConstraintStart(token))
                {
                    break;
                }

                index++;
            }

            var typeTokens = tokens.Skip(typeStart).Take(index - typeStart).ToArray();
            var sqlType = SqliteRenderType(typeTokens);
            var isNullable = true;
            var isPrimaryKey = false;
            var isIdentity = false;
            string? defaultExpression = null;

            while (index < tokens.Count)
            {
                var token = tokens[index];
                if (token.SqliteIsWord("CONSTRAINT"))
                {
                    index++;
                    if (index < tokens.Count)
                    {
                        SqliteReadIdentifier(tokens, ref index, "Expected a constraint name.");
                    }

                    continue;
                }

                if (token.SqliteIsWord("PRIMARY"))
                {
                    index++;
                    SqliteRequireLocalKeyword(tokens, ref index, "KEY", "Expected KEY after PRIMARY.");
                    isPrimaryKey = true;
                    isNullable = false;
                    if (SqliteLocalWord(tokens, index, "ASC") || SqliteLocalWord(tokens, index, "DESC"))
                    {
                        index++;
                    }

                    if (SqliteLocalWord(tokens, index, "AUTOINCREMENT"))
                    {
                        isIdentity = true;
                        index++;
                    }

                    SqliteConsumeConflictClause(tokens, ref index);
                    continue;
                }

                if (token.SqliteIsWord("NOT"))
                {
                    index++;
                    SqliteRequireLocalKeyword(tokens, ref index, "NULL", "Expected NULL after NOT.");
                    isNullable = false;
                    SqliteConsumeConflictClause(tokens, ref index);
                    continue;
                }

                if (token.SqliteIsWord("NULL"))
                {
                    index++;
                    isNullable = true;
                    continue;
                }

                if (token.SqliteIsWord("UNIQUE"))
                {
                    index++;
                    SqliteConsumeConflictClause(tokens, ref index);
                    continue;
                }

                if (token.SqliteIsWord("CHECK"))
                {
                    index++;
                    SqliteConsumeLocalParenthesized(tokens, ref index, "CHECK");
                    continue;
                }

                if (token.SqliteIsWord("DEFAULT"))
                {
                    index++;
                    var expressionStart = index;
                    index = SqliteConsumeDefaultExpression(tokens, index);
                    if (index == expressionStart)
                    {
                        SqliteReport("DDL100", "DEFAULT requires an expression.", token.Span);
                    }
                    else
                    {
                        defaultExpression = SqliteRenderTokens(
                            tokens.Skip(expressionStart).Take(index - expressionStart),
                            false);
                    }

                    continue;
                }

                if (token.SqliteIsWord("COLLATE"))
                {
                    index++;
                    SqliteReadIdentifier(tokens, ref index, "COLLATE requires a collation name.");
                    continue;
                }

                if (token.SqliteIsWord("REFERENCES"))
                {
                    index++;
                    SqliteReadIdentifier(
                        tokens,
                        ref index,
                        "REFERENCES requires a table name.");
                    if (index < tokens.Count && tokens[index].Kind == SqliteDdlTokenKind.Dot)
                    {
                        SqliteReport(
                            "DDL101",
                            "SQLite compile-time analysis does not support schema-qualified REFERENCES names.",
                            tokens[index].Span);
                        index++;
                        SqliteReadIdentifier(tokens, ref index, "Expected a table name after REFERENCES '.'.");
                    }

                    if (index < tokens.Count && tokens[index].Kind == SqliteDdlTokenKind.OpenParen)
                    {
                        SqliteConsumeLocalParenthesized(tokens, ref index, "REFERENCES");
                    }

                    SqliteConsumeReferenceTail(tokens, ref index);
                    continue;
                }

                if (token.SqliteIsWord("GENERATED"))
                {
                    index++;
                    if (SqliteLocalWord(tokens, index, "ALWAYS"))
                    {
                        index++;
                    }

                    SqliteRequireLocalKeyword(tokens, ref index, "AS", "Expected AS in a generated column.");
                    SqliteConsumeLocalParenthesized(tokens, ref index, "GENERATED");
                    if (SqliteLocalWord(tokens, index, "STORED") || SqliteLocalWord(tokens, index, "VIRTUAL"))
                    {
                        index++;
                    }

                    continue;
                }

                if (token.SqliteIsWord("AUTOINCREMENT"))
                {
                    index++;
                    isIdentity = true;
                    SqliteReport(
                        "DDL101",
                        "SQLite AUTOINCREMENT requires an INTEGER PRIMARY KEY column.",
                        token.Span);
                    continue;
                }

                if (token.SqliteIsWord("ON"))
                {
                    SqliteConsumeConflictClause(tokens, ref index);
                    continue;
                }

                if (token.SqliteIsWord("DEFERRABLE"))
                {
                    index++;
                    if (SqliteLocalWord(tokens, index, "INITIALLY"))
                    {
                        index++;
                        if (index < tokens.Count)
                        {
                            index++;
                        }
                    }

                    continue;
                }

                SqliteReport(
                    "DDL101",
                    "This SQLite column constraint is not supported by the schema analyzer.",
                    token.Span);
                index++;
            }

            if (isIdentity && (!isPrimaryKey || !string.Equals(sqlType, "INTEGER", StringComparison.OrdinalIgnoreCase)))
            {
                SqliteReport(
                    "DDL101",
                    "SQLite AUTOINCREMENT requires the exact declared type INTEGER and a PRIMARY KEY.",
                    new SourceSpan(start, Math.Max(0, tokens[tokens.Count - 1].Span.Start + tokens[tokens.Count - 1].Span.Length - start)));
            }

            if (isPrimaryKey)
            {
                isNullable = false;
            }

            var end = tokens.Count == 0
                ? start
                : SqliteEndOf(tokens[tokens.Count - 1].Span);
            return new SqliteDdlColumnDefinition(
                name,
                sqlType,
                isNullable,
                isPrimaryKey,
                defaultExpression,
                isIdentity && isPrimaryKey && string.Equals(sqlType, "INTEGER", StringComparison.OrdinalIgnoreCase),
                SqliteFromBounds(start, end));
        }
        catch (SqliteDdlLocalParseException exception)
        {
            SqliteReport("DDL100", exception.Message, exception.Span);
            return null;
        }
    }

    private void SqliteParseTableConstraint(
        IReadOnlyList<SqliteDdlToken> tokens,
        ICollection<IReadOnlyList<SqliteDdlIdentifier>> primaryKeys)
    {
        try
        {
            var index = 0;
        if (SqliteLocalWord(tokens, index, "CONSTRAINT"))
        {
            index += 2;
        }

        if (SqliteLocalWord(tokens, index, "PRIMARY"))
        {
            index++;
            SqliteRequireLocalKeyword(tokens, ref index, "KEY", "Expected KEY after PRIMARY.");
            if (index >= tokens.Count || tokens[index].Kind != SqliteDdlTokenKind.OpenParen)
            {
                SqliteReport(
                    "DDL100",
                    "A SQLite PRIMARY KEY constraint must list its columns.",
                    tokens[Math.Min(index, tokens.Count - 1)].Span);
                return;
            }

            index++;
            var columns = new List<SqliteDdlIdentifier>();
            while (index < tokens.Count && tokens[index].Kind != SqliteDdlTokenKind.CloseParen)
            {
                if (tokens[index].Kind == SqliteDdlTokenKind.Comma)
                {
                    index++;
                    continue;
                }

                columns.Add(SqliteReadIdentifier(tokens, ref index, "Expected a PRIMARY KEY column name."));
                if (SqliteLocalWord(tokens, index, "ASC") || SqliteLocalWord(tokens, index, "DESC"))
                {
                    index++;
                }
            }

            if (index >= tokens.Count)
            {
                SqliteReport(
                    "DDL100",
                    "A SQLite PRIMARY KEY constraint must close its column list.",
                    tokens[tokens.Count - 1].Span);
                return;
            }

            primaryKeys.Add(columns);
            return;
        }

        if (SqliteLocalWord(tokens, index, "UNIQUE") ||
            SqliteLocalWord(tokens, index, "CHECK") ||
            SqliteLocalWord(tokens, index, "FOREIGN"))
        {
            // These constraints are valid SQLite DDL, but DatabaseSchema stores
            // only columns and primary-key metadata. Their complete token item
            // has already been isolated, so ignoring it cannot lose table shape.
            SqliteRejectQualifiedReferences(tokens);
            return;
        }

            SqliteReport(
                "DDL101",
                "This SQLite table constraint is not supported by the schema analyzer.",
                tokens[Math.Min(index, tokens.Count - 1)].Span);
        }
        catch (SqliteDdlLocalParseException exception)
        {
            SqliteReport("DDL100", exception.Message, exception.Span);
        }
    }

    private static bool SqliteIsTableConstraint(IReadOnlyList<SqliteDdlToken> tokens)
    {
        var index = 0;
        if (SqliteLocalWord(tokens, index, "CONSTRAINT"))
        {
            index += 2;
        }

        return SqliteLocalWord(tokens, index, "PRIMARY") ||
            SqliteLocalWord(tokens, index, "UNIQUE") ||
            SqliteLocalWord(tokens, index, "CHECK") ||
            SqliteLocalWord(tokens, index, "FOREIGN");
    }

    private SqliteDdlQualifiedName SqliteParseTableName(string message)
    {
        var first = SqliteParseIdentifier(message);
        if (!SqliteMatch(SqliteDdlTokenKind.Dot))
        {
            return new SqliteDdlQualifiedName(null, first, first.Span);
        }

        var name = SqliteParseIdentifier("Expected a table name after '.'.");
        SqliteReport(
            "DDL101",
            "SQLite compile-time analysis does not support non-empty schema names.",
            first.Span);
        return new SqliteDdlQualifiedName(
            first,
            name,
            SqliteFromBounds(first.Span.Start, SqliteEndOf(name.Span)));
    }

    private SqliteDdlIdentifier SqliteParseIdentifier(string message)
    {
        var token = Current;
        if (SqliteIsIdentifier(token))
        {
            SqliteAdvance();
            return new SqliteDdlIdentifier(
                token.Value ?? token.Text,
                token.Kind == SqliteDdlTokenKind.QuotedIdentifier,
                token.Span);
        }

        SqliteReport("DDL100", message, token.Span);
        if (Current.Kind != SqliteDdlTokenKind.End)
        {
            SqliteAdvance();
        }

        return new SqliteDdlIdentifier(token.Text, false, token.Span);
    }

    private static SqliteDdlIdentifier SqliteReadIdentifier(
        IReadOnlyList<SqliteDdlToken> tokens,
        ref int index,
        string message)
    {
        if (index < tokens.Count && SqliteIsIdentifier(tokens[index]))
        {
            var token = tokens[index++];
            return new SqliteDdlIdentifier(
                token.Value ?? token.Text,
                token.Kind == SqliteDdlTokenKind.QuotedIdentifier,
                token.Span);
        }

        var fallback = index < tokens.Count ? tokens[index] : tokens[tokens.Count - 1];
        throw new SqliteDdlLocalParseException(message, fallback.Span);
    }

    private static void SqliteRequireLocalKeyword(
        IReadOnlyList<SqliteDdlToken> tokens,
        ref int index,
        string keyword,
        string message)
    {
        if (!SqliteLocalWord(tokens, index, keyword))
        {
            throw new SqliteDdlLocalParseException(
                message,
                index < tokens.Count ? tokens[index].Span : tokens[tokens.Count - 1].Span);
        }

        index++;
    }

    private static int SqliteConsumeDefaultExpression(
        IReadOnlyList<SqliteDdlToken> tokens,
        int index)
    {
        var start = index;
        var depth = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (depth == 0 && SqliteIsDefaultBoundary(tokens, index) && index != start)
            {
                break;
            }

            if (token.Kind == SqliteDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == SqliteDdlTokenKind.CloseParen)
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }

            index++;
        }

        return index;
    }

    private static bool SqliteIsDefaultBoundary(
        IReadOnlyList<SqliteDdlToken> tokens,
        int index)
    {
        var token = tokens[index];
        return token.SqliteIsWord("CONSTRAINT") || token.SqliteIsWord("PRIMARY") ||
            token.SqliteIsWord("NOT") && SqliteLocalWord(tokens, index + 1, "NULL") ||
            token.SqliteIsWord("UNIQUE") || token.SqliteIsWord("CHECK") ||
            token.SqliteIsWord("REFERENCES") || token.SqliteIsWord("COLLATE") ||
            token.SqliteIsWord("GENERATED") || token.SqliteIsWord("DEFERRABLE");
    }

    private static void SqliteConsumeLocalParenthesized(
        IReadOnlyList<SqliteDdlToken> tokens,
        ref int index,
        string context)
    {
        if (index >= tokens.Count || tokens[index].Kind != SqliteDdlTokenKind.OpenParen)
        {
            throw new SqliteDdlLocalParseException(
                context + " requires a parenthesized expression.",
                index < tokens.Count ? tokens[index].Span : tokens[tokens.Count - 1].Span);
        }

        var depth = 0;
        while (index < tokens.Count)
        {
            if (tokens[index].Kind == SqliteDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (tokens[index].Kind == SqliteDdlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    index++;
                    return;
                }
            }

            index++;
        }

        throw new SqliteDdlLocalParseException(
            context + " has an unterminated parenthesized expression.",
            tokens[tokens.Count - 1].Span);
    }

    private static void SqliteConsumeReferenceTail(
        IReadOnlyList<SqliteDdlToken> tokens,
        ref int index)
    {
        var depth = 0;
        while (index < tokens.Count)
        {
            if (depth == 0 && SqliteIsColumnConstraintStart(tokens[index]))
            {
                return;
            }

            if (tokens[index].Kind == SqliteDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (tokens[index].Kind == SqliteDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }

            index++;
        }
    }

    private static void SqliteConsumeConflictClause(
        IReadOnlyList<SqliteDdlToken> tokens,
        ref int index)
    {
        if (!SqliteLocalWord(tokens, index, "ON") || !SqliteLocalWord(tokens, index + 1, "CONFLICT"))
        {
            return;
        }

        index += 2;
        if (index < tokens.Count)
        {
            index++;
        }
    }

    private void SqliteRejectQualifiedReferences(IReadOnlyList<SqliteDdlToken> tokens)
    {
        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (!tokens[index].SqliteIsWord("REFERENCES") ||
                !SqliteIsIdentifier(tokens[index + 1]) ||
                tokens[index + 2].Kind != SqliteDdlTokenKind.Dot)
            {
                continue;
            }

            SqliteReport(
                "DDL101",
                "SQLite compile-time analysis does not support schema-qualified REFERENCES names.",
                tokens[index + 2].Span);
            return;
        }
    }

    private IReadOnlyList<SqliteDdlToken> SqliteReadBalancedBody()
    {
        var body = new List<SqliteDdlToken>();
        var depth = 1;
        while (Current.Kind != SqliteDdlTokenKind.End)
        {
            var token = SqliteAdvance();
            if (token.Kind == SqliteDdlTokenKind.OpenParen)
            {
                depth++;
                body.Add(token);
                continue;
            }

            if (token.Kind == SqliteDdlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    return body;
                }

                body.Add(token);
                continue;
            }

            body.Add(token);
        }

        SqliteReport(
            "DDL100",
            "CREATE TABLE has an unterminated column list.",
            new SourceSpan(_sql.Length, 0));
        return body;
    }

    private IReadOnlyList<SqliteDdlToken> SqliteReadToStatementEnd()
    {
        var result = new List<SqliteDdlToken>();
        var depth = 0;
        while (Current.Kind != SqliteDdlTokenKind.End && Current.Kind != SqliteDdlTokenKind.Semicolon)
        {
            var token = SqliteAdvance();
            if (token.Kind == SqliteDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == SqliteDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }

            result.Add(token);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<SqliteDdlToken>> SqliteSplitTopLevel(
        IReadOnlyList<SqliteDdlToken> tokens)
    {
        var result = new List<IReadOnlyList<SqliteDdlToken>>();
        var current = new List<SqliteDdlToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == SqliteDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == SqliteDdlTokenKind.CloseParen)
            {
                depth--;
            }

            if (depth == 0 && token.Kind == SqliteDdlTokenKind.Comma)
            {
                result.Add(current);
                current = new List<SqliteDdlToken>();
            }
            else
            {
                current.Add(token);
            }
        }

        result.Add(current);
        return result;
    }

    private bool SqliteIsSchemaNeutralStatement()
    {
        if (Current.Kind != SqliteDdlTokenKind.Identifier)
        {
            return false;
        }

        var first = Current.Text;
        if (string.Equals(first, "CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var next = SqlitePeek(1);
            return next.SqliteIsWord("INDEX") || next.SqliteIsWord("TRIGGER") ||
                next.SqliteIsWord("UNIQUE") && SqlitePeek(2).SqliteIsWord("INDEX");
        }

        if (string.Equals(first, "DROP", StringComparison.OrdinalIgnoreCase))
        {
            var next = SqlitePeek(1);
            return next.SqliteIsWord("INDEX") || next.SqliteIsWord("TRIGGER");
        }

        return string.Equals(first, "INSERT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "UPDATE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "DELETE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "REPLACE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "PRAGMA", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "BEGIN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "COMMIT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "END", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "SAVEPOINT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "RELEASE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "VACUUM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "ANALYZE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "REINDEX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "ATTACH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "DETACH", StringComparison.OrdinalIgnoreCase);
    }

    private void SqliteValidateSchemaNeutralQualification()
    {
        var cursor = _position + 1;
        if (Current.SqliteIsWord("CREATE") && SqliteTokenAt(cursor).SqliteIsWord("UNIQUE"))
        {
            cursor++;
        }

        if ((Current.SqliteIsWord("CREATE") &&
             (SqliteTokenAt(cursor).SqliteIsWord("INDEX") || SqliteTokenAt(cursor).SqliteIsWord("TRIGGER"))) ||
            (Current.SqliteIsWord("DROP") &&
             (SqliteTokenAt(cursor).SqliteIsWord("INDEX") || SqliteTokenAt(cursor).SqliteIsWord("TRIGGER"))))
        {
            cursor++;
            if (SqliteTokenAt(cursor).SqliteIsWord("IF"))
            {
                if (SqliteTokenAt(cursor + 1).SqliteIsWord("NOT"))
                {
                    cursor += 3;
                }
                else
                {
                    cursor += 2;
                }
            }

            if (SqliteIsIdentifier(SqliteTokenAt(cursor)) &&
                SqliteTokenAt(cursor + 1).Kind == SqliteDdlTokenKind.Dot)
            {
                SqliteReport(
                    "DDL101",
                    "SQLite compile-time analysis does not support non-empty schema names.",
                    SqliteTokenAt(cursor).Span);
            }
        }
    }

    private void SqliteSkipToStatementEnd()
    {
        while (Current.Kind != SqliteDdlTokenKind.Semicolon && Current.Kind != SqliteDdlTokenKind.End)
        {
            SqliteAdvance();
        }
    }

    private bool SqliteMatchIfNotExists()
    {
        if (!Current.SqliteIsWord("IF") || !SqlitePeek(1).SqliteIsWord("NOT") ||
            !SqlitePeek(2).SqliteIsWord("EXISTS"))
        {
            return false;
        }

        SqliteAdvance();
        SqliteAdvance();
        SqliteAdvance();
        return true;
    }

    private bool SqliteMatchIfExists()
    {
        if (!Current.SqliteIsWord("IF") || !SqlitePeek(1).SqliteIsWord("EXISTS"))
        {
            return false;
        }

        SqliteAdvance();
        SqliteAdvance();
        return true;
    }

    private void SqliteExpectKeyword(string keyword, string message)
    {
        if (!SqliteMatchKeyword(keyword))
        {
            SqliteReport("DDL100", message, Current.Span);
        }
    }

    private bool SqliteMatchKeyword(string keyword)
    {
        if (!Current.SqliteIsWord(keyword))
        {
            return false;
        }

        SqliteAdvance();
        return true;
    }

    private bool SqliteMatch(SqliteDdlTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        SqliteAdvance();
        return true;
    }

    private SqliteDdlToken SqliteAdvance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private SqliteDdlToken SqlitePeek(int offset)
    {
        var index = _position + offset;
        return index < _tokens.Count ? _tokens[index] : _tokens[_tokens.Count - 1];
    }

    private SqliteDdlToken SqliteTokenAt(int index) =>
        index >= 0 && index < _tokens.Count ? _tokens[index] : _tokens[_tokens.Count - 1];

    private SqliteDdlToken Current => _tokens[_position];

    private void SqliteReport(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));

    private static bool SqliteIsIdentifier(SqliteDdlToken token) =>
        token.Kind == SqliteDdlTokenKind.Identifier ||
        token.Kind == SqliteDdlTokenKind.QuotedIdentifier;

    private static bool SqliteIsColumnConstraintStart(SqliteDdlToken token) =>
        token.Kind == SqliteDdlTokenKind.Identifier &&
        (token.SqliteIsWord("CONSTRAINT") || token.SqliteIsWord("PRIMARY") ||
         token.SqliteIsWord("NOT") || token.SqliteIsWord("NULL") ||
         token.SqliteIsWord("UNIQUE") || token.SqliteIsWord("CHECK") ||
         token.SqliteIsWord("DEFAULT") || token.SqliteIsWord("COLLATE") ||
         token.SqliteIsWord("REFERENCES") || token.SqliteIsWord("GENERATED") ||
         token.SqliteIsWord("AUTOINCREMENT"));

    private static bool SqliteLocalWord(
        IReadOnlyList<SqliteDdlToken> tokens,
        int index,
        string word) =>
        index >= 0 && index < tokens.Count && tokens[index].SqliteIsWord(word);

    private static string SqliteRenderType(IEnumerable<SqliteDdlToken> tokens)
    {
        var rendered = SqliteRenderTokens(tokens, true);
        return rendered.Length == 0 ? "BLOB" : rendered;
    }

    private static string SqliteRenderTokens(
        IEnumerable<SqliteDdlToken> tokens,
        bool upperCaseWords)
    {
        var builder = new StringBuilder();
        SqliteDdlToken? previous = null;
        foreach (var token in tokens)
        {
            var text = upperCaseWords && token.Kind == SqliteDdlTokenKind.Identifier
                ? token.Text.ToUpperInvariant()
                : token.Text;
            var noSpaceBefore = text == ")" || text == "," || text == "." ||
                previous.HasValue && (previous.Value.Text == "(" || previous.Value.Text == ".");
            var noSpaceAfterPrevious = previous.HasValue && previous.Value.Text == ",";
            if (builder.Length != 0 && !noSpaceBefore && !noSpaceAfterPrevious && text != "(")
            {
                builder.Append(' ');
            }

            builder.Append(text);
            previous = token;
        }

        return builder.ToString().Trim();
    }

    private static int SqliteEndOf(SourceSpan span) => span.Start + span.Length;

    private static SourceSpan SqliteFromBounds(int start, int end) =>
        new SourceSpan(start, Math.Max(0, end - start));

    private sealed class SqliteDdlLocalParseException : Exception
    {
        internal SqliteDdlLocalParseException(string message, SourceSpan span)
            : base(message)
        {
            Span = span;
        }

        internal SourceSpan Span { get; }
    }
}
