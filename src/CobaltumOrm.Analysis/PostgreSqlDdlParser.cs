using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

internal abstract class DdlStatement
{
    protected DdlStatement(SourceSpan span, bool isValid)
    {
        Span = span;
        IsValid = isValid;
    }

    internal SourceSpan Span { get; }
    internal bool IsValid { get; }
}

internal sealed class CreateTableStatement : DdlStatement
{
    internal CreateTableStatement(
        SqlQualifiedName table,
        bool ifNotExists,
        IReadOnlyList<DdlColumnDefinition> columns,
        IReadOnlyList<IReadOnlyList<SqlIdentifier>> primaryKeys,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        IfNotExists = ifNotExists;
        Columns = columns;
        PrimaryKeys = primaryKeys;
    }

    internal SqlQualifiedName Table { get; }
    internal bool IfNotExists { get; }
    internal IReadOnlyList<DdlColumnDefinition> Columns { get; }
    internal IReadOnlyList<IReadOnlyList<SqlIdentifier>> PrimaryKeys { get; }
}

internal sealed class DropTableStatement : DdlStatement
{
    internal DropTableStatement(
        IReadOnlyList<SqlQualifiedName> tables,
        bool ifExists,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Tables = tables;
        IfExists = ifExists;
    }

    internal IReadOnlyList<SqlQualifiedName> Tables { get; }
    internal bool IfExists { get; }
}

internal sealed class AlterTableStatement : DdlStatement
{
    internal AlterTableStatement(
        SqlQualifiedName table,
        bool ifExists,
        IReadOnlyList<DdlAlterAction> actions,
        SourceSpan span,
        bool isValid)
        : base(span, isValid)
    {
        Table = table;
        IfExists = ifExists;
        Actions = actions;
    }

    internal SqlQualifiedName Table { get; }
    internal bool IfExists { get; }
    internal IReadOnlyList<DdlAlterAction> Actions { get; }
}

internal sealed class DdlColumnDefinition
{
    internal DdlColumnDefinition(
        SqlIdentifier name,
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

    internal SqlIdentifier Name { get; }
    internal string SqlType { get; }
    internal bool IsNullable { get; }
    internal bool IsPrimaryKey { get; }
    internal string? DefaultExpression { get; }
    internal bool IsIdentity { get; }
    internal SourceSpan Span { get; }
}

internal abstract class DdlAlterAction
{
    protected DdlAlterAction(SourceSpan span)
    {
        Span = span;
    }

    internal SourceSpan Span { get; }
}

internal sealed class AddColumnAction : DdlAlterAction
{
    internal AddColumnAction(DdlColumnDefinition column, bool ifNotExists, SourceSpan span)
        : base(span)
    {
        Column = column;
        IfNotExists = ifNotExists;
    }

    internal DdlColumnDefinition Column { get; }
    internal bool IfNotExists { get; }
}

internal sealed class DropColumnAction : DdlAlterAction
{
    internal DropColumnAction(SqlIdentifier column, bool ifExists, SourceSpan span)
        : base(span)
    {
        Column = column;
        IfExists = ifExists;
    }

    internal SqlIdentifier Column { get; }
    internal bool IfExists { get; }
}

internal sealed class RenameColumnAction : DdlAlterAction
{
    internal RenameColumnAction(SqlIdentifier oldName, SqlIdentifier newName, SourceSpan span)
        : base(span)
    {
        OldName = oldName;
        NewName = newName;
    }

    internal SqlIdentifier OldName { get; }
    internal SqlIdentifier NewName { get; }
}

internal sealed class RenameTableAction : DdlAlterAction
{
    internal RenameTableAction(SqlIdentifier newName, SourceSpan span)
        : base(span)
    {
        NewName = newName;
    }

    internal SqlIdentifier NewName { get; }
}

internal sealed class AlterColumnTypeAction : DdlAlterAction
{
    internal AlterColumnTypeAction(SqlIdentifier column, string sqlType, SourceSpan span)
        : base(span)
    {
        Column = column;
        SqlType = sqlType;
    }

