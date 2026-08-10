using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CobaltumOrm.Analysis;
using CobaltumOrm.Migrations;

namespace CobaltumOrm.Migrations.Oracle;

internal static class OracleSchemaBuilder
{
    internal static MigrationSchema Build(IReadOnlyList<MigrationCommand> commands)
    {
        var tables = new List<Table>();
        foreach (var command in commands)
        {
            if (command is null)
            {
                throw new MigrationValidationException("The schema preview command collection contains null.");
            }

            if (IsHistoryEnsureBlock(command.CommandText))
            {
                continue;
            }

            var statements = OracleSqlLexer.SplitStatements(command.CommandText);
            if (statements.Count == 0)
            {
                throw Unsupported(command.CommandText);
            }

            foreach (var statement in statements)
            {
                ApplyStatement(tables, statement);
            }
        }

        return new MigrationSchema(tables.Select(table =>
            new MigrationSchemaTable(
                table.Schema,
                table.Name,
                table.Columns.Select(column =>
                    new MigrationSchemaColumn(
                        column.Name,
                        column.SqlType,
                        column.IsNullable,
                        column.IsPrimaryKey,
                        column.DefaultExpression,
                        column.IsIdentity)))));
    }

    private static void ApplyStatement(List<Table> tables, OracleSqlStatement statement)
    {
        var tokens = statement.Tokens;
        if (tokens.Count == 0)
        {
            return;
        }

        var first = Word(tokens[0]);
        if (EqualsWord(first, "CREATE"))
        {
            if (tokens.Count > 1 && EqualsWord(Word(tokens[1]), "TABLE"))
            {
                ApplyCreateTable(tables, tokens);
                return;
            }

            if (tokens.Count > 2 && EqualsWord(Word(tokens[1]), "GLOBAL") &&
                EqualsWord(Word(tokens[2]), "TEMPORARY") &&
                tokens.Count > 3 && EqualsWord(Word(tokens[3]), "TABLE"))
            {
                ApplyCreateTable(tables, tokens, 3);
                return;
            }

            throw Unsupported(statement.Text);
        }

        if (EqualsWord(first, "ALTER"))
        {
            if (tokens.Count > 1 && EqualsWord(Word(tokens[1]), "TABLE"))
            {
                ApplyAlterTable(tables, tokens);
                return;
            }

            if (tokens.Count > 1 && EqualsWord(Word(tokens[1]), "SESSION"))
            {
                return;
            }

            throw Unsupported(statement.Text);
        }

        if (EqualsWord(first, "DROP"))
        {
            if (tokens.Count > 1 && EqualsWord(Word(tokens[1]), "TABLE"))
            {
                ApplyDropTable(tables, tokens);
                return;
            }

            throw Unsupported(statement.Text);
        }

        if (EqualsWord(first, "RENAME"))
        {
            ApplyRenameTable(tables, tokens);
            return;
        }

        if (IsSchemaNeutral(first))
        {
            return;
        }

        throw Unsupported(statement.Text);
    }

    private static void ApplyCreateTable(List<Table> tables, IReadOnlyList<OracleSqlToken> tokens, int tableKeywordIndex = 1)
    {
        var cursor = new OracleSqlCursor(tokens, tableKeywordIndex + 1);
        var ifNotExists = false;
        if (cursor.MatchWord("IF"))
        {
            cursor.ExpectWord("NOT");
            cursor.ExpectWord("EXISTS");
            ifNotExists = true;
        }

        var tableName = cursor.ParseQualifiedName();
        var body = cursor.ParseParenthesizedTokens();
        if (body.Count == 0)
        {
            throw InvalidDdl("CREATE TABLE must declare at least one column.");
        }

        var columns = new List<ParsedColumn>();
        var primaryKeyColumns = new List<OracleIdentifier>();
        foreach (var segment in SplitTopLevel(body))
        {
            if (segment.Count == 0)
            {
                continue;
            }

            if (IsTableConstraint(segment))
            {
                ParseTableConstraint(segment, primaryKeyColumns);
                continue;
            }

            columns.Add(ParseColumn(segment, allowMissingType: false));
        }

        if (columns.Count == 0)
        {
            throw InvalidDdl("CREATE TABLE must declare at least one column.");
        }

        foreach (var primaryKey in primaryKeyColumns)
        {
            var index = columns.FindIndex(column => column.Name == primaryKey.Name);
            if (index < 0)
            {
                throw InvalidDdl($"Primary key column '{primaryKey.Name}' was not declared by the table.");
            }

            columns[index].IsPrimaryKey = true;
            columns[index].IsNullable = false;
        }

        if (FindTable(tables, tableName.Schema, tableName.Name) is not null)
        {
            if (ifNotExists)
            {
                return;
            }

            throw InvalidDdl($"Table '{tableName.DisplayName}' already exists in the preview schema.");
        }

        var duplicate = columns
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw InvalidDdl($"Table '{tableName.DisplayName}' declares column '{duplicate.Key}' more than once.");
        }

