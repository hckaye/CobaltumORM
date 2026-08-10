using System;
using System.Collections.Generic;

namespace CobaltumOrm.Analysis;

public static class QueryAnalyzer
{
    public static AnalysisResult Analyze(DatabaseSchema schema, string sql)
        => PostgreSqlQueryAnalyzer.Instance.Analyze(schema, sql);
}

public sealed class PostgreSqlQueryAnalyzer : IQueryAnalyzer
{
    private readonly QueryAnalyzerEngine _engine;

    internal static PostgreSqlQueryAnalyzer Instance { get; } = new PostgreSqlQueryAnalyzer();

    public PostgreSqlQueryAnalyzer()
        : this(QueryDialectProfiles.PostgreSql)
    {
    }

    internal PostgreSqlQueryAnalyzer(QueryDialectProfile profile)
    {
        _engine = new QueryAnalyzerEngine(profile);
    }

    public AnalysisResult Analyze(DatabaseSchema schema, string sql) =>
        _engine.Analyze(schema, sql);
}

/// <summary>Analyzes the supported SQL subset using a dialect query profile.</summary>
public sealed class QueryAnalyzerEngine : IQueryAnalyzer
{
    private readonly QueryDialectProfile _profile;

    public QueryAnalyzerEngine(QueryDialectProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public QueryDialectProfile Profile => _profile;

    public AnalysisResult Analyze(DatabaseSchema schema, string sql)
    {
        var diagnostics = new List<Diagnostic>();
        if (schema is null)
        {
            diagnostics.Add(new Diagnostic("SQL000", "A database schema is required.", new SourceSpan(0, 0)));
            return new AnalysisResult(Array.Empty<ResultColumn>(), Array.Empty<QueryParameter>(), diagnostics);
        }

        if (sql is null)
        {
            diagnostics.Add(new Diagnostic("SQL000", "SQL text is required.", new SourceSpan(0, 0)));
            return new AnalysisResult(Array.Empty<ResultColumn>(), Array.Empty<QueryParameter>(), diagnostics);
        }

        try
        {
            var tokens = new Lexer(sql, diagnostics, _profile.Syntax).Lex();
            var statement = new Parser(tokens, diagnostics, _profile).Parse();
            if (statement == null)
            {
                return new AnalysisResult(Array.Empty<ResultColumn>(), Array.Empty<QueryParameter>(), diagnostics);
            }

            return new Binder(schema, diagnostics, _profile).Bind(statement);
        }
        catch (Exception)
        {
            diagnostics.Add(new Diagnostic(
                "SQL999",
                "The SQL could not be analyzed because of an internal analysis error.",
                new SourceSpan(0, sql.Length)));
            return new AnalysisResult(Array.Empty<ResultColumn>(), Array.Empty<QueryParameter>(), diagnostics);
        }
    }
}
