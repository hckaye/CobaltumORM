using System;
using System.Collections.Generic;
using System.Linq;

namespace CobaltumOrm.Analysis;

internal abstract class OracleDdlStatement
{
    protected OracleDdlStatement(SourceSpan span) => Span = span;

    internal SourceSpan Span { get; }
}

internal sealed class OracleDdlCreateTableStatement : OracleDdlStatement
{
    internal OracleDdlCreateTableStatement(
        OracleDdlQualifiedName table,
        IReadOnlyList<OracleDdlColumnDefinition> columns,
        IReadOnlyList<IReadOnlyList<OracleDdlIdentifier>> primaryKeys,
        SourceSpan span)
        : base(span)
    {
        Table = table;
        Columns = columns;
        PrimaryKeys = primaryKeys;
    }

    internal OracleDdlQualifiedName Table { get; }
    internal IReadOnlyList<OracleDdlColumnDefinition> Columns { get; }
    internal IReadOnlyList<IReadOnlyList<OracleDdlIdentifier>> PrimaryKeys { get; }
}

internal sealed class OracleDdlDropTableStatement : OracleDdlStatement
{
    internal OracleDdlDropTableStatement(OracleDdlQualifiedName table, SourceSpan span)
        : base(span)
    {
        Table = table;
    }

    internal OracleDdlQualifiedName Table { get; }
}

internal sealed class OracleDdlRenameTableStatement : OracleDdlStatement
{
    internal OracleDdlRenameTableStatement(
        OracleDdlQualifiedName oldName,
        OracleDdlIdentifier newName,
        SourceSpan span)
        : base(span)
    {
        OldName = oldName;
        NewName = newName;
    }

    internal OracleDdlQualifiedName OldName { get; }
    internal OracleDdlIdentifier NewName { get; }
}

internal sealed class OracleDdlAlterTableStatement : OracleDdlStatement
{
    internal OracleDdlAlterTableStatement(
        OracleDdlQualifiedName table,
        IReadOnlyList<OracleDdlAlterAction> actions,
        SourceSpan span)
        : base(span)
    {
        Table = table;
        Actions = actions;
    }

    internal OracleDdlQualifiedName Table { get; }
    internal IReadOnlyList<OracleDdlAlterAction> Actions { get; }
}

internal abstract class OracleDdlAlterAction
{
    protected OracleDdlAlterAction(SourceSpan span) => Span = span;

    internal SourceSpan Span { get; }
}

internal sealed class OracleDdlAddColumnAction : OracleDdlAlterAction
{
    internal OracleDdlAddColumnAction(OracleDdlColumnDefinition column, SourceSpan span)
        : base(span)
    {
        Column = column;
    }

    internal OracleDdlColumnDefinition Column { get; }
}

internal sealed class OracleDdlModifyColumnAction : OracleDdlAlterAction
{
    internal OracleDdlModifyColumnAction(OracleDdlColumnDefinition column, SourceSpan span)
        : base(span)
    {
        Column = column;
    }

    internal OracleDdlColumnDefinition Column { get; }
}

internal sealed class OracleDdlDropColumnAction : OracleDdlAlterAction
{
    internal OracleDdlDropColumnAction(OracleDdlIdentifier column, SourceSpan span)
        : base(span)
    {
        Column = column;
    }

    internal OracleDdlIdentifier Column { get; }
}

internal sealed class OracleDdlRenameColumnAction : OracleDdlAlterAction
{
    internal OracleDdlRenameColumnAction(
        OracleDdlIdentifier oldName,
        OracleDdlIdentifier newName,
        SourceSpan span)
        : base(span)
    {
        OldName = oldName;
        NewName = newName;
    }

    internal OracleDdlIdentifier OldName { get; }
    internal OracleDdlIdentifier NewName { get; }
}

internal sealed class OracleDdlRenameTableAction : OracleDdlAlterAction
{
    internal OracleDdlRenameTableAction(OracleDdlIdentifier newName, SourceSpan span)
        : base(span)
    {
        NewName = newName;
    }

    internal OracleDdlIdentifier NewName { get; }
}

internal sealed class OracleDdlAddPrimaryKeyAction : OracleDdlAlterAction
{
    internal OracleDdlAddPrimaryKeyAction(
        IReadOnlyList<OracleDdlIdentifier> columns,
        SourceSpan span)
        : base(span)
    {
        Columns = columns;
    }

    internal IReadOnlyList<OracleDdlIdentifier> Columns { get; }
}

internal sealed class OracleDdlNoOpAction : OracleDdlAlterAction
{
    internal OracleDdlNoOpAction(SourceSpan span)
        : base(span)
    {
    }
}

