using System;

namespace CobaltumOrm.Analysis;

/// <summary>Analyzes the common query subset using MySQL 8 lexical and type rules.</summary>
public sealed class MySqlQueryAnalyzer : IQueryAnalyzer
{
    private readonly QueryAnalyzerEngine _engine;

    public MySqlQueryAnalyzer()
        : this(new MySqlTypeMapper())
    {
    }

    internal MySqlQueryAnalyzer(ISqlTypeMapper mapper)
    {
        if (mapper is null)
        {
            throw new ArgumentNullException(nameof(mapper));
        }

        _engine = new QueryAnalyzerEngine(
            new QueryDialectProfile(
                QuerySyntaxProfile.MySql,
                new QueryTypeProfile(mapper, MySqlAggregateResult),
                "MySql"));
    }

    public QueryDialectProfile Profile => _engine.Profile;

    public AnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        _engine.Analyze(schema, sql);

    private static SqlValueKind MySqlAggregateResult(string aggregateName, SqlValueKind argumentKind)
    {
        switch (aggregateName.ToLowerInvariant())
        {
            case "sum":
                return MySqlIsExactNumeric(argumentKind)
                    ? SqlValueKind.Decimal
                    : SqlTypeMapper.IsFloat(argumentKind)
                        ? SqlValueKind.Double
                        : argumentKind;
            case "avg":
                return MySqlIsExactNumeric(argumentKind)
                    ? SqlValueKind.Decimal
                    : SqlTypeMapper.IsFloat(argumentKind)
                        ? SqlValueKind.Double
                        : argumentKind;
            case "count":
                return SqlValueKind.Int64;
            default:
                return argumentKind;
        }
    }

    private static bool MySqlIsExactNumeric(SqlValueKind kind) =>
        kind == SqlValueKind.Int16 || kind == SqlValueKind.Int32 ||
        kind == SqlValueKind.Int64 || kind == SqlValueKind.Decimal;
}
