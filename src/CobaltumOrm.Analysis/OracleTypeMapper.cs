using System;
using System.Collections.Generic;
using System.Globalization;

namespace CobaltumOrm.Analysis;

/// <summary>Maps Oracle SQL types and CobaltumORM migration types to the common model.</summary>
public sealed class OracleTypeMapper : ISqlTypeMapper
{
    public bool TryMap(string sqlType, out SqlValueKind kind)
    {
        if (!OracleTypeMapperParser.TryParse(sqlType, out var descriptor))
        {
            kind = SqlValueKind.Error;
            return false;
        }

        switch (descriptor.Family)
        {
            case OracleTypeMapperFamily.Number:
                kind = MapNumber(descriptor.Precision, descriptor.Scale);
                return true;
            case OracleTypeMapperFamily.BinaryFloat:
                kind = SqlValueKind.Float;
                return true;
            case OracleTypeMapperFamily.BinaryDouble:
            case OracleTypeMapperFamily.FloatingNumber:
                kind = SqlValueKind.Double;
                return true;
            case OracleTypeMapperFamily.String:
            case OracleTypeMapperFamily.Clob:
            case OracleTypeMapperFamily.Nclob:
                kind = SqlValueKind.String;
                return true;
            case OracleTypeMapperFamily.Date:
            case OracleTypeMapperFamily.Timestamp:
            case OracleTypeMapperFamily.TimestampLocalTimeZone:
                // Oracle DATE contains a time of day. A local-time-zone timestamp is
                // normalized to the session time zone before it is returned.
                kind = SqlValueKind.DateTime;
                return true;
            case OracleTypeMapperFamily.TimestampWithTimeZone:
                kind = SqlValueKind.DateTimeOffset;
                return true;
            case OracleTypeMapperFamily.Interval:
            case OracleTypeMapperFamily.IntervalYearToMonth:
                kind = SqlValueKind.Error;
                return false;
            case OracleTypeMapperFamily.Raw:
                // OracleMigrationAdapter represents Guid as the exact RAW(16)
                // declaration; other RAW widths remain arbitrary byte storage.
                kind = descriptor.Precision == 16 ? SqlValueKind.Guid : SqlValueKind.Bytes;
                return true;
            case OracleTypeMapperFamily.Blob:
                kind = SqlValueKind.Bytes;
                return true;
            case OracleTypeMapperFamily.Json:
                kind = SqlValueKind.Json;
                return true;
            default:
                kind = SqlValueKind.Error;
                return false;
        }
    }

    public string ToClrTypeName(SqlValueKind kind, bool nullable) =>
        SqlTypeMapper.ToClrName(kind, nullable);

    public string? ToDatabaseTypeName(SqlValueKind kind)
    {
        switch (kind)
        {
            case SqlValueKind.Json:
                return "CLOB";
            case SqlValueKind.JsonBinary:
                return "BLOB";
            default:
                return null;
        }
    }