internal sealed class OracleDdlColumnDefinition
{
    internal OracleDdlColumnDefinition(
        OracleDdlIdentifier name,
        string? sqlType,
        SourceSpan span)
    {
        Name = name;
        SqlType = sqlType;
        Span = span;
        IsNullable = true;
    }

    internal OracleDdlIdentifier Name { get; }
    internal string? SqlType { get; }
    internal SourceSpan Span { get; }
    internal bool IsNullable { get; set; }
    internal bool IsNullableSpecified { get; set; }
    internal bool IsPrimaryKey { get; set; }
    internal bool IsIdentity { get; set; }
    internal bool IsDefaultSpecified { get; set; }
    internal string? DefaultExpression { get; set; }
}

internal sealed class OracleDdlIdentifier
{
    internal OracleDdlIdentifier(string name, bool isQuoted, SourceSpan span)
    {
        Name = name;
        IsQuoted = isQuoted;
        Span = span;
    }

    internal string Name { get; }
    internal bool IsQuoted { get; }
    internal SourceSpan Span { get; }
}

internal sealed class OracleDdlQualifiedName
{
    internal OracleDdlQualifiedName(
        OracleDdlIdentifier? schema,
        OracleDdlIdentifier name,
        SourceSpan span)
    {
        Schema = schema;
        Name = name;
        Span = span;
    }

    internal OracleDdlIdentifier? Schema { get; }
    internal OracleDdlIdentifier Name { get; }
    internal SourceSpan Span { get; }
}

internal sealed class OracleDdlParser
{
    private readonly IReadOnlyList<OracleDdlToken> _tokens;
    private readonly List<Diagnostic> _diagnostics;
    private int _position;

