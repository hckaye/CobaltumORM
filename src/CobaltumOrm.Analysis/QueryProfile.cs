using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CobaltumOrm.Analysis;

/// <summary>Controls how an SQL dialect writes and compares identifiers.</summary>
public enum SqlIdentifierCaseNormalization
{
    Preserve,
    LowerInvariant,
    UpperInvariant,
}

/// <summary>Describes one pair of identifier delimiters and its escape character.</summary>
public readonly struct SqlIdentifierDelimiter : IEquatable<SqlIdentifierDelimiter>
{
    public SqlIdentifierDelimiter(char opening, char closing)
        : this(opening, closing, closing)
    {
    }

    public SqlIdentifierDelimiter(char opening, char closing, char escape)
    {
        Opening = opening;
        Closing = closing;
        Escape = escape;
    }

    public char Opening { get; }
    public char Closing { get; }
    public char Escape { get; }

    public bool Equals(SqlIdentifierDelimiter other) =>
        Opening == other.Opening && Closing == other.Closing && Escape == other.Escape;

    public override bool Equals(object? obj) =>
        obj is SqlIdentifierDelimiter other && Equals(other);

    public override int GetHashCode() =>
        (Opening * 397) ^ (Closing * 17) ^ Escape;
}

/// <summary>Describes the lexical identifier and parameter rules for a query dialect.</summary>
public sealed class QuerySyntaxProfile
{
    private readonly ReadOnlyCollection<SqlIdentifierDelimiter> _identifierDelimiters;
    private readonly ReadOnlyCollection<char> _parameterPrefixes;

    public QuerySyntaxProfile(
        IEnumerable<SqlIdentifierDelimiter> identifierDelimiters,
        SqlIdentifierCaseNormalization unquotedIdentifierCase,
        IEnumerable<char> parameterPrefixes,
        StringComparison unquotedIdentifierComparison = StringComparison.OrdinalIgnoreCase,
        StringComparison quotedIdentifierComparison = StringComparison.Ordinal,
        bool allowNumericParameterNames = false)
    {
        if (identifierDelimiters is null)
        {
            throw new ArgumentNullException(nameof(identifierDelimiters));
        }

        if (parameterPrefixes is null)
        {
            throw new ArgumentNullException(nameof(parameterPrefixes));
        }

        var delimiters = new List<SqlIdentifierDelimiter>(identifierDelimiters);
        if (delimiters.Count == 0)
        {
            throw new ArgumentException("At least one identifier delimiter is required.", nameof(identifierDelimiters));
        }

        var prefixes = new List<char>(parameterPrefixes);
        _identifierDelimiters = new ReadOnlyCollection<SqlIdentifierDelimiter>(delimiters);
        _parameterPrefixes = new ReadOnlyCollection<char>(prefixes);
        UnquotedIdentifierCase = unquotedIdentifierCase;
        UnquotedIdentifierComparison = unquotedIdentifierComparison;
        QuotedIdentifierComparison = quotedIdentifierComparison;
        AllowNumericParameterNames = allowNumericParameterNames;
    }

    public IReadOnlyList<SqlIdentifierDelimiter> IdentifierDelimiters => _identifierDelimiters;
    public SqlIdentifierCaseNormalization UnquotedIdentifierCase { get; }
    public IReadOnlyList<char> ParameterPrefixes => _parameterPrefixes;
    public StringComparison UnquotedIdentifierComparison { get; }
    public StringComparison QuotedIdentifierComparison { get; }
    public bool AllowNumericParameterNames { get; }

    public string NormalizeUnquotedIdentifier(string identifier)
    {
        if (identifier is null)
        {
            throw new ArgumentNullException(nameof(identifier));
        }

        switch (UnquotedIdentifierCase)
        {
            case SqlIdentifierCaseNormalization.LowerInvariant:
                return identifier.ToLowerInvariant();
            case SqlIdentifierCaseNormalization.UpperInvariant:
                return identifier.ToUpperInvariant();
            default:
                return identifier;
        }
    }

