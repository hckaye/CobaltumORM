using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

/// <summary>Splits and classifies MySQL migration scripts.</summary>
public static class MySqlScriptClassifier
{
    public static IReadOnlyList<SqlScriptStatement> SplitAndClassify(
        string sql,
        out SqlScriptError? error)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        var statements = new List<SqlScriptStatement>();
        error = null;
        var statementStart = 0;
        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];
            if (current == '\'' || current == '"' || current == '`')
            {
                if (!MySqlSkipQuoted(sql, ref index, current, out error))
                {
                    break;
                }

                continue;
            }

            if (current == '#' ||
                current == '-' && MySqlPeek(sql, index + 1) == '-' &&
                (index + 2 >= sql.Length || char.IsWhiteSpace(sql[index + 2])))
            {
                index += current == '#' ? 1 : 2;
                while (index < sql.Length && sql[index] != '\r' && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && MySqlPeek(sql, index + 1) == '*')
            {
                if (!MySqlSkipBlockComment(sql, ref index, out error))
                {
                    break;
                }

                continue;
            }

            if (current == ';')
            {
                MySqlAddStatement(sql, statementStart, index + 1, statements);
                statementStart = index + 1;
            }

            index++;
        }

        if (error is null)
        {
            MySqlAddStatement(sql, statementStart, sql.Length, statements);
        }

        return statements;
    }

    private static void MySqlAddStatement(
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
            MySqlClassify(text)));
    }

    private static SqlStatementKind MySqlClassify(string statement)
    {
        var words = MySqlReadLeadingWords(statement, 5);
        if (words.Count == 0)
        {
            return SqlStatementKind.Empty;
        }

        var first = words[0];
        if (first == "SELECT" || first == "WITH" || first == "EXPLAIN" || first == "SHOW" ||
            first == "DESCRIBE" || first == "DESC")
        {
            return first == "SELECT" || first == "WITH"
                ? SqlStatementKind.Select
                : SqlStatementKind.SchemaNeutral;
        }

        if (first == "INSERT" || first == "UPDATE" || first == "DELETE" || first == "REPLACE" ||
            first == "CALL" || first == "DO" || first == "LOAD")
        {
            return SqlStatementKind.DataManipulation;
        }

        if (first == "USE")
        {
            return SqlStatementKind.SupportedTableDdl;
        }

        if (first == "CREATE")
        {
            if (MySqlWord(words, 1) == "TEMPORARY" && MySqlWord(words, 2) == "TABLE" ||
                MySqlWord(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            if (MySqlWord(words, 1) == "INDEX" ||
                MySqlWord(words, 1) == "UNIQUE" && MySqlWord(words, 2) == "INDEX")
            {
                return SqlStatementKind.SchemaNeutral;
            }

            return SqlStatementKind.Unsupported;
        }

        if (first == "DROP")
        {
            if (MySqlWord(words, 1) == "TEMPORARY" && MySqlWord(words, 2) == "TABLE" ||
                MySqlWord(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            return MySqlWord(words, 1) == "INDEX"
                ? SqlStatementKind.SchemaNeutral
                : SqlStatementKind.Unsupported;
        }

        if (first == "ALTER")
        {
            return MySqlWord(words, 1) == "TABLE"
                ? SqlStatementKind.SupportedTableDdl
                : SqlStatementKind.Unsupported;
        }

        if (first == "RENAME")
        {
            return MySqlWord(words, 1) == "TABLE"
                ? SqlStatementKind.SupportedTableDdl
                : SqlStatementKind.Unsupported;
        }

        if (first == "COMMENT" || first == "ANALYZE" || first == "BEGIN" || first == "COMMIT" ||
            first == "ROLLBACK" || first == "SET" || first == "START" || first == "TRUNCATE" ||
            first == "LOCK" || first == "UNLOCK" || first == "OPTIMIZE" || first == "REPAIR" ||
            first == "FLUSH" || first == "GRANT" || first == "REVOKE" || first == "RESET" ||
            first == "KILL" || first == "HANDLER")
        {
            return SqlStatementKind.SchemaNeutral;
        }

        return SqlStatementKind.Unsupported;
    }

    private static List<string> MySqlReadLeadingWords(string statement, int maximum)
    {
        var words = new List<string>();
        var index = 0;
        while (words.Count < maximum)
        {
            MySqlSkipTrivia(statement, ref index);
            if (index >= statement.Length || statement[index] == ';')
            {
                break;
            }

            if (!MySqlIsWordStart(statement[index]))
            {
                break;
            }

            var start = index++;
            while (index < statement.Length && MySqlIsWordPart(statement[index]))
            {
                index++;
            }

            words.Add(statement.Substring(start, index - start).ToUpperInvariant());
        }

        return words;
    }

    private static void MySqlSkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '#' ||
                text[index] == '-' && MySqlPeek(text, index + 1) == '-' &&
                (index + 2 >= text.Length || char.IsWhiteSpace(text[index + 2])))
            {
                index += text[index] == '#' ? 1 : 2;
                while (index < text.Length && text[index] != '\r' && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (text[index] == '/' && MySqlPeek(text, index + 1) == '*')
            {
                index += 2;
                while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/'))
                {
                    index++;
                }

                if (index + 1 >= text.Length)
                {
                    index = text.Length;
                    return;
                }

                index += 2;
                continue;
            }

            break;
        }
    }

    private static bool MySqlSkipQuoted(
        string text,
        ref int index,
        char quote,
        out SqlScriptError? error)
    {
        var opening = index++;
        while (index < text.Length)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index += 2;
                continue;
            }

            if (text[index] == quote)
            {
                if (index + 1 < text.Length && text[index + 1] == quote)
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
            quote == '`'
                ? "Unterminated MySQL backtick-quoted identifier."
                : "Unterminated MySQL string literal.",
            new SourceSpan(opening, text.Length - opening));
        return false;
    }

    private static bool MySqlSkipBlockComment(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var opening = index;
        var depth = 1;
        index += 2;
        while (index < text.Length)
        {
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
            {
                depth++;
                index += 2;
                continue;
            }

            if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '/')
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
            "Unterminated MySQL block comment.",
            new SourceSpan(opening, text.Length - opening));
        return false;
    }

    private static string? MySqlWord(IReadOnlyList<string> words, int index) =>
        index < words.Count ? words[index] : null;

    private static char MySqlPeek(string text, int index) =>
        index >= 0 && index < text.Length ? text[index] : '\0';

    private static bool MySqlIsWordStart(char value) => char.IsLetter(value) || value == '_';

    private static bool MySqlIsWordPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '$';
}

/// <summary>Adapts the MySQL script classifier to the dialect service contract.</summary>
public sealed class MySqlScriptClassifierService : ISqlScriptClassifier
{
    public IReadOnlyList<SqlScriptStatement> SplitAndClassify(
        string sql,
        out SqlScriptError? error) =>
        MySqlScriptClassifier.SplitAndClassify(sql, out error);
}