    internal SqlIdentifier Column { get; }
    internal string SqlType { get; }
}

internal sealed class SetColumnNullabilityAction : DdlAlterAction
{
    internal SetColumnNullabilityAction(SqlIdentifier column, bool isNullable, SourceSpan span)
        : base(span)
    {
        Column = column;
        IsNullable = isNullable;
    }

    internal SqlIdentifier Column { get; }
    internal bool IsNullable { get; }
}

internal sealed class SetColumnDefaultAction : DdlAlterAction
{
    internal SetColumnDefaultAction(SqlIdentifier column, string? defaultExpression, SourceSpan span)
        : base(span)
    {
        Column = column;
        DefaultExpression = defaultExpression;
    }

    internal SqlIdentifier Column { get; }
    internal string? DefaultExpression { get; }
}

internal sealed class SchemaNeutralAlterAction : DdlAlterAction
{
    internal SchemaNeutralAlterAction(SourceSpan span) : base(span)
    {
    }
}

internal sealed class PostgreSqlDdlParser
{
    private readonly IReadOnlyList<DdlToken> _tokens;
    private readonly List<Diagnostic> _diagnostics;
    private readonly string _sql;
    private int _position;

    internal PostgreSqlDdlParser(IReadOnlyList<DdlToken> tokens, string sql, List<Diagnostic> diagnostics)
    {
        _tokens = tokens;
        _sql = sql;
        _diagnostics = diagnostics;
    }

    internal IReadOnlyList<DdlStatement> Parse()
    {
        var statements = new List<DdlStatement>();
        while (Current.Kind != DdlTokenKind.End)
        {
            if (Match(DdlTokenKind.Semicolon))
            {
                continue;
            }

            var statementStart = Current.Span.Start;
            var diagnosticCount = _diagnostics.Count;
            DdlStatement? statement;
            if (MatchKeyword("CREATE"))
            {
                statement = ParseCreateTable(statementStart, diagnosticCount);
            }
            else if (MatchKeyword("DROP"))
            {
                statement = ParseDropTable(statementStart, diagnosticCount);
            }
            else if (MatchKeyword("ALTER"))
            {
                statement = ParseAlterTable(statementStart, diagnosticCount);
            }
            else
            {
                Report("DDL100", "Only CREATE TABLE, DROP TABLE, and ALTER TABLE statements are supported.", Current.Span);
                SkipToStatementEnd();
                statement = null;
            }

            if (Current.Kind == DdlTokenKind.Semicolon)
            {
                Advance();
            }
            else if (Current.Kind != DdlTokenKind.End)
            {
                Report("DDL100", "Expected a semicolon between migration statements.", Current.Span);
                SkipToStatementEnd();
            }

            if (statement != null && statement.IsValid && _diagnostics.Count == diagnosticCount)
            {
                statements.Add(statement);
            }
        }

        return statements;
    }

