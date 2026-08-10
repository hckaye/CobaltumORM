using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

internal enum SqliteDdlTokenKind
{
    End,
    Invalid,
    Identifier,
    QuotedIdentifier,
    Number,
    String,
    Symbol,
    Comma,
    Dot,
    OpenParen,
    CloseParen,
    Semicolon,
}

internal readonly struct SqliteDdlToken
{
    internal SqliteDdlToken(
        SqliteDdlTokenKind kind,
        string text,
        string? value,
        SourceSpan span)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Span = span;
    }

    internal SqliteDdlTokenKind Kind { get; }
    internal string Text { get; }
    internal string? Value { get; }
    internal SourceSpan Span { get; }

    internal bool SqliteIsWord(string word) =>
        Kind == SqliteDdlTokenKind.Identifier &&
        string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

internal sealed class SqliteDdlLexer
{
    private readonly string _sql;
    private readonly List<Diagnostic> _diagnostics;
    private int _position;

    internal SqliteDdlLexer(string sql, List<Diagnostic> diagnostics)
    {
        _sql = sql;
        _diagnostics = diagnostics;
    }

    internal IReadOnlyList<SqliteDdlToken> Lex()
    {
        var tokens = new List<SqliteDdlToken>();
        while (true)
        {
            SqliteSkipTrivia();
            if (_position >= _sql.Length)
            {
                tokens.Add(new SqliteDdlToken(
                    SqliteDdlTokenKind.End,
                    string.Empty,
                    null,
                    new SourceSpan(_position, 0)));
                return tokens;
            }

            tokens.Add(SqliteNextToken());
        }
    }

    private SqliteDdlToken SqliteNextToken()
    {
        var start = _position;
        var current = _sql[_position++];
        switch (current)
        {
            case ',': return SqliteSimple(SqliteDdlTokenKind.Comma, start);
            case '.':
                if (_position < _sql.Length && char.IsDigit(_sql[_position]))
                {
                    return SqliteReadNumber(start, true);
                }

                return SqliteSimple(SqliteDdlTokenKind.Dot, start);
            case '(': return SqliteSimple(SqliteDdlTokenKind.OpenParen, start);
            case ')': return SqliteSimple(SqliteDdlTokenKind.CloseParen, start);
            case ';': return SqliteSimple(SqliteDdlTokenKind.Semicolon, start);
            case '\'': return SqliteReadString(start);
            case '"':
            case '`':
                return SqliteReadQuotedIdentifier(start, current);
            case '[':
                return SqliteReadBracketIdentifier(start);
            default:
                if (char.IsDigit(current))
                {
                    return SqliteReadNumber(start, false);
                }

                if (SqliteIsIdentifierStart(current))
                {
                    return SqliteReadIdentifier(start);
                }

                return SqliteReadSymbol(start);
        }
    }