        tables.Add(new Table(
            tableName.Name,
            columns.Select(column => column.ToAnalysisColumn()),
            tableName.Schema));
    }

    private static void ApplyAlterTable(List<Table> tables, IReadOnlyList<OracleSqlToken> tokens)
    {
        var cursor = new OracleSqlCursor(tokens, 2);
        var tableName = cursor.ParseQualifiedName();
        var table = FindTable(tables, tableName.Schema, tableName.Name) ??
            throw InvalidDdl($"Table '{tableName.DisplayName}' does not exist in the preview schema.");

        if (cursor.MatchWord("ADD"))
        {
            cursor.MatchWord("COLUMN");
            if (cursor.PeekWord("CONSTRAINT") || cursor.PeekWord("PRIMARY"))
            {
                var constraint = cursor.RemainingTokens();
                var primaryKeyColumns = new List<OracleIdentifier>();
                ParseTableConstraint(constraint, primaryKeyColumns);
                ReplaceTable(tables, table, ApplyPrimaryKeyConstraint(table, primaryKeyColumns));
                return;
            }

            if (cursor.PeekWord("UNIQUE") || cursor.PeekWord("FOREIGN") || cursor.PeekWord("CHECK"))
            {
                throw Unsupported("ALTER TABLE ADD constraint");
            }

            var body = cursor.PeekKind(OracleSqlTokenKind.OpenParen)
                ? cursor.ParseParenthesizedTokens()
                : cursor.RemainingTokens();
            var additions = new List<ParsedColumn>();
            var primaryKeyColumnsForAdd = new List<OracleIdentifier>();
            foreach (var segment in SplitTopLevel(body))
            {
                if (segment.Count == 0)
                {
                    continue;
                }

                if (IsTableConstraint(segment))
                {
                    ParseTableConstraint(segment, primaryKeyColumnsForAdd);
                }
                else
                {
                    additions.Add(ParseColumn(segment, allowMissingType: false));
                }
            }

            foreach (var addition in additions)
            {
                if (table.Columns.Any(column => column.Name == addition.Name))
                {
                    throw InvalidDdl($"Column '{addition.Name}' already exists in table '{tableName.DisplayName}'.");
                }
            }

            var updatedColumns = table.Columns.Concat(additions.Select(column => column.ToAnalysisColumn()));
            var updatedTable = new Table(table.Name, updatedColumns, table.Schema);
            if (primaryKeyColumnsForAdd.Count != 0)
            {
                updatedTable = ApplyPrimaryKeyConstraint(updatedTable, primaryKeyColumnsForAdd);
            }

            ReplaceTable(tables, table, updatedTable);
            return;
        }

        if (cursor.MatchWord("MODIFY"))
        {
            var body = cursor.PeekKind(OracleSqlTokenKind.OpenParen)
                ? cursor.ParseParenthesizedTokens()
                : cursor.RemainingTokens();
            var modifications = SplitTopLevel(body)
                .Where(segment => segment.Count != 0)
                .Select(segment => ParseColumn(segment, allowMissingType: true))
                .ToArray();
            if (modifications.Length == 0)
            {
                throw InvalidDdl("ALTER TABLE MODIFY requires a column definition.");
            }

            var updated = table.Columns.ToList();
            foreach (var modification in modifications)
            {
                var index = updated.FindIndex(column => column.Name == modification.Name);
                if (index < 0)
                {
                    throw InvalidDdl($"Column '{modification.Name}' does not exist in table '{tableName.DisplayName}'.");
                }

                var existing = updated[index];
                updated[index] = new Column(
                    existing.Name,
                    modification.SqlType ?? existing.SqlType,
                    modification.IsNullableSpecified ? modification.IsNullable : existing.IsNullable,
                    modification.IsPrimaryKey || existing.IsPrimaryKey,
                    modification.IsDefaultSpecified ? modification.DefaultExpression : existing.DefaultExpression,
                    modification.IsIdentity || existing.IsIdentity);
            }

            ReplaceTable(tables, table, new Table(table.Name, updated, table.Schema));
            return;
        }

        if (cursor.MatchWord("DROP"))
        {
            cursor.MatchWord("COLUMN");
            if (cursor.MatchWord("CONSTRAINT"))
            {
                throw Unsupported("ALTER TABLE DROP CONSTRAINT");
            }

            var column = cursor.ParseIdentifier();
            var remaining = cursor.RemainingTokens();
            if (remaining.Any(token => EqualsWord(Word(token), "CASCADE")))
            {
                // CASCADE CONSTRAINTS changes constraint metadata, which is not
                // represented by MigrationSchema. The column change is still
                // deterministic and is therefore retained in the preview.
            }

            var index = table.Columns.ToList().FindIndex(existing => existing.Name == column.Name);
            if (index < 0)
            {
                throw InvalidDdl($"Column '{column.Name}' does not exist in table '{tableName.DisplayName}'.");
            }

            var updated = table.Columns.ToList();
            updated.RemoveAt(index);
            ReplaceTable(tables, table, new Table(table.Name, updated, table.Schema));
            return;
        }

        if (cursor.MatchWord("RENAME"))
        {
            if (cursor.MatchWord("COLUMN"))
            {
                var oldName = cursor.ParseIdentifier();
                cursor.ExpectWord("TO");
                var newName = cursor.ParseIdentifier();
                RenameColumn(tables, table, tableName, oldName, newName);
                return;
            }

            cursor.ExpectWord("TO");
            var newTableName = cursor.ParseIdentifier();
            RenameTable(tables, table, newTableName);
            return;
        }

        throw Unsupported("ALTER TABLE statement");
    }

    private static void ApplyDropTable(List<Table> tables, IReadOnlyList<OracleSqlToken> tokens)
    {
        var cursor = new OracleSqlCursor(tokens, 2);
        var tableName = cursor.ParseQualifiedName();
        var table = FindTable(tables, tableName.Schema, tableName.Name);
        if (table is null)
        {
            throw InvalidDdl($"Table '{tableName.DisplayName}' does not exist in the preview schema.");
        }

        ReplaceTable(tables, table, null);
    }

    private static void ApplyRenameTable(List<Table> tables, IReadOnlyList<OracleSqlToken> tokens)
    {
        var cursor = new OracleSqlCursor(tokens, 1);
        var oldName = cursor.ParseQualifiedName();
        cursor.ExpectWord("TO");
        var newName = cursor.ParseIdentifier();
        var table = FindTable(tables, oldName.Schema, oldName.Name) ??
            throw InvalidDdl($"Table '{oldName.DisplayName}' does not exist in the preview schema.");
        RenameTable(tables, table, newName);
    }

    private static void RenameTable(
        List<Table> tables,
        Table table,
        OracleIdentifier newName)
    {
        if (FindTable(tables, table.Schema, newName.Name) is not null)
        {
            throw InvalidDdl($"Table '{newName.Name}' already exists in the preview schema.");
        }

        ReplaceTable(tables, table, new Table(newName.Name, table.Columns, table.Schema));
    }

    private static void RenameColumn(
        List<Table> tables,
        Table table,
        OracleQualifiedName tableName,
        OracleIdentifier oldName,
        OracleIdentifier newName)
    {
        var columns = table.Columns.ToList();
        var oldIndex = columns.FindIndex(column => column.Name == oldName.Name);
        if (oldIndex < 0)
        {
            throw InvalidDdl($"Column '{oldName.Name}' does not exist in table '{tableName.DisplayName}'.");
        }

        if (columns.Any(column => column.Name == newName.Name))
        {
            throw InvalidDdl($"Column '{newName.Name}' already exists in table '{tableName.DisplayName}'.");
        }

        var existing = columns[oldIndex];
        columns[oldIndex] = new Column(
            newName.Name,
            existing.SqlType,
            existing.IsNullable,
            existing.IsPrimaryKey,
            existing.DefaultExpression,
            existing.IsIdentity);
        ReplaceTable(tables, table, new Table(table.Name, columns, table.Schema));
    }

    private static Table ApplyPrimaryKeyConstraint(Table table, IReadOnlyList<OracleIdentifier> columns)
    {
        if (columns.Count == 0)
        {
            throw InvalidDdl("A primary key constraint must name at least one column.");
        }

        var existing = table.Columns.ToList();
        foreach (var key in columns)
        {
            var index = existing.FindIndex(column => column.Name == key.Name);
            if (index < 0)
            {
                throw InvalidDdl($"Primary key column '{key.Name}' was not declared by the table.");
            }

            var column = existing[index];
            existing[index] = new Column(
                column.Name,
                column.SqlType,
                false,
                true,
                column.DefaultExpression,
                column.IsIdentity);
        }

        return new Table(table.Name, existing, table.Schema);
    }

    private static void ParseTableConstraint(
        IReadOnlyList<OracleSqlToken> segment,
        List<OracleIdentifier> primaryKeyColumns)
    {
        var primaryKeyIndex = IndexOfWord(segment, "PRIMARY");
        if (primaryKeyIndex < 0 || primaryKeyIndex + 1 >= segment.Count ||
            !EqualsWord(Word(segment[primaryKeyIndex + 1]), "KEY"))
        {
            return;
        }

        var open = segment.ToList().FindIndex(primaryKeyIndex, token => token.Kind == OracleSqlTokenKind.OpenParen);
        if (open < 0)
        {
            throw InvalidDdl("A primary key constraint must name its columns.");
        }

        var close = FindMatchingParen(segment, open);
        foreach (var key in SplitTopLevel(segment.Skip(open + 1).Take(close - open - 1).ToList()))
        {
            if (key.Count != 1 || !IsIdentifier(key[0]))
            {
                throw InvalidDdl("A primary key column name is invalid.");
            }

            primaryKeyColumns.Add(ParseIdentifier(key[0]));
        }
    }

    private static ParsedColumn ParseColumn(IReadOnlyList<OracleSqlToken> segment, bool allowMissingType)
    {
        if (segment.Count == 0 || !IsIdentifier(segment[0]))
        {
            throw InvalidDdl("A column definition must start with a column name.");
        }

        var name = ParseIdentifier(segment[0]);
        var typeStart = 1;
        var typeEnd = typeStart;
        var startsWithConstraint = typeStart < segment.Count && IsColumnConstraintStart(segment[typeStart]);
        if (startsWithConstraint)
        {
            typeEnd = typeStart;
        }

        var depth = 0;
        while (typeEnd < segment.Count)
        {
            var token = segment[typeEnd];
            if (token.Kind == OracleSqlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == OracleSqlTokenKind.CloseParen)
            {
                depth--;
            }

            if (depth == 0 && IsColumnConstraintStart(token) && typeEnd > typeStart)
            {
                break;
            }

            typeEnd++;
        }

        string? sqlType = typeEnd == typeStart
            ? null
            : FormatSqlTokens(segment.Skip(typeStart).Take(typeEnd - typeStart).ToList());
        if (!allowMissingType && sqlType is null)
        {
            throw InvalidDdl($"Column '{name.Name}' must declare a type.");
        }

        if (allowMissingType && startsWithConstraint)
        {
            sqlType = null;
        }

        var column = new ParsedColumn(name.Name, sqlType)
        {
            IsNullable = allowMissingType ? false : true,
        };
        var index = typeEnd;
        while (index < segment.Count)
        {
            var token = segment[index];
            if (EqualsWord(Word(token), "NOT") && index + 1 < segment.Count &&
                EqualsWord(Word(segment[index + 1]), "NULL"))
            {
                column.IsNullable = false;
                column.IsNullableSpecified = true;
                index += 2;
                continue;
            }

            if (EqualsWord(Word(token), "NULL"))
            {
                column.IsNullable = true;
                column.IsNullableSpecified = true;
                index++;
                continue;
            }

            if (EqualsWord(Word(token), "PRIMARY") && index + 1 < segment.Count &&
                EqualsWord(Word(segment[index + 1]), "KEY"))
            {
                column.IsPrimaryKey = true;
                column.IsNullable = false;
                column.IsNullableSpecified = true;
                index += 2;
                continue;
            }

            if (EqualsWord(Word(token), "GENERATED"))
            {
                if (segment.Skip(index).Any(item => EqualsWord(Word(item), "IDENTITY")))
                {
                    column.IsIdentity = true;
                }

                index++;
                continue;
            }

            if (EqualsWord(Word(token), "IDENTITY"))
            {
                column.IsIdentity = true;
                index++;
                continue;
            }

            if (EqualsWord(Word(token), "DEFAULT"))
            {
                var defaultStart = index + 1;
                var defaultEnd = defaultStart;
                var defaultDepth = 0;
                while (defaultEnd < segment.Count)
                {
                    var defaultToken = segment[defaultEnd];
                    if (defaultToken.Kind == OracleSqlTokenKind.OpenParen)
                    {
                        defaultDepth++;
                    }
                    else if (defaultToken.Kind == OracleSqlTokenKind.CloseParen)
                    {
                        defaultDepth--;
                    }

                    if (defaultDepth == 0 && defaultEnd > defaultStart &&
                        IsDefaultConstraintStart(defaultToken))
                    {
                        break;
                    }

                    defaultEnd++;
                }

                column.DefaultExpression = defaultStart == defaultEnd
                    ? null
                    : FormatSqlTokens(segment.Skip(defaultStart).Take(defaultEnd - defaultStart).ToList());
                column.IsDefaultSpecified = true;
                index = defaultEnd;
                continue;
            }

            // CHECK, REFERENCES, UNIQUE, constraint names, and storage options
            // do not affect the column shape exposed by MigrationSchema.
            index++;
        }

        return column;
    }

    private static bool IsTableConstraint(IReadOnlyList<OracleSqlToken> segment)
    {
        var first = Word(segment[0]);
        return EqualsWord(first, "CONSTRAINT") || EqualsWord(first, "PRIMARY") ||
               EqualsWord(first, "UNIQUE") || EqualsWord(first, "FOREIGN") ||
               EqualsWord(first, "CHECK") || EqualsWord(first, "EXCLUDE");
    }

    private static bool IsColumnConstraintStart(OracleSqlToken token)
    {
        var word = Word(token);
        return EqualsWord(word, "DEFAULT") || EqualsWord(word, "NOT") ||
               EqualsWord(word, "NULL") || EqualsWord(word, "PRIMARY") ||
               EqualsWord(word, "UNIQUE") || EqualsWord(word, "REFERENCES") ||
               EqualsWord(word, "CHECK") || EqualsWord(word, "CONSTRAINT") ||
               EqualsWord(word, "GENERATED") || EqualsWord(word, "IDENTITY") ||
               EqualsWord(word, "COLLATE") || EqualsWord(word, "VISIBLE") ||
               EqualsWord(word, "INVISIBLE") || EqualsWord(word, "ENABLE") ||
               EqualsWord(word, "DISABLE");
    }

    private static bool IsDefaultConstraintStart(OracleSqlToken token)
    {
        var word = Word(token);
        return EqualsWord(word, "NOT") || EqualsWord(word, "PRIMARY") ||
               EqualsWord(word, "UNIQUE") || EqualsWord(word, "REFERENCES") ||
               EqualsWord(word, "CHECK") || EqualsWord(word, "CONSTRAINT") ||
               EqualsWord(word, "GENERATED") || EqualsWord(word, "IDENTITY") ||
               EqualsWord(word, "COLLATE") || EqualsWord(word, "VISIBLE") ||
               EqualsWord(word, "INVISIBLE") || EqualsWord(word, "ENABLE") ||
               EqualsWord(word, "DISABLE");
    }

    private static int IndexOfWord(IReadOnlyList<OracleSqlToken> tokens, string word)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (EqualsWord(Word(tokens[index]), word))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingParen(IReadOnlyList<OracleSqlToken> tokens, int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == OracleSqlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (tokens[index].Kind == OracleSqlTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        throw InvalidDdl("A parenthesized Oracle definition is not closed.");
    }

    private static IReadOnlyList<IReadOnlyList<OracleSqlToken>> SplitTopLevel(IReadOnlyList<OracleSqlToken> tokens)
    {
        var result = new List<IReadOnlyList<OracleSqlToken>>();
        var current = new List<OracleSqlToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == OracleSqlTokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == OracleSqlTokenKind.CloseParen)
            {
                depth--;
            }

            if (token.Kind == OracleSqlTokenKind.Comma && depth == 0)
            {
                result.Add(current);
                current = new List<OracleSqlToken>();
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

    private static string FormatSqlTokens(IReadOnlyList<OracleSqlToken> tokens)
    {
        var builder = new StringBuilder();
        OracleSqlToken? previous = null;
        foreach (var token in tokens)
        {
            var noSpaceBefore = token.Kind == OracleSqlTokenKind.OpenParen ||
                                token.Kind == OracleSqlTokenKind.CloseParen ||
                                token.Kind == OracleSqlTokenKind.Comma ||
                                token.Kind == OracleSqlTokenKind.Dot;
            var noSpaceAfterPrevious = previous is not null &&
                                       (previous.Kind == OracleSqlTokenKind.OpenParen ||
                                        previous.Kind == OracleSqlTokenKind.Dot ||
                                        previous.Kind == OracleSqlTokenKind.Comma);
            if (builder.Length != 0 && !noSpaceBefore && !noSpaceAfterPrevious)
            {
                builder.Append(' ');
            }

            builder.Append(token.Text);
            previous = token;
        }

        return builder.ToString();
    }

    private static Table? FindTable(List<Table> tables, string? schema, string name) =>
        tables.FirstOrDefault(table => string.Equals(table.Schema, schema, StringComparison.Ordinal) &&
                                       string.Equals(table.Name, name, StringComparison.Ordinal));

    private static void ReplaceTable(List<Table> tables, Table current, Table? replacement)
    {
        var index = tables.IndexOf(current);
        if (index < 0)
        {
            throw InvalidDdl($"Table '{current.Name}' is not present in the preview schema.");
        }

        if (replacement is null)
        {
            tables.RemoveAt(index);
        }
        else
        {
            tables[index] = replacement;
        }
    }

    private static string Word(OracleSqlToken token) =>
        token.Kind == OracleSqlTokenKind.Word ? token.Value! : string.Empty;

    private static bool IsIdentifier(OracleSqlToken token) =>
        token.Kind == OracleSqlTokenKind.Word || token.Kind == OracleSqlTokenKind.QuotedIdentifier;

    private static OracleIdentifier ParseIdentifier(OracleSqlToken token)
    {
        if (!IsIdentifier(token))
        {
            throw InvalidDdl("An Oracle identifier was expected.");
        }

        return new OracleIdentifier(
            token.Kind == OracleSqlTokenKind.QuotedIdentifier
                ? token.Value!
                : token.Value!.ToUpperInvariant(),
            token.Kind == OracleSqlTokenKind.QuotedIdentifier);
    }

    private static bool EqualsWord(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsSchemaNeutral(string firstWord)
    {
        switch (firstWord.ToUpperInvariant())
        {
            case "SELECT":
            case "INSERT":
            case "UPDATE":
            case "DELETE":
            case "MERGE":
            case "WITH":
            case "COMMIT":
            case "ROLLBACK":
            case "SAVEPOINT":
            case "SET":
            case "CALL":
            case "EXEC":
            case "EXPLAIN":
            case "TRUNCATE":
            case "COMMENT":
            case "GRANT":
            case "REVOKE":
            case "ANALYZE":
            case "LOCK":
                return true;
            default:
                return false;
        }
    }

    private static bool IsHistoryEnsureBlock(string sql)
    {
        return sql.TrimStart().StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) &&
               sql.IndexOf("EXECUTE IMMEDIATE", StringComparison.OrdinalIgnoreCase) >= 0 &&
               sql.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase) >= 0 &&
               sql.IndexOf("SQLCODE", StringComparison.OrdinalIgnoreCase) >= 0 &&
               sql.IndexOf("-955", StringComparison.Ordinal) >= 0;
    }

    private static MigrationValidationException InvalidDdl(string message) =>
        new MigrationValidationException("Final schema could not be determined from Oracle migration SQL: " + message);

    private static MigrationValidationException Unsupported(string sql) =>
        new MigrationValidationException(
            "Final schema could not be determined from Oracle migration SQL because the statement may change the queryable schema and is not supported: " +
            sql.Trim());

    private sealed class ParsedColumn
    {
        internal ParsedColumn(string name, string? sqlType)
        {
            Name = name;
            SqlType = sqlType;
        }

        internal string Name { get; }
        internal string? SqlType { get; }
        internal bool IsNullable { get; set; }
        internal bool IsNullableSpecified { get; set; }
        internal bool IsPrimaryKey { get; set; }
        internal bool IsIdentity { get; set; }
        internal bool IsDefaultSpecified { get; set; }
        internal string? DefaultExpression { get; set; }

        internal Column ToAnalysisColumn() =>
            new Column(Name, SqlType!, IsNullable, IsPrimaryKey, DefaultExpression, IsIdentity);
    }

    private sealed class OracleIdentifier
    {
        internal OracleIdentifier(string name, bool quoted)
        {
            Name = name;
            Quoted = quoted;
        }

        internal string Name { get; }
        internal bool Quoted { get; }
        internal string DisplayName => Quoted ? "\"" + Name + "\"" : Name;
    }

    private sealed class OracleQualifiedName
    {
        internal OracleQualifiedName(string? schema, string name, string displayName)
        {
            Schema = schema;
            Name = name;
            DisplayName = displayName;
        }

        internal string? Schema { get; }
        internal string Name { get; }
        internal string DisplayName { get; }
    }

    private sealed class OracleSqlCursor
    {
        private readonly IReadOnlyList<OracleSqlToken> _tokens;
        private int _index;

        internal OracleSqlCursor(IReadOnlyList<OracleSqlToken> tokens, int index)
        {
            _tokens = tokens;
            _index = index;
        }

        internal bool MatchWord(string word)
        {
            if (!PeekWord(word))
            {
                return false;
            }

            _index++;
            return true;
        }

        internal bool PeekWord(string word) =>
            _index < _tokens.Count && EqualsWord(Word(_tokens[_index]), word);

        internal bool PeekKind(OracleSqlTokenKind kind) =>
            _index < _tokens.Count && _tokens[_index].Kind == kind;

        internal void ExpectWord(string word)
        {
            if (!MatchWord(word))
            {
                throw InvalidDdl("Expected " + word + " in Oracle migration SQL.");
            }
        }

        internal OracleIdentifier ParseIdentifier()
        {
            if (_index >= _tokens.Count)
            {
                throw InvalidDdl("Expected an Oracle identifier.");
            }

            return OracleSchemaBuilder.ParseIdentifier(_tokens[_index++]);
        }

        internal OracleQualifiedName ParseQualifiedName()
        {
            var first = ParseIdentifier();
            if (!(_index < _tokens.Count && _tokens[_index].Kind == OracleSqlTokenKind.Dot))
            {
                return new OracleQualifiedName(null, first.Name, first.DisplayName);
            }

            _index++;
            var second = ParseIdentifier();
            return new OracleQualifiedName(first.Name, second.Name, first.DisplayName + "." + second.DisplayName);
        }

        internal IReadOnlyList<OracleSqlToken> ParseParenthesizedTokens()
        {
            if (!PeekKind(OracleSqlTokenKind.OpenParen))
            {
                throw InvalidDdl("Expected '(' in Oracle migration SQL.");
            }

            _index++;
            var depth = 1;
            var body = new List<OracleSqlToken>();
            while (_index < _tokens.Count)
            {
                var token = _tokens[_index++];
                if (token.Kind == OracleSqlTokenKind.OpenParen)
                {
                    depth++;
                }
                else if (token.Kind == OracleSqlTokenKind.CloseParen)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return body;
                    }
                }

                body.Add(token);
            }

            throw InvalidDdl("A parenthesized Oracle definition is not closed.");
        }

        internal IReadOnlyList<OracleSqlToken> RemainingTokens()
        {
            var remaining = _tokens.Skip(_index).ToArray();
            _index = _tokens.Count;
            return remaining;
        }
    }
}

