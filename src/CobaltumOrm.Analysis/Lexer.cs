using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

internal enum TokenKind
{
    End,
    Invalid,
    Identifier,
    QuotedIdentifier,
    Number,
    String,
    Parameter,
    Comma,
    Dot,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Star,
    Plus,
    Minus,
    Slash,
    Percent,
    Caret,
    Concat,
    DoubleColon,
    JsonGet,
    JsonGetText,
    JsonPathGet,
    JsonPathGetText,
    Contains,
    ContainedBy,
    Overlaps,
    RegexMatch,
    RegexInsensitiveMatch,
    RegexNotMatch,
    RegexNotInsensitiveMatch,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Select,
    From,
    As,
    Inner,
    Join,
    Left,
    Right,
    Full,
    Outer,
    On,
    Where,
    Group,
    By,
    Having,
    Order,
    Asc,
    Desc,
    Limit,
    Offset,
    And,
    Or,
    Not,
    Is,
    Null,
    Like,
    In,
    Between,
    Case,
    When,
    Then,
    Else,
    EndKeyword,
    Cast,
    True,
    False,
    Insert,
    Into,
    Values,
    Update,
    Set,
    Delete,
    Truncate,
    Default,
    With,
    Recursive,
    Distinct,
    All,
    Union,
    Intersect,
    Except,
    Cross,
    Natural,
    Using,
    Lateral,
    Nulls,
    First,
    Last,
    Fetch,
    Next,
    Row,
    Rows,
    Only,
    Returning,
    Conflict,
    Do,
    Nothing,
    Constraint,
    Exists,
    Ilike,
    Filter,
    Over,
    Partition,
    Window,
    For,
    Unknown,
}

internal readonly struct Token
{
    internal Token(TokenKind kind, string text, object? value, int start, int length)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Span = new SourceSpan(start, length);
    }

    internal TokenKind Kind { get; }
    internal string Text { get; }
    internal object? Value { get; }
    internal SourceSpan Span { get; }
}

internal sealed class Lexer
{
    private readonly string _sql;
    private readonly List<Diagnostic> _diagnostics;
    private readonly QuerySyntaxProfile _syntax;
    private int _position;

    internal Lexer(string sql, List<Diagnostic> diagnostics)
        : this(sql, diagnostics, QuerySyntaxProfile.PostgreSql)
    {
    }

    internal Lexer(string sql, List<Diagnostic> diagnostics, QuerySyntaxProfile syntax)
    {
        _sql = sql;
        _diagnostics = diagnostics;
        _syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
    }

    internal IReadOnlyList<Token> Lex()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipTrivia();
            if (_position >= _sql.Length)
            {
                tokens.Add(new Token(TokenKind.End, string.Empty, null, _position, 0));
                return tokens;
            }