    public string NormalizeQuotedIdentifier(string identifier) =>
        identifier ?? throw new ArgumentNullException(nameof(identifier));

    public string NormalizeIdentifier(string identifier, bool isQuoted) =>
        isQuoted ? NormalizeQuotedIdentifier(identifier) : NormalizeUnquotedIdentifier(identifier);

    public string NormalizeIdentifierForComparison(string identifier, bool isQuoted)
    {
        var normalized = NormalizeIdentifier(identifier, isQuoted);
        var comparison = isQuoted ? QuotedIdentifierComparison : UnquotedIdentifierComparison;
        return comparison == StringComparison.OrdinalIgnoreCase
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    public bool AreIdentifiersEqual(string reference, bool referenceIsQuoted, string declared)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (declared is null)
        {
            throw new ArgumentNullException(nameof(declared));
        }

        return referenceIsQuoted
            ? string.Equals(NormalizeQuotedIdentifier(reference), declared, QuotedIdentifierComparison)
            : string.Equals(
                NormalizeUnquotedIdentifier(reference),
                NormalizeUnquotedIdentifier(declared),
                UnquotedIdentifierComparison);
    }

    public bool AreIdentifiersEquivalent(
        string left,
        bool leftIsQuoted,
        string right,
        bool rightIsQuoted)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (!leftIsQuoted && !rightIsQuoted)
        {
            return string.Equals(
                NormalizeUnquotedIdentifier(left),
                NormalizeUnquotedIdentifier(right),
                UnquotedIdentifierComparison);
        }

        if (leftIsQuoted && rightIsQuoted)
        {
            return string.Equals(
                NormalizeQuotedIdentifier(left),
                NormalizeQuotedIdentifier(right),
                QuotedIdentifierComparison);
        }

        var quoted = leftIsQuoted ? left : right;
        var unquoted = leftIsQuoted ? right : left;
        return string.Equals(
            NormalizeQuotedIdentifier(quoted),
            NormalizeUnquotedIdentifier(unquoted),
            QuotedIdentifierComparison);
    }

    public bool TryGetIdentifierDelimiter(char opening, out SqlIdentifierDelimiter delimiter)
    {
        foreach (var candidate in _identifierDelimiters)
        {
            if (candidate.Opening == opening)
            {
                delimiter = candidate;
                return true;
            }
        }

        delimiter = default;
        return false;
    }

    public bool IsParameterPrefix(char value)
    {
        foreach (var prefix in _parameterPrefixes)
        {
            if (prefix == value)
            {
                return true;
            }
        }

        return false;
    }

    public bool IsParameterNameStart(char value) =>
        IsIdentifierStart(value) || AllowNumericParameterNames && char.IsDigit(value);

    public bool IsParameterNamePart(char value) =>
        IsIdentifierPart(value);

    public static QuerySyntaxProfile PostgreSql { get; } = new QuerySyntaxProfile(
        new[] { new SqlIdentifierDelimiter('"', '"') },
        SqlIdentifierCaseNormalization.LowerInvariant,
        new[] { '@' });

    public static QuerySyntaxProfile MySql { get; } = new QuerySyntaxProfile(
        new[] { new SqlIdentifierDelimiter('`', '`') },
        SqlIdentifierCaseNormalization.LowerInvariant,
        new[] { '@' });

    public static QuerySyntaxProfile SqlServer { get; } = new QuerySyntaxProfile(
        new[]
        {
            new SqlIdentifierDelimiter('[', ']'),
            new SqlIdentifierDelimiter('"', '"'),
        },
        SqlIdentifierCaseNormalization.Preserve,
        new[] { '@' },
        StringComparison.OrdinalIgnoreCase,
        StringComparison.OrdinalIgnoreCase);

    public static QuerySyntaxProfile Sqlite { get; } = new QuerySyntaxProfile(
        new[]
        {
            new SqlIdentifierDelimiter('"', '"'),
            new SqlIdentifierDelimiter('`', '`'),
            new SqlIdentifierDelimiter('[', ']'),
        },
        SqlIdentifierCaseNormalization.Preserve,
        new[] { '@', ':', '$' },
        StringComparison.OrdinalIgnoreCase,
        StringComparison.OrdinalIgnoreCase,
        true);

    public static QuerySyntaxProfile Oracle { get; } = new QuerySyntaxProfile(
        new[] { new SqlIdentifierDelimiter('"', '"') },
        SqlIdentifierCaseNormalization.UpperInvariant,
        new[] { ':' });

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
}