internal enum OracleSqlTokenKind
{
    Word,
    QuotedIdentifier,
    String,
    Number,
    Symbol,
    Comma,
    Dot,
    OpenParen,
    CloseParen,
    Semicolon,
}

internal sealed class OracleSqlToken
{
    internal OracleSqlToken(OracleSqlTokenKind kind, string text, string? value)
    {
        Kind = kind;
        Text = text;
        Value = value;
    }

    internal OracleSqlTokenKind Kind { get; }
    internal string Text { get; }
    internal string? Value { get; }
}

internal sealed class OracleSqlStatement
{
    internal OracleSqlStatement(IReadOnlyList<OracleSqlToken> tokens)
    {
        Tokens = tokens;
        Text = string.Join(" ", tokens.Select(token => token.Text));
    }

    internal IReadOnlyList<OracleSqlToken> Tokens { get; }
    internal string Text { get; }
}

internal static class OracleSqlLexer
{
    internal static IReadOnlyList<OracleSqlStatement> SplitStatements(string sql)
    {
        var tokens = Lex(sql);
        var statements = new List<OracleSqlStatement>();
        var current = new List<OracleSqlToken>();
        foreach (var token in tokens)
        {
            if (token.Kind == OracleSqlTokenKind.Semicolon)
            {
                if (current.Count != 0)
                {
                    statements.Add(new OracleSqlStatement(current.ToArray()));
                    current.Clear();
                }

                continue;
            }

            current.Add(token);
        }

        if (current.Count != 0)
        {
            statements.Add(new OracleSqlStatement(current.ToArray()));
        }

        return statements;
    }