    private DdlStatement? ParseCreateTable(int start, int diagnosticCount)
    {
        if (!MatchKeyword("TABLE"))
        {
            Report("DDL100", "Expected TABLE after CREATE.", Current.Span);
            SkipToStatementEnd();
            return null;
        }

        var ifNotExists = MatchIfNotExists();
        var table = ParseTableName("Expected a table name after CREATE TABLE.");
        if (!Match(DdlTokenKind.OpenParen))
        {
            Report("DDL100", "Expected '(' with the CREATE TABLE column definitions.", Current.Span);
            SkipToStatementEnd();
            return null;
        }

        var columns = new List<DdlColumnDefinition>();
        var primaryKeys = new List<IReadOnlyList<SqlIdentifier>>();
        while (Current.Kind != DdlTokenKind.CloseParen && Current.Kind != DdlTokenKind.End)
        {
            if (Match(DdlTokenKind.Comma))
            {
                continue;
            }

            if (IsKeyword("CONSTRAINT"))
            {
                Advance();
                ParseIdentifier("Expected a constraint name.");
                ParseTableConstraint(primaryKeys);
            }
            else if (IsKeyword("PRIMARY"))
            {
                ParsePrimaryKey(primaryKeys);
            }
            else if (IsUnsupportedTableConstraint())
            {
                SkipToTableItemEnd();
            }
            else
            {
                var column = ParseColumnDefinition();
                if (column != null)
                {
                    columns.Add(column);
                }
            }

            if (Current.Kind != DdlTokenKind.Comma && Current.Kind != DdlTokenKind.CloseParen &&
                Current.Kind != DdlTokenKind.End)
            {
                Report("DDL100", "Expected a comma between table definitions.", Current.Span);
                SkipToTableItemEnd();
            }

            Match(DdlTokenKind.Comma);
        }

        var close = Expect(DdlTokenKind.CloseParen, "Expected ')' after CREATE TABLE definitions.");
        var span = FromBounds(start, EndOf(close));
        return new CreateTableStatement(
            table,
            ifNotExists,
            columns,
            primaryKeys,
            span,
            _diagnostics.Count == diagnosticCount);
    }

    private DdlStatement? ParseDropTable(int start, int diagnosticCount)
    {
        if (!MatchKeyword("TABLE"))
        {
            Report("DDL100", "Expected TABLE after DROP.", Current.Span);
            SkipToStatementEnd();
            return null;
        }

        var ifExists = MatchIfExists();
        var tables = new List<SqlQualifiedName>();
        tables.Add(ParseTableName("Expected a table name after DROP TABLE."));
        while (Match(DdlTokenKind.Comma))
        {
            tables.Add(ParseTableName("Expected another table name after ','."));
        }

        if (IsKeyword("CASCADE") || IsKeyword("RESTRICT"))
        {
            Advance();
        }

        var end = tables.Count == 0 ? Current.Span.Start : EndOf(tables[tables.Count - 1].Span);
        return new DropTableStatement(
            tables,
            ifExists,
            FromBounds(start, end),
            _diagnostics.Count == diagnosticCount);
    }

    private DdlStatement? ParseAlterTable(int start, int diagnosticCount)
    {
        if (!MatchKeyword("TABLE"))
        {
            Report("DDL100", "Expected TABLE after ALTER.", Current.Span);
            SkipToStatementEnd();
            return null;
        }

        var ifExists = MatchIfExists();
        var table = ParseTableName("Expected a table name after ALTER TABLE.");
        var actions = new List<DdlAlterAction>();
        do
        {
            var action = ParseAlterAction();
            if (action != null)
            {
                actions.Add(action);
            }

            if (Current.Kind != DdlTokenKind.Comma)
            {
                break;
            }

            Advance();
        }
        while (Current.Kind != DdlTokenKind.Semicolon && Current.Kind != DdlTokenKind.End);

        if (actions.Count == 0)
        {
            Report("DDL100", "ALTER TABLE requires a supported table change.", Current.Span);
        }

        var end = actions.Count == 0
            ? EndOf(table.Span)
            : EndOf(actions[actions.Count - 1].Span);
        return new AlterTableStatement(
            table,
            ifExists,
            actions,
            FromBounds(start, end),
            _diagnostics.Count == diagnosticCount);
    }