/// <summary>Combines provider type mapping with the common query type rules.</summary>
public sealed class QueryTypeProfile
{
    private readonly Func<string, SqlValueKind, SqlValueKind> _aggregateResult;
    private readonly Func<SqlValueKind, SqlValueKind, SqlValueKind> _unify;

    public QueryTypeProfile(
        ISqlTypeMapper mapper,
        Func<string, SqlValueKind, SqlValueKind>? aggregateResult = null,
        Func<SqlValueKind, SqlValueKind, SqlValueKind>? unify = null)
    {
        Mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _aggregateResult = aggregateResult ?? DefaultAggregateResult;
        _unify = unify ?? DefaultUnify;
    }

    public ISqlTypeMapper Mapper { get; }

    public bool TryMap(string sqlType, out SqlValueKind kind) => Mapper.TryMap(sqlType, out kind);
    public string ToClrName(SqlValueKind kind, bool nullable) => Mapper.ToClrTypeName(kind, nullable);
    public string? ToDatabaseTypeName(SqlValueKind kind) => Mapper.ToDatabaseTypeName(kind);

    internal bool TryMapType(string sqlType, out SqlTypeShape type)
    {
        if (Mapper is PostgreSqlTypeMapper postgreSql)
        {
            return postgreSql.TryMapType(sqlType, out type);
        }

        if (Mapper.TryMap(sqlType, out var kind))
        {
            type = new SqlTypeShape(kind);
            return true;
        }

        type = new SqlTypeShape(SqlValueKind.Error);
        return false;
    }

    internal string ToClrName(SqlTypeShape type, bool nullable) =>
        type.IsArray ? SqlTypeMapper.ToClrName(type, nullable) : Mapper.ToClrTypeName(type.Kind, nullable);

    internal string? ToDatabaseTypeName(SqlTypeShape type) =>
        Mapper is PostgreSqlTypeMapper postgreSql
            ? postgreSql.ToDatabaseTypeName(type)
            : type.IsArray ? null : Mapper.ToDatabaseTypeName(type.Kind);