    public string MapMigrationType(
        string logicalType,
        int? length = null,
        int? precision = null,
        int? scale = null)
    {
        if (logicalType is null)
        {
            throw new ArgumentNullException(nameof(logicalType));
        }

        var normalized = logicalType.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "int16":
                RequireNoModifiers(normalized, length, precision, scale);
                return "NUMBER(5,0)";
            case "int32":
                RequireNoModifiers(normalized, length, precision, scale);
                return "NUMBER(10,0)";
            case "int64":
                RequireNoModifiers(normalized, length, precision, scale);
                return "NUMBER(19,0)";
            case "boolean":
                RequireNoModifiers(normalized, length, precision, scale);
                return "NUMBER(1,0)";
            case "float":
                RequireNoModifiers(normalized, length, precision, scale);
                return "BINARY_FLOAT";
            case "double":
                RequireNoModifiers(normalized, length, precision, scale);
                return "BINARY_DOUBLE";
            case "string":
                if (precision.HasValue || scale.HasValue)
                {
                    throw new ArgumentException(
                        "Oracle string migration types accept length only.",
                        nameof(length));
                }

                if (!length.HasValue)
                {
                    return "CLOB";
                }

                ValidateLength(length.Value);
                return "VARCHAR2(" + length.Value.ToString(CultureInfo.InvariantCulture) + ")";
            case "text":
                RequireNoModifiers(normalized, length, precision, scale);
                return "CLOB";
            case "date":
                RequireNoModifiers(normalized, length, precision, scale);
                return "DATE";
            case "datetime":
                RequireNoModifiers(normalized, length, precision, scale);
                return "TIMESTAMP";
            case "datetimeoffset":
                RequireNoModifiers(normalized, length, precision, scale);
                return "TIMESTAMP WITH TIME ZONE";
            case "time":
                RequireNoModifiers(normalized, length, precision, scale);
                return "TIMESTAMP";
            case "guid":
                RequireNoModifiers(normalized, length, precision, scale);
                return "RAW(16)";
            case "binary":
                RequireNoModifiers(normalized, length, precision, scale);
                return "BLOB";
            case "json":
                RequireNoModifiers(normalized, length, precision, scale);
                return "CLOB";
            case "jsonb":
                RequireNoModifiers(normalized, length, precision, scale);
                return "BLOB";
            case "decimal":
                if (length.HasValue)
                {
                    throw new ArgumentException(
                        "Oracle decimal migration types accept precision and scale only.",
                        nameof(length));
                }

                if (!precision.HasValue && !scale.HasValue)
                {
                    return "NUMBER";
                }

                if (!precision.HasValue || !scale.HasValue)
                {
                    throw new ArgumentException(
                        "Oracle decimal migration types require both precision and scale.",
                        nameof(precision));
                }

                ValidateNumberModifiers(precision.Value, scale.Value);
                return "NUMBER(" + precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                    scale.Value.ToString(CultureInfo.InvariantCulture) + ")";
            default:
                throw new ArgumentException(
                    $"Unknown Oracle migration type '{logicalType}'.",
                    nameof(logicalType));
        }
    }

    private static SqlValueKind MapNumber(int? precision, int scale)
    {
        if (!precision.HasValue || scale != 0)
        {
            return SqlValueKind.Decimal;
        }

        if (precision.Value == 1)
        {
            return SqlValueKind.Bool;
        }

        if (precision.Value <= 5)
        {
            return SqlValueKind.Int16;
        }

        if (precision.Value <= 10)
        {
            return SqlValueKind.Int32;
        }

        if (precision.Value <= 19)
        {
            return SqlValueKind.Int64;
        }

        return SqlValueKind.Decimal;
    }

    private static void RequireNoModifiers(
        string logicalType,
        int? length,
        int? precision,
        int? scale)
    {
        if (length.HasValue || precision.HasValue || scale.HasValue)
        {
            throw new ArgumentException(
                $"Oracle migration type '{logicalType}' does not accept type modifiers.",
                nameof(logicalType));
        }
    }

    private static void ValidateLength(int length)
    {
        if (length <= 0 || length > 32767)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "Oracle VARCHAR2 length must be between 1 and 32767.");
        }
    }

    private static void ValidateNumberModifiers(int precision, int scale)
    {
        if (precision < 1 || precision > 38)
        {
            throw new ArgumentOutOfRangeException(
                nameof(precision),
                precision,
                "Oracle NUMBER precision must be between 1 and 38.");
        }

        if (scale < -84 || scale > 127 || scale > precision)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                scale,
                "Oracle NUMBER scale must be between -84 and 127 and cannot exceed precision for a migration type.");
        }
    }
}

internal enum OracleTypeMapperFamily
{
    Number,
    BinaryFloat,
    BinaryDouble,
    FloatingNumber,
    String,
    Clob,
    Nclob,
    Date,
    Timestamp,
    TimestampWithTimeZone,
    TimestampLocalTimeZone,
    Interval,
    IntervalYearToMonth,
    Raw,
    Blob,
    Json,
}