    internal OracleDdlParser(
        IReadOnlyList<OracleDdlToken> tokens,
        List<Diagnostic> diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    internal OracleDdlStatement? Parse()
    {
        if (Current.Kind == OracleDdlTokenKind.End || Current.Kind == OracleDdlTokenKind.Semicolon)
        {
            return null;
        }

        var start = Current.Span.Start;
        OracleDdlStatement? statement;
        if (MatchWord("CREATE"))
        {
            statement = ParseCreate(start);
        }
        else if (MatchWord("DROP"))
        {
            statement = ParseDrop(start);
        }
        else if (MatchWord("ALTER"))
        {
            statement = ParseAlter(start);
        }
        else if (MatchWord("RENAME"))
        {
            statement = ParseRename(start);
        }
        else
        {
            Report("DDL100", "Only supported Oracle table DDL can be analyzed.", Current.Span);
            return null;
        }

        if (Current.Kind == OracleDdlTokenKind.Semicolon)
        {
            Advance();
        }

        if (Current.Kind != OracleDdlTokenKind.End)
        {
            Report("DDL100", "Unexpected tokens remain after the Oracle DDL statement.", Current.Span);
        }

        return statement;
    }

    private OracleDdlStatement? ParseCreate(int start)
    {
        if (MatchWord("GLOBAL"))
        {
            ExpectWord("TEMPORARY", "Expected TEMPORARY after GLOBAL.");
            ExpectWord("TABLE", "Expected TABLE after GLOBAL TEMPORARY.");
        }
        else if (!MatchWord("TABLE"))
        {
            Report("DDL101", "Only CREATE TABLE and CREATE GLOBAL TEMPORARY TABLE are supported.", Current.Span);
            ConsumeToEnd();
            return null;
        }

        var table = ParseQualifiedName("Expected an Oracle table name after CREATE TABLE.");
        var body = ParseParenthesizedTokens("CREATE TABLE requires a parenthesized definition.");
        if (body is null)
        {
            ConsumeToEnd();
            return null;
        }

        var columns = new List<OracleDdlColumnDefinition>();
        var primaryKeys = new List<IReadOnlyList<OracleDdlIdentifier>>();
        foreach (var segment in SplitTopLevel(body))
        {
            if (segment.Count == 0)
            {
                continue;
            }

            if (IsTableConstraint(segment))
            {
                ParseTableConstraint(segment, primaryKeys);
            }
            else
            {
                var column = ParseColumn(segment, false);
                if (column is not null)
                {
                    columns.Add(column);
                }
            }
        }

        ParseCreateOptions();
        return new OracleDdlCreateTableStatement(
            table,
            columns,
            primaryKeys,
            SpanFrom(start, PreviousEnd()));
    }

    private OracleDdlStatement? ParseDrop(int start)
    {
        if (!MatchWord("TABLE"))
        {
            Report("DDL101", "Only DROP TABLE is supported by Oracle schema analysis.", Current.Span);
            ConsumeToEnd();
            return null;
        }

        var table = ParseQualifiedName("Expected an Oracle table name after DROP TABLE.");
        while (Current.Kind != OracleDdlTokenKind.End && Current.Kind != OracleDdlTokenKind.Semicolon)
        {
            if (MatchWord("CASCADE"))
            {
                ExpectWord("CONSTRAINTS", "Expected CONSTRAINTS after CASCADE.");
            }
            else if (!MatchWord("PURGE"))
            {
                Report("DDL101", "Only CASCADE CONSTRAINTS and PURGE may follow DROP TABLE.", Current.Span);
                ConsumeToEnd();
                break;
            }
        }

        return new OracleDdlDropTableStatement(
            table,
            SpanFrom(start, PreviousEnd()));
    }

    private OracleDdlStatement? ParseRename(int start)
    {
        var oldName = ParseQualifiedName("Expected an Oracle table name after RENAME.");
        ExpectWord("TO", "Expected TO in RENAME table TO statement.");
        var newName = ParseIdentifier("Expected the new Oracle table name after RENAME ... TO.");
        return new OracleDdlRenameTableStatement(
            oldName,
            newName,
            SpanFrom(start, PreviousEnd()));
    }

    private OracleDdlStatement? ParseAlter(int start)
    {
        if (!MatchWord("TABLE"))
        {
            Report("DDL101", "Only ALTER TABLE is supported by Oracle schema analysis.", Current.Span);
            ConsumeToEnd();
            return null;
        }

        var table = ParseQualifiedName("Expected an Oracle table name after ALTER TABLE.");
        var actions = new List<OracleDdlAlterAction>();
        while (Current.Kind != OracleDdlTokenKind.End && Current.Kind != OracleDdlTokenKind.Semicolon)
        {
            if (Match(OracleDdlTokenKind.Comma))
            {
                continue;
            }

            if (MatchWord("ADD"))
            {
                ParseAddActions(actions);
                continue;
            }

            if (MatchWord("MODIFY"))
            {
                ParseModifyActions(actions);
                continue;
            }

            if (MatchWord("DROP"))
            {
                ParseDropActions(actions);
                continue;
            }

            if (MatchWord("RENAME"))
            {
                ParseAlterRenameAction(actions);
                continue;
            }

            if (MatchWord("ENABLE") || MatchWord("DISABLE"))
            {
                // Constraint and trigger enablement does not change the fields
                // represented by DatabaseSchema.
                ConsumeToEnd();
                actions.Add(new OracleDdlNoOpAction(SpanFrom(start, PreviousEnd())));
                break;
            }

            Report("DDL101", "This ALTER TABLE action is not supported by Oracle schema analysis.", Current.Span);
            ConsumeToEnd();
        }

        if (actions.Count == 0)
        {
            Report("DDL100", "ALTER TABLE requires a supported table change.", Current.Span);
        }

        return new OracleDdlAlterTableStatement(
            table,
            actions,
            SpanFrom(start, PreviousEnd()));
    }

    private void ParseAddActions(List<OracleDdlAlterAction> actions)
    {
        if (MatchWord("COLUMN"))
        {
            Report("DDL101", "Oracle uses ADD (column_definition), not ADD COLUMN.", Previous.Span);
        }

        if (Match(OracleDdlTokenKind.OpenParen))
        {
            var body = ReadParenthesizedBody();
            foreach (var segment in SplitTopLevel(body))
            {
                ParseAddSegment(segment, actions);
            }

            return;
        }

        var singleSegment = ReadUntilTopLevelCommaOrEnd();
        ParseAddSegment(singleSegment, actions);
    }

    private void ParseAddSegment(
        IReadOnlyList<OracleDdlToken> segment,
        List<OracleDdlAlterAction> actions)
    {
        if (segment.Count == 0)
        {
            Report("DDL100", "ALTER TABLE ADD requires a column or constraint definition.", Current.Span);
            return;
        }

        if (IsTableConstraint(segment))
        {
            var primaryKeys = new List<IReadOnlyList<OracleDdlIdentifier>>();
            ParseTableConstraint(segment, primaryKeys);
            foreach (var primaryKey in primaryKeys)
            {
                actions.Add(new OracleDdlAddPrimaryKeyAction(primaryKey, SpanOf(segment)));
            }

            if (primaryKeys.Count == 0 &&
                ContainsAnyWord(segment, "UNIQUE", "FOREIGN", "CHECK"))
            {
                Report("DDL101", "Only PRIMARY KEY constraints are represented by DatabaseSchema.", SpanOf(segment));
            }

            return;
        }

        var column = ParseColumn(segment, false);
        if (column is not null)
        {
            actions.Add(new OracleDdlAddColumnAction(column, column.Span));
        }
    }

    private void ParseModifyActions(List<OracleDdlAlterAction> actions)
    {
        if (Match(OracleDdlTokenKind.OpenParen))
        {
            var body = ReadParenthesizedBody();
            foreach (var segment in SplitTopLevel(body))
            {
                var column = ParseColumn(segment, true);
                if (column is not null)
                {
                    actions.Add(new OracleDdlModifyColumnAction(column, column.Span));
                }
            }

            return;
        }

        var singleSegment = ReadUntilTopLevelCommaOrEnd();
        var definition = ParseColumn(singleSegment, true);
        if (definition is not null)
        {
            actions.Add(new OracleDdlModifyColumnAction(definition, definition.Span));
        }
    }

    private void ParseDropActions(List<OracleDdlAlterAction> actions)
    {
        if (MatchWord("COLUMN"))
        {
            // Oracle's COLUMN keyword is required for the single-column form.
        }
        else if (MatchWord("CONSTRAINT") || MatchWord("PRIMARY"))
        {
            Report("DDL101", "Dropping constraints is not represented by DatabaseSchema.", Previous.Span);
            ConsumeToEnd();
            return;
        }

        if (Match(OracleDdlTokenKind.OpenParen))
        {
            var body = ReadParenthesizedBody();
            foreach (var segment in SplitTopLevel(body))
            {
                if (segment.Count != 1 || !IsIdentifier(segment[0]))
                {
                    Report("DDL100", "ALTER TABLE DROP must name columns.", SpanOf(segment));
                    continue;
                }

                actions.Add(new OracleDdlDropColumnAction(ParseIdentifier(segment[0]), SpanOf(segment)));
            }

            ParseDropCascadeOptions();

            return;
        }

        var column = ParseIdentifier("Expected a column name after ALTER TABLE DROP COLUMN.");
        actions.Add(new OracleDdlDropColumnAction(column, column.Span));
        ParseDropCascadeOptions();
    }

    private void ParseDropCascadeOptions()
    {
        if (!MatchWord("CASCADE"))
        {
            return;
        }

        ExpectWord("CONSTRAINTS", "Expected CONSTRAINTS after CASCADE.");
    }

    private void ParseAlterRenameAction(List<OracleDdlAlterAction> actions)
    {
        if (MatchWord("COLUMN"))
        {
            var oldName = ParseIdentifier("Expected the old column name after RENAME COLUMN.");
            ExpectWord("TO", "Expected TO in RENAME COLUMN.");
            var newName = ParseIdentifier("Expected the new column name after RENAME COLUMN.");
            actions.Add(new OracleDdlRenameColumnAction(
                oldName,
                newName,
                SpanFrom(oldName.Span.Start, newName.Span.Start + newName.Span.Length)));
            return;
        }

        ExpectWord("TO", "Expected TO in ALTER TABLE RENAME TO.");
        var newTableName = ParseIdentifier("Expected the new table name after ALTER TABLE RENAME TO.");
        actions.Add(new OracleDdlRenameTableAction(newTableName, newTableName.Span));
    }

    private OracleDdlColumnDefinition? ParseColumn(
        IReadOnlyList<OracleDdlToken> segment,
        bool allowMissingType)
    {
        if (segment.Count == 0 || !IsIdentifier(segment[0]))
        {
            Report("DDL100", "An Oracle column definition must start with a column name.", SpanOf(segment));
            return null;
        }

        var name = ParseIdentifier(segment[0]);
        var typeStart = 1;
        var typeEnd = typeStart;
        var depth = 0;
        while (typeEnd < segment.Count)
        {
            var token = segment[typeEnd];
            if (token.Kind == OracleDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == OracleDdlTokenKind.CloseParen)
            {
                depth--;
            }

            if (depth == 0 && typeEnd > typeStart && IsColumnConstraintStart(token))
            {
                break;
            }

            if (depth == 0 && typeEnd == typeStart && IsColumnConstraintStart(token))
            {
                break;
            }

            typeEnd++;
        }

        var sqlType = typeEnd == typeStart
            ? null
            : FormatTokens(segment.Skip(typeStart).Take(typeEnd - typeStart).ToArray());
        if (sqlType is null && !allowMissingType)
        {
            Report("DDL100", $"Column '{name.Name}' must declare a type.", name.Span);
            return null;
        }

        var column = new OracleDdlColumnDefinition(name, sqlType, SpanOf(segment));
        var index = typeEnd;
        while (index < segment.Count)
        {
            var token = segment[index];
            if (IsWord(token, "NOT"))
            {
                if (index + 1 >= segment.Count || !IsWord(segment[index + 1], "NULL"))
                {
                    Report("DDL100", "Expected NULL after NOT in an Oracle column definition.", token.Span);
                    index++;
                    continue;
                }

                column.IsNullable = false;
                column.IsNullableSpecified = true;
                index += 2;
                continue;
            }

            if (IsWord(token, "NULL"))
            {
                column.IsNullable = true;
                column.IsNullableSpecified = true;
                index++;
                continue;
            }

            if (IsWord(token, "PRIMARY") && index + 1 < segment.Count && IsWord(segment[index + 1], "KEY"))
            {
                column.IsPrimaryKey = true;
                column.IsNullable = false;
                column.IsNullableSpecified = true;
                index += 2;
                continue;
            }

            if (IsWord(token, "GENERATED"))
            {
                var identityIndex = IndexOfWordFrom(segment, index, "IDENTITY");
                if (identityIndex >= 0)
                {
                    column.IsIdentity = true;
                    index = identityIndex + 1;
                    if (index < segment.Count && segment[index].Kind == OracleDdlTokenKind.OpenParen)
                    {
                        index = FindMatchingParen(segment, index) + 1;
                    }

                    continue;
                }

                Report("DDL101", "Generated virtual columns are not represented by DatabaseSchema.", token.Span);
                index++;
                continue;
            }

            if (IsWord(token, "IDENTITY"))
            {
                column.IsIdentity = true;
                index++;
                continue;
            }

            if (IsWord(token, "DEFAULT"))
            {
                var defaultStart = index + 1;
                var defaultEnd = defaultStart;
                var defaultDepth = 0;
                while (defaultEnd < segment.Count)
                {
                    var defaultToken = segment[defaultEnd];
                    if (defaultToken.Kind == OracleDdlTokenKind.OpenParen)
                    {
                        defaultDepth++;
                    }
                    else if (defaultToken.Kind == OracleDdlTokenKind.CloseParen)
                    {
                        defaultDepth--;
                    }

                    if (defaultDepth == 0 && defaultEnd > defaultStart && IsColumnConstraintStart(defaultToken))
                    {
                        break;
                    }

                    defaultEnd++;
                }

                if (defaultStart == defaultEnd)
                {
                    Report("DDL100", "DEFAULT requires an Oracle expression.", token.Span);
                }
                else
                {
                    column.DefaultExpression = FormatTokens(
                        segment.Skip(defaultStart).Take(defaultEnd - defaultStart).ToArray());
                }

                column.IsDefaultSpecified = true;
                index = defaultEnd;
                continue;
            }

            if (IsWord(token, "CHECK") || IsWord(token, "REFERENCES") || IsWord(token, "UNIQUE") ||
                IsWord(token, "ENABLE") || IsWord(token, "DISABLE"))
            {
                index = SkipConstraintClause(segment, index);
                continue;
            }

            if (IsWord(token, "COLLATE"))
            {
                index += Math.Min(2, segment.Count - index);
                continue;
            }

            if (IsWord(token, "CONSTRAINT"))
            {
                index++;
                if (index < segment.Count && IsIdentifier(segment[index]))
                {
                    index++;
                }

                continue;
            }

            if (IsIgnorableColumnConstraint(token))
            {
                index++;
                continue;
            }

            Report("DDL101", "This Oracle column constraint or option is not supported.", token.Span);
            index++;
        }

        return column;
    }

    private void ParseTableConstraint(
        IReadOnlyList<OracleDdlToken> segment,
        List<IReadOnlyList<OracleDdlIdentifier>> primaryKeys)
    {
        var primaryIndex = IndexOfWord(segment, "PRIMARY");
        if (primaryIndex >= 0 && primaryIndex + 1 < segment.Count && IsWord(segment[primaryIndex + 1], "KEY"))
        {
            var open = IndexOfKind(segment, OracleDdlTokenKind.OpenParen, primaryIndex + 2);
            if (open < 0)
            {
                Report("DDL100", "A PRIMARY KEY constraint must list its columns.", SpanOf(segment));
                return;
            }

            var close = FindMatchingParen(segment, open);
            var columns = new List<OracleDdlIdentifier>();
            foreach (var key in SplitTopLevel(segment.Skip(open + 1).Take(close - open - 1).ToArray()))
            {
                if (key.Count != 1 || !IsIdentifier(key[0]))
                {
                    Report("DDL100", "A PRIMARY KEY column list must contain identifiers only.", SpanOf(key));
                    continue;
                }

                columns.Add(ParseIdentifier(key[0]));
            }

            if (columns.Count == 0)
            {
                Report("DDL100", "A PRIMARY KEY must contain at least one column.", SpanOf(segment));
            }
            else
            {
                primaryKeys.Add(columns);
            }

            return;
        }

        if (ContainsAnyWord(segment, "UNIQUE", "FOREIGN", "CHECK"))
        {
            // These constraints do not have fields in DatabaseSchema, but accepting
            // them allows ordinary Oracle and Flyway table definitions to be previewed.
            return;
        }

        Report("DDL101", "This Oracle table constraint is not supported.", SpanOf(segment));
    }

    private void ParseCreateOptions()
    {
        while (Current.Kind != OracleDdlTokenKind.End && Current.Kind != OracleDdlTokenKind.Semicolon)
        {
            if (IsCreateOptionStart(Current))
            {
                ParseCreateOption();
                continue;
            }

            Report("DDL101", "This CREATE TABLE option is not supported by schema analysis.", Current.Span);
            ConsumeToEnd();
            break;
        }
    }

    private void ParseCreateOption()
    {
        if (MatchWord("SEGMENT"))
        {
            SkipCreateOptionClause();
            return;
        }

        if (MatchWord("TABLESPACE"))
        {
            ParseIdentifier("Expected a tablespace name after TABLESPACE.");
            return;
        }

        if (MatchWord("PCTFREE") || MatchWord("INITRANS") || MatchWord("MAXTRANS"))
        {
            RequireCreateOptionValue("Expected a numeric value for the CREATE TABLE option.");
            return;
        }

        if (MatchWord("PARALLEL"))
        {
            if (Current.Kind == OracleDdlTokenKind.Number)
            {
                Advance();
            }

            return;
        }

        if (MatchWord("ROW") || MatchWord("NOROW"))
        {
            ExpectWord("MOVEMENT", "Expected MOVEMENT after ROW or NOROW.");
            return;
        }

        if (MatchWord("ON"))
        {
            ExpectWord("COMMIT", "Expected COMMIT after ON in a temporary table option.");
            if (!MatchWord("DELETE") && !MatchWord("PRESERVE"))
            {
                Report("DDL100", "Expected DELETE or PRESERVE after ON COMMIT.", Current.Span);
            }

            ExpectWord("ROWS", "Expected ROWS after ON COMMIT.");
            return;
        }

        if (MatchWord("COMPRESS") || MatchWord("NOCOMPRESS") || MatchWord("PARTITION") ||
            MatchWord("ORGANIZATION"))
        {
            SkipCreateOptionClause();
            return;
        }

        // The remaining recognized words are flags whose values are not part of
        // DatabaseSchema (LOGGING, CACHE, MONITORING, and similar options).
        Advance();
    }

    private void RequireCreateOptionValue(string message)
    {
        if (Current.Kind != OracleDdlTokenKind.Number && !IsIdentifier(Current))
        {
            Report("DDL100", message, Current.Span);
            return;
        }

        Advance();
    }

    private void SkipCreateOptionClause()
    {
        while (Current.Kind != OracleDdlTokenKind.End && Current.Kind != OracleDdlTokenKind.Semicolon &&
               !IsCreateOptionStart(Current))
        {
            Advance();
        }
    }

    private static bool IsCreateOptionStart(OracleDdlToken token)
    {
        return IsWord(token, "SEGMENT") || IsWord(token, "PCTFREE") || IsWord(token, "INITRANS") ||
            IsWord(token, "MAXTRANS") || IsWord(token, "TABLESPACE") || IsWord(token, "COMPRESS") ||
            IsWord(token, "NOCOMPRESS") || IsWord(token, "LOGGING") || IsWord(token, "NOLOGGING") ||
            IsWord(token, "CACHE") || IsWord(token, "NOCACHE") || IsWord(token, "MONITORING") ||
            IsWord(token, "NOMONITORING") || IsWord(token, "PARALLEL") || IsWord(token, "NOPARALLEL") ||
            IsWord(token, "ENABLE") || IsWord(token, "DISABLE") || IsWord(token, "ROW") ||
            IsWord(token, "NOROW") || IsWord(token, "ON") || IsWord(token, "PARTITION") ||
            IsWord(token, "ORGANIZATION");
    }

    private OracleDdlQualifiedName ParseQualifiedName(string message)
    {
        var first = ParseIdentifier(message);
        if (!Match(OracleDdlTokenKind.Dot))
        {
            return new OracleDdlQualifiedName(null, first, first.Span);
        }

        var second = ParseIdentifier("Expected an Oracle object name after '.'.");
        return new OracleDdlQualifiedName(first, second, SpanFrom(first.Span.Start, second.Span.Start + second.Span.Length));
    }

    private OracleDdlIdentifier ParseIdentifier(string message)
    {
        if (IsIdentifier(Current))
        {
            var token = Advance();
            return new OracleDdlIdentifier(
                token.Kind == OracleDdlTokenKind.QuotedIdentifier ? token.Value! : token.Value!,
                token.Kind == OracleDdlTokenKind.QuotedIdentifier,
                token.Span);
        }

        Report("DDL100", message, Current.Span);
        var fallback = Current;
        if (Current.Kind != OracleDdlTokenKind.End)
        {
            Advance();
        }

        return new OracleDdlIdentifier(
            fallback.Value ?? fallback.Text,
            false,
            fallback.Span);
    }

    private IReadOnlyList<OracleDdlToken>? ParseParenthesizedTokens(string message)
    {
        if (!Match(OracleDdlTokenKind.OpenParen))
        {
            Report("DDL100", message, Current.Span);
            return null;
        }

        return ReadParenthesizedBody();
    }

    private IReadOnlyList<OracleDdlToken> ReadParenthesizedBody()
    {
        var depth = 1;
        var body = new List<OracleDdlToken>();
        while (Current.Kind != OracleDdlTokenKind.End)
        {
            var token = Advance();
            if (token.Kind == OracleDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == OracleDdlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    return body;
                }
            }

            body.Add(token);
        }

        Report("DDL100", "A parenthesized Oracle definition is not closed.", Current.Span);
        return body;
    }