    private SqliteDdlToken SqliteReadString(int start)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == '\'')
            {
                if (_position < _sql.Length && _sql[_position] == '\'')
                {
                    _position++;
                    value.Append('\'');
                    continue;
                }

                return SqliteMake(SqliteDdlTokenKind.String, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated SQLite string literal in the migration.",
            new SourceSpan(start, _position - start)));
        return SqliteMake(SqliteDdlTokenKind.Invalid, start, null);
    }

    private SqliteDdlToken SqliteReadQuotedIdentifier(int start, char quote)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == quote)
            {
                if (_position < _sql.Length && _sql[_position] == quote)
                {
                    _position++;
                    value.Append(quote);
                    continue;
                }

                return SqliteMake(SqliteDdlTokenKind.QuotedIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated SQLite quoted identifier in the migration.",
            new SourceSpan(start, _position - start)));
        return SqliteMake(SqliteDdlTokenKind.Invalid, start, null);
    }

    private SqliteDdlToken SqliteReadBracketIdentifier(int start)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == ']')
            {
                return SqliteMake(SqliteDdlTokenKind.QuotedIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated SQLite bracket-quoted identifier in the migration.",
            new SourceSpan(start, _position - start)));
        return SqliteMake(SqliteDdlTokenKind.Invalid, start, null);
    }

    private SqliteDdlToken SqliteReadIdentifier(int start)
    {
        while (_position < _sql.Length && SqliteIsIdentifierPart(_sql[_position]))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new SqliteDdlToken(
            SqliteDdlTokenKind.Identifier,
            text,
            text,
            new SourceSpan(start, _position - start));
    }

    private SqliteDdlToken SqliteReadNumber(int start, bool beganWithDot)
    {
        if (!beganWithDot)
        {
            while (_position < _sql.Length && char.IsDigit(_sql[_position]))
            {
                _position++;
            }
        }

        if (_position < _sql.Length && _sql[_position] == '.')
        {
            _position++;
            while (_position < _sql.Length && char.IsDigit(_sql[_position]))
            {
                _position++;
            }
        }

        if (_position < _sql.Length && (_sql[_position] == 'e' || _sql[_position] == 'E'))
        {
            var exponent = _position++;
            if (_position < _sql.Length && (_sql[_position] == '+' || _sql[_position] == '-'))
            {
                _position++;
            }

            var digitStart = _position;
            while (_position < _sql.Length && char.IsDigit(_sql[_position]))
            {
                _position++;
            }

            if (digitStart == _position)
            {
                _position = exponent;
            }
        }

        var text = _sql.Substring(start, _position - start);
        return new SqliteDdlToken(
            SqliteDdlTokenKind.Number,
            text,
            text,
            new SourceSpan(start, _position - start));
    }

    private SqliteDdlToken SqliteReadSymbol(int start)
    {
        if (_position < _sql.Length)
        {
            var next = _sql[_position];
            var first = _sql[start];
            if ((first == '|' && next == '|') ||
                (first == '-' && next == '>') ||
                (first == '<' && (next == '=' || next == '>')) ||
                (first == '>' && next == '=') ||
                (first == '!' && next == '=') ||
                (first == '=' && next == '=') ||
                (first == '<' && next == '<') ||
                (first == '>' && next == '>'))
            {
                _position++;
                if (first == '-' && _position < _sql.Length && _sql[_position] == '>')
                {
                    _position++;
                }
            }
        }

        return SqliteMake(SqliteDdlTokenKind.Symbol, start, null);
    }

    private SqliteDdlToken SqliteSimple(SqliteDdlTokenKind kind, int start) =>
        SqliteMake(kind, start, null);

    private SqliteDdlToken SqliteMake(
        SqliteDdlTokenKind kind,
        int start,
        string? value) =>
        new SqliteDdlToken(
            kind,
            _sql.Substring(start, _position - start),
            value,
            new SourceSpan(start, _position - start));

    private void SqliteSkipTrivia()
    {
        while (_position < _sql.Length)
        {
            if (char.IsWhiteSpace(_sql[_position]))
            {
                _position++;
                continue;
            }

            if (_sql[_position] == '-' && SqlitePeek(_position + 1) == '-')
            {
                _position += 2;
                while (_position < _sql.Length && _sql[_position] != '\r' && _sql[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (_sql[_position] == '/' && SqlitePeek(_position + 1) == '*')
            {
                SqliteSkipBlockComment();
                continue;
            }

            break;
        }
    }

    private void SqliteSkipBlockComment()
    {
        var start = _position;
        _position += 2;
        var depth = 1;
        while (_position < _sql.Length && depth != 0)
        {
            if (_sql[_position] == '/' && SqlitePeek(_position + 1) == '*')
            {
                depth++;
                _position += 2;
            }
            else if (_sql[_position] == '*' && SqlitePeek(_position + 1) == '/')
            {
                depth--;
                _position += 2;
            }
            else
            {
                _position++;
            }
        }

        if (depth != 0)
        {
            _diagnostics.Add(new Diagnostic(
                "DDL001",
                "Unterminated SQLite block comment in the migration.",
                new SourceSpan(start, _position - start)));
        }
    }

    private char SqlitePeek(int index) =>
        index >= 0 && index < _sql.Length ? _sql[index] : '\0';

    private static bool SqliteIsIdentifierStart(char value) =>
        value == '_' || value == '$' || char.IsLetter(value);

    private static bool SqliteIsIdentifierPart(char value) =>
        value == '_' || value == '$' || char.IsLetterOrDigit(value);
}