    private DdlAlterAction? ParseAlterAction()
    {
        var start = Current.Span.Start;
        if (MatchKeyword("ADD"))
        {
            if (IsKeyword("CONSTRAINT") || IsKeyword("PRIMARY") || IsKeyword("UNIQUE") ||
                IsKeyword("FOREIGN") || IsKeyword("CHECK") || IsKeyword("EXCLUDE"))
            {
                SkipToActionEnd();
                return new SchemaNeutralAlterAction(FromBounds(start, Current.Span.Start));
            }

            MatchKeyword("COLUMN");
            var ifNotExists = MatchIfNotExists();
            var column = ParseColumnDefinition();
            return column == null ? null : new AddColumnAction(column, ifNotExists, FromBounds(start, EndOf(column.Span)));
        }

        if (MatchKeyword("DROP"))
        {
            if (MatchKeyword("CONSTRAINT"))
            {
                MatchIfExists();
                ParseIdentifier("Expected a constraint name after DROP CONSTRAINT.");
                if (IsKeyword("CASCADE") || IsKeyword("RESTRICT")) Advance();
                return new SchemaNeutralAlterAction(FromBounds(start, Current.Span.Start));
            }

            MatchKeyword("COLUMN");
            var ifExists = MatchIfExists();
            var column = ParseIdentifier("Expected a column name after ALTER TABLE DROP COLUMN.");
            if (IsKeyword("CASCADE") || IsKeyword("RESTRICT"))
            {
                Advance();
            }

            return new DropColumnAction(column, ifExists, FromBounds(start, EndOf(column.Span)));
        }

        if (MatchKeyword("RENAME"))
        {
            if (MatchKeyword("COLUMN"))
            {
                var oldName = ParseIdentifier("Expected the old column name after RENAME COLUMN.");
                ExpectKeyword("TO", "Expected TO in RENAME COLUMN.");
                var newName = ParseIdentifier("Expected the new column name in RENAME COLUMN.");
                return new RenameColumnAction(oldName, newName, FromBounds(start, EndOf(newName.Span)));
            }

            if (MatchKeyword("CONSTRAINT"))
            {
                ParseIdentifier("Expected the old constraint name after RENAME CONSTRAINT.");
                ExpectKeyword("TO", "Expected TO in RENAME CONSTRAINT.");
                ParseIdentifier("Expected the new constraint name in RENAME CONSTRAINT.");
                return new SchemaNeutralAlterAction(FromBounds(start, Current.Span.Start));
            }

            if (IsKeyword("TO"))
            {
                ExpectKeyword("TO", "Expected TO in RENAME TABLE.");
                var newTableName = ParseIdentifier("Expected the new table name in RENAME TABLE.");
                return new RenameTableAction(newTableName, FromBounds(start, EndOf(newTableName.Span)));
            }

            if (IsIdentifier(Current))
            {
                var oldName = ParseIdentifier("Expected a column name after RENAME.");
                if (MatchKeyword("TO"))
                {
                    var newName = ParseIdentifier("Expected the new column name in RENAME.");
                    return new RenameColumnAction(oldName, newName, FromBounds(start, EndOf(newName.Span)));
                }

                Report("DDL100", "Expected TO in RENAME COLUMN.", Current.Span);
                SkipToActionEnd();
                return null;
            }

            ExpectKeyword("TO", "Expected TO in RENAME TABLE.");
            var tableName = ParseIdentifier("Expected the new table name in RENAME TABLE.");
            return new RenameTableAction(tableName, FromBounds(start, EndOf(tableName.Span)));
        }

        if (MatchKeyword("ALTER"))
        {
            if (MatchKeyword("CONSTRAINT"))
            {
                SkipToActionEnd();
                return new SchemaNeutralAlterAction(FromBounds(start, Current.Span.Start));
            }

            MatchKeyword("COLUMN");
            var column = ParseIdentifier("Expected a column name after ALTER COLUMN.");
            if (MatchKeyword("TYPE"))
            {
                var sqlType = ParseTypeName();
                if (MatchKeyword("USING"))
                {
                    SkipToActionEnd();
                }

                return new AlterColumnTypeAction(column, sqlType, FromBounds(start, Current.Span.Start));
            }

            if (MatchKeyword("SET"))
            {
                if (MatchKeyword("NOT"))
                {
                    ExpectKeyword("NULL", "Expected NULL after SET NOT.");
                    return new SetColumnNullabilityAction(column, false, FromBounds(start, EndOf(column.Span)));
                }

                if (MatchKeyword("DEFAULT"))
                {
                    var expressionStart = Current.Span.Start;
                    SkipToActionEnd();
                    var expressionEnd = Current.Span.Start;
                    var defaultExpression = expressionEnd <= expressionStart
                        ? null
                        : _sql.Substring(expressionStart, expressionEnd - expressionStart).Trim();
                    if (string.IsNullOrEmpty(defaultExpression))
                    {
                        Report("DDL100", "SET DEFAULT requires an expression.", Current.Span);
                    }

                    return new SetColumnDefaultAction(
                        column,
                        defaultExpression,
                        FromBounds(start, expressionEnd));
                }

                if (MatchKeyword("STATISTICS") || MatchKeyword("STORAGE") || MatchKeyword("COMPRESSION") ||
                    MatchKeyword("GENERATED"))
                {
                    SkipToActionEnd();
                    return new SchemaNeutralAlterAction(FromBounds(start, Current.Span.Start));
                }

                Report("DDL101", "Unsupported ALTER COLUMN SET action.", Current.Span);
                SkipToActionEnd();
                return null;
            }

            if (MatchKeyword("DROP"))
            {
                if (MatchKeyword("NOT"))
                {
                    ExpectKeyword("NULL", "Expected NULL after DROP NOT.");
                    return new SetColumnNullabilityAction(column, true, FromBounds(start, EndOf(column.Span)));
                }

                if (MatchKeyword("DEFAULT"))
                {
                    return new SetColumnDefaultAction(column, null, FromBounds(start, Current.Span.Start));
                }

                if (MatchKeyword("IDENTITY") || MatchKeyword("EXPRESSION"))
                {
                    SkipToActionEnd();
                    return new SchemaNeutralAlterAction(FromBounds(start, Current.Span.Start));
                }

                Report("DDL101", "Unsupported ALTER COLUMN DROP action.", Current.Span);
                SkipToActionEnd();
                return null;
            }

            Report("DDL101", "Only TYPE, SET NOT NULL, and DROP NOT NULL are supported for ALTER COLUMN.", Current.Span);
            SkipToActionEnd();
            return null;
        }

        Report("DDL100", "Expected ADD, DROP, RENAME, or ALTER in ALTER TABLE.", Current.Span);
        SkipToActionEnd();
        return null;
    }

