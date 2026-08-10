using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

internal enum DdlTokenKind
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

internal readonly struct DdlToken
{
    internal DdlToken(DdlTokenKind kind, string text, string? value, SourceSpan span)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Span = span;
    }

    internal DdlTokenKind Kind { get; }
    internal string Text { get; }
    internal string? Value { get; }
    internal SourceSpan Span { get; }
}

internal sealed class PostgreSqlDdlLexer
{
    private readonly string _sql;
    private readonly List<Diagnostic> _diagnostics;
    private int _position;

    internal PostgreSqlDdlLexer(string sql, List<Diagnostic> diagnostics)
    {
        _sql = sql;
        _diagnostics = diagnostics;
    }

    internal IReadOnlyList<DdlToken> Lex()
    {
        var tokens = new List<DdlToken>();
        while (true)
        {
            SkipTrivia();
            if (_position >= _sql.Length)
            {
                tokens.Add(new DdlToken(
                    DdlTokenKind.End,
                    string.Empty,
                    null,
                    new SourceSpan(_position, 0)));
                return tokens;
            }

            tokens.Add(NextToken());
        }
    }

    private DdlToken NextToken()
    {
        var start = _position;
        var current = _sql[_position++];
        switch (current)
        {
            case ',': return Simple(DdlTokenKind.Comma, start);
            case '.': return Simple(DdlTokenKind.Dot, start);
            case '(': return Simple(DdlTokenKind.OpenParen, start);
            case ')': return Simple(DdlTokenKind.CloseParen, start);
            case ';': return Simple(DdlTokenKind.Semicolon, start);
            case '\'': return LexString(start, false);
            case '"': return LexQuotedIdentifier(start);
            case '$':
                if (TryReadDollarQuoted(start, out var dollarQuoted))
                {
                    return dollarQuoted;
                }

                return Symbol(start);
            default:
                if ((current == 'e' || current == 'E') &&
                    _position < _sql.Length && _sql[_position] == '\'')
                {
                    _position++;
                    return LexString(start, true);
                }

                if (char.IsDigit(current))
                {
                    return LexNumber(start);
                }

                if (IsIdentifierStart(current))
                {
                    return LexIdentifier(start);
                }

                return Symbol(start);
        }
    }

    private DdlToken LexString(int start, bool allowsBackslashEscapes)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (allowsBackslashEscapes && current == '\\' && _position < _sql.Length)
            {
                value.Append(current);
                value.Append(_sql[_position++]);
                continue;
            }

            if (current == '\'')
            {
                if (_position < _sql.Length && _sql[_position] == '\'')
                {
                    _position++;
                    value.Append('\'');
                    continue;
                }

                return Make(DdlTokenKind.String, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated string literal in the migration.",
            new SourceSpan(start, _position - start)));
        return Make(DdlTokenKind.Invalid, start, null);
    }

    private DdlToken LexQuotedIdentifier(int start)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == '"')
            {
                if (_position < _sql.Length && _sql[_position] == '"')
                {
                    _position++;
                    value.Append('"');
                    continue;
                }

                return Make(DdlTokenKind.QuotedIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated quoted identifier in the migration.",
            new SourceSpan(start, _position - start)));
        return Make(DdlTokenKind.Invalid, start, null);
    }

    private bool TryReadDollarQuoted(int start, out DdlToken token)
    {
        token = default(DdlToken);
        var tagEnd = _position;
        while (tagEnd < _sql.Length && IsDollarTagPart(_sql[tagEnd]))
        {
            tagEnd++;
        }

        if (tagEnd >= _sql.Length || _sql[tagEnd] != '$')
        {
            return false;
        }

        var delimiter = _sql.Substring(start, tagEnd - start + 1);
        var contentStart = tagEnd + 1;
        var close = _sql.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        if (close < 0)
        {
            _position = _sql.Length;
            _diagnostics.Add(new Diagnostic(
                "DDL001",
                "Unterminated dollar-quoted expression in the migration.",
                new SourceSpan(start, _position - start)));
            token = Make(DdlTokenKind.Invalid, start, null);
            return true;
        }

        _position = close + delimiter.Length;
        token = Make(DdlTokenKind.String, start, _sql.Substring(contentStart, close - contentStart));
        return true;
    }

    private DdlToken LexIdentifier(int start)
    {
        while (_position < _sql.Length && IsIdentifierPart(_sql[_position]))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new DdlToken(
            DdlTokenKind.Identifier,
            text,
            text,
            new SourceSpan(start, _position - start));
    }

    private DdlToken LexNumber(int start)
    {
        while (_position < _sql.Length && char.IsDigit(_sql[_position]))
        {
            _position++;
        }

        if (_position + 1 < _sql.Length && _sql[_position] == '.' && char.IsDigit(_sql[_position + 1]))
        {
            _position++;
            while (_position < _sql.Length && char.IsDigit(_sql[_position]))
            {
                _position++;
            }
        }

        var text = _sql.Substring(start, _position - start);
        return new DdlToken(
            DdlTokenKind.Number,
            text,
            text,
            new SourceSpan(start, _position - start));
    }

    private DdlToken Symbol(int start)
    {
        if (_position < _sql.Length)
        {
            var next = _sql[_position];
            if ((next == ':' && _sql[start] == ':') ||
                (next == '=' && (_sql[start] == '<' || _sql[start] == '>' || _sql[start] == '!')) ||
                (next == '>' && _sql[start] == '<'))
            {
                _position++;
            }
        }

        return Make(DdlTokenKind.Symbol, start, null);
    }

    private DdlToken Simple(DdlTokenKind kind, int start) => Make(kind, start, null);

    private DdlToken Make(DdlTokenKind kind, int start, string? value)
    {
        return new DdlToken(
            kind,
            _sql.Substring(start, _position - start),
            value,
            new SourceSpan(start, _position - start));
    }

    private void SkipTrivia()
    {
        while (_position < _sql.Length)
        {
            if (char.IsWhiteSpace(_sql[_position]))
            {
                _position++;
                continue;
            }

            if (_sql[_position] == '-' && _position + 1 < _sql.Length && _sql[_position + 1] == '-')
            {
                _position += 2;
                while (_position < _sql.Length && _sql[_position] != '\r' && _sql[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (_sql[_position] == '/' && _position + 1 < _sql.Length && _sql[_position + 1] == '*')
            {
                SkipBlockComment();
                continue;
            }

            return;
        }
    }

    private void SkipBlockComment()
    {
        var start = _position;
        _position += 2;
        var depth = 1;
        while (_position < _sql.Length && depth > 0)
        {
            if (_position + 1 < _sql.Length && _sql[_position] == '/' && _sql[_position + 1] == '*')
            {
                depth++;
                _position += 2;
            }
            else if (_position + 1 < _sql.Length && _sql[_position] == '*' && _sql[_position + 1] == '/')
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
                "Unterminated block comment in the migration.",
                new SourceSpan(start, _position - start)));
        }
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) =>
        value == '_' || value == '$' || char.IsLetterOrDigit(value);

    private static bool IsDollarTagPart(char value) =>
        value == '_' || char.IsLetterOrDigit(value);
}
