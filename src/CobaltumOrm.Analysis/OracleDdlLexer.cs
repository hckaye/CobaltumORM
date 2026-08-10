using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

internal enum OracleDdlTokenKind
{
    End,
    Invalid,
    Word,
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

internal readonly struct OracleDdlToken
{
    internal OracleDdlToken(
        OracleDdlTokenKind kind,
        string text,
        string? value,
        SourceSpan span)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Span = span;
    }

    internal OracleDdlTokenKind Kind { get; }
    internal string Text { get; }
    internal string? Value { get; }
    internal SourceSpan Span { get; }
}

internal sealed class OracleDdlLexer
{
    private readonly string _sql;
    private readonly List<Diagnostic> _diagnostics;
    private int _position;

    internal OracleDdlLexer(string sql, List<Diagnostic> diagnostics)
    {
        _sql = sql;
        _diagnostics = diagnostics;
    }

    internal IReadOnlyList<OracleDdlToken> Lex()
    {
        var tokens = new List<OracleDdlToken>();
        while (true)
        {
            SkipTrivia();
            if (_position >= _sql.Length)
            {
                tokens.Add(new OracleDdlToken(
                    OracleDdlTokenKind.End,
                    string.Empty,
                    null,
                    new SourceSpan(_position, 0)));
                return tokens;
            }

            tokens.Add(ReadToken());
        }
    }

    private OracleDdlToken ReadToken()
    {
        var start = _position;
        var current = _sql[_position++];
        switch (current)
        {
            case ',':
                return Simple(OracleDdlTokenKind.Comma, start);
            case '.':
                return Simple(OracleDdlTokenKind.Dot, start);
            case '(':
                return Simple(OracleDdlTokenKind.OpenParen, start);
            case ')':
                return Simple(OracleDdlTokenKind.CloseParen, start);
            case ';':
                return Simple(OracleDdlTokenKind.Semicolon, start);
            case '\'':
                return ReadString(start);
            case '"':
                return ReadQuotedIdentifier(start);
            case 'q':
            case 'Q':
                if (_position < _sql.Length && _sql[_position] == '\'')
                {
                    _position--;
                    return ReadOracleQuotedString(start);
                }

                return ReadWord(start);
            default:
                if (char.IsLetter(current) || current == '_' || current == '$' || current == '#')
                {
                    return ReadWord(start);
                }

                if (char.IsDigit(current))
                {
                    return ReadNumber(start);
                }

                return ReadSymbol(start);
        }
    }

    private OracleDdlToken ReadWord(int start)
    {
        while (_position < _sql.Length &&
               (char.IsLetterOrDigit(_sql[_position]) || _sql[_position] == '_' ||
                _sql[_position] == '$' || _sql[_position] == '#'))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new OracleDdlToken(
            OracleDdlTokenKind.Word,
            text,
            text.ToUpperInvariant(),
            new SourceSpan(start, _position - start));
    }

    private OracleDdlToken ReadNumber(int start)
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

        if (_position < _sql.Length && (_sql[_position] == 'e' || _sql[_position] == 'E'))
        {
            var exponent = _position++;
            if (_position < _sql.Length && (_sql[_position] == '+' || _sql[_position] == '-'))
            {
                _position++;
            }

            var exponentDigits = _position;
            while (_position < _sql.Length && char.IsDigit(_sql[_position]))
            {
                _position++;
            }

            if (exponentDigits == _position)
            {
                _position = exponent;
            }
        }

        var text = _sql.Substring(start, _position - start);
        return new OracleDdlToken(
            OracleDdlTokenKind.Number,
            text,
            text,
            new SourceSpan(start, _position - start));
    }

    private OracleDdlToken ReadString(int start)
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

                return Make(OracleDdlTokenKind.String, start, value.ToString());
            }

            value.Append(current);
        }

        return Invalid(
            start,
            "Unterminated Oracle string literal in the migration.");
    }

    private OracleDdlToken ReadOracleQuotedString(int start)
    {
        // The current position is at the q/Q prefix.
        _position++;
        if (_position >= _sql.Length || _sql[_position] != '\'')
        {
            return Invalid(start, "Invalid Oracle alternative-quoted string.");
        }

        _position++;
        if (_position >= _sql.Length)
        {
            return Invalid(start, "Unterminated Oracle alternative-quoted string.");
        }

        var opener = _sql[_position++];
        var closer = opener == '[' ? ']' : opener == '{' ? '}' : opener == '(' ? ')' :
            opener == '<' ? '>' : opener;
        while (_position < _sql.Length)
        {
            if (_sql[_position] == closer && OracleDdlLexerText.Peek(_sql, _position + 1) == '\'')
            {
                _position += 2;
                return Make(
                    OracleDdlTokenKind.String,
                    start,
                    _sql.Substring(start, _position - start));
            }

            _position++;
        }

        return Invalid(
            start,
            "Unterminated Oracle alternative-quoted string.");
    }

    private OracleDdlToken ReadQuotedIdentifier(int start)
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

                return Make(OracleDdlTokenKind.QuotedIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        return Invalid(
            start,
            "Unterminated Oracle quoted identifier in the migration.");
    }

    private OracleDdlToken ReadSymbol(int start)
    {
        if (_position < _sql.Length)
        {
            var pair = _sql.Substring(start, 2);
            if (pair == "<>" || pair == "!=" || pair == "<=" || pair == ">=" || pair == "||" ||
                pair == ":=")
            {
                _position++;
            }
        }

        return Make(OracleDdlTokenKind.Symbol, start, null);
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

            if (_sql[_position] == '-' && OracleDdlLexerText.Peek(_sql, _position + 1) == '-')
            {
                _position += 2;
                while (_position < _sql.Length && _sql[_position] != '\r' && _sql[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (_sql[_position] == '/' && OracleDdlLexerText.Peek(_sql, _position + 1) == '*')
            {
                var start = _position;
                _position += 2;
                var depth = 1;
                while (_position < _sql.Length && depth > 0)
                {
                    if (_sql[_position] == '/' && OracleDdlLexerText.Peek(_sql, _position + 1) == '*')
                    {
                        depth++;
                        _position += 2;
                    }
                    else if (_sql[_position] == '*' && OracleDdlLexerText.Peek(_sql, _position + 1) == '/')
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
                        "Unterminated Oracle block comment in the migration.",
                        new SourceSpan(start, _position - start)));
                }

                continue;
            }

            break;
        }
    }

    private OracleDdlToken Simple(OracleDdlTokenKind kind, int start) => Make(kind, start, null);

    private OracleDdlToken Make(OracleDdlTokenKind kind, int start, string? value) =>
        new OracleDdlToken(
            kind,
            _sql.Substring(start, _position - start),
            value,
            new SourceSpan(start, _position - start));

    private OracleDdlToken Invalid(int start, string message)
    {
        _diagnostics.Add(new Diagnostic(
            "DDL001",
            message,
            new SourceSpan(start, _position - start)));
        return Make(OracleDdlTokenKind.Invalid, start, null);
    }
}

internal static class OracleDdlLexerText
{
    internal static char Peek(string text, int index) =>
        index >= 0 && index < text.Length ? text[index] : '\0';
}
