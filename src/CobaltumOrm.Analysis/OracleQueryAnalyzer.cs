using System;

namespace CobaltumOrm.Analysis;

/// <summary>Supplies the Oracle query profile used by compile-time SQL analysis.</summary>
public static class OracleQueryDialectProfile
{
    public static QueryDialectProfile Create(ISqlTypeMapper typeMapper)
    {
        if (typeMapper is null)
        {
            throw new ArgumentNullException(nameof(typeMapper));
        }

        return new QueryDialectProfile(
            QuerySyntaxProfile.Oracle,
            new QueryTypeProfile(typeMapper, OracleAggregateResult),
            "Oracle");
    }

    private static SqlValueKind OracleAggregateResult(string aggregateName, SqlValueKind argumentKind)
    {
        switch (aggregateName.ToLowerInvariant())
        {
            case "count":
                // Oracle exposes COUNT as NUMBER. Int64 is the common CLR result
                // used by the query analyzer for row counts.
                return SqlValueKind.Int64;
            case "sum":
            case "avg":
                if (argumentKind == SqlValueKind.Float || argumentKind == SqlValueKind.Double)
                {
                    return argumentKind;
                }

                return SqlTypeMapper.IsNumeric(argumentKind)
                    ? SqlValueKind.Decimal
                    : argumentKind;
            default:
                return argumentKind;
        }
    }
}

/// <summary>Analyzes the supported SQL query subset with Oracle identifier and type rules.</summary>
public sealed class OracleQueryAnalyzer : IQueryAnalyzer
{
    private readonly QueryAnalyzerEngine _engine;

    public OracleQueryAnalyzer()
        : this(new OracleTypeMapper())
    {
    }

    internal OracleQueryAnalyzer(ISqlTypeMapper typeMapper)
    {
        _engine = new QueryAnalyzerEngine(OracleQueryDialectProfile.Create(typeMapper));
    }

    public QueryDialectProfile Profile => _engine.Profile;

    public AnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        _engine.Analyze(schema, sql);
}