            var token = NextToken();
            tokens.Add(token);
        }
    }

    private Token NextToken()
    {
        var start = _position;
        var current = _sql[_position++];
        switch (current)
        {
            case ',': return Simple(TokenKind.Comma, start);
            case '.': return Simple(TokenKind.Dot, start);
            case '(': return Simple(TokenKind.OpenParen, start);
            case ')': return Simple(TokenKind.CloseParen, start);
            case '[':
                if (_syntax.TryGetIdentifierDelimiter(current, out var bracketDelimiter))
                {
                    return LexQuotedIdentifier(start, bracketDelimiter);
                }

                return Simple(TokenKind.OpenBracket, start);
            case ']': return Simple(TokenKind.CloseBracket, start);
            case ';': return Simple(TokenKind.Semicolon, start);
            case '*': return Simple(TokenKind.Star, start);
            case '+': return Simple(TokenKind.Plus, start);
            case '-':
                if (Match('>'))
                {
                    return Match('>')
                        ? Make(TokenKind.JsonGetText, start, null)
                        : Make(TokenKind.JsonGet, start, null);
                }

                return Simple(TokenKind.Minus, start);
            case '/': return Simple(TokenKind.Slash, start);
            case '%': return Simple(TokenKind.Percent, start);
            case '^': return Simple(TokenKind.Caret, start);
            case '=': return Simple(TokenKind.Equal, start);
            case '|':
                if (Match('|'))
                {
                    return Make(TokenKind.Concat, start, null);
                }

                return Invalid(start, "Expected a second '|' for string concatenation.");
            case '<':
                if (Match('=')) return Make(TokenKind.LessEqual, start, null);
                if (Match('>')) return Make(TokenKind.NotEqual, start, null);
                if (Match('@')) return Make(TokenKind.ContainedBy, start, null);
                return Simple(TokenKind.Less, start);
            case '>':
                if (Match('=')) return Make(TokenKind.GreaterEqual, start, null);
                return Simple(TokenKind.Greater, start);
            case '!':
                if (Match('=')) return Make(TokenKind.NotEqual, start, null);
                if (Match('~'))
                {
                    return Match('*')
                        ? Make(TokenKind.RegexNotInsensitiveMatch, start, null)
                        : Make(TokenKind.RegexNotMatch, start, null);
                }

                return Invalid(start, "Expected '=', '~', or '~*' after '!'.");
            case ':':
                if (_syntax.IsParameterPrefix(':')) return LexParameter(start);
                if (Match(':')) return Make(TokenKind.DoubleColon, start, null);
                return Invalid(start, "Expected a second ':' for a PostgreSQL cast.");
            case '#':
                if (Match('>'))
                {
                    return Match('>')
                        ? Make(TokenKind.JsonPathGetText, start, null)
                        : Make(TokenKind.JsonPathGet, start, null);
                }

                return Invalid(start, "Expected '>' or '>>' after '#'.");
            case '@':
                if (Match('>')) return Make(TokenKind.Contains, start, null);
                if (_syntax.IsParameterPrefix('@')) return LexParameter(start);
                return Invalid(start, "Unexpected '@'.");
            case '&':
                if (Match('&')) return Make(TokenKind.Overlaps, start, null);
                return Invalid(start, "Expected a second '&'.");
            case '~':
                return Match('*')
                    ? Make(TokenKind.RegexInsensitiveMatch, start, null)
                    : Make(TokenKind.RegexMatch, start, null);
            case '\'': return LexString(start);
            default:
                if (_syntax.TryGetIdentifierDelimiter(current, out var delimiter))
                {
                    return LexQuotedIdentifier(start, delimiter);
                }

                if (_syntax.IsParameterPrefix(current))
                {
                    return LexParameter(start);
                }

                if (char.IsDigit(current)) return LexNumber(start);
                if (IsIdentifierStart(current)) return LexIdentifier(start);
                return Invalid(start, $"Unexpected character '{current}'.");
        }
    }

    private Token LexString(int start)
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

                return Make(TokenKind.String, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic("SQL001", "Unterminated string literal.", new SourceSpan(start, _position - start)));
        return Make(TokenKind.Invalid, start, null);
    }

    private Token LexQuotedIdentifier(int start, SqlIdentifierDelimiter delimiter)
    {
        var value = new StringBuilder();
        while (_position < _sql.Length)
        {
            var current = _sql[_position++];
            if (current == delimiter.Closing)
            {
                if (_position < _sql.Length && _sql[_position] == delimiter.Escape)
                {
                    _position++;
                    value.Append(delimiter.Closing);
                    continue;
                }

                return Make(TokenKind.QuotedIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        _diagnostics.Add(new Diagnostic("SQL001", "Unterminated quoted identifier.", new SourceSpan(start, _position - start)));
        return Make(TokenKind.Invalid, start, null);
    }

    private Token LexParameter(int start)
    {
        var prefix = _sql[start];
        if (_position >= _sql.Length || !_syntax.IsParameterNameStart(_sql[_position]))
        {
            return Invalid(start, $"A parameter name must follow '{prefix}'.");
        }

        _position++;
        while (_position < _sql.Length && _syntax.IsParameterNamePart(_sql[_position]))
        {
            _position++;
        }

        var name = _sql.Substring(start, _position - start);
        return new Token(TokenKind.Parameter, name, name, start, _position - start);
    }

    private Token LexNumber(int start)
    {
        while (_position < _sql.Length && char.IsDigit(_sql[_position]))
        {
            _position++;
        }

        var isDecimal = false;
        if (_position + 1 < _sql.Length && _sql[_position] == '.' && char.IsDigit(_sql[_position + 1]))
        {
            isDecimal = true;
            _position++;
            while (_position < _sql.Length && char.IsDigit(_sql[_position]))
            {
                _position++;
            }
        }

        var text = _sql.Substring(start, _position - start);
        return new Token(TokenKind.Number, text, isDecimal, start, _position - start);
    }

    private Token LexIdentifier(int start)
    {
        while (_position < _sql.Length && IsIdentifierPart(_sql[_position]))
        {
            _position++;
        }

        var text = _sql.Substring(start, _position - start);
        return new Token(KeywordKind(text), text, text, start, _position - start);
    }

    private Token Invalid(int start, string message)
    {
        var span = new SourceSpan(start, _position - start);
        _diagnostics.Add(new Diagnostic("SQL001", message, span));
        return new Token(TokenKind.Invalid, _sql.Substring(start, _position - start), null, start, _position - start);
    }

    private Token Simple(TokenKind kind, int start) => Make(kind, start, null);

    private Token Make(TokenKind kind, int start, object? value) =>
        new Token(kind, _sql.Substring(start, _position - start), value, start, _position - start);

    private bool Match(char expected)
    {
        if (_position >= _sql.Length || _sql[_position] != expected)
        {
            return false;
        }

        _position++;
        return true;
    }

    private void SkipTrivia()
    {
        while (_position < _sql.Length)
        {
            while (_position < _sql.Length && char.IsWhiteSpace(_sql[_position]))
            {
                _position++;
            }

            if (_position + 1 < _sql.Length &&
                _sql[_position] == '-' && _sql[_position + 1] == '-')
            {
                _position += 2;
                while (_position < _sql.Length && _sql[_position] != '\r' && _sql[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (_position + 1 < _sql.Length &&
                _sql[_position] == '/' && _sql[_position + 1] == '*')
            {
                var start = _position;
                _position += 2;
                var depth = 1;
                while (_position < _sql.Length && depth > 0)
                {
                    if (_position + 1 < _sql.Length &&
                        _sql[_position] == '/' && _sql[_position + 1] == '*')
                    {
                        depth++;
                        _position += 2;
                    }
                    else if (_position + 1 < _sql.Length &&
                             _sql[_position] == '*' && _sql[_position + 1] == '/')
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
                        "SQL001",
                        "Unterminated block comment.",
                        new SourceSpan(start, _position - start)));
                }

                continue;
            }

            break;
        }
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);

    private static TokenKind KeywordKind(string text)
    {
        switch (text.ToUpperInvariant())
        {
            case "SELECT": return TokenKind.Select;
            case "FROM": return TokenKind.From;
            case "AS": return TokenKind.As;
            case "INNER": return TokenKind.Inner;
            case "JOIN": return TokenKind.Join;
            case "LEFT": return TokenKind.Left;
            case "RIGHT": return TokenKind.Right;
            case "FULL": return TokenKind.Full;
            case "OUTER": return TokenKind.Outer;
            case "ON": return TokenKind.On;
            case "WHERE": return TokenKind.Where;
            case "GROUP": return TokenKind.Group;
            case "BY": return TokenKind.By;
            case "HAVING": return TokenKind.Having;
            case "ORDER": return TokenKind.Order;
            case "ASC": return TokenKind.Asc;
            case "DESC": return TokenKind.Desc;
            case "LIMIT": return TokenKind.Limit;
            case "OFFSET": return TokenKind.Offset;
            case "AND": return TokenKind.And;
            case "OR": return TokenKind.Or;
            case "NOT": return TokenKind.Not;
            case "IS": return TokenKind.Is;
            case "NULL": return TokenKind.Null;
            case "LIKE": return TokenKind.Like;
            case "IN": return TokenKind.In;
            case "BETWEEN": return TokenKind.Between;
            case "CASE": return TokenKind.Case;
            case "WHEN": return TokenKind.When;
            case "THEN": return TokenKind.Then;
            case "ELSE": return TokenKind.Else;
            case "END": return TokenKind.EndKeyword;
            case "CAST": return TokenKind.Cast;
            case "TRUE": return TokenKind.True;
            case "FALSE": return TokenKind.False;
            case "INSERT": return TokenKind.Insert;
            case "INTO": return TokenKind.Into;
            case "VALUES": return TokenKind.Values;
            case "UPDATE": return TokenKind.Update;
            case "SET": return TokenKind.Set;
            case "DELETE": return TokenKind.Delete;
            case "TRUNCATE": return TokenKind.Truncate;
            case "DEFAULT": return TokenKind.Default;
            case "WITH": return TokenKind.With;
            case "RECURSIVE": return TokenKind.Recursive;
            case "DISTINCT": return TokenKind.Distinct;
            case "ALL": return TokenKind.All;
            case "UNION": return TokenKind.Union;
            case "INTERSECT": return TokenKind.Intersect;
            case "EXCEPT": return TokenKind.Except;
            case "CROSS": return TokenKind.Cross;
            case "NATURAL": return TokenKind.Natural;
            case "USING": return TokenKind.Using;
            case "LATERAL": return TokenKind.Lateral;
            case "NULLS": return TokenKind.Nulls;
            case "FIRST": return TokenKind.First;
            case "LAST": return TokenKind.Last;
            case "FETCH": return TokenKind.Fetch;
            case "NEXT": return TokenKind.Next;
            case "ROW": return TokenKind.Row;
            case "ROWS": return TokenKind.Rows;
            case "ONLY": return TokenKind.Only;
            case "RETURNING": return TokenKind.Returning;
            case "CONFLICT": return TokenKind.Conflict;
            case "DO": return TokenKind.Do;
            case "NOTHING": return TokenKind.Nothing;
            case "CONSTRAINT": return TokenKind.Constraint;
            case "EXISTS": return TokenKind.Exists;
            case "ILIKE": return TokenKind.Ilike;
            case "FILTER": return TokenKind.Filter;
            case "OVER": return TokenKind.Over;
            case "PARTITION": return TokenKind.Partition;
            case "WINDOW": return TokenKind.Window;
            case "FOR": return TokenKind.For;
            case "UNKNOWN": return TokenKind.Unknown;
            default: return TokenKind.Identifier;
        }
    }
}
