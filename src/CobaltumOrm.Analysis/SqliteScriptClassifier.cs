using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

/// <summary>Adapts the SQLite script classifier to the dialect service contract.</summary>
public sealed class SqliteScriptClassifierService : ISqlScriptClassifier
{
    /// <inheritdoc />
    public IReadOnlyList<SqlScriptStatement> SplitAndClassify(
        string sql,
        out SqlScriptError? error)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        return SqliteScriptClassifier.SplitAndClassify(sql, out error);
    }
}

/// <summary>Splits SQLite scripts without treating quoted semicolons as boundaries.</summary>
internal static class SqliteScriptClassifier
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
                if (!SqliteSkipQuoted(
                        sql,
                        ref index,
                        '\'',
                        "Unterminated SQLite string literal.",
                        out error))
                {
                    break;
                }

                continue;
            }

            if (current == '"' || current == '`')
            {
                if (!SqliteSkipQuoted(
                        sql,
                        ref index,
                        current,
                        "Unterminated SQLite quoted identifier.",
                        out error))
                {
                    break;
                }

                continue;
            }

            if (current == '[')
            {
                if (!SqliteSkipBracketIdentifier(sql, ref index, out error))
                {
                    break;
                }

                continue;
            }

            if (current == '-' && SqlitePeek(sql, index + 1) == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\r' && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && SqlitePeek(sql, index + 1) == '*')
            {
                if (!SqliteSkipBlockComment(sql, ref index, out error))
                {
                    break;
                }

                continue;
            }

            if (current == ';')
            {
                SqliteAddStatement(sql, statementStart, index + 1, statements);
                statementStart = index + 1;
            }

            index++;
        }

        if (error is null)
        {
            SqliteAddStatement(sql, statementStart, sql.Length, statements);
        }

        return statements;
    }

    private static void SqliteAddStatement(
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
            SqliteClassify(text)));
    }

    private static SqlStatementKind SqliteClassify(string statement)
    {
        var words = SqliteReadLeadingWords(statement, 5);
        if (words.Count == 0)
        {
            return SqlStatementKind.Empty;
        }

        var first = words[0];
        if (first == "SELECT" || first == "WITH" || first == "EXPLAIN")
        {
            return SqlStatementKind.Select;
        }

        if (first == "INSERT" || first == "UPDATE" || first == "DELETE" || first == "REPLACE")
        {
            return SqlStatementKind.DataManipulation;
        }

        if (first == "CREATE")
        {
            if (SqliteWord(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            if (SqliteWord(words, 1) == "TEMP" || SqliteWord(words, 1) == "TEMPORARY")
            {
                return SqlStatementKind.Unsupported;
            }

            if (SqliteWord(words, 1) == "INDEX" ||
                SqliteWord(words, 1) == "UNIQUE" && SqliteWord(words, 2) == "INDEX" ||
                SqliteWord(words, 1) == "TRIGGER")
            {
                return SqlStatementKind.SchemaNeutral;
            }

            return SqlStatementKind.Unsupported;
        }

        if (first == "DROP")
        {
            if (SqliteWord(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            if (SqliteWord(words, 1) == "INDEX" || SqliteWord(words, 1) == "TRIGGER")
            {
                return SqlStatementKind.SchemaNeutral;
            }

            return SqlStatementKind.Unsupported;
        }

        if (first == "ALTER" && SqliteWord(words, 1) == "TABLE")
        {
            return SqlStatementKind.SupportedTableDdl;
        }

        if (first == "PRAGMA" || first == "BEGIN" || first == "COMMIT" || first == "END" ||
            first == "ROLLBACK" || first == "SAVEPOINT" || first == "RELEASE" || first == "VACUUM" ||
            first == "ANALYZE" || first == "REINDEX" || first == "ATTACH" || first == "DETACH")
        {
            return SqlStatementKind.SchemaNeutral;
        }

        return SqlStatementKind.Unsupported;
    }

    private static List<string> SqliteReadLeadingWords(string statement, int maximum)
    {
        var words = new List<string>();
        var index = 0;
        while (words.Count < maximum)
        {
            SqliteSkipTrivia(statement, ref index);
            if (index >= statement.Length || statement[index] == ';')
            {
                break;
            }

            if (!SqliteIsWordStart(statement[index]))
            {
                break;
            }

            var start = index++;
            while (index < statement.Length && SqliteIsWordPart(statement[index]))
            {
                index++;
            }

            words.Add(statement.Substring(start, index - start).ToUpperInvariant());
        }

        return words;
    }

    private static void SqliteSkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '-' && SqlitePeek(text, index + 1) == '-')
            {
                index += 2;
                while (index < text.Length && text[index] != '\r' && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (text[index] == '/' && SqlitePeek(text, index + 1) == '*')
            {
                index += 2;
                var depth = 1;
                while (index < text.Length && depth != 0)
                {
                    if (text[index] == '/' && SqlitePeek(text, index + 1) == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (text[index] == '*' && SqlitePeek(text, index + 1) == '/')
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
    }

    private static bool SqliteSkipQuoted(
        string text,
        ref int index,
        char quote,
        string errorMessage,
        out SqlScriptError? error)
    {
        var opening = index++;
        while (index < text.Length)
        {
            if (text[index] == quote)
            {
                if (SqlitePeek(text, index + 1) == quote)
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
            errorMessage,
            new SourceSpan(opening, text.Length - opening));
        return false;
    }

    private static bool SqliteSkipBracketIdentifier(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var opening = index++;
        while (index < text.Length)
        {
            if (text[index] == ']')
            {
                index++;
                error = null;
                return true;
            }

            index++;
        }

        error = new SqlScriptError(
            "Unterminated SQLite bracket-quoted identifier.",
            new SourceSpan(opening, text.Length - opening));
        return false;
    }

    private static bool SqliteSkipBlockComment(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var opening = index;
        index += 2;
        var depth = 1;
        while (index < text.Length && depth != 0)
        {
            if (text[index] == '/' && SqlitePeek(text, index + 1) == '*')
            {
                depth++;
                index += 2;
            }
            else if (text[index] == '*' && SqlitePeek(text, index + 1) == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }

        if (depth == 0)
        {
            error = null;
            return true;
        }

        error = new SqlScriptError(
            "Unterminated SQLite block comment.",
            new SourceSpan(opening, text.Length - opening));
        return false;
    }

    private static char SqlitePeek(string text, int index) =>
        index >= 0 && index < text.Length ? text[index] : '\0';

    private static bool SqliteIsWordStart(char value) => char.IsLetter(value) || value == '_';

    private static bool SqliteIsWordPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '$';

    private static string? SqliteWord(IReadOnlyList<string> words, int index) =>
        index < words.Count ? words[index] : null;
}
