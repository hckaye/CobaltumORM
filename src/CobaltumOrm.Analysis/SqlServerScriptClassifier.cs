using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

/// <summary>Splits T-SQL batches without treating lexical semicolons as boundaries.</summary>
internal static class SqlServerScriptClassifier
{
    internal static IReadOnlyList<SqlScriptStatement> SplitAndClassify(
        string sql,
        out SqlScriptError? error)
    {
        var statements = new List<SqlScriptStatement>();
        error = null;
        var statementStart = 0;
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            if (current == '\'')
            {
                if (!SqlServerSkipQuoted(sql, ref index, '\'', "Unterminated SQL Server string literal.", out error))
                {
                    break;
                }

                continue;
            }

            if (current == '[')
            {
                if (!SqlServerSkipBracketIdentifier(sql, ref index, out error))
                {
                    break;
                }

                continue;
            }

            if (current == '"')
            {
                if (!SqlServerSkipQuoted(sql, ref index, '"', "Unterminated SQL Server quoted identifier.", out error))
                {
                    break;
                }

                continue;
            }

            if (current == '-' && SqlServerPeek(sql, index + 1) == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\r' && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && SqlServerPeek(sql, index + 1) == '*')
            {
                if (!SqlServerSkipBlockComment(sql, ref index, out error))
                {
                    break;
                }

                continue;
            }

            if (current == ';')
            {
                SqlServerAddStatement(sql, statementStart, index + 1, statements);
                statementStart = index + 1;
                index++;
                continue;
            }

            if (SqlServerIsBatchSeparator(sql, index))
            {
                SqlServerAddStatement(sql, statementStart, index, statements);
                index += 2;
                while (index < sql.Length && sql[index] != '\r' && sql[index] != '\n')
                {
                    index++;
                }

                statementStart = index;
                continue;
            }

            index++;
        }

        if (error is null)
        {
            SqlServerAddStatement(sql, statementStart, sql.Length, statements);
        }

        return statements;
    }

    private static void SqlServerAddStatement(
        string sql,
        int start,
        int end,
        ICollection<SqlScriptStatement> statements)
    {
        if (end <= start)
        {
            return;
        }

        var text = sql.Substring(start, end - start);
        statements.Add(new SqlScriptStatement(
            text,
            new SourceSpan(start, end - start),
            SqlServerClassify(text)));
    }

