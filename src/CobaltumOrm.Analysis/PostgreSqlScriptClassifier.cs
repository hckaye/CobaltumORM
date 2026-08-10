using System;
using System.Collections.Generic;
namespace CobaltumOrm.Analysis;

public enum SqlStatementKind
{
    Empty,
    Select,
    DataManipulation,
    SupportedTableDdl,
    SchemaNeutral,
    Unsupported,
}

public sealed class SqlScriptStatement
{
    public SqlScriptStatement(string text, SourceSpan span, SqlStatementKind kind)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Span = span;
        Kind = kind;
    }

    public string Text { get; }
    public SourceSpan Span { get; }
    public SqlStatementKind Kind { get; }
}

public sealed class SqlScriptError
{
    public SqlScriptError(string message, SourceSpan span)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Span = span;
    }

    public string Message { get; }
    public SourceSpan Span { get; }
}

/// <summary>
/// Splits a PostgreSQL script without treating semicolons inside lexical constructs as
/// statement boundaries, then classifies statements by their effect on table shape.
/// </summary>
internal static class PostgreSqlScriptClassifier
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
                var allowsBackslashEscapes = IsEscapeStringPrefix(sql, index);
                if (!SkipQuoted(
                        sql,
                        ref index,
                        '\'',
                        allowsBackslashEscapes,
                        "Unterminated PostgreSQL string literal.",
                        out error))
                {
                    break;
                }

                continue;
            }

            if (current == '"')
            {
                if (!SkipQuoted(
                        sql,
                        ref index,
                        '"',
                        false,
                        "Unterminated quoted PostgreSQL identifier.",
                        out error))
                {
                    break;
                }

                continue;
            }

            if (current == '-' && Peek(sql, index + 1) == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\r' && sql[index] != '\n') index++;
                continue;
            }

            if (current == '/' && Peek(sql, index + 1) == '*')
            {
                if (!SkipBlockComment(sql, ref index, out error))
                {
                    break;
                }

                continue;
            }

            if (current == '$' && TryReadDollarDelimiter(sql, index, out var delimiter))
            {
                var opening = index;
                index += delimiter.Length;
                var closing = sql.IndexOf(delimiter, index, StringComparison.Ordinal);
                if (closing < 0)
                {
                    error = new SqlScriptError(
                        $"Unterminated PostgreSQL dollar-quoted string '{delimiter}'.",
                        new SourceSpan(opening, sql.Length - opening));
                    break;
                }

                index = closing + delimiter.Length;
                continue;
            }

            if (current == ';')
            {
                AddStatement(sql, statementStart, index + 1, statements);
                statementStart = index + 1;
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
        var words = ReadLeadingWords(statement, 4);
        if (words.Count == 0)
        {
            return SqlStatementKind.Empty;
        }

        var first = words[0];
        if (first == "WITH")
        {
            return ClassifyWithStatement(statement);
        }

        if (first == "SELECT" || first == "VALUES")
        {
            return SqlStatementKind.Select;
        }

        if (first == "INSERT" || first == "UPDATE" || first == "DELETE")
        {
            return HasTopLevelReturning(statement)
                ? SqlStatementKind.Select
                : SqlStatementKind.DataManipulation;
        }

        if (first == "TRUNCATE")
        {
            return SqlStatementKind.DataManipulation;
        }

        if (first == "COMMENT")
        {
            return SqlStatementKind.SchemaNeutral;
        }

        if (first == "CREATE")
        {
            if (Word(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            if (Word(words, 1) == "INDEX" ||
                Word(words, 1) == "UNIQUE" && Word(words, 2) == "INDEX")
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

            return Word(words, 1) == "INDEX"
                ? SqlStatementKind.SchemaNeutral
                : SqlStatementKind.Unsupported;
        }

        if (first == "ALTER")
        {
            if (Word(words, 1) == "TABLE")
            {
                return SqlStatementKind.SupportedTableDdl;
            }

            return Word(words, 1) == "INDEX"
                ? SqlStatementKind.SchemaNeutral
                : SqlStatementKind.Unsupported;
        }

        return SqlStatementKind.Unsupported;
    }

    private static SqlStatementKind ClassifyWithStatement(string statement)
    {
        var diagnostics = new List<Diagnostic>();
        var tokens = new Lexer(statement, diagnostics, QuerySyntaxProfile.PostgreSql).Lex();
        var depth = 0;
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == TokenKind.OpenParen)
            {
                depth++;
                continue;
            }

            if (token.Kind == TokenKind.CloseParen)
            {
                if (depth > 0) depth--;
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (token.Kind == TokenKind.Select)
            {
                return SqlStatementKind.Select;
            }

            if (token.Kind == TokenKind.Values)
            {
                return SqlStatementKind.Select;
            }

            if (token.Kind == TokenKind.Insert || token.Kind == TokenKind.Update || token.Kind == TokenKind.Delete)
            {
                return HasTopLevelReturning(tokens, index)
                    ? SqlStatementKind.Select
                    : SqlStatementKind.DataManipulation;
            }
        }

        return SqlStatementKind.Unsupported;
    }

    private static bool HasTopLevelReturning(string statement)
    {
        var diagnostics = new List<Diagnostic>();
        var tokens = new Lexer(statement, diagnostics, QuerySyntaxProfile.PostgreSql).Lex();
        return HasTopLevelReturning(tokens, 0);
    }

    private static bool HasTopLevelReturning(IReadOnlyList<Token> tokens, int start)
    {
        var depth = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == TokenKind.OpenParen)
            {
                depth++;
            }
            else if (token.Kind == TokenKind.CloseParen)
            {
                if (depth > 0) depth--;
            }
            else if (depth == 0 && token.Kind == TokenKind.Returning)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ReadLeadingWords(string statement, int maximum)
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

            var start = index;
            if (!IsWordStart(statement[index]))
            {
                break;
            }

            index++;
            while (index < statement.Length && IsWordPart(statement[index])) index++;
            words.Add(statement.Substring(start, index - start).ToUpperInvariant());
        }

        return words;
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
                while (index < text.Length && text[index] != '\r' && text[index] != '\n') index++;
                continue;
            }

            if (text[index] == '/' && Peek(text, index + 1) == '*')
            {
                index += 2;
                var depth = 1;
                while (index < text.Length && depth != 0)
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

                continue;
            }

            break;
        }
    }

    private static bool SkipQuoted(
        string text,
        ref int index,
        char quote,
        bool allowsBackslashEscapes,
        string errorMessage,
        out SqlScriptError? error)
    {
        var opening = index;
        index++;
        while (index < text.Length)
        {
            if (allowsBackslashEscapes && text[index] == '\\' && index + 1 < text.Length)
            {
                index += 2;
                continue;
            }

            if (text[index] != quote)
            {
                index++;
                continue;
            }

            if (Peek(text, index + 1) == quote)
            {
                index += 2;
                continue;
            }

            index++;
            error = null;
            return true;
        }

        error = new SqlScriptError(errorMessage, new SourceSpan(opening, text.Length - opening));
        return false;
    }

    private static bool SkipBlockComment(
        string text,
        ref int index,
        out SqlScriptError? error)
    {
        var opening = index;
        var depth = 1;
        index += 2;
        while (index < text.Length)
        {
            if (text[index] == '/' && Peek(text, index + 1) == '*')
            {
                depth++;
                index += 2;
                continue;
            }

            if (text[index] == '*' && Peek(text, index + 1) == '/')
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
            "Unterminated PostgreSQL block comment.",
            new SourceSpan(opening, text.Length - opening));
        return false;
    }

    private static bool TryReadDollarDelimiter(string text, int index, out string delimiter)
    {
        delimiter = string.Empty;
        if (Peek(text, index) != '$')
        {
            return false;
        }

        var cursor = index + 1;
        if (Peek(text, cursor) == '$')
        {
            delimiter = "$$";
            return true;
        }

        if (cursor >= text.Length || !(char.IsLetter(text[cursor]) || text[cursor] == '_'))
        {
            return false;
        }

        cursor++;
        while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
        if (Peek(text, cursor) != '$')
        {
            return false;
        }

        delimiter = text.Substring(index, cursor - index + 1);
        return true;
    }

    private static bool IsEscapeStringPrefix(string text, int quoteIndex)
    {
        if (quoteIndex == 0 || (text[quoteIndex - 1] != 'E' && text[quoteIndex - 1] != 'e'))
        {
            return false;
        }

        return quoteIndex == 1 || !IsWordPart(text[quoteIndex - 2]);
    }

    private static char Peek(string text, int index) =>
        index >= 0 && index < text.Length ? text[index] : '\0';

    private static bool IsWordStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsWordPart(char value) => char.IsLetterOrDigit(value) || value == '_' || value == '$';

    private static string? Word(IReadOnlyList<string> words, int index) =>
        index < words.Count ? words[index] : null;
}
