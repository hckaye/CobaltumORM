using System.Linq;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class AggregateValidationTests
{
    [Theory]
    [InlineData("SELECT id FROM users WHERE COUNT(*) > 0", "SQL214")]
    [InlineData("SELECT u.id FROM users u JOIN orders o ON COUNT(o.id) > 0", "SQL214")]
    [InlineData("SELECT SUM(COUNT(id)) FROM users", "SQL215")]
    [InlineData("SELECT name, COUNT(*) FROM users GROUP BY id", "SQL216")]
    [InlineData("SELECT id, COUNT(*) FROM users", "SQL216")]
    [InlineData("SELECT active, COUNT(*) FROM users GROUP BY active ORDER BY name", "SQL216")]
    [InlineData("SELECT active, COUNT(*) FROM users GROUP BY active HAVING name = 'x'", "SQL216")]
    public void RejectsInvalidAggregatePlacementAndGrouping(string sql, string expectedCode)
    {
        var result = TestSchema.Analyze(sql);

        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Theory]
    [InlineData("SELECT id + 1 FROM users GROUP BY id", "?column?", "int")]
    [InlineData("SELECT LOWER(name) FROM users GROUP BY LOWER(name)", "lower", "string")]
    [InlineData("SELECT active, COUNT(*) FROM users GROUP BY active ORDER BY COUNT(*)", "active", "bool")]
    [InlineData("SELECT active, COUNT(*) FROM users GROUP BY active HAVING COUNT(*) > 0", "active", "bool")]
    public void AcceptsGroupedExpressionsAndAggregateOrderOrHaving(string sql, string firstName, string firstType)
    {
        var result = TestSchema.Analyze(sql);

        TestSchema.AssertSuccess(result);
        Assert.Equal(firstName, result.Columns[0].Name);
        Assert.Equal(firstType, result.Columns[0].ClrType);
    }
}