    public string NormalizeSqlTypeName(string sqlType)
    {
        if (sqlType is null)
        {
            throw new ArgumentNullException(nameof(sqlType));
        }

        var parts = sqlType.Trim().ToLowerInvariant().Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    public bool IsNumeric(SqlValueKind kind) => SqlTypeMapper.IsNumeric(kind);
    public bool IsInteger(SqlValueKind kind) => SqlTypeMapper.IsInteger(kind);
    public bool IsFloat(SqlValueKind kind) => SqlTypeMapper.IsFloat(kind);

    public bool TryUnify(SqlValueKind left, SqlValueKind right, out SqlValueKind result)
    {
        result = _unify(left, right);
        return result != SqlValueKind.Error;
    }

    internal bool TryUnify(SqlTypeShape left, SqlTypeShape right, out SqlTypeShape result)
    {
        if (!left.IsKnown)
        {
            result = right;
            return right.Kind != SqlValueKind.Error;
        }

        if (!right.IsKnown)
        {
            result = left;
            return left.Kind != SqlValueKind.Error;
        }

        if (left.IsArray != right.IsArray || !TryUnify(left.Kind, right.Kind, out var kind))
        {
            result = new SqlTypeShape(SqlValueKind.Error);
            return false;
        }

        result = new SqlTypeShape(kind, left.IsArray);
        return true;
    }

    public SqlValueKind AggregateResult(string aggregateName, SqlValueKind argumentKind)
    {
        if (aggregateName is null)
        {
            throw new ArgumentNullException(nameof(aggregateName));
        }

        return _aggregateResult(aggregateName, argumentKind);
    }

    private static SqlValueKind DefaultAggregateResult(string aggregateName, SqlValueKind argumentKind)
    {
        switch (aggregateName.ToLowerInvariant())
        {
            case "sum":
                if (argumentKind == SqlValueKind.Int16 || argumentKind == SqlValueKind.Int32)
                {
                    return SqlValueKind.Int64;
                }

                return argumentKind;
            case "avg":
                if (SqlTypeMapper.IsInteger(argumentKind) || argumentKind == SqlValueKind.Decimal)
                {
                    return SqlValueKind.Decimal;
                }

                return SqlTypeMapper.IsFloat(argumentKind)
                    ? SqlValueKind.Double
                    : argumentKind;
            default:
                return argumentKind;
        }
    }

    private static SqlValueKind DefaultUnify(SqlValueKind left, SqlValueKind right)
    {
        SqlValueKind result;
        return SqlTypeMapper.TryUnify(left, right, out result)
            ? result
            : SqlValueKind.Error;
    }
}

/// <summary>Supplies the syntax and type profiles used by one query analyzer.</summary>
public sealed class QueryDialectProfile
{
    public QueryDialectProfile(
        QuerySyntaxProfile syntax,
        QueryTypeProfile types,
        string? name = null)
    {
        Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
        Types = types ?? throw new ArgumentNullException(nameof(types));
        Name = name ?? string.Empty;
    }

    public string Name { get; }
    public QuerySyntaxProfile Syntax { get; }
    public QueryTypeProfile Types { get; }
}

/// <summary>Built-in query profiles. Provider dialect classes can supply their own type mapper.</summary>
public static class QueryDialectProfiles
{
    public static QueryDialectProfile PostgreSql { get; } = new QueryDialectProfile(
        QuerySyntaxProfile.PostgreSql,
        new QueryTypeProfile(new PostgreSqlTypeMapper(), PostgreSqlAggregateResult),
        "PostgreSql");

    internal static QueryDialectProfile MySql(ISqlTypeMapper mapper) =>
        new QueryDialectProfile(QuerySyntaxProfile.MySql, new QueryTypeProfile(mapper), "MySql");

    internal static QueryDialectProfile Sqlite(ISqlTypeMapper mapper) =>
        new QueryDialectProfile(QuerySyntaxProfile.Sqlite, new QueryTypeProfile(mapper), "Sqlite");

    internal static QueryDialectProfile SqlServer(ISqlTypeMapper mapper) =>
        new QueryDialectProfile(QuerySyntaxProfile.SqlServer, new QueryTypeProfile(mapper), "SqlServer");

    internal static QueryDialectProfile Oracle(ISqlTypeMapper mapper) =>
        new QueryDialectProfile(QuerySyntaxProfile.Oracle, new QueryTypeProfile(mapper), "Oracle");

    private static SqlValueKind PostgreSqlAggregateResult(string aggregateName, SqlValueKind argumentKind)
    {
        switch (aggregateName.ToLowerInvariant())
        {
            case "sum":
                if (argumentKind == SqlValueKind.Int16 || argumentKind == SqlValueKind.Int32)
                {
                    return SqlValueKind.Int64;
                }

                return argumentKind == SqlValueKind.Int64
                    ? SqlValueKind.Decimal
                    : argumentKind;
            case "avg":
                if (SqlTypeMapper.IsInteger(argumentKind) || argumentKind == SqlValueKind.Decimal)
                {
                    return SqlValueKind.Decimal;
                }

                return SqlTypeMapper.IsFloat(argumentKind)
                    ? SqlValueKind.Double
                    : argumentKind;
            default:
                return argumentKind;
        }
    }
}