    private DdlColumnDefinition? ParseColumnDefinition()
    {
        var start = Current.Span.Start;
        var name = ParseIdentifier("Expected a column name.");
        var sqlType = ParseTypeName();
        if (string.IsNullOrEmpty(sqlType))
        {
            SkipToTableItemEnd();
            return null;
        }

        var isNullable = true;
        var isPrimaryKey = false;
        var isIdentity = false;
        string? defaultExpression = null;
        while (!IsDefinitionEnd())
        {
            if (MatchKeyword("NULL"))
            {
                isNullable = true;
                continue;
            }

            if (MatchKeyword("NOT"))
            {
                if (MatchKeyword("NULL"))
                {
                    isNullable = false;
                }
                else
                {
                    Report("DDL101", "Expected NULL after NOT in a column definition.", Current.Span);
                    SkipToTableItemEnd();
                }

                continue;
            }

            if (MatchKeyword("PRIMARY"))
            {
                ExpectKeyword("KEY", "Expected KEY after PRIMARY in a column definition.");
                isNullable = false;
                isPrimaryKey = true;
                continue;
            }

            if (MatchKeyword("CONSTRAINT"))
            {
                ParseIdentifier("Expected a constraint name.");
                if (IsKeyword("PRIMARY"))
                {
                    MatchKeyword("PRIMARY");
                    ExpectKeyword("KEY", "Expected KEY after PRIMARY in a column definition.");
                    isNullable = false;
                    isPrimaryKey = true;
                }
                else
                {
                    SkipToTableItemEnd();
                }

                continue;
            }

            if (MatchKeyword("DEFAULT"))
            {
                defaultExpression = ReadDefaultExpression();
                continue;
            }

            if (MatchKeyword("GENERATED"))
            {
                if (MatchKeyword("BY"))
                {
                    ExpectKeyword("DEFAULT", "Expected DEFAULT after GENERATED BY.");
                }
                else if (!MatchKeyword("ALWAYS"))
                {
                    Report("DDL101", "Expected ALWAYS or BY DEFAULT after GENERATED.", Current.Span);
                    SkipToTableItemEnd();
                    continue;
                }

                ExpectKeyword("AS", "Expected AS in an identity column definition.");
                if (MatchKeyword("IDENTITY"))
                {
                    isIdentity = true;
                }
                else if (Current.Kind == DdlTokenKind.OpenParen)
                {
                    SkipBalancedParentheses();
                    MatchKeyword("STORED");
                    MatchKeyword("VIRTUAL");
                }
                else
                {
                    Report("DDL101", "Expected IDENTITY or a generated-column expression after GENERATED AS.", Current.Span);
                }
                continue;
            }

            if (IsKeyword("UNIQUE") || IsKeyword("REFERENCES") || IsKeyword("CHECK") ||
                IsKeyword("EXCLUDE") || IsKeyword("COLLATE"))
            {
                SkipToTableItemEnd();
                continue;
            }

            Report("DDL101", "This column constraint is not supported by the schema analyzer.", Current.Span);
            SkipToTableItemEnd();
        }

        return new DdlColumnDefinition(
            name,
            sqlType,
            isNullable,
            isPrimaryKey,
            defaultExpression,
            isIdentity,
            FromBounds(start, Current.Span.Start));
    }

