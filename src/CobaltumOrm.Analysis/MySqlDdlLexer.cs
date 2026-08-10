using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

internal enum MySqlDdlTokenKind
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

internal readonly struct MySqlDdlToken
{
    internal MySqlDdlToken(
        MySqlDdlTokenKind kind,
        string text,
        string? value,
        SourceSpan span)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Span = span;
    }

    internal MySqlDdlTokenKind Kind { get; }
    internal string Text { get; }
    internal string? Value { get; }
    internal SourceSpan Span { get; }
}

internal sealed class MySqlDdlLexer
{
    private readonly string _sql;
    private readonly List<Diagnostic> _diagnostics;
    private int _position;

    internal MySqlDdlLexer(string sql, List<Diagnostic> diagnostics)
    {
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    internal IReadOnlyList<MySqlDdlToken> Lex()
    {
        var tokens = new List<MySqlDdlToken>();
        while (true)
        {
            MySqlSkipTrivia();
            if (_position >= _sql.Length)
            {
                tokens.Add(new MySqlDdlToken(
                    MySqlDdlTokenKind.End,
                    string.Empty,
                    null,
                    new SourceSpan(_position, 0)));
                return tokens;
            }

            tokens.Add(MySqlNextToken());
        }
    }

    private MySqlDdlToken MySqlNextToken()
    {
        var start = _position;
        var current = _sql[_position++];
        switch (current)
        {
            case ',': return MySqlSimple(MySqlDdlTokenKind.Comma, start);
            case '.': return MySqlSimple(MySqlDdlTokenKind.Dot, start);
            case '(': return MySqlSimple(MySqlDdlTokenKind.OpenParen, start);
            case ')': return MySqlSimple(MySqlDdlTokenKind.CloseParen, start);
            case ';': return MySqlSimple(MySqlDdlTokenKind.Semicolon, start);
            case '\'': return MySqlLexString(start, '\'');
            case '"': return MySqlLexString(start, '"');
            case '`': return MySqlLexQuotedIdentifier(start);
            default:
                if (char.IsDigit(current))
                {
                    return MySqlLexNumber(start);
                }

                if (MySqlIsIdentifierStart(current))
                {
                    return MySqlLexIdentifier(start);
                }

                return MySqlSymbol(start);
        }
    }

    private MySqlDdlToken MySqlLexString(int start, char quote)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == '\\' && _position < _sql.Length)
            {
                value.Append(current);
                value.Append(_sql[_position++]);
                continue;
            }

            if (current == quote)
            {
                if (_position < _sql.Length && _sql[_position] == quote)
                {
                    value.Append(quote);
                    _position++;
                    continue;
                }

                return MySqlMake(MySqlDdlTokenKind.String, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated MySQL string literal in the migration.",
            new SourceSpan(start, _position - start)));
        return MySqlMake(MySqlDdlTokenKind.Invalid, start, null);
    }

    private MySqlDdlToken MySqlLexQuotedIdentifier(int start)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == '\\' && _position < _sql.Length)
            {
                value.Append(_sql[_position++]);
                continue;
            }

            if (current == '`')
            {
                if (_position < _sql.Length && _sql[_position] == '`')
                {
                    value.Append('`');
                    _position++;
                    continue;
                }

                return MySqlMake(MySqlDdlTokenKind.QuotedIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated MySQL backtick-quoted identifier in the migration.",
            new SourceSpan(start, _position - start)));
        return MySqlMake(MySqlDdlTokenKind.Invalid, start, null);
    }

    private MySqlDdlToken MySqlLexIdentifier(int start)
    {
        while (_position < _sql.Length && MySqlIsIdentifierPart(_sql[_position]))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new MySqlDdlToken(
            MySqlDdlTokenKind.Identifier,
            text,
            text,
            new SourceSpan(start, _position - start));
    }

    private MySqlDdlToken MySqlLexNumber(int start)
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

        return MySqlMake(MySqlDdlTokenKind.Number, start, _sql.Substring(start, _position - start));
    }

    private MySqlDdlToken MySqlSymbol(int start)
    {
        if (_position < _sql.Length)
        {
            var next = _sql[_position];
            var current = _sql[start];
            if ((next == '=' && (current == '<' || current == '>' || current == '!')) ||
                (next == '>' && current == '<') ||
                (next == '|' && current == '|'))
            {
                _position++;
            }
        }

        return MySqlMake(MySqlDdlTokenKind.Symbol, start, null);
    }

    private MySqlDdlToken MySqlSimple(MySqlDdlTokenKind kind, int start) =>
        MySqlMake(kind, start, null);

    private MySqlDdlToken MySqlMake(MySqlDdlTokenKind kind, int start, string? value)
    {
        return new MySqlDdlToken(
            kind,
            _sql.Substring(start, _position - start),
            value,
            new SourceSpan(start, _position - start));
    }

    private void MySqlSkipTrivia()
    {
        while (_position < _sql.Length)
        {
            if (char.IsWhiteSpace(_sql[_position]))
            {
                _position++;
                continue;
            }

            if (_sql[_position] == '#' ||
                _sql[_position] == '-' && _position + 1 < _sql.Length && _sql[_position + 1] == '-' &&
                (_position + 2 >= _sql.Length || char.IsWhiteSpace(_sql[_position + 2])))
            {
                _position += _sql[_position] == '#' ? 1 : 2;
                while (_position < _sql.Length && _sql[_position] != '\r' && _sql[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (_sql[_position] == '/' && _position + 1 < _sql.Length && _sql[_position + 1] == '*')
            {
                MySqlSkipBlockComment();
                continue;
            }

            break;
        }
    }

    private void MySqlSkipBlockComment()
    {
        var start = _position;
        var depth = 1;
        _position += 2;
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
                "Unterminated MySQL block comment in the migration.",
                new SourceSpan(start, _position - start)));
        }
    }

    private static bool MySqlIsIdentifierStart(char value) =>
        char.IsLetter(value) || value == '_' || value == '$';

    private static bool MySqlIsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '$';
}
