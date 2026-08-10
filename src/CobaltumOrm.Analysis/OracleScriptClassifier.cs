using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

/// <summary>Adapts the Oracle statement classifier to the dialect service contract.</summary>
public sealed class OracleScriptClassifierService : ISqlScriptClassifier
{
    public IReadOnlyList<SqlScriptStatement> SplitAndClassify(string sql, out SqlScriptError? error)
    {
        if (sql is null)
        {
            throw new ArgumentNullException(nameof(sql));
        }

        return OracleScriptClassifier.SplitAndClassify(sql, out error);
    }
}

/// <summary>
/// Splits Oracle SQL at semicolons outside strings, quoted identifiers, and comments.
/// Procedural blocks are returned as unsupported statements instead of being analyzed as DDL.
/// </summary>
public static class OracleScriptClassifier
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
        var procedural = false;
        while (index < sql.Length)
        {
            var current = sql[index];
            if (current == '\'')
            {
                if (!OracleScriptClassifierText.SkipQuoted(
                        sql,
                        ref index,
                        '\'',
                        "Unterminated Oracle string literal.",
                        out error))
                {
                    break;
                }

                continue;
            }

            if ((current == 'q' || current == 'Q') && OracleScriptClassifierText.IsOracleQuotePrefix(sql, index))
            {
                if (!OracleScriptClassifierText.SkipOracleQuotedString(
                        sql,
                        ref index,
                        out error))
                {
                    break;
                }

                continue;
            }

            if (current == '"')
            {
                if (!OracleScriptClassifierText.SkipQuoted(
                        sql,
                        ref index,
                        '"',
                        "Unterminated Oracle quoted identifier.",
                        out error))
                {
                    break;
                }

                continue;
            }

            if (current == '-' && OracleScriptClassifierText.Peek(sql, index + 1) == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\r' && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && OracleScriptClassifierText.Peek(sql, index + 1) == '*')
            {
                if (!OracleScriptClassifierText.SkipBlockComment(sql, ref index, out error))
                {
                    break;
                }

                continue;
            }

            if (current == ';')
            {
                if (!procedural || OracleScriptClassifierText.LooksLikeProceduralEnd(sql, statementStart, index))
                {
                    AddStatement(sql, statementStart, index + 1, statements);
                    statementStart = index + 1;
                    procedural = false;
                }

                index++;
                continue;
            }

            if (!procedural && OracleScriptClassifierText.LooksLikeProceduralStart(sql, statementStart, index))
            {
                procedural = true;
            }

            index++;
        }

        if (error is null)
        {
            AddStatement(sql, statementStart, sql.Length, statements);
        }

        return statements;
    }

    private static void AddStatement(
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
            Classify(text)));
    }

    private static SqlStatementKind Classify(string statement)
    {
        var words = OracleScriptClassifierText.ReadLeadingWords(statement, 6);
        if (words.Count == 0)
        {
            return SqlStatementKind.Empty;
        }

        var first = words[0];
        if (first == "SELECT" || first == "WITH" || first == "EXPLAIN")
        {
            return first == "EXPLAIN" ? SqlStatementKind.SchemaNeutral : SqlStatementKind.Select;
        }

        if (first == "INSERT" || first == "UPDATE" || first == "DELETE" || first == "MERGE")
        {
            return SqlStatementKind.DataManipulation;
        }

        if (first == "COMMENT" || first == "GRANT" || first == "REVOKE" || first == "ANALYZE" ||
            first == "TRUNCATE" || first == "COMMIT" || first == "ROLLBACK" || first == "SAVEPOINT" ||
            first == "SET" || first == "WHENEVER" || first == "PROMPT" || first == "LOCK")
        {
            return SqlStatementKind.SchemaNeutral;
        }

        if (first == "ALTER")
        {
            if (Word(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            return Word(words, 1) == "INDEX" || Word(words, 1) == "SEQUENCE" ||
                Word(words, 1) == "SESSION"
                ? SqlStatementKind.SchemaNeutral
                : SqlStatementKind.Unsupported;
        }

        if (first == "CREATE")
        {
            if (Word(words, 1) == "TABLE" ||
                Word(words, 1) == "GLOBAL" && Word(words, 2) == "TEMPORARY" && Word(words, 3) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            if (Word(words, 1) == "INDEX" ||
                Word(words, 1) == "UNIQUE" && Word(words, 2) == "INDEX" ||
                Word(words, 1) == "BITMAP" && Word(words, 2) == "INDEX" ||
                Word(words, 1) == "SEQUENCE")
            {
                return SqlStatementKind.SchemaNeutral;
            }

            return SqlStatementKind.Unsupported;
        }

        if (first == "DROP")
        {
            if (Word(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            return Word(words, 1) == "INDEX" || Word(words, 1) == "SEQUENCE"
                ? SqlStatementKind.SchemaNeutral
                : SqlStatementKind.Unsupported;
        }

        if (first == "RENAME")
        {
            return SqlStatementKind.SupportedTableDdl;
        }

        return SqlStatementKind.Unsupported;
    }

    private static string? Word(IReadOnlyList<string> words, int index) =>
        index < words.Count ? words[index] : null;
}

internal static class OracleScriptClassifierText
{
    internal static char Peek(string text, int index) =>
        index >= 0 && index < text.Length ? text[index] : '\0';

    internal static bool IsOracleQuotePrefix(string text, int index) =>
        Peek(text, index + 1) == '\'';

    internal static bool SkipQuoted(
        string text,
        ref int index,
        char quote,
        string message,
        out SqlScriptError? error)
    {
        var start = index;
        error = null;
        index++;
        while (index < text.Length)
        {
            if (text[index] == quote)
            {
                index++;
                if (index < text.Length && text[index] == quote)
                {
                    index++;
                    continue;
                }

                return true;
            }

            index++;
        }

        error = new SqlScriptError(message, new SourceSpan(start, index - start));
        return false;
    }

    internal static bool SkipOracleQuotedString(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var start = index;
        error = null;
        index += 2;
        if (index >= text.Length)
        {
            error = new SqlScriptError(
                "Unterminated Oracle alternative-quoted string.",
                new SourceSpan(start, index - start));
            return false;
        }

        var opener = text[index++];
        var closer = opener == '[' ? ']' : opener == '{' ? '}' : opener == '(' ? ')' :
            opener == '<' ? '>' : opener;
        while (index < text.Length)
        {
            if (text[index] == closer && Peek(text, index + 1) == '\'')
            {
                index += 2;
                return true;
            }

            index++;
        }

        error = new SqlScriptError(
            "Unterminated Oracle alternative-quoted string.",
            new SourceSpan(start, index - start));
        return false;
    }

    internal static bool SkipBlockComment(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var start = index;
        error = null;
        var depth = 1;
        index += 2;
        while (index < text.Length && depth > 0)
        {
            if (text[index] == '/' && Peek(text, index + 1) == '*')
            {
                depth++;
                index += 2;
            }
            else if (text[index] == '*' && Peek(text, index + 1) == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }

        if (depth != 0)
        {
            error = new SqlScriptError(
                "Unterminated Oracle block comment.",
                new SourceSpan(start, index - start));
            return false;
        }

        return true;
    }

    internal static List<string> ReadLeadingWords(string statement, int maximum)
    {
        var words = new List<string>();
        var index = 0;
        while (words.Count < maximum)
        {
            SkipTrivia(statement, ref index);
            if (index >= statement.Length || statement[index] == ';')
            {
                break;
            }

            if (!IsWordStart(statement[index]))
            {
                break;
            }

            var start = index++;
            while (index < statement.Length && IsWordPart(statement[index]))
            {
                index++;
            }

            words.Add(statement.Substring(start, index - start).ToUpperInvariant());
        }

        return words;
    }

    internal static bool LooksLikeProceduralStart(string text, int statementStart, int currentIndex)
    {
        if (currentIndex < statementStart)
        {
            return false;
        }

        var words = ReadLeadingWords(text.Substring(statementStart, currentIndex - statementStart), 6);
        if (words.Count == 0)
        {
            return false;
        }

        return words[0] == "BEGIN" || words[0] == "DECLARE" ||
            words[0] == "CREATE" && words.Contains("PROCEDURE") ||
            words[0] == "CREATE" && words.Contains("FUNCTION") ||
            words[0] == "CREATE" && words.Contains("PACKAGE") ||
            words[0] == "CREATE" && words.Contains("TRIGGER");
    }

    internal static bool LooksLikeProceduralEnd(string text, int start, int semicolonIndex)
    {
        var value = text.Substring(start, semicolonIndex - start).TrimEnd();
        return value.EndsWith("END", StringComparison.OrdinalIgnoreCase) &&
            (value.Length == 3 || !IsWordPart(value[value.Length - 4]));
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '-' && Peek(text, index + 1) == '-')
            {
                index += 2;
                while (index < text.Length && text[index] != '\r' && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (text[index] == '/' && Peek(text, index + 1) == '*')
            {
                var ignored = (SqlScriptError?)null;
                if (!SkipBlockComment(text, ref index, out ignored))
                {
                    index = text.Length;
                }

                continue;
            }

            break;
        }
    }

    private static bool IsWordStart(char value) => char.IsLetter(value) || value == '_' || value == '$' || value == '#';

    private static bool IsWordPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#';
}