    private string? ReadDefaultExpression()
    {
        if (IsDefinitionEnd() || IsDefaultConstraintBoundary())
        {
            Report("DDL100", "DEFAULT requires an expression.", Current.Span);
            return null;
        }

        var start = Current.Span.Start;
        var end = start;
        var depth = 0;
        while (Current.Kind != DdlTokenKind.End)
        {
            if (Current.Kind == DdlTokenKind.Invalid)
            {
                Report("DDL100", "The migration contains an invalid token.", Current.Span);
                Advance();
                continue;
            }

            if (depth == 0 && (Current.Kind == DdlTokenKind.Comma || Current.Kind == DdlTokenKind.CloseParen ||
                Current.Kind == DdlTokenKind.Semicolon || IsDefaultConstraintBoundary()))
            {
                break;
            }

            if (Current.Kind == DdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (Current.Kind == DdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }
            else if (Current.Kind == DdlTokenKind.Symbol && Current.Text == "[")
            {
                depth++;
            }
            else if (Current.Kind == DdlTokenKind.Symbol && Current.Text == "]" && depth > 0)
            {
                depth--;
            }

            end = EndOf(Current.Span);
            Advance();
        }

        return end <= start ? null : _sql.Substring(start, end - start).Trim();
    }

    private string ParseTypeName()
    {
        if (!IsIdentifier(Current))
        {
            Report("DDL100", "Expected a PostgreSQL type name.", Current.Span);
            return string.Empty;
        }

        var firstToken = Advance();
        var first = TypeWord(firstToken);
        var builder = new StringBuilder(first);
        if (Match(DdlTokenKind.Dot))
        {
            var schemaType = ParseIdentifier("Expected a type name after '.'.");
            builder.Append('.').Append(TypeWord(schemaType));
        }

        AppendTypeModifier(builder);
        var firstLower = first.ToLowerInvariant();
        if (firstLower == "double" && IsKeyword("PRECISION"))
        {
            builder.Append(' ').Append(TypeWord(Advance()));
            AppendTypeModifier(builder);
        }
        else if (firstLower == "character" && (IsKeyword("VARYING") || IsKeyword("LARGE")))
        {
            builder.Append(' ').Append(TypeWord(Advance()));
            AppendTypeModifier(builder);
        }
        else if ((firstLower == "timestamp" || firstLower == "time") &&
            (IsKeyword("WITH") || IsKeyword("WITHOUT")))
        {
            builder.Append(' ').Append(TypeWord(Advance()));
            if (IsKeyword("TIME"))
            {
                builder.Append(' ').Append(TypeWord(Advance()));
            }

            if (IsKeyword("ZONE"))
            {
                builder.Append(' ').Append(TypeWord(Advance()));
            }
        }

        if (Current.Kind == DdlTokenKind.Symbol && Current.Text == "[")
        {
            builder.Append(Advance().Text);
            if (Current.Kind == DdlTokenKind.Symbol && Current.Text == "]")
            {
                builder.Append(Advance().Text);
            }
        }

        return builder.ToString();
    }

    private void AppendTypeModifier(StringBuilder builder)
    {
        if (!Match(DdlTokenKind.OpenParen))
        {
            return;
        }

        builder.Append('(');
        var depth = 1;
        while (Current.Kind != DdlTokenKind.End && depth > 0)
        {
            if (Current.Kind == DdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (Current.Kind == DdlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    builder.Append(')');
                    Advance();
                    break;
                }
            }

            builder.Append(Current.Text);
            Advance();
        }
    }

    private void ParseTableConstraint(List<IReadOnlyList<SqlIdentifier>> primaryKeys)
    {
        if (IsKeyword("PRIMARY"))
        {
            ParsePrimaryKey(primaryKeys);
            return;
        }

        SkipToTableItemEnd();
    }

    private void SkipBalancedParentheses()
    {
        if (!Match(DdlTokenKind.OpenParen))
        {
            return;
        }

        var depth = 1;
        while (Current.Kind != DdlTokenKind.End && depth > 0)
        {
            if (Current.Kind == DdlTokenKind.OpenParen) depth++;
            else if (Current.Kind == DdlTokenKind.CloseParen) depth--;
            Advance();
        }
    }

    private void ParsePrimaryKey(List<IReadOnlyList<SqlIdentifier>> primaryKeys)
    {
        MatchKeyword("PRIMARY");
        ExpectKeyword("KEY", "Expected KEY after PRIMARY.");
        primaryKeys.Add(ParseIdentifierList("Expected a column list after PRIMARY KEY."));
    }

    private IReadOnlyList<SqlIdentifier> ParseIdentifierList(string message)
    {
        var result = new List<SqlIdentifier>();
        if (!Match(DdlTokenKind.OpenParen))
        {
            Report("DDL100", message, Current.Span);
            return result;
        }

        if (Current.Kind != DdlTokenKind.CloseParen)
        {
            do
            {
                result.Add(ParseIdentifier("Expected a column name in the column list."));
            }
            while (Match(DdlTokenKind.Comma));
        }

        Expect(DdlTokenKind.CloseParen, "Expected ')' after the column list.");
        return result;
    }

    private bool IsDefinitionEnd() =>
        Current.Kind == DdlTokenKind.Comma || Current.Kind == DdlTokenKind.CloseParen ||
        Current.Kind == DdlTokenKind.Semicolon || Current.Kind == DdlTokenKind.End;

    private bool IsUnsupportedTableConstraint() =>
        IsKeyword("UNIQUE") || IsKeyword("FOREIGN") || IsKeyword("CHECK") || IsKeyword("EXCLUDE");

    private bool IsDefaultConstraintBoundary()
    {
        if (IsKeyword("NOT"))
        {
            return IsKeyword(Peek(1), "NULL");
        }

        if (IsKeyword("PRIMARY"))
        {
            return IsKeyword(Peek(1), "KEY");
        }

        return IsKeyword("CONSTRAINT") || IsKeyword("UNIQUE") || IsKeyword("REFERENCES") ||
            IsKeyword("CHECK") || IsKeyword("GENERATED") || IsKeyword("COLLATE");
    }

    private bool MatchIfNotExists() => MatchKeyword("IF") && MatchKeyword("NOT") && MatchKeyword("EXISTS");

    private bool MatchIfExists() => MatchKeyword("IF") && MatchKeyword("EXISTS");

    private SqlIdentifier ParseIdentifier(string message)
    {
        if (IsIdentifier(Current))
        {
            var token = Advance();
            return new SqlIdentifier(
                token.Value ?? token.Text,
                token.Kind == DdlTokenKind.QuotedIdentifier,
                token.Span);
        }

        Report("DDL100", message, Current.Span);
        var fallback = Current;
        if (Current.Kind != DdlTokenKind.End)
        {
            Advance();
        }

        return new SqlIdentifier(fallback.Text, false, fallback.Span);
    }

    private SqlQualifiedName ParseTableName(string message)
    {
        var first = ParseIdentifier(message);
        if (!Match(DdlTokenKind.Dot))
        {
            return new SqlQualifiedName(null, first, first.Span);
        }

        var name = ParseIdentifier("Expected a table name after '.'.");
        return new SqlQualifiedName(first, name, FromBounds(first.Span.Start, EndOf(name.Span)));
    }

    private void ExpectKeyword(string keyword, string message)
    {
        if (!MatchKeyword(keyword))
        {
            Report("DDL100", message, Current.Span);
        }
    }

    private DdlToken Expect(DdlTokenKind kind, string message)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        Report("DDL100", message, Current.Span);
        return new DdlToken(kind, string.Empty, null, new SourceSpan(Current.Span.Start, 0));
    }

