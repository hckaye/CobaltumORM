using System;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class DiagnosticTests
{
    [Theory]
    [InlineData("SELECT 'unterminated", "SQL001")]
    [InlineData("SELECT id FROM", "SQL100")]
    [InlineData("SELECT id FROM missing", "SQL200")]
    [InlineData("SELECT u.id FROM users u JOIN orders u ON true", "SQL201")]
    [InlineData("SELECT x.id FROM users u", "SQL202")]
    [InlineData("SELECT missing FROM users", "SQL203")]
    [InlineData("SELECT id FROM users JOIN orders ON true", "SQL204")]
    [InlineData("SELECT MYSTERY(id) FROM users", "SQL206")]
    [InlineData("SELECT name + id FROM users", "SQL207")]
    [InlineData("SELECT id FROM users WHERE id", "SQL208")]
    [InlineData("SELECT @unknown", "SQL209")]
    [InlineData("SELECT id FROM users WHERE id = @value AND name = @value", "SQL210")]
    [InlineData("SELECT NULL", "SQL211")]
    [InlineData("SELECT LOWER(name, name) FROM users", "SQL212")]
    [InlineData("SELECT LOWER(*) FROM users", "SQL213")]
    public void ReportsEveryDiagnosticCategory(string sql, string expectedCode)
    {
        var result = TestSchema.Analyze(sql);

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == expectedCode);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.InRange(diagnostic.Span.Start, 0, sql.Length);
        Assert.InRange(diagnostic.Span.Length, 0, sql.Length - diagnostic.Span.Start);
    }

    [Fact]
    public void ReportsUnsupportedSchemaAndCastTypes()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[] { new Column("payload", "custom_type") }),
        });

        var columnResult = QueryAnalyzer.Analyze(schema, "SELECT payload FROM items");
        var castResult = TestSchema.Analyze("SELECT CAST(id AS custom_type) FROM users");

        Assert.Contains(columnResult.Diagnostics, item => item.Code == "SQL205");
        Assert.Contains(castResult.Diagnostics, item => item.Code == "SQL205");
    }

    [Fact]
    public void DiagnosticsPointAtTheOffendingSqlText()
    {
        const string sql = "SELECT missing FROM users";
        var result = TestSchema.Analyze(sql);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("missing", sql.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }

    [Fact]
    public void AnalyzeReturnsDiagnosticsForNullInputsInsteadOfThrowing()
    {
        var nullSchema = QueryAnalyzer.Analyze(null!, "SELECT 1");
        var nullSql = QueryAnalyzer.Analyze(TestSchema.Create(), null!);

        Assert.Equal("SQL000", Assert.Single(nullSchema.Diagnostics).Code);
        Assert.Equal("SQL000", Assert.Single(nullSql.Diagnostics).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("SELECT (")]
    [InlineData("SELECT CASE WHEN THEN END")]
    [InlineData("SELECT CAST(1 AS)")]
    [InlineData("SELECT id FROM users LEFT JOIN orders")]
    [InlineData("SELECT @")]
    [InlineData("SELECT \"unterminated")]
    [InlineData("SELECT 1.")]
    public void MalformedInputNeverEscapesAsAnException(string sql)
    {
        var exception = Record.Exception(() => TestSchema.Analyze(sql));

        Assert.Null(exception);
        Assert.True(TestSchema.Analyze(sql).HasErrors);
    }

    [Fact]
    public void MalformedAdvancedSelectFormsProduceDiagnostics()
    {
        var sqlStatements = new[]
        {
            "WITH x AS (SELECT 1 SELECT * FROM x",
            "SELECT id FROM users UNION DELETE FROM orders",
            "SELECT DISTINCT ON id FROM users",
        };

        foreach (var sql in sqlStatements)
        {
            Assert.True(TestSchema.Analyze(sql).HasErrors, sql);
        }
    }
}
