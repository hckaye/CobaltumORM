using System;

namespace CobaltumOrm.Analysis;

/// <summary>Analyzes queries using SQLite identifier, parameter, and type rules.</summary>
public sealed class SqliteQueryAnalyzer : IQueryAnalyzer
{
    private readonly QueryAnalyzerEngine _engine;

    /// <summary>Initializes an analyzer with the built-in SQLite type mapper.</summary>
    public SqliteQueryAnalyzer()
        : this(new SqliteTypeMapper())
    {
    }

    /// <summary>Initializes an analyzer with a type mapper for the SQLite query profile.</summary>
    public SqliteQueryAnalyzer(ISqlTypeMapper typeMapper)
    {
        if (typeMapper is null)
        {
            throw new ArgumentNullException(nameof(typeMapper));
        }

        _engine = new QueryAnalyzerEngine(SqliteCreateQueryProfile(typeMapper));
    }

    /// <summary>Gets the profile used by the common query analyzer engine.</summary>
    public QueryDialectProfile Profile => _engine.Profile;

    /// <inheritdoc />
    public AnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        _engine.Analyze(schema, sql);

    private static QueryDialectProfile SqliteCreateQueryProfile(ISqlTypeMapper typeMapper) =>
        new QueryDialectProfile(
            QuerySyntaxProfile.Sqlite,
            new QueryTypeProfile(typeMapper, SqliteAggregateResult),
            "Sqlite");

    private static SqlValueKind SqliteAggregateResult(string aggregateName, SqlValueKind argumentKind)
    {
        switch (aggregateName.ToLowerInvariant())
        {
            case "count":
                return SqlValueKind.Int64;
            case "sum":
                if (SqlTypeMapper.IsInteger(argumentKind))
                {
                    return SqlValueKind.Int64;
                }

                return argumentKind == SqlValueKind.Float
                    ? SqlValueKind.Double
                    : argumentKind;
            case "avg":
                // SQLite's avg() is implemented as total()/count() and always
                // has a floating-point result for numeric input.
                return SqlTypeMapper.IsNumeric(argumentKind)
                    ? SqlValueKind.Double
                    : argumentKind;
            default:
                return argumentKind;
        }
    }
}