    private bool MatchKeyword(string keyword)
    {
        if (!IsKeyword(keyword))
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool IsKeyword(string keyword) =>
        Current.Kind == DdlTokenKind.Identifier &&
        string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsKeyword(DdlToken token, string keyword) =>
        token.Kind == DdlTokenKind.Identifier &&
        string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifier(DdlToken token) =>
        token.Kind == DdlTokenKind.Identifier || token.Kind == DdlTokenKind.QuotedIdentifier;

    private static string TypeWord(DdlToken token) =>
        token.Kind == DdlTokenKind.QuotedIdentifier ? token.Value ?? token.Text : token.Text.ToLowerInvariant();

    private static string TypeWord(SqlIdentifier identifier) =>
        identifier.IsQuoted ? identifier.Name : identifier.Name.ToLowerInvariant();

    private void SkipToStatementEnd()
    {
        while (Current.Kind != DdlTokenKind.Semicolon && Current.Kind != DdlTokenKind.End)
        {
            Advance();
        }
    }

    private void SkipToTableItemEnd()
    {
        var depth = 0;
        while (Current.Kind != DdlTokenKind.End)
        {
            if (depth == 0 && (Current.Kind == DdlTokenKind.Comma || Current.Kind == DdlTokenKind.CloseParen ||
                Current.Kind == DdlTokenKind.Semicolon))
            {
                return;
            }

            if (Current.Kind == DdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (Current.Kind == DdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }

            Advance();
        }
    }

    private void SkipToActionEnd()
    {
        var depth = 0;
        while (Current.Kind != DdlTokenKind.End && Current.Kind != DdlTokenKind.Semicolon)
        {
            if (depth == 0 && Current.Kind == DdlTokenKind.Comma)
            {
                return;
            }

            if (Current.Kind == DdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (Current.Kind == DdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }

            Advance();
        }
    }

    private void Report(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));

    private bool Match(DdlTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private DdlToken Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private DdlToken Peek(int offset)
    {
        var index = _position + offset;
        return index < _tokens.Count ? _tokens[index] : _tokens[_tokens.Count - 1];
    }

    private DdlToken Current => _tokens[_position];

    private static int EndOf(SourceSpan span) => span.Start + span.Length;

    private static int EndOf(DdlToken token) => token.Span.Start + token.Span.Length;

    private static SourceSpan FromBounds(int start, int end) => new SourceSpan(start, Math.Max(0, end - start));
}