    private IReadOnlyList<OracleDdlToken> ReadUntilTopLevelCommaOrEnd()
    {
        var result = new List<OracleDdlToken>();
        var depth = 0;
        while (Current.Kind != OracleDdlTokenKind.End && Current.Kind != OracleDdlTokenKind.Semicolon)
        {
            if (depth == 0 && Current.Kind == OracleDdlTokenKind.Comma)
            {
                break;
            }

            var token = Advance();
            if (token.Kind == OracleDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == OracleDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }

            result.Add(token);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<OracleDdlToken>> SplitTopLevel(
        IReadOnlyList<OracleDdlToken> tokens)
    {
        var result = new List<IReadOnlyList<OracleDdlToken>>();
        var current = new List<OracleDdlToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == OracleDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == OracleDdlTokenKind.CloseParen)
            {
                depth--;
            }

            if (token.Kind == OracleDdlTokenKind.Comma && depth == 0)
            {
                result.Add(current);
                current = new List<OracleDdlToken>();
            }
            else
            {
                current.Add(token);
            }
        }

        if (current.Count != 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static int FindMatchingParen(IReadOnlyList<OracleDdlToken> tokens, int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == OracleDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (tokens[index].Kind == OracleDdlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return tokens.Count - 1;
    }

    private static string FormatTokens(IReadOnlyList<OracleDdlToken> tokens)
    {
        var result = string.Empty;
        OracleDdlToken? previous = null;
        foreach (var token in tokens)
        {
            var noSpaceBefore = token.Kind == OracleDdlTokenKind.OpenParen ||
                token.Kind == OracleDdlTokenKind.CloseParen ||
                token.Kind == OracleDdlTokenKind.Comma || token.Kind == OracleDdlTokenKind.Dot;
            var noSpaceAfterPrevious = previous.HasValue &&
                (previous.Value.Kind == OracleDdlTokenKind.OpenParen ||
                 previous.Value.Kind == OracleDdlTokenKind.Dot ||
                 previous.Value.Kind == OracleDdlTokenKind.Comma);
            if (result.Length != 0 && !noSpaceBefore && !noSpaceAfterPrevious)
            {
                result += " ";
            }

            result += token.Text;
            previous = token;
        }

        return result;
    }

    private static bool IsTableConstraint(IReadOnlyList<OracleDdlToken> segment)
    {
        return segment.Count != 0 &&
            (IsWord(segment[0], "CONSTRAINT") || IsWord(segment[0], "PRIMARY") ||
             IsWord(segment[0], "UNIQUE") || IsWord(segment[0], "FOREIGN") ||
             IsWord(segment[0], "CHECK"));
    }

    private static bool IsColumnConstraintStart(OracleDdlToken token)
    {
        return IsWord(token, "DEFAULT") || IsWord(token, "NOT") || IsWord(token, "NULL") ||
            IsWord(token, "PRIMARY") || IsWord(token, "UNIQUE") || IsWord(token, "REFERENCES") ||
            IsWord(token, "CHECK") || IsWord(token, "CONSTRAINT") || IsWord(token, "GENERATED") ||
            IsWord(token, "IDENTITY") || IsWord(token, "COLLATE") || IsWord(token, "VISIBLE") ||
            IsWord(token, "INVISIBLE") || IsWord(token, "ENABLE") || IsWord(token, "DISABLE") ||
            IsWord(token, "DEFERRABLE") || IsWord(token, "INITIALLY") || IsWord(token, "ENCRYPT");
    }

    private static bool IsIgnorableColumnConstraint(OracleDdlToken token)
    {
        return IsWord(token, "VISIBLE") || IsWord(token, "INVISIBLE") ||
            IsWord(token, "ENABLE") || IsWord(token, "DISABLE") || IsWord(token, "DEFERRABLE") ||
            IsWord(token, "INITIALLY") || IsWord(token, "IMMEDIATE") || IsWord(token, "DEFERRED") ||
            IsWord(token, "USING") || IsWord(token, "INDEX") || IsWord(token, "ENCRYPT") ||
            IsWord(token, "NO") || IsWord(token, "SALT") || IsWord(token, "COMPRESS");
    }

    private static int SkipConstraintClause(IReadOnlyList<OracleDdlToken> tokens, int start)
    {
        var index = start + 1;
        var depth = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token.Kind == OracleDdlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == OracleDdlTokenKind.CloseParen && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && index > start + 1 && IsColumnConstraintStart(token))
            {
                break;
            }

            index++;
        }

        return index;
    }

    private static bool ContainsAnyWord(
        IReadOnlyList<OracleDdlToken> tokens,
        params string[] words) => tokens.Any(token => words.Any(word => IsWord(token, word)));

    private static bool ContainsWord(IReadOnlyList<OracleDdlToken> tokens, int start, string word)
    {
        for (var index = start; index < tokens.Count; index++)
        {
            if (IsWord(tokens[index], word))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfWord(IReadOnlyList<OracleDdlToken> tokens, string word)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsWord(tokens[index], word))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfWordFrom(
        IReadOnlyList<OracleDdlToken> tokens,
        int start,
        string word)
    {
        for (var index = start; index < tokens.Count; index++)
        {
            if (IsWord(tokens[index], word))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfKind(
        IReadOnlyList<OracleDdlToken> tokens,
        OracleDdlTokenKind kind,
        int start)
    {
        for (var index = start; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == kind)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsIdentifier(OracleDdlToken token) =>
        token.Kind == OracleDdlTokenKind.Word || token.Kind == OracleDdlTokenKind.QuotedIdentifier;

    private static bool IsWord(OracleDdlToken token, string word) =>
        token.Kind == OracleDdlTokenKind.Word && string.Equals(token.Value, word, StringComparison.OrdinalIgnoreCase);

    private OracleDdlIdentifier ParseIdentifier(OracleDdlToken token) =>
        new OracleDdlIdentifier(
            token.Kind == OracleDdlTokenKind.QuotedIdentifier ? token.Value! : token.Value!,
            token.Kind == OracleDdlTokenKind.QuotedIdentifier,
            token.Span);

    private bool MatchWord(string word)
    {
        if (!IsWord(Current, word))
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
            Report("DDL100", message, Current.Span);
        }
    }

    private bool Match(OracleDdlTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private OracleDdlToken Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private void ConsumeToEnd()
    {
        while (Current.Kind != OracleDdlTokenKind.End && Current.Kind != OracleDdlTokenKind.Semicolon)
        {
            Advance();
        }
    }

    private void Report(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(code, message, span));

    private OracleDdlToken Current => _tokens[_position];
    private OracleDdlToken Previous => _tokens[Math.Max(0, _position - 1)];

    private int PreviousEnd() => Previous.Span.Start + Previous.Span.Length;

    private static SourceSpan SpanFrom(int start, int end) => new SourceSpan(start, Math.Max(0, end - start));

    private static SourceSpan SpanOf(IReadOnlyList<OracleDdlToken> tokens) =>
        tokens.Count == 0
            ? new SourceSpan(0, 0)
            : SpanFrom(tokens[0].Span.Start, tokens[tokens.Count - 1].Span.Start + tokens[tokens.Count - 1].Span.Length);
}