    private static IReadOnlyList<OracleSqlToken> Lex(string sql)
    {
        var tokens = new List<OracleSqlToken>();
        var position = 0;
        while (position < sql.Length)
        {
            if (char.IsWhiteSpace(sql[position]))
            {
                position++;
                continue;
            }

            if (sql[position] == '-' && position + 1 < sql.Length && sql[position + 1] == '-')
            {
                position += 2;
                while (position < sql.Length && sql[position] != '\r' && sql[position] != '\n')
                {
                    position++;
                }

                continue;
            }

            if (sql[position] == '/' && position + 1 < sql.Length && sql[position + 1] == '*')
            {
                position = SkipBlockComment(sql, position);
                continue;
            }

            var start = position;
            var current = sql[position++];
            if (current == '\'')
            {
                tokens.Add(ReadString(sql, ref position, start));
                continue;
            }

            if ((current == 'q' || current == 'Q') && position < sql.Length && sql[position] == '\'')
            {
                tokens.Add(ReadOracleQuotedString(sql, ref position, start));
                continue;
            }

            if (current == '"')
            {
                tokens.Add(ReadQuotedIdentifier(sql, ref position, start));
                continue;
            }

            if (char.IsLetter(current) || current == '_' || current == '$' || current == '#')
            {
                while (position < sql.Length &&
                       (char.IsLetterOrDigit(sql[position]) || sql[position] == '_' ||
                        sql[position] == '$' || sql[position] == '#'))
                {
                    position++;
                }

                var text = sql.Substring(start, position - start);
                tokens.Add(new OracleSqlToken(OracleSqlTokenKind.Word, text, text));
                continue;
            }

            if (char.IsDigit(current))
            {
                while (position < sql.Length && (char.IsDigit(sql[position]) || sql[position] == '.'))
                {
                    position++;
                }

                var text = sql.Substring(start, position - start);
                tokens.Add(new OracleSqlToken(OracleSqlTokenKind.Number, text, text));
                continue;
            }

            OracleSqlTokenKind kind;
            switch (current)
            {
                case ',': kind = OracleSqlTokenKind.Comma; break;
                case '.': kind = OracleSqlTokenKind.Dot; break;
                case '(': kind = OracleSqlTokenKind.OpenParen; break;
                case ')': kind = OracleSqlTokenKind.CloseParen; break;
                case ';': kind = OracleSqlTokenKind.Semicolon; break;
                default: kind = OracleSqlTokenKind.Symbol; break;
            }

            tokens.Add(new OracleSqlToken(kind, sql.Substring(start, position - start), null));
        }

        return tokens;
    }