internal sealed class OracleTypeMapperDescriptor
{
    internal OracleTypeMapperDescriptor(
        OracleTypeMapperFamily family,
        int? precision = null,
        int scale = 0)
    {
        Family = family;
        Precision = precision;
        Scale = scale;
    }

    internal OracleTypeMapperFamily Family { get; }
    internal int? Precision { get; }
    internal int Scale { get; }
}

internal static class OracleTypeMapperParser
{
    internal static bool TryParse(string? sqlType, out OracleTypeMapperDescriptor descriptor)
    {
        descriptor = null!;
        if (string.IsNullOrWhiteSpace(sqlType))
        {
            return false;
        }

        var tokens = OracleTypeMapperTokenizer.Tokenize(sqlType!);
        if (tokens.Count == 0)
        {
            return false;
        }

        var cursor = new OracleTypeMapperCursor(tokens);
        var first = cursor.ReadWord();
        if (first is null)
        {
            return false;
        }

        switch (first)
        {
            case "NUMBER":
            case "DECIMAL":
            case "NUMERIC":
            case "DEC":
                return TryParseNumber(cursor, out descriptor);
            case "SMALLINT":
            case "INTEGER":
            case "INT":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Number, 38, 0);
            case "BINARY_FLOAT":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.BinaryFloat);
            case "BINARY_DOUBLE":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.BinaryDouble);
            case "FLOAT":
            case "REAL":
                return TryParseFloatingNumber(cursor, out descriptor);
            case "DOUBLE":
                return cursor.MatchWord("PRECISION") && cursor.IsAtEnd &&
                    Set(out descriptor, OracleTypeMapperFamily.FloatingNumber);
            case "VARCHAR2":
            case "VARCHAR":
            case "NVARCHAR2":
                return TryParseCharacter(cursor, true, out descriptor);
            case "CHAR":
            case "CHARACTER":
                if (first == "CHARACTER" && cursor.MatchWord("VARYING"))
                {
                    return TryParseCharacter(cursor, true, out descriptor);
                }

                return TryParseCharacter(cursor, false, out descriptor);
            case "NCHAR":
                return TryParseCharacter(cursor, false, out descriptor);
            case "CLOB":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Clob);
            case "NCLOB":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Nclob);
            case "LONG":
                if (cursor.MatchWord("RAW"))
                {
                    return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Raw);
                }

                if (cursor.MatchWord("VARCHAR"))
                {
                    return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.String);
                }

                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.String);
            case "DATE":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Date);
            case "TIMESTAMP":
                return TryParseTimestamp(cursor, out descriptor);
            case "INTERVAL":
                return TryParseInterval(cursor, out descriptor);
            case "RAW":
                return TryParseRaw(cursor, out descriptor);
            case "BLOB":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Blob);
            case "JSON":
                return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Json);
            default:
                return false;
        }
    }

    private static bool TryParseNumber(
        OracleTypeMapperCursor cursor,
        out OracleTypeMapperDescriptor descriptor)
    {
        descriptor = null!;
        int? precision = null;
        var scale = 0;
        if (cursor.Match(OracleTypeMapperTokenKind.OpenParen))
        {
            if (!cursor.TryReadInteger(out var parsedPrecision) || parsedPrecision < 1 || parsedPrecision > 38)
            {
                return false;
            }

            precision = parsedPrecision;
            if (cursor.Match(OracleTypeMapperTokenKind.Comma))
            {
                if (!cursor.TryReadInteger(out scale) || scale < -84 || scale > 127)
                {
                    return false;
                }
            }

            if (!cursor.Match(OracleTypeMapperTokenKind.CloseParen))
            {
                return false;
            }
        }

        return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Number, precision, scale);
    }

    private static bool TryParseFloatingNumber(
        OracleTypeMapperCursor cursor,
        out OracleTypeMapperDescriptor descriptor)
    {
        descriptor = null!;
        if (cursor.Match(OracleTypeMapperTokenKind.OpenParen))
        {
            if (!cursor.TryReadInteger(out var precision) || precision < 1 || precision > 126 ||
                !cursor.Match(OracleTypeMapperTokenKind.CloseParen))
            {
                return false;
            }
        }

        return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.FloatingNumber);
    }

    private static bool TryParseCharacter(
        OracleTypeMapperCursor cursor,
        bool requiresLength,
        out OracleTypeMapperDescriptor descriptor)
    {
        descriptor = null!;
        if (!cursor.Match(OracleTypeMapperTokenKind.OpenParen))
        {
            return !requiresLength && cursor.IsAtEnd &&
                Set(out descriptor, OracleTypeMapperFamily.String);
        }

        if (!cursor.TryReadInteger(out var length) || length <= 0 || length > 32767)
        {
            return false;
        }

        if (cursor.PeekWord("BYTE") || cursor.PeekWord("CHAR"))
        {
            cursor.ReadWord();
        }

        return cursor.Match(OracleTypeMapperTokenKind.CloseParen) && cursor.IsAtEnd &&
            Set(out descriptor, OracleTypeMapperFamily.String);
    }

    private static bool TryParseTimestamp(
        OracleTypeMapperCursor cursor,
        out OracleTypeMapperDescriptor descriptor)
    {
        descriptor = null!;
        if (cursor.Match(OracleTypeMapperTokenKind.OpenParen))
        {
            if (!cursor.TryReadInteger(out var precision) || precision < 0 || precision > 9 ||
                !cursor.Match(OracleTypeMapperTokenKind.CloseParen))
            {
                return false;
            }
        }

        if (!cursor.MatchWord("WITH"))
        {
            return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Timestamp);
        }

        if (cursor.MatchWord("LOCAL"))
        {
            return cursor.MatchWord("TIME") && cursor.MatchWord("ZONE") && cursor.IsAtEnd &&
                Set(out descriptor, OracleTypeMapperFamily.TimestampLocalTimeZone);
        }

        return cursor.MatchWord("TIME") && cursor.MatchWord("ZONE") && cursor.IsAtEnd &&
            Set(out descriptor, OracleTypeMapperFamily.TimestampWithTimeZone);
    }

    private static bool TryParseInterval(
        OracleTypeMapperCursor cursor,
        out OracleTypeMapperDescriptor descriptor)
    {
        descriptor = null!;
        var leading = cursor.ReadWord();
        if (leading is null)
        {
            return false;
        }

        if (cursor.Match(OracleTypeMapperTokenKind.OpenParen))
        {
            if (!cursor.TryReadInteger(out var leadingPrecision) || leadingPrecision < 0 || leadingPrecision > 9 ||
                !cursor.Match(OracleTypeMapperTokenKind.CloseParen))
            {
                return false;
            }
        }

        if (leading == "YEAR")
        {
            if (!cursor.MatchWord("TO") || !cursor.MatchWord("MONTH") || !cursor.IsAtEnd)
            {
                return false;
            }

            return Set(out descriptor, OracleTypeMapperFamily.IntervalYearToMonth);
        }

        if (leading != "DAY" && leading != "HOUR" && leading != "MINUTE" && leading != "SECOND")
        {
            return false;
        }

        var ending = leading;
        if (cursor.MatchWord("TO"))
        {
            ending = cursor.ReadWord() ?? string.Empty;
            if (ending != "HOUR" && ending != "MINUTE" && ending != "SECOND")
            {
                return false;
            }
        }

        if (ending == "SECOND" && cursor.Match(OracleTypeMapperTokenKind.OpenParen))
        {
            if (!cursor.TryReadInteger(out var fractionalPrecision) || fractionalPrecision < 0 ||
                fractionalPrecision > 9 || !cursor.Match(OracleTypeMapperTokenKind.CloseParen))
            {
                return false;
            }
        }

        return cursor.IsAtEnd && Set(out descriptor, OracleTypeMapperFamily.Interval);
    }

    private static bool TryParseRaw(
        OracleTypeMapperCursor cursor,
        out OracleTypeMapperDescriptor descriptor)
    {
        descriptor = null!;
        if (!cursor.Match(OracleTypeMapperTokenKind.OpenParen) ||
            !cursor.TryReadInteger(out var length) || length < 1 || length > 2000 ||
            !cursor.Match(OracleTypeMapperTokenKind.CloseParen) || !cursor.IsAtEnd)
        {
            return false;
        }

        return Set(out descriptor, OracleTypeMapperFamily.Raw, length);
    }

    private static bool Set(
        out OracleTypeMapperDescriptor descriptor,
        OracleTypeMapperFamily family,
        int? precision = null,
        int scale = 0)
    {
        descriptor = new OracleTypeMapperDescriptor(family, precision, scale);
        return true;
    }
}

