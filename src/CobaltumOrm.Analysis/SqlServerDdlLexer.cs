using System;
using System.Collections.Generic;
using System.Text;

namespace CobaltumOrm.Analysis;

internal enum SqlServerDdlTokenKind
{
    End,
    Invalid,
    Identifier,
    BracketIdentifier,
    QuotedIdentifier,
    String,
    Parameter,
    Number,
    Symbol,
}

internal readonly struct SqlServerDdlToken
{
    internal SqlServerDdlToken(
        SqlServerDdlTokenKind kind,
        string text,
        string? value,
        SourceSpan span)
    {
        Kind = kind;
        Text = text;
        Value = value;
        Span = span;
    }

    internal SqlServerDdlTokenKind Kind { get; }
    internal string Text { get; }
    internal string? Value { get; }
    internal SourceSpan Span { get; }

    internal bool SqlServerIs(string keyword) =>
        (Kind == SqlServerDdlTokenKind.Identifier ||
         Kind == SqlServerDdlTokenKind.BracketIdentifier ||
         Kind == SqlServerDdlTokenKind.QuotedIdentifier) &&
        string.Equals(Value, keyword, StringComparison.OrdinalIgnoreCase);

    internal bool SqlServerIsIdentifier() =>
        Kind == SqlServerDdlTokenKind.Identifier ||
        Kind == SqlServerDdlTokenKind.BracketIdentifier ||
        Kind == SqlServerDdlTokenKind.QuotedIdentifier;
}

internal sealed class SqlServerDdlLexer
{
    private readonly string _sqlServerText;
    private readonly List<Diagnostic> _sqlServerDiagnostics;
    private int _sqlServerPosition;

