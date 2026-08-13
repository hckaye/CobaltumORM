using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class QuerySecurityAndRobustnessTests
{
    public static IEnumerable<object[]> Dialects()
    {
        yield return new object[] { "PostgreSql", "@name" };
        yield return new object[] { "MySql", "@name" };
        yield return new object[] { "Sqlite", "@name" };
        yield return new object[] { "SqlServer", "@name" };
        yield return new object[] { "Oracle", ":name" };
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void ParametersInsideStringsIdentifiersAndCommentsAreNotBound(
        string provider,
        string realParameter)
    {
        var dialect = Resolve(provider);
        var analyzer = dialect.QueryAnalyzer;
        var prefix = realParameter[0];
        var sql =
            "SELECT '" + prefix + "string_parameter' AS literal_value, " +
            "'x''; DROP TABLE users; --' AS " +
            dialect.IdentifierQuoter.QuoteIdentifier(prefix + "identifier_parameter") + ", " +
            "id FROM users " +
            "WHERE name = " + realParameter + " " +
            "/* " + prefix + "block_parameter ; DELETE FROM users */ " +
            "-- " + prefix + "line_parameter ; DROP TABLE users\n";

        var result = analyzer.Analyze(CreateSchema(provider), sql);

        Assert.False(result.HasErrors, Diagnostics(result));
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal(realParameter, parameter.Name, ignoreCase: true);
        Assert.Equal("string", parameter.ClrType);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void RejectsAdditionalStatementsAfterAValidQuery(string provider, string parameter)
    {
        var analyzer = Resolve(provider).QueryAnalyzer;
        var sql = "SELECT id FROM users WHERE name = " + parameter + "; DROP TABLE users";

        var result = analyzer.Analyze(CreateSchema(provider), sql);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Unexpected token", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "SQL999");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TruncatedQueriesProduceNormalDiagnosticsInsteadOfInternalErrors(
        string provider,
        string parameter)
    {
        var analyzer = Resolve(provider).QueryAnalyzer;
        var sql =
            "SELECT CASE WHEN u.id IN (SELECT o.user_id FROM orders AS o " +
            "WHERE o.total > 0) THEN COALESCE(u.name, 'none') ELSE 'other' END AS label " +
            "FROM users AS u WHERE u.name = " + parameter + " ORDER BY u.id";

        for (var length = 0; length <= sql.Length; length++)
        {
            var candidate = sql.Substring(0, length);
            var result = analyzer.Analyze(CreateSchema(provider), candidate);
            AssertNoInternalErrorsOrInvalidSpans(result, candidate);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void DeterministicMalformedQueryCorpusDoesNotCrashTheAnalyzer(
        string provider,
        string parameter)
    {
        var analyzer = Resolve(provider).QueryAnalyzer;
        var random = new Random(0x5EED);
        var fragments = new[]
        {
            "SELECT", "FROM", "users", "orders", "AS", "id", "name", "user_id", "total",
            "WHERE", "AND", "OR", "NOT", "NULL", "IS", "IN", "BETWEEN", "LIKE", "EXISTS",
            "CASE", "WHEN", "THEN", "ELSE", "END", "COALESCE", "COUNT", "SUM", "DISTINCT",
            "GROUP", "BY", "HAVING", "ORDER", "ASC", "DESC", "LIMIT", "OFFSET", "FETCH",
            "WITH", "RECURSIVE", "UNION", "INTERSECT", "EXCEPT", "INSERT", "INTO", "VALUES",
            "UPDATE", "SET", "DELETE", "RETURNING", "ON", "JOIN", "LEFT", "RIGHT", "FULL",
            "(", ")", ",", ".", "*", "+", "-", "/", "%", "=", "<>", "<=", ">=", ";",
            "0", "1", "-1", "1.5", "'text'", "'unterminated", "\"quoted\"", "`quoted`", "[quoted]",
            parameter, "@other", ":other", "-- comment\n", "/* comment */", "/* unterminated", "雪",
        };

        for (var sample = 0; sample < 300; sample++)
        {
            var builder = new StringBuilder();
            var fragmentCount = random.Next(1, 30);
            for (var index = 0; index < fragmentCount; index++)
            {
                if (builder.Length != 0)
                {
                    builder.Append(random.Next(4) == 0 ? '\n' : ' ');
                }

                builder.Append(fragments[random.Next(fragments.Length)]);
            }

            var sql = builder.ToString();
            var result = analyzer.Analyze(CreateSchema(provider), sql);
            AssertNoInternalErrorsOrInvalidSpans(result, sql);
        }
    }

    private static DatabaseSchema CreateSchema(string provider)
    {
        var types = provider switch
        {
            "PostgreSql" => (Id: "integer", Name: "text", Total: "numeric"),
            "MySql" => (Id: "int", Name: "varchar(100)", Total: "decimal(18,2)"),
            "Sqlite" => (Id: "INTEGER", Name: "TEXT", Total: "NUMERIC"),
            "SqlServer" => (Id: "int", Name: "nvarchar(100)", Total: "decimal(18,2)"),
            "Oracle" => (Id: "NUMBER(10)", Name: "VARCHAR2(100)", Total: "NUMBER(18,2)"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        return new DatabaseSchema(new[]
        {
            new Table("users", new[]
            {
                new Column("id", types.Id),
                new Column("name", types.Name, isNullable: true),
            }),
            new Table("orders", new[]
            {
                new Column("id", types.Id),
                new Column("user_id", types.Id),
                new Column("total", types.Total),
            }),
        });
    }

    private static IDatabaseDialect Resolve(string provider)
    {
        Assert.True(DatabaseDialects.TryResolve(provider, out var dialect, out var error), error);
        return dialect;
    }

    private static void AssertNoInternalErrorsOrInvalidSpans(AnalysisResult result, string sql)
    {
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "SQL999");
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.InRange(diagnostic.Span.Start, 0, sql.Length);
            Assert.InRange(diagnostic.Span.Length, 0, sql.Length - diagnostic.Span.Start);
        });
    }

    private static string Diagnostics(AnalysisResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString()));
}