internal enum OracleTypeMapperTokenKind
{
    Word,
    Number,
    Minus,
    OpenParen,
    CloseParen,
    Comma,
    Other,
}

internal readonly struct OracleTypeMapperToken
{
    internal OracleTypeMapperToken(OracleTypeMapperTokenKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    internal OracleTypeMapperTokenKind Kind { get; }
    internal string Text { get; }
}

internal static class OracleTypeMapperTokenizer
{
    internal static IReadOnlyList<OracleTypeMapperToken> Tokenize(string text)
    {
        var tokens = new List<OracleTypeMapperToken>();
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            var current = text[index++];
            if (char.IsLetter(current) || current == '_')
            {
                while (index < text.Length &&
                       (char.IsLetterOrDigit(text[index]) || text[index] == '_' || text[index] == '$'))
                {
                    index++;
                }

                tokens.Add(new OracleTypeMapperToken(
                    OracleTypeMapperTokenKind.Word,
                    text.Substring(start, index - start).ToUpperInvariant()));
                continue;
            }

            if (char.IsDigit(current))
            {
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }

                tokens.Add(new OracleTypeMapperToken(
                    OracleTypeMapperTokenKind.Number,
                    text.Substring(start, index - start)));
                continue;
            }

            var kind = current switch
            {
                '-' => OracleTypeMapperTokenKind.Minus,
                '(' => OracleTypeMapperTokenKind.OpenParen,
                ')' => OracleTypeMapperTokenKind.CloseParen,
                ',' => OracleTypeMapperTokenKind.Comma,
                _ => OracleTypeMapperTokenKind.Other,
            };
            tokens.Add(new OracleTypeMapperToken(kind, text.Substring(start, 1)));
        }

        return tokens;
    }
}