    private static SqlStatementKind SqlServerClassify(string statement)
    {
        var words = SqlServerReadLeadingWords(statement, 6);
        if (words.Count == 0)
        {
            return SqlStatementKind.Empty;
        }

        var first = words[0];
        if (first == "SELECT" || first == "WITH")
        {
            return SqlStatementKind.Select;
        }

        if (first == "INSERT" || first == "UPDATE" || first == "DELETE" || first == "MERGE" ||
            first == "TRUNCATE")
        {
            return SqlStatementKind.DataManipulation;
        }

        if (SqlServerIsSchemaNeutralLeadingWord(first))
        {
            return SqlStatementKind.SchemaNeutral;
        }

        if (first == "EXEC" || first == "EXECUTE")
        {
            return SqlServerClassifyExecute(statement);
        }

        if (first == "CREATE")
        {
            if (SqlServerWord(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            if (SqlServerContainsIndexWord(words))
            {
                return SqlStatementKind.SchemaNeutral;
            }

            return SqlStatementKind.Unsupported;
        }

        if (first == "DROP")
        {
            if (SqlServerWord(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            return SqlServerWord(words, 1) == "INDEX"
                ? SqlStatementKind.SchemaNeutral
                : SqlStatementKind.Unsupported;
        }

        if (first == "ALTER")
        {
            if (SqlServerWord(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            return SqlServerWord(words, 1) == "INDEX"
                ? SqlStatementKind.SchemaNeutral
                : SqlStatementKind.Unsupported;
        }

        return SqlStatementKind.Unsupported;
    }

    private static SqlStatementKind SqlServerClassifyExecute(string statement)
    {
        var position = SqlServerSkipTrivia(statement, 0);
        position = SqlServerReadWordEnd(statement, position);
        position = SqlServerSkipTrivia(statement, position);
        var firstStart = position;
        position = SqlServerReadWordEnd(statement, position);
        if (position == firstStart)
        {
            return SqlStatementKind.Unsupported;
        }

        var first = statement.Substring(firstStart, position - firstStart);
        position = SqlServerSkipTrivia(statement, position);
        if (SqlServerPeek(statement, position) == '.')
        {
            position = SqlServerSkipTrivia(statement, position + 1);
            var secondStart = position;
            position = SqlServerReadWordEnd(statement, position);
            if (position == secondStart)
            {
                return SqlStatementKind.Unsupported;
            }

            first = statement.Substring(secondStart, position - secondStart);
        }

        if (string.Equals(first, "sp_rename", StringComparison.OrdinalIgnoreCase))
        {
            return SqlStatementKind.SupportedTableDdl;
        }

        return string.Equals(first, "sp_addextendedproperty", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "sp_dropextendedproperty", StringComparison.OrdinalIgnoreCase)
            ? SqlStatementKind.SchemaNeutral
            : SqlStatementKind.Unsupported;
    }

    private static bool SqlServerContainsIndexWord(IReadOnlyList<string> words)
    {
        for (var index = 1; index < words.Count; index++)
        {
            if (words[index] == "INDEX")
            {
                return true;
            }
        }

        return false;
    }

    private static bool SqlServerIsSchemaNeutralLeadingWord(string word)
    {
        switch (word)
        {
            case "BEGIN":
            case "COMMIT":
            case "DECLARE":
            case "DENY":
            case "GRANT":
            case "PRINT":
            case "ROLLBACK":
            case "SAVE":
            case "SET":
            case "USE":
            case "WAITFOR":
                return true;
            default:
                return false;
        }
    }

    private static List<string> SqlServerReadLeadingWords(string statement, int maximum)
    {
        var words = new List<string>();
        var index = 0;
        while (words.Count < maximum)
        {
            index = SqlServerSkipTrivia(statement, index);
            if (index >= statement.Length || statement[index] == ';' || !SqlServerIsWordStart(statement[index]))
            {
                break;
            }

            var start = index;
            index = SqlServerReadWordEnd(statement, index);
            words.Add(statement.Substring(start, index - start).ToUpperInvariant());
        }

        return words;
    }

    private static int SqlServerSkipTrivia(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '-' && SqlServerPeek(text, index + 1) == '-')
            {
                index += 2;
                while (index < text.Length && text[index] != '\r' && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (text[index] == '/' && SqlServerPeek(text, index + 1) == '*')
            {
                index += 2;
                var depth = 1;
                while (index < text.Length && depth > 0)
                {
                    if (text[index] == '/' && SqlServerPeek(text, index + 1) == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (text[index] == '*' && SqlServerPeek(text, index + 1) == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }

                continue;
            }

            break;
        }

        return index;
    }

    private static int SqlServerReadWordEnd(string text, int index)
    {
        while (index < text.Length && SqlServerIsWordPart(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool SqlServerIsBatchSeparator(string text, int index)
    {
        if (index + 1 >= text.Length || (text[index] != 'G' && text[index] != 'g') ||
            (text[index + 1] != 'O' && text[index + 1] != 'o'))
        {
            return false;
        }

        if (!SqlServerIsLineStart(text, index))
        {
            return false;
        }

        var after = index + 2;
        return after == text.Length || char.IsWhiteSpace(text[after]);
    }

    private static bool SqlServerIsLineStart(string text, int index)
    {
        var cursor = index - 1;
        while (cursor >= 0 && text[cursor] != '\r' && text[cursor] != '\n')
        {
            if (!char.IsWhiteSpace(text[cursor]))
            {
                return false;
            }

            cursor--;
        }

        return true;
    }

    private static bool SqlServerSkipQuoted(
        string text,
        ref int index,
        char quote,
        string message,
        out SqlScriptError? error)
    {
        var start = index++;
        while (index < text.Length)
        {
            if (text[index] == quote)
            {
                if (SqlServerPeek(text, index + 1) == quote)
                {
                    index += 2;
                    continue;
                }

                index++;
                error = null;
                return true;
            }

            index++;
        }

        error = new SqlScriptError(message, new SourceSpan(start, text.Length - start));
        return false;
    }

    private static bool SqlServerSkipBracketIdentifier(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var start = index++;
        while (index < text.Length)
        {
            if (text[index] == ']')
            {
                if (SqlServerPeek(text, index + 1) == ']')
                {
                    index += 2;
                    continue;
                }

                index++;
                error = null;
                return true;
            }

            index++;
        }

        error = new SqlScriptError(
            "Unterminated SQL Server bracket identifier.",
            new SourceSpan(start, text.Length - start));
        return false;
    }

    private static bool SqlServerSkipBlockComment(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var start = index;
        var depth = 1;
        index += 2;
        while (index < text.Length)
        {
            if (text[index] == '/' && SqlServerPeek(text, index + 1) == '*')
            {
                depth++;
                index += 2;
                continue;
            }

            if (text[index] == '*' && SqlServerPeek(text, index + 1) == '/')
            {
                depth--;
                index += 2;
                if (depth == 0)
                {
                    error = null;
                    return true;
                }

                continue;
            }

            index++;
        }

        error = new SqlScriptError(
            "Unterminated SQL Server block comment.",
            new SourceSpan(start, text.Length - start));
        return false;
    }

    private static bool SqlServerIsWordStart(char value) =>
        char.IsLetter(value) || value == '_' || value == '#' || value == '@';

    private static bool SqlServerIsWordPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#' || value == '@';

    private static char SqlServerPeek(string text, int index) =>
        index >= 0 && index < text.Length ? text[index] : '\0';

    private static string? SqlServerWord(IReadOnlyList<string> words, int index) =>
        index < words.Count ? words[index] : null;
}