    private static OracleSqlToken ReadString(string sql, ref int position, int start)
    {
        while (position < sql.Length)
        {
            if (sql[position] == '\'')
            {
                position++;
                if (position < sql.Length && sql[position] == '\'')
                {
                    position++;
                    continue;
                }

                break;
            }

            position++;
        }

        var text = sql.Substring(start, position - start);
        return new OracleSqlToken(OracleSqlTokenKind.String, text, text);
    }

    private static OracleSqlToken ReadOracleQuotedString(string sql, ref int position, int start)
    {
        // position points at the quote after the q/Q prefix.
        position++;
        if (position >= sql.Length)
        {
            return new OracleSqlToken(OracleSqlTokenKind.String, sql.Substring(start), sql.Substring(start));
        }

        var delimiter = sql[position++];
        var closing = delimiter == '[' ? ']' : delimiter == '{' ? '}' : delimiter == '(' ? ')' :
            delimiter == '<' ? '>' : delimiter;
        while (position < sql.Length)
        {
            if (sql[position] == closing && position + 1 < sql.Length && sql[position + 1] == '\'')
            {
                position += 2;
                break;
            }

            position++;
        }

        var text = sql.Substring(start, position - start);
        return new OracleSqlToken(OracleSqlTokenKind.String, text, text);
    }

    private static OracleSqlToken ReadQuotedIdentifier(string sql, ref int position, int start)
    {
        var value = new StringBuilder();
        while (position < sql.Length)
        {
            var current = sql[position++];
            if (current == '"')
            {
                if (position < sql.Length && sql[position] == '"')
                {
                    position++;
                    value.Append('"');
                    continue;
                }

                break;
            }

            value.Append(current);
        }

        return new OracleSqlToken(
            OracleSqlTokenKind.QuotedIdentifier,
            sql.Substring(start, position - start),
            value.ToString());
    }

    private static int SkipBlockComment(string sql, int position)
    {
        var depth = 1;
        position += 2;
        while (position < sql.Length && depth > 0)
        {
            if (position + 1 < sql.Length && sql[position] == '/' && sql[position + 1] == '*')
            {
                depth++;
                position += 2;
            }
            else if (position + 1 < sql.Length && sql[position] == '*' && sql[position + 1] == '/')
            {
                depth--;
                position += 2;
            }
            else
            {
                position++;
            }
        }

        return position;
    }
}