internal sealed class OracleTypeMapperCursor
{
    private readonly IReadOnlyList<OracleTypeMapperToken> _tokens;
    private int _index;

    internal OracleTypeMapperCursor(IReadOnlyList<OracleTypeMapperToken> tokens)
    {
        _tokens = tokens;
    }

    internal bool IsAtEnd => _index == _tokens.Count;

    internal string? ReadWord()
    {
        if (_index >= _tokens.Count || _tokens[_index].Kind != OracleTypeMapperTokenKind.Word)
        {
            return null;
        }

        return _tokens[_index++].Text;
    }

    internal bool PeekWord(string word) =>
        _index < _tokens.Count && _tokens[_index].Kind == OracleTypeMapperTokenKind.Word &&
        string.Equals(_tokens[_index].Text, word, StringComparison.Ordinal);

    internal bool MatchWord(string word)
    {
        if (!PeekWord(word))
        {
            return false;
        }

        _index++;
        return true;
    }

    internal bool Match(OracleTypeMapperTokenKind kind)
    {
        if (_index >= _tokens.Count || _tokens[_index].Kind != kind)
        {
            return false;
        }

        _index++;
        return true;
    }

    internal bool TryReadInteger(out int value)
    {
        value = 0;
        var negative = Match(OracleTypeMapperTokenKind.Minus);
        if (_index >= _tokens.Count || _tokens[_index].Kind != OracleTypeMapperTokenKind.Number)
        {
            return false;
        }

        if (!int.TryParse(
                _tokens[_index++].Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value))
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }
}