    internal SqlServerDdlLexer(string sql, List<Diagnostic> diagnostics)
    {
        _sqlServerText = sql ?? throw new ArgumentNullException(nameof(sql));
        _sqlServerDiagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    internal IReadOnlyList<SqlServerDdlToken> Lex()
    {
        var tokens = new List<SqlServerDdlToken>();
        while (true)
        {
            SqlServerSkipTrivia();
            if (_sqlServerPosition >= _sqlServerText.Length)
            {
                tokens.Add(new SqlServerDdlToken(
                    SqlServerDdlTokenKind.End,
                    string.Empty,
                    null,
                    new SourceSpan(_sqlServerPosition, 0)));
                return tokens;
            }

            tokens.Add(SqlServerNextToken());
        }
    }

    private SqlServerDdlToken SqlServerNextToken()
    {
        var start = _sqlServerPosition;
        var current = _sqlServerText[_sqlServerPosition++];
        if (current == '[')
        {
            return SqlServerReadBracketIdentifier(start);
        }

        if (current == '"')
        {
            return SqlServerReadQuotedIdentifier(start);
        }

        if (current == '\'')
        {
            return SqlServerReadString(start);
        }

        if (current == '@')
        {
            return SqlServerReadParameter(start);
        }

        if (char.IsLetter(current) || current == '_' || current == '#' || current == '$')
        {
            return SqlServerReadIdentifier(start);
        }

        if (char.IsDigit(current))
        {
            return SqlServerReadNumber(start);
        }

        return SqlServerMake(
            SqlServerDdlTokenKind.Symbol,
            start,
            _sqlServerText.Substring(start, 1));
    }

    private void SqlServerSkipTrivia()
    {
        while (_sqlServerPosition < _sqlServerText.Length)
        {
            if (char.IsWhiteSpace(_sqlServerText[_sqlServerPosition]))
            {
                _sqlServerPosition++;
                continue;
            }

            if (SqlServerPeek(_sqlServerPosition) == '-' && SqlServerPeek(_sqlServerPosition + 1) == '-')
            {
                _sqlServerPosition += 2;
                while (_sqlServerPosition < _sqlServerText.Length &&
                       _sqlServerText[_sqlServerPosition] != '\r' &&
                       _sqlServerText[_sqlServerPosition] != '\n')
                {
                    _sqlServerPosition++;
                }

                continue;
            }

            if (SqlServerPeek(_sqlServerPosition) == '/' && SqlServerPeek(_sqlServerPosition + 1) == '*')
            {
                SqlServerSkipBlockComment();
                continue;
            }

            break;
        }
    }

    private void SqlServerSkipBlockComment()
    {
        var start = _sqlServerPosition;
        var depth = 1;
        _sqlServerPosition += 2;
        while (_sqlServerPosition < _sqlServerText.Length)
        {
            if (SqlServerPeek(_sqlServerPosition) == '/' && SqlServerPeek(_sqlServerPosition + 1) == '*')
            {
                depth++;
                _sqlServerPosition += 2;
                continue;
            }

            if (SqlServerPeek(_sqlServerPosition) == '*' && SqlServerPeek(_sqlServerPosition + 1) == '/')
            {
                depth--;
                _sqlServerPosition += 2;
                if (depth == 0)
                {
                    return;
                }

                continue;
            }

            _sqlServerPosition++;
        }

        _sqlServerDiagnostics.Add(new Diagnostic(
            "DDL001",
            "Unterminated SQL Server block comment in the migration.",
            new SourceSpan(start, _sqlServerPosition - start)));
    }

    private SqlServerDdlToken SqlServerReadBracketIdentifier(int start)
    {
        var value = new StringBuilder();
        while (_sqlServerPosition < _sqlServerText.Length)
        {
            var current = _sqlServerText[_sqlServerPosition++];
            if (current == ']')
            {
                if (SqlServerPeek(_sqlServerPosition) == ']')
                {
                    _sqlServerPosition++;
                    value.Append(']');
                    continue;
                }

                return SqlServerMake(SqlServerDdlTokenKind.BracketIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        SqlServerReportUnterminated("Unterminated SQL Server bracket identifier.", start);
        return SqlServerMake(SqlServerDdlTokenKind.Invalid, start, null);
    }

    private SqlServerDdlToken SqlServerReadQuotedIdentifier(int start)
    {
        var value = new StringBuilder();
        while (_sqlServerPosition < _sqlServerText.Length)
        {
            var current = _sqlServerText[_sqlServerPosition++];
            if (current == '"')
            {
                if (SqlServerPeek(_sqlServerPosition) == '"')
                {
                    _sqlServerPosition++;
                    value.Append('"');
                    continue;
                }

                return SqlServerMake(SqlServerDdlTokenKind.QuotedIdentifier, start, value.ToString());
            }

            value.Append(current);
        }

        SqlServerReportUnterminated("Unterminated SQL Server quoted identifier.", start);
        return SqlServerMake(SqlServerDdlTokenKind.Invalid, start, null);
    }

    private SqlServerDdlToken SqlServerReadString(int start)
    {
        var value = new StringBuilder();
        while (_sqlServerPosition < _sqlServerText.Length)
        {
            var current = _sqlServerText[_sqlServerPosition++];
            if (current == '\'')
            {
                if (SqlServerPeek(_sqlServerPosition) == '\'')
                {
                    _sqlServerPosition++;
                    value.Append('\'');
                    continue;
                }

                return SqlServerMake(SqlServerDdlTokenKind.String, start, value.ToString());
            }

            value.Append(current);
        }

        SqlServerReportUnterminated("Unterminated SQL Server string literal.", start);
        return SqlServerMake(SqlServerDdlTokenKind.Invalid, start, null);
    }

    private SqlServerDdlToken SqlServerReadParameter(int start)
    {
        while (_sqlServerPosition < _sqlServerText.Length &&
               SqlServerIsIdentifierPart(_sqlServerText[_sqlServerPosition]))
        {
            _sqlServerPosition++;
        }

        var text = _sqlServerText.Substring(start, _sqlServerPosition - start);
        return SqlServerMake(SqlServerDdlTokenKind.Parameter, start, text.Substring(1));
    }

    private SqlServerDdlToken SqlServerReadIdentifier(int start)
    {
        while (_sqlServerPosition < _sqlServerText.Length &&
               SqlServerIsIdentifierPart(_sqlServerText[_sqlServerPosition]))
        {
            _sqlServerPosition++;
        }

        var text = _sqlServerText.Substring(start, _sqlServerPosition - start);
        return SqlServerMake(SqlServerDdlTokenKind.Identifier, start, text);
    }

    private SqlServerDdlToken SqlServerReadNumber(int start)
    {
        while (_sqlServerPosition < _sqlServerText.Length &&
               (char.IsDigit(_sqlServerText[_sqlServerPosition]) ||
                _sqlServerText[_sqlServerPosition] == '.' ||
                _sqlServerText[_sqlServerPosition] == 'e' ||
                _sqlServerText[_sqlServerPosition] == 'E' ||
                _sqlServerText[_sqlServerPosition] == '+' ||
                _sqlServerText[_sqlServerPosition] == '-'))
        {
            _sqlServerPosition++;
        }

        var text = _sqlServerText.Substring(start, _sqlServerPosition - start);
        return SqlServerMake(SqlServerDdlTokenKind.Number, start, text);
    }

    private SqlServerDdlToken SqlServerMake(
        SqlServerDdlTokenKind kind,
        int start,
        string? value)
    {
        return new SqlServerDdlToken(
            kind,
            _sqlServerText.Substring(start, _sqlServerPosition - start),
            value,
            new SourceSpan(start, _sqlServerPosition - start));
    }

    private void SqlServerReportUnterminated(string message, int start)
    {
        _sqlServerDiagnostics.Add(new Diagnostic(
            "DDL001",
            message,
            new SourceSpan(start, _sqlServerPosition - start)));
    }

    private char SqlServerPeek(int index) =>
        index >= 0 && index < _sqlServerText.Length ? _sqlServerText[index] : '\0';

    private static bool SqlServerIsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#' || value == '@';
}
