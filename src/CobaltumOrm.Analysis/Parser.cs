using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

internal sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly List<Diagnostic> _diagnostics;
    private readonly QueryTypeProfile _types;
    private readonly bool _supportsPostgreSqlArrays;
    private int _position;

    internal Parser(IReadOnlyList<Token> tokens, List<Diagnostic> diagnostics)
        : this(tokens, diagnostics, QueryDialectProfiles.PostgreSql)
    {
    }

    internal Parser(
        IReadOnlyList<Token> tokens,
        List<Diagnostic> diagnostics,
        QueryDialectProfile profile)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
        var dialect = profile ?? throw new ArgumentNullException(nameof(profile));
        _types = dialect.Types;
        _supportsPostgreSqlArrays = dialect.Types.Mapper is PostgreSqlTypeMapper;
    }

    internal SqlStatement? Parse()
    {
        var statement = ParseStatement(true);
        if (statement == null)
        {
            return null;
        }

        FinishStatement();
        return statement;
    }

    private SqlStatement? ParseStatement(bool allowWith)
    {
        if (allowWith && Current.Kind == TokenKind.With)
        {
            return ParseWith();
        }

        if (Current.Kind == TokenKind.Select)
        {
            return ParseSelect();
        }

        if (Current.Kind == TokenKind.Values)
        {
            return ParseValues();
        }

        if (Current.Kind == TokenKind.Update)
        {
            return ParseUpdate();
        }

        if (Current.Kind == TokenKind.Insert)
        {
            return ParseInsert();
        }

        if (Current.Kind == TokenKind.Delete)
        {
            return ParseDelete();
        }

        if (Current.Kind == TokenKind.Truncate)
        {
            return ParseTruncate();
        }

        Report(Current.Span, allowWith
            ? "Expected WITH, SELECT, VALUES, INSERT, UPDATE, DELETE, or TRUNCATE."
            : "Expected SELECT, VALUES, INSERT, UPDATE, DELETE, or TRUNCATE.");
        return null;
    }

    private WithStatement? ParseWith()
    {
        Expect(TokenKind.With, "Expected WITH.");
        var recursive = Match(TokenKind.Recursive);
        var expressions = new List<CommonTableExpression>();
        do
        {
            var name = ParseIdentifier("Expected a CTE name after WITH.");
            var columnNames = new List<SqlIdentifier>();
            if (Match(TokenKind.OpenParen))
            {
                if (Current.Kind != TokenKind.CloseParen)
                {
                    do
                    {
                        columnNames.Add(ParseIdentifier("Expected a CTE column name."));
                    }
                    while (Match(TokenKind.Comma));
                }

                Expect(TokenKind.CloseParen, "Expected ')' after CTE column names.");
            }

            Expect(TokenKind.As, "Expected AS after the CTE name.");
            if (Current.Kind == TokenKind.Not && IsIdentifier(Peek(1).Kind) &&
                string.Equals(Peek(1).Text, "MATERIALIZED", StringComparison.OrdinalIgnoreCase))
            {
                Advance();
                Advance();
            }
            else if (IsIdentifier(Current.Kind) &&
                     string.Equals(Current.Text, "MATERIALIZED", StringComparison.OrdinalIgnoreCase))
            {
                Advance();
            }

            Expect(TokenKind.OpenParen, "Expected '(' before the CTE query.");
            var query = ParseStatement(true);
            Expect(TokenKind.CloseParen, "Expected ')' after the CTE query.");
            if (query != null)
            {
                expressions.Add(new CommonTableExpression(name, columnNames, query));
            }
        }
        while (Match(TokenKind.Comma));

        var statement = ParseStatement(false);
        return statement == null ? null : new WithStatement(expressions, statement, recursive);
    }

    private SelectStatement ParseSelect()
    {
        var core = ParseSelectCore();
        var setOperations = new List<SetOperation>();
        while (Current.Kind == TokenKind.Union || Current.Kind == TokenKind.Intersect ||
               Current.Kind == TokenKind.Except)
        {
            var token = Advance();
            var kind = token.Kind == TokenKind.Union
                ? SetOperationKind.Union
                : token.Kind == TokenKind.Intersect
                    ? SetOperationKind.Intersect
                    : SetOperationKind.Except;
            var all = Match(TokenKind.All);
            if (!all)
            {
                Match(TokenKind.Distinct);
            }

            SelectStatement right;
            if (Match(TokenKind.OpenParen))
            {
                right = ParseSelect();
                Expect(TokenKind.CloseParen, "Expected ')' after the set-operation query.");
            }
            else
            {
                right = ParseSelectCore();
            }

            setOperations.Add(new SetOperation(kind, all, right, FromBounds(token.Span.Start, Current.Span.Start)));
        }

        var orderBy = ParseOrderBy();
        Expression? limit = null;
        Expression? offset = null;
        while (true)
        {
            if (limit == null && Match(TokenKind.Limit))
            {
                if (Match(TokenKind.All))
                {
                    limit = null;
                }
                else
                {
                    limit = ParseExpression();
                }

                continue;
            }

            if (offset == null && Match(TokenKind.Offset))
            {
                offset = ParseExpression();
                Match(TokenKind.Row);
                Match(TokenKind.Rows);
                continue;
            }

            if (limit == null && Match(TokenKind.Fetch))
            {
                if (!Match(TokenKind.First))
                {
                    Match(TokenKind.Next);
                }

                if (Current.Kind == TokenKind.Row || Current.Kind == TokenKind.Rows)
                {
                    limit = new LiteralExpression(LiteralKind.Integer, "1", Previous.Span);
                }
                else
                {
                    limit = ParseExpression();
                }

                if (!Match(TokenKind.Row)) Match(TokenKind.Rows);
                Expect(TokenKind.Only, "Expected ONLY after FETCH row count.");
                continue;
            }

            break;
        }

        var lockTables = ParseLockingClauses();

        return new SelectStatement(
            core.Items,
            core.From,
            core.Joins,
            core.Where,
            core.GroupBy,
            core.Having,
            orderBy,
            limit,
            offset,
            core.Distinct,
            core.DistinctOn,
            setOperations,
            core.Windows,
            lockTables);
    }

    private ValuesStatement ParseValues()
    {
        Expect(TokenKind.Values, "Expected VALUES.");
        var rows = new List<IReadOnlyList<Expression>>();
        do
        {
            Expect(TokenKind.OpenParen, "Expected '(' before a VALUES row.");
            var row = new List<Expression>();
            if (Current.Kind == TokenKind.CloseParen)
            {
                Report(Current.Span, "A VALUES row must contain at least one expression.");
            }
            else
            {
                do
                {
                    row.Add(ParseExpression());
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.CloseParen, "Expected ')' after a VALUES row.");
            rows.Add(row);
        }
        while (Match(TokenKind.Comma));

        var orderBy = ParseOrderBy();
        Expression? limit = null;
        Expression? offset = null;
        while (true)
        {
            if (limit == null && Match(TokenKind.Limit))
            {
                limit = Match(TokenKind.All) ? null : ParseExpression();
                continue;
            }

            if (offset == null && Match(TokenKind.Offset))
            {
                offset = ParseExpression();
                Match(TokenKind.Row);
                Match(TokenKind.Rows);
                continue;
            }

            if (limit == null && Match(TokenKind.Fetch))
            {
                if (!Match(TokenKind.First)) Match(TokenKind.Next);
                if (Current.Kind == TokenKind.Row || Current.Kind == TokenKind.Rows)
                {
                    limit = new LiteralExpression(LiteralKind.Integer, "1", Current.Span);
                }
                else
                {
                    limit = ParseExpression();
                }

                if (!Match(TokenKind.Row)) Match(TokenKind.Rows);
                Expect(TokenKind.Only, "Expected ONLY after FETCH row count.");
                continue;
            }

            break;
        }

        return new ValuesStatement(rows, orderBy, limit, offset);
    }

    private SelectStatement ParseSelectCore()
    {
        Expect(TokenKind.Select, "Expected SELECT.");
        var distinct = Match(TokenKind.Distinct);
        var distinctOn = new List<Expression>();
        if (distinct && Match(TokenKind.On))
        {
            Expect(TokenKind.OpenParen, "Expected '(' after DISTINCT ON.");
            if (Current.Kind != TokenKind.CloseParen)
            {
                do
                {
                    distinctOn.Add(ParseExpression());
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.CloseParen, "Expected ')' after DISTINCT ON expressions.");
        }
        else if (!distinct)
        {
            Match(TokenKind.All);
        }

        var items = ParseSelectList();
        TableReference? from = null;
        var joins = new List<JoinClause>();
        if (Match(TokenKind.From))
        {
            from = ParseTableReference();
            while (IsJoinStart(Current.Kind) || Current.Kind == TokenKind.Comma)
            {
                if (Match(TokenKind.Comma))
                {
                    var table = ParseTableReference();
                    joins.Add(new JoinClause(JoinKind.Cross, table, null, null, false, table.Name.Span));
                }
                else
                {
                    joins.Add(ParseJoin());
                }
            }
        }

        Expression? where = null;
        if (Match(TokenKind.Where))
        {
            where = ParseExpression();
        }

        var groupBy = new List<Expression>();
        if (Match(TokenKind.Group))
        {
            Expect(TokenKind.By, "Expected BY after GROUP.");
            groupBy.Add(ParseExpression());
            while (Match(TokenKind.Comma))
            {
                groupBy.Add(ParseExpression());
            }
        }

        Expression? having = null;
        if (Match(TokenKind.Having))
        {
            having = ParseExpression();
        }

        var windows = ParseNamedWindows();

        return new SelectStatement(
            items,
            from,
            joins,
            where,
            groupBy,
            having,
            new List<OrderItem>(),
            null,
            null,
            distinct,
            distinctOn,
            null,
            windows);
    }

    private IReadOnlyList<NamedWindow> ParseNamedWindows()
    {
        var windows = new List<NamedWindow>();
        if (!Match(TokenKind.Window))
        {
            return windows;
        }

        do
        {
            var name = ParseIdentifier("Expected a window name after WINDOW.");
            Expect(TokenKind.As, "Expected AS after a window name.");
            Expect(TokenKind.OpenParen, "Expected '(' before a window specification.");
            var specification = ParseWindowSpecification();
            Expect(TokenKind.CloseParen, "Expected ')' after a window specification.");
            windows.Add(new NamedWindow(name, specification));
        }
        while (Match(TokenKind.Comma));

        return windows;
    }

    private WindowSpecification ParseWindowSpecification()
    {
        SqlIdentifier? baseWindow = null;
        if (IsIdentifier(Current.Kind) &&
            !string.Equals(Current.Text, "RANGE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Current.Text, "GROUPS", StringComparison.OrdinalIgnoreCase))
        {
            baseWindow = TakeIdentifier();
        }

        var partitionBy = new List<Expression>();
        if (Match(TokenKind.Partition))
        {
            Expect(TokenKind.By, "Expected BY after PARTITION.");
            do
            {
                partitionBy.Add(ParseExpression());
            }
            while (Match(TokenKind.Comma));
        }

        var orderBy = ParseOrderBy();
        if (Current.Kind == TokenKind.Rows ||
            IsIdentifier(Current.Kind) &&
            (string.Equals(Current.Text, "RANGE", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Current.Text, "GROUPS", StringComparison.OrdinalIgnoreCase)))
        {
            SkipWindowFrame();
        }

        return new WindowSpecification(baseWindow, partitionBy, orderBy);
    }

    private IReadOnlyList<SqlIdentifier> ParseLockingClauses()
    {
        var tables = new List<SqlIdentifier>();
        while (Match(TokenKind.For))
        {
            if (Match(TokenKind.Update))
            {
            }
            else if (MatchWord("NO"))
            {
                ExpectWord("KEY", "Expected KEY after FOR NO.");
                Expect(TokenKind.Update, "Expected UPDATE after FOR NO KEY.");
            }
            else if (MatchWord("KEY"))
            {
                ExpectWord("SHARE", "Expected SHARE after FOR KEY.");
            }
            else if (!MatchWord("SHARE"))
            {
                Report(Current.Span, "Expected UPDATE, NO KEY UPDATE, SHARE, or KEY SHARE after FOR.");
            }

            if (MatchWord("OF"))
            {
                do
                {
                    tables.Add(ParseIdentifier("Expected a table name after FOR ... OF."));
                }
                while (Match(TokenKind.Comma));
            }

            if (!MatchWord("NOWAIT") && MatchWord("SKIP"))
            {
                ExpectWord("LOCKED", "Expected LOCKED after SKIP.");
            }
        }

        return tables;
    }

    private IReadOnlyList<OrderItem> ParseOrderBy()
    {
        var orderBy = new List<OrderItem>();
        if (!Match(TokenKind.Order))
        {
            return orderBy;
        }

        Expect(TokenKind.By, "Expected BY after ORDER.");
        do
        {
            var expression = ParseExpression();
            var descending = Match(TokenKind.Desc);
            if (!descending)
            {
                Match(TokenKind.Asc);
            }

            bool? nullsFirst = null;
            if (Match(TokenKind.Nulls))
            {
                if (Match(TokenKind.First)) nullsFirst = true;
                else if (Match(TokenKind.Last)) nullsFirst = false;
                else Report(Current.Span, "Expected FIRST or LAST after NULLS.");
            }

            orderBy.Add(new OrderItem(expression, descending, nullsFirst));
        }
        while (Match(TokenKind.Comma));

        return orderBy;
    }

    private UpdateStatement ParseUpdate()
    {
        Expect(TokenKind.Update, "Expected UPDATE.");
        var table = ParseTableReference(allowFunction: false);
        Expect(TokenKind.Set, "Expected SET after the UPDATE table.");
        var assignments = new List<UpdateAssignment>();
        do
        {
            var column = ParseIdentifier("Expected a column name in SET.");
            Expect(TokenKind.Equal, "Expected '=' after the SET column.");
            assignments.Add(new UpdateAssignment(column, ParseExpression()));
        }
        while (Match(TokenKind.Comma));

        var from = new List<TableReference>();
        if (Match(TokenKind.From))
        {
            do
            {
                from.Add(ParseTableReference());
            }
            while (Match(TokenKind.Comma));
        }

        Expression? where = null;
        if (Match(TokenKind.Where))
        {
            where = ParseExpression();
        }

        var returning = Match(TokenKind.Returning)
            ? ParseSelectList()
            : new List<SelectItem>();
        return new UpdateStatement(table, assignments, where, from, returning);
    }

    private InsertStatement ParseInsert()
    {
        Expect(TokenKind.Insert, "Expected INSERT.");
        Expect(TokenKind.Into, "Expected INTO after INSERT.");
        var table = ParseTableReference(allowFunction: false);
        var columns = new List<SqlIdentifier>();
        if (Match(TokenKind.OpenParen))
        {
            if (Current.Kind == TokenKind.CloseParen)
            {
                Report(Current.Span, "An INSERT column list cannot be empty.");
            }
            else
            {
                do
                {
                    columns.Add(ParseIdentifier("Expected a column name in INSERT."));
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.CloseParen, "Expected ')' after the INSERT column list.");
        }

        if (Match(TokenKind.Default))
        {
            Expect(TokenKind.Values, "Expected VALUES after DEFAULT.");
            var defaultOnConflict = ParseOnConflict();
            var defaultReturning = Match(TokenKind.Returning)
                ? ParseSelectList()
                : new List<SelectItem>();
            return new InsertStatement(
                table,
                columns,
                Array.Empty<IReadOnlyList<Expression>>(),
                true,
                null,
                defaultOnConflict,
                defaultReturning);
        }

        var rows = new List<IReadOnlyList<Expression>>();
        SqlStatement? source = null;
        if (Match(TokenKind.Values))
        {
            do
            {
                Expect(TokenKind.OpenParen, "Expected '(' before an INSERT values row.");
                var values = new List<Expression>();
                if (Current.Kind == TokenKind.CloseParen)
                {
                    Report(Current.Span, "An INSERT values row cannot be empty.");
                }
                else
                {
                    do
                    {
                        values.Add(ParseExpression());
                    }
                    while (Match(TokenKind.Comma));
                }

                Expect(TokenKind.CloseParen, "Expected ')' after an INSERT values row.");
                rows.Add(values);
            }
            while (Match(TokenKind.Comma));
        }
        else if (Current.Kind == TokenKind.Select || Current.Kind == TokenKind.With)
        {
            source = ParseStatement(true);
        }
        else
        {
            Report(Current.Span, "Expected VALUES, DEFAULT VALUES, or a SELECT query in INSERT.");
        }

        var onConflict = ParseOnConflict();
        var returning = Match(TokenKind.Returning)
            ? ParseSelectList()
            : new List<SelectItem>();
        return new InsertStatement(table, columns, rows, false, source, onConflict, returning);
    }

    private OnConflictClause? ParseOnConflict()
    {
        if (!Match(TokenKind.On))
        {
            return null;
        }

        if (!Match(TokenKind.Conflict))
        {
            Report(Current.Span, "Expected CONFLICT after ON in INSERT.");
            return null;
        }

        var targetColumns = new List<SqlIdentifier>();
        SqlIdentifier? constraint = null;
        Expression? targetWhere = null;
        if (Match(TokenKind.OpenParen))
        {
            if (Current.Kind != TokenKind.CloseParen)
            {
                do
                {
                    targetColumns.Add(ParseIdentifier("Expected a conflict target column."));
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.CloseParen, "Expected ')' after the conflict target.");
            if (Match(TokenKind.Where))
            {
                targetWhere = ParseExpression();
            }
        }
        else if (Match(TokenKind.On))
        {
            Expect(TokenKind.Constraint, "Expected CONSTRAINT after ON in the conflict target.");
            constraint = ParseIdentifier("Expected a constraint name after ON CONSTRAINT.");
        }

        Expect(TokenKind.Do, "Expected DO in ON CONFLICT.");
        if (Match(TokenKind.Nothing))
        {
            return new OnConflictClause(
                targetColumns,
                constraint,
                targetWhere,
                true,
                new List<UpdateAssignment>(),
                null);
        }

        Expect(TokenKind.Update, "Expected NOTHING or UPDATE after DO.");
        Expect(TokenKind.Set, "Expected SET after DO UPDATE.");
        var assignments = new List<UpdateAssignment>();
        do
        {
            var column = ParseIdentifier("Expected a column name in DO UPDATE SET.");
            Expect(TokenKind.Equal, "Expected '=' after the SET column.");
            assignments.Add(new UpdateAssignment(column, ParseExpression()));
        }
        while (Match(TokenKind.Comma));

        var updateWhere = Match(TokenKind.Where) ? ParseExpression() : null;
        return new OnConflictClause(
            targetColumns,
            constraint,
            targetWhere,
            false,
            assignments,
            updateWhere);
    }

    private DeleteStatement ParseDelete()
    {
        Expect(TokenKind.Delete, "Expected DELETE.");
        Expect(TokenKind.From, "Expected FROM after DELETE.");
        var table = ParseTableReference(allowFunction: false);
        var usingTables = new List<TableReference>();
        if (Match(TokenKind.Using))
        {
            do
            {
                usingTables.Add(ParseTableReference());
            }
            while (Match(TokenKind.Comma));
        }

        Expression? where = null;
        if (Match(TokenKind.Where))
        {
            where = ParseExpression();
        }

        var returning = Match(TokenKind.Returning)
            ? ParseSelectList()
            : new List<SelectItem>();
        return new DeleteStatement(table, where, usingTables, returning);
    }

    private TruncateStatement ParseTruncate()
    {
        Expect(TokenKind.Truncate, "Expected TRUNCATE.");
        MatchWord("TABLE");
        var tables = new List<TableReference>();
        do
        {
            Match(TokenKind.Only);
            var first = ParseIdentifier("Expected a table name after TRUNCATE.");
            SqlIdentifier? schema = null;
            var name = first;
            if (Match(TokenKind.Dot))
            {
                schema = first;
                name = ParseIdentifier("Expected a table name after '.'.");
            }

            Match(TokenKind.Star);
            tables.Add(new TableReference(schema, name, null));
        }
        while (Match(TokenKind.Comma));

        if (MatchWord("RESTART") || MatchWord("CONTINUE"))
        {
            ExpectWord("IDENTITY", "Expected IDENTITY after RESTART or CONTINUE.");
        }

        MatchWord("CASCADE");
        MatchWord("RESTRICT");
        return new TruncateStatement(tables);
    }

    private void FinishStatement()
    {
        Match(TokenKind.Semicolon);
        if (Current.Kind != TokenKind.End)
        {
            Report(Current.Span, $"Unexpected token '{Current.Text}'.");
        }
    }

    private IReadOnlyList<SelectItem> ParseSelectList()
    {
        var items = new List<SelectItem>();
        do
        {
            var expression = ParseExpression();
            SqlIdentifier? alias = null;
            if (Match(TokenKind.As))
            {
                alias = ParseIdentifier("Expected an alias after AS.");
            }
            else if (IsIdentifier(Current.Kind))
            {
                alias = TakeIdentifier();
            }

            items.Add(new SelectItem(expression, alias));
        }
        while (Match(TokenKind.Comma));

        return items;
    }

    private TableReference ParseTableReference(bool allowFunction = true)
    {
        var lateral = Match(TokenKind.Lateral);
        if (allowFunction && Match(TokenKind.OpenParen))
        {
            var query = ParseStatement(true);
            Expect(TokenKind.CloseParen, "Expected ')' after the derived-table query.");
            if (query == null)
            {
                var missing = new SqlIdentifier("missing", false, Current.Span);
                return new TableReference(null, missing, missing);
            }

            Match(TokenKind.As);
            var derivedAlias = ParseIdentifier("A derived table requires an alias.");
            var columnAliases = new List<SqlIdentifier>();
            if (Match(TokenKind.OpenParen))
            {
                if (Current.Kind != TokenKind.CloseParen)
                {
                    do
                    {
                        columnAliases.Add(ParseIdentifier("Expected a derived-table column alias."));
                    }
                    while (Match(TokenKind.Comma));
                }

                Expect(TokenKind.CloseParen, "Expected ')' after derived-table column aliases.");
            }

            return new TableReference(query, derivedAlias, columnAliases, lateral);
        }

        var first = ParseIdentifier("Expected a table name.");
        SqlIdentifier? schema = null;
        var name = first;
        if (Match(TokenKind.Dot))
        {
            schema = first;
            name = ParseIdentifier("Expected a table name after '.'.");
        }

        if (allowFunction && Match(TokenKind.OpenParen))
        {
            if (schema != null)
            {
                Report(schema.Span, "Schema-qualified table functions are not supported.");
            }

            var function = ParseFunction(name);
            SqlIdentifier? functionAlias = null;
            if (Match(TokenKind.As))
            {
                functionAlias = ParseIdentifier("Expected a table-function alias after AS.");
            }
            else if (IsIdentifier(Current.Kind))
            {
                functionAlias = TakeIdentifier();
            }

            var functionColumnAliases = new List<SqlIdentifier>();
            if (functionAlias != null && Match(TokenKind.OpenParen))
            {
                if (Current.Kind != TokenKind.CloseParen)
                {
                    do
                    {
                        functionColumnAliases.Add(ParseIdentifier("Expected a table-function column alias."));
                    }
                    while (Match(TokenKind.Comma));
                }

                Expect(TokenKind.CloseParen, "Expected ')' after table-function column aliases.");
            }

            return new TableReference(
                function,
                functionAlias ?? name,
                functionAlias,
                functionColumnAliases,
                lateral);
        }

        SqlIdentifier? alias = null;
        if (Match(TokenKind.As))
        {
            alias = ParseIdentifier("Expected a table alias after AS.");
        }
        else if (IsIdentifier(Current.Kind))
        {
            alias = TakeIdentifier();
        }

        var aliases = new List<SqlIdentifier>();
        if (alias != null && Match(TokenKind.OpenParen))
        {
            if (Current.Kind != TokenKind.CloseParen)
            {
                do
                {
                    aliases.Add(ParseIdentifier("Expected a table column alias."));
                }
                while (Match(TokenKind.Comma));
            }

            Expect(TokenKind.CloseParen, "Expected ')' after table column aliases.");
        }

        return new TableReference(schema, name, alias, aliases);
    }

    private JoinClause ParseJoin()
    {
        var start = Current.Span.Start;
        var kind = JoinKind.Inner;
        var natural = Match(TokenKind.Natural);
        if (Match(TokenKind.Cross))
        {
            kind = JoinKind.Cross;
            Expect(TokenKind.Join, "Expected JOIN after CROSS.");
        }
        else if (Match(TokenKind.Left))
        {
            kind = JoinKind.Left;
            Match(TokenKind.Outer);
            Expect(TokenKind.Join, "Expected JOIN after LEFT.");
        }
        else if (Match(TokenKind.Right))
        {
            kind = JoinKind.Right;
            Match(TokenKind.Outer);
            Expect(TokenKind.Join, "Expected JOIN after RIGHT.");
        }
        else if (Match(TokenKind.Full))
        {
            kind = JoinKind.Full;
            Match(TokenKind.Outer);
            Expect(TokenKind.Join, "Expected JOIN after FULL.");
        }
        else
        {
            Match(TokenKind.Inner);
            Expect(TokenKind.Join, "Expected JOIN.");
        }

        var table = ParseTableReference();
        Expression? on = null;
        var usingColumns = new List<SqlIdentifier>();
        if (kind != JoinKind.Cross && !natural)
        {
            if (Match(TokenKind.On))
            {
                on = ParseExpression();
            }
            else if (Match(TokenKind.Using))
            {
                Expect(TokenKind.OpenParen, "Expected '(' after USING.");
                if (Current.Kind != TokenKind.CloseParen)
                {
                    do
                    {
                        usingColumns.Add(ParseIdentifier("Expected a column name in JOIN USING."));
                    }
                    while (Match(TokenKind.Comma));
                }

                Expect(TokenKind.CloseParen, "Expected ')' after JOIN USING columns.");
            }
            else
            {
                Report(Current.Span, "A JOIN requires ON or USING.");
            }
        }

        var end = on == null ? Current.Span.Start : EndOf(on);
        return new JoinClause(kind, table, on, usingColumns, natural, FromBounds(start, end));
    }

    private Expression ParseExpression() => ParseOr();

    private Expression ParseOr()
    {
        var expression = ParseAnd();
        while (Match(TokenKind.Or))
        {
            var right = ParseAnd();
            expression = Binary(expression, "OR", right);
        }

        return expression;
    }

    private Expression ParseAnd()
    {
        var expression = ParseNot();
        while (Match(TokenKind.And))
        {
            var right = ParseNot();
            expression = Binary(expression, "AND", right);
        }

        return expression;
    }

    private Expression ParseNot()
    {
        if (Match(TokenKind.Not))
        {
            var start = Previous.Span.Start;
            var operand = ParseNot();
            return new UnaryExpression("NOT", operand, FromBounds(start, EndOf(operand)));
        }

        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        var expression = ParseConcat();
        while (true)
        {
            if (Match(TokenKind.Equal)) expression = ParseComparedValue(expression, "=");
            else if (Match(TokenKind.NotEqual)) expression = ParseComparedValue(expression, "<>");
            else if (Match(TokenKind.Less)) expression = ParseComparedValue(expression, "<");
            else if (Match(TokenKind.LessEqual)) expression = ParseComparedValue(expression, "<=");
            else if (Match(TokenKind.Greater)) expression = ParseComparedValue(expression, ">");
            else if (Match(TokenKind.GreaterEqual)) expression = ParseComparedValue(expression, ">=");
            else if (Match(TokenKind.Is))
            {
                var negated = Match(TokenKind.Not);
                if (Match(TokenKind.Distinct))
                {
                    Expect(TokenKind.From, "Expected FROM after IS DISTINCT.");
                    expression = Binary(expression, negated ? "IS NOT DISTINCT FROM" : "IS DISTINCT FROM", ParseConcat());
                }
                else if (Match(TokenKind.Null))
                {
                    expression = new IsNullExpression(
                        expression,
                        negated,
                        FromBounds(expression.Span.Start, EndOf(Previous)));
                }
                else if (Match(TokenKind.True) || Match(TokenKind.False) || Match(TokenKind.Unknown))
                {
                    var test = Previous.Kind == TokenKind.Unknown ? LiteralKind.Null : LiteralKind.Boolean;
                    expression = new IsTruthExpression(
                        expression,
                        test,
                        negated,
                        FromBounds(expression.Span.Start, EndOf(Previous)));
                }
                else
                {
                    Report(Current.Span, "Expected NULL, TRUE, FALSE, UNKNOWN, or DISTINCT FROM after IS.");
                }
            }
            else
            {
                var negated = Current.Kind == TokenKind.Not &&
                    (Peek(1).Kind == TokenKind.Like || Peek(1).Kind == TokenKind.Ilike ||
                     Peek(1).Kind == TokenKind.In || Peek(1).Kind == TokenKind.Between);
                if (negated)
                {
                    Advance();
                }

                if (Match(TokenKind.Like))
                {
                    expression = Binary(expression, negated ? "NOT LIKE" : "LIKE", ParseConcat());
                }
                else if (Match(TokenKind.Ilike))
                {
                    expression = Binary(expression, negated ? "NOT ILIKE" : "ILIKE", ParseConcat());
                }
                else if (Match(TokenKind.In))
                {
                    var values = ParseInValues(out var subquery);
                    var end = Previous.Span.Start + Previous.Span.Length;
                    expression = new InExpression(expression, values, subquery, negated, FromBounds(expression.Span.Start, end));
                }
                else if (Match(TokenKind.Between))
                {
                    var lower = ParseConcat();
                    Expect(TokenKind.And, "Expected AND in BETWEEN expression.");
                    var upper = ParseConcat();
                    expression = new BetweenExpression(expression, lower, upper, negated, FromBounds(expression.Span.Start, EndOf(upper)));
                }
                else
                {
                    if (Match(TokenKind.RegexMatch))
                    {
                        expression = Binary(expression, "~", ParseConcat());
                        continue;
                    }

                    if (Match(TokenKind.RegexInsensitiveMatch))
                    {
                        expression = Binary(expression, "~*", ParseConcat());
                        continue;
                    }

                    if (Match(TokenKind.RegexNotMatch))
                    {
                        expression = Binary(expression, "!~", ParseConcat());
                        continue;
                    }

                    if (Match(TokenKind.RegexNotInsensitiveMatch))
                    {
                        expression = Binary(expression, "!~*", ParseConcat());
                        continue;
                    }

                    if (Match(TokenKind.Contains))
                    {
                        expression = Binary(expression, "@>", ParseConcat());
                        continue;
                    }

                    if (Match(TokenKind.ContainedBy))
                    {
                        expression = Binary(expression, "<@", ParseConcat());
                        continue;
                    }

                    if (Match(TokenKind.Overlaps))
                    {
                        expression = Binary(expression, "&&", ParseConcat());
                        continue;
                    }

                    if (negated)
                    {
                        Report(Previous.Span, "Expected LIKE, IN, or BETWEEN after NOT.");
                    }

                    break;
                }
            }
        }

        return expression;
    }

    private Expression ParseComparedValue(Expression left, string op)
    {
        QuantifierKind? quantifier = null;
        if (_supportsPostgreSqlArrays && MatchWord("ANY"))
        {
            quantifier = QuantifierKind.Any;
        }
        else if (_supportsPostgreSqlArrays && Match(TokenKind.All))
        {
            quantifier = QuantifierKind.All;
        }

        if (!quantifier.HasValue)
        {
            return Binary(left, op, ParseConcat());
        }

        Expect(TokenKind.OpenParen, $"Expected '(' after {quantifier.Value.ToString().ToUpperInvariant()}.");
        var array = ParseExpression();
        var close = Expect(TokenKind.CloseParen, "Expected ')' after the quantified array expression.");
        return new QuantifiedComparisonExpression(
            left,
            op,
            quantifier.Value,
            array,
            FromBounds(left.Span.Start, EndOf(close)));
    }

    private IReadOnlyList<Expression> ParseInValues(out SqlStatement? subquery)
    {
        var values = new List<Expression>();
        subquery = null;
        Expect(TokenKind.OpenParen, "Expected '(' after IN.");
        if (Current.Kind == TokenKind.Select || Current.Kind == TokenKind.With)
        {
            subquery = ParseStatement(true);
        }
        else if (Current.Kind != TokenKind.CloseParen)
        {
            do
            {
                values.Add(ParseExpression());
            }
            while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.CloseParen, "Expected ')' after IN list.");
        return values;
    }

    private Expression ParseConcat()
    {
        var expression = ParseJson();
        while (Match(TokenKind.Concat))
        {
            expression = Binary(expression, "||", ParseJson());
        }

        return expression;
    }

    private Expression ParseJson()
    {
        var expression = ParseAdditive();
        while (Current.Kind == TokenKind.JsonGet || Current.Kind == TokenKind.JsonGetText ||
               Current.Kind == TokenKind.JsonPathGet || Current.Kind == TokenKind.JsonPathGetText)
        {
            var token = Advance();
            var op = token.Kind == TokenKind.JsonGet
                ? "->"
                : token.Kind == TokenKind.JsonGetText
                    ? "->>"
                    : token.Kind == TokenKind.JsonPathGet
                        ? "#>"
                        : "#>>";
            expression = Binary(expression, op, ParseAdditive());
        }

        return expression;
    }

    private Expression ParseAdditive()
    {
        var expression = ParseMultiplicative();
        while (Current.Kind == TokenKind.Plus || Current.Kind == TokenKind.Minus)
        {
            var op = Advance().Kind == TokenKind.Plus ? "+" : "-";
            expression = Binary(expression, op, ParseMultiplicative());
        }

        return expression;
    }

    private Expression ParseMultiplicative()
    {
        var expression = ParseUnary();
        while (Current.Kind == TokenKind.Star || Current.Kind == TokenKind.Slash ||
               Current.Kind == TokenKind.Percent || Current.Kind == TokenKind.Caret)
        {
            var kind = Advance().Kind;
            var op = kind == TokenKind.Star ? "*" : kind == TokenKind.Slash ? "/" : kind == TokenKind.Percent ? "%" : "^";
            expression = Binary(expression, op, ParseUnary());
        }

        return expression;
    }

    private Expression ParseUnary()
    {
        if (Current.Kind == TokenKind.Plus || Current.Kind == TokenKind.Minus)
        {
            var token = Advance();
            var operand = ParseUnary();
            return new UnaryExpression(token.Kind == TokenKind.Plus ? "+" : "-", operand, FromBounds(token.Span.Start, EndOf(operand)));
        }

        var expression = ParsePrimary();
        while (true)
        {
            if (Match(TokenKind.DoubleColon))
            {
                var type = ParseTypeName();
                expression = new CastExpression(
                    expression,
                    type,
                    FromBounds(expression.Span.Start, Previous.Span.Start + Previous.Span.Length));
                continue;
            }

            if (Match(TokenKind.OpenBracket))
            {
                var index = ParseExpression();
                var close = Expect(TokenKind.CloseBracket, "Expected ']' after an array subscript.");
                expression = new ArraySubscriptExpression(
                    expression,
                    index,
                    FromBounds(expression.Span.Start, EndOf(close)));
                continue;
            }

            break;
        }

        return expression;
    }

    private Expression ParsePrimary()
    {
        var token = Current;
        if (_supportsPostgreSqlArrays &&
            Current.Kind == TokenKind.Identifier &&
            string.Equals(Current.Text, "ARRAY", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == TokenKind.OpenBracket)
        {
            Advance();
            Advance();
            var elements = new List<Expression>();
            if (Current.Kind != TokenKind.CloseBracket)
            {
                do
                {
                    elements.Add(ParseExpression());
                }
                while (Match(TokenKind.Comma));
            }

            var close = Expect(TokenKind.CloseBracket, "Expected ']' after the ARRAY constructor.");
            return new ArrayExpression(elements, FromBounds(token.Span.Start, EndOf(close)));
        }

        if (Match(TokenKind.OpenParen))
        {
            if (Current.Kind == TokenKind.Select || Current.Kind == TokenKind.With)
            {
                var query = ParseStatement(true);
                var close = Expect(TokenKind.CloseParen, "Expected ')' after the subquery.");
                return query == null
                    ? new LiteralExpression(LiteralKind.Null, null, token.Span)
                    : new SubqueryExpression(query, FromBounds(token.Span.Start, EndOf(close)));
            }

            var expression = ParseExpression();
            Expect(TokenKind.CloseParen, "Expected ')' after expression.");
            return expression;
        }

        if (Match(TokenKind.Star))
        {
            return new StarExpression(null, token.Span);
        }

        if (Match(TokenKind.Number))
        {
            var isDecimal = (bool)(token.Value ?? false);
            return new LiteralExpression(isDecimal ? LiteralKind.Decimal : LiteralKind.Integer, token.Text, token.Span);
        }

        if (Match(TokenKind.String))
        {
            return new LiteralExpression(LiteralKind.String, token.Value, token.Span);
        }

        if (Match(TokenKind.True) || Match(TokenKind.False))
        {
            return new LiteralExpression(LiteralKind.Boolean, token.Kind == TokenKind.True, token.Span);
        }

        if (Match(TokenKind.Null))
        {
            return new LiteralExpression(LiteralKind.Null, null, token.Span);
        }

        if (Match(TokenKind.Parameter))
        {
            return new ParameterExpression((string)(token.Value ?? token.Text), token.Span);
        }

        if (Match(TokenKind.Default))
        {
            return new DefaultExpression(token.Span);
        }

        if (Match(TokenKind.Case))
        {
            return ParseCase(token.Span.Start);
        }

        if (Match(TokenKind.Cast))
        {
            return ParseCast(token.Span.Start);
        }

        if (Match(TokenKind.Exists))
        {
            var start = token.Span.Start;
            Expect(TokenKind.OpenParen, "Expected '(' after EXISTS.");
            var query = ParseStatement(true);
            var close = Expect(TokenKind.CloseParen, "Expected ')' after EXISTS query.");
            return query == null
                ? new LiteralExpression(LiteralKind.Boolean, false, token.Span)
                : new ExistsExpression(query, FromBounds(start, EndOf(close)));
        }

        if (IsIdentifier(Current.Kind))
        {
            if (Current.Kind != TokenKind.QuotedIdentifier)
            {
                var special = Current.Text.ToUpperInvariant();
                if (Peek(1).Kind == TokenKind.String)
                {
                    var literalKind = special == "DATE"
                        ? LiteralKind.Date
                        : special == "TIME"
                            ? LiteralKind.Time
                            : special == "TIMESTAMP"
                                ? LiteralKind.Timestamp
                                : special == "INTERVAL"
                                    ? LiteralKind.Interval
                                    : (LiteralKind?)null;
                    if (literalKind.HasValue)
                    {
                        Advance();
                        var value = Advance();
                        return new LiteralExpression(
                            literalKind.Value,
                            value.Value,
                            FromBounds(token.Span.Start, EndOf(value)));
                    }
                }

                var specialKind = special == "CURRENT_DATE"
                    ? LiteralKind.Date
                    : special == "CURRENT_TIMESTAMP"
                        ? LiteralKind.TimestampWithTimeZone
                        : special == "LOCALTIME"
                            ? LiteralKind.Time
                            : special == "LOCALTIMESTAMP"
                                ? LiteralKind.Timestamp
                                : (LiteralKind?)null;
                if (specialKind.HasValue)
                {
                    Advance();
                    var end = EndOf(token);
                    if (special != "CURRENT_DATE" && Match(TokenKind.OpenParen))
                    {
                        Expect(TokenKind.Number, "Expected a precision in the current time value.");
                        var close = Expect(TokenKind.CloseParen, "Expected ')' after current time precision.");
                        end = EndOf(close);
                    }

                    return new LiteralExpression(
                        specialKind.Value,
                        null,
                        FromBounds(token.Span.Start, end));
                }

                if (special == "CURRENT_USER" || special == "SESSION_USER" ||
                    special == "CURRENT_ROLE" || special == "CURRENT_CATALOG" ||
                    special == "CURRENT_SCHEMA")
                {
                    Advance();
                    return new LiteralExpression(LiteralKind.String, null, token.Span);
                }
            }

            var identifier = TakeIdentifier();
            if (Match(TokenKind.OpenParen))
            {
                if (!identifier.IsQuoted &&
                    string.Equals(identifier.Name, "extract", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseExtract(identifier);
                }

                return ParseFunction(identifier);
            }

            if (Match(TokenKind.Dot))
            {
                if (Match(TokenKind.Star))
                {
                    return new StarExpression(identifier, FromBounds(identifier.Span.Start, EndOf(Previous)));
                }

                var name = ParseIdentifier("Expected a column name after '.'.");
                return new ColumnExpression(identifier, name, FromBounds(identifier.Span.Start, EndOf(name.Span)));
            }

            return new ColumnExpression(null, identifier, identifier.Span);
        }

        Report(token.Span, $"Expected an expression, found '{token.Text}'.");
        if (Current.Kind != TokenKind.End)
        {
            Advance();
        }

        return new LiteralExpression(LiteralKind.Null, null, token.Span);
    }

    private Expression ParseExtract(SqlIdentifier name)
    {
        var field = ParseIdentifier("Expected a field name in EXTRACT.");
        Expect(TokenKind.From, "Expected FROM in EXTRACT.");
        var value = ParseExpression();
        var close = Expect(TokenKind.CloseParen, "Expected ')' after EXTRACT.");
        return new FunctionExpression(
            name,
            new Expression[]
            {
                new LiteralExpression(LiteralKind.String, field.Name, field.Span),
                value,
            },
            FromBounds(name.Span.Start, EndOf(close)));
    }

    private FunctionExpression ParseFunction(SqlIdentifier name)
    {
        var arguments = new List<Expression>();
        var distinct = Match(TokenKind.Distinct);
        if (!distinct)
        {
            Match(TokenKind.All);
        }

        if (Current.Kind != TokenKind.CloseParen)
        {
            do
            {
                arguments.Add(ParseExpression());
            }
            while (Match(TokenKind.Comma));
        }

        var close = Expect(TokenKind.CloseParen, "Expected ')' after function arguments.");
        Expression? filter = null;
        if (Match(TokenKind.Filter))
        {
            Expect(TokenKind.OpenParen, "Expected '(' after FILTER.");
            Expect(TokenKind.Where, "Expected WHERE in FILTER.");
            filter = ParseExpression();
            close = Expect(TokenKind.CloseParen, "Expected ')' after FILTER.");
        }

        WindowSpecification? window = null;
        if (Match(TokenKind.Over))
        {
            if (IsIdentifier(Current.Kind))
            {
                window = new WindowSpecification(TakeIdentifier(), new List<Expression>(), new List<OrderItem>());
            }
            else
            {
                Expect(TokenKind.OpenParen, "Expected a window name or '(' after OVER.");
                window = ParseWindowSpecification();
                close = Expect(TokenKind.CloseParen, "Expected ')' after the window specification.");
            }
        }

        return new FunctionExpression(
            name,
            arguments,
            FromBounds(name.Span.Start, EndOf(close)),
            distinct,
            filter,
            window);
    }

    private void SkipWindowFrame()
    {
        var depth = 0;
        while (Current.Kind != TokenKind.End)
        {
            if (Current.Kind == TokenKind.OpenParen)
            {
                depth++;
            }
            else if (Current.Kind == TokenKind.CloseParen)
            {
                if (depth == 0)
                {
                    return;
                }

                depth--;
            }

            Advance();
        }
    }

    private Expression ParseCast(int start)
    {
        Expect(TokenKind.OpenParen, "Expected '(' after CAST.");
        var operand = ParseExpression();
        Expect(TokenKind.As, "Expected AS in CAST.");
        var sqlType = ParseTypeName();
        var close = Expect(TokenKind.CloseParen, "Expected ')' after CAST.");
        return new CastExpression(operand, sqlType, FromBounds(start, EndOf(close)));
    }

    private string ParseTypeName()
    {
        if (!IsIdentifier(Current.Kind))
        {
            Report(Current.Span, "Expected a SQL type name.");
            return string.Empty;
        }

        var type = _types.NormalizeSqlTypeName(Advance().Text);
        while (IsIdentifier(Current.Kind))
        {
            type = _types.NormalizeSqlTypeName(type + " " + Advance().Text);
        }

        if (Match(TokenKind.OpenParen))
        {
            var modifiers = new List<string>();
            do
            {
                modifiers.Add(Expect(TokenKind.Number, "Expected a numeric type modifier.").Text);
            }
            while (Match(TokenKind.Comma));
            type += "(" + string.Join(",", modifiers) + ")";
            Expect(TokenKind.CloseParen, "Expected ')' after type size.");
        }

        while (Match(TokenKind.OpenBracket))
        {
            Expect(TokenKind.CloseBracket, "Expected ']' in an array type name.");
            type += "[]";
        }

        return type;
    }

    private Expression ParseCase(int start)
    {
        Expression? operand = null;
        if (Current.Kind != TokenKind.When)
        {
            operand = ParseExpression();
        }

        var clauses = new List<WhenClause>();
        while (Match(TokenKind.When))
        {
            var condition = ParseExpression();
            Expect(TokenKind.Then, "Expected THEN in CASE expression.");
            var result = ParseExpression();
            clauses.Add(new WhenClause(condition, result));
        }

        if (clauses.Count == 0)
        {
            Report(Current.Span, "CASE requires at least one WHEN clause.");
        }

        Expression? elseExpression = null;
        if (Match(TokenKind.Else))
        {
            elseExpression = ParseExpression();
        }

        var end = Expect(TokenKind.EndKeyword, "Expected END after CASE expression.");
        return new CaseExpression(operand, clauses, elseExpression, FromBounds(start, EndOf(end)));
    }

    private BinaryExpression Binary(Expression left, string op, Expression right) =>
        new BinaryExpression(left, op, right, FromBounds(left.Span.Start, EndOf(right)));

    private SqlIdentifier ParseIdentifier(string message)
    {
        if (IsIdentifier(Current.Kind))
        {
            return TakeIdentifier();
        }

        Report(Current.Span, message);
        var token = Current;
        if (Current.Kind != TokenKind.End)
        {
            Advance();
        }

        return new SqlIdentifier(token.Text, false, token.Span);
    }

    private SqlIdentifier TakeIdentifier()
    {
        var token = Advance();
        return new SqlIdentifier(
            (string)(token.Value ?? token.Text),
            token.Kind == TokenKind.QuotedIdentifier,
            token.Span);
    }

    private Token Expect(TokenKind kind, string message)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        Report(Current.Span, message);
        return new Token(kind, string.Empty, null, Current.Span.Start, 0);
    }

    private void Report(SourceSpan span, string message) => _diagnostics.Add(new Diagnostic("SQL100", message, span));

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool MatchWord(string word)
    {
        if (!IsIdentifier(Current.Kind) ||
            !string.Equals(Current.Text, word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Advance();
        return true;
    }

    private void ExpectWord(string word, string message)
    {
        if (!MatchWord(word))
        {
            Report(Current.Span, message);
        }
    }

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private Token Peek(int offset)
    {
        var position = _position + offset;
        return position < _tokens.Count ? _tokens[position] : _tokens[_tokens.Count - 1];
    }

    private Token Current => _tokens[_position];
    private Token Previous => _tokens[Math.Max(0, _position - 1)];

    private static bool IsIdentifier(TokenKind kind) =>
        kind == TokenKind.Identifier || kind == TokenKind.QuotedIdentifier;

    private static bool IsJoinStart(TokenKind kind) =>
        kind == TokenKind.Join || kind == TokenKind.Inner || kind == TokenKind.Left ||
        kind == TokenKind.Right || kind == TokenKind.Full || kind == TokenKind.Cross ||
        kind == TokenKind.Natural;

    private static int EndOf(Expression expression) => expression.Span.Start + expression.Span.Length;
    private static int EndOf(Token token) => token.Span.Start + token.Span.Length;
    private static int EndOf(SourceSpan span) => span.Start + span.Length;
    private static SourceSpan FromBounds(int start, int end) => new SourceSpan(start, Math.Max(0, end - start));
}
