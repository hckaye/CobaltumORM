using System;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class PostgreSqlTypeTests
{
    [Theory]
    [InlineData("time with time zone")]
    [InlineData("timetz")]
    public void RejectsTimeTypesThatWouldLoseTheirOffset(string sqlType)
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("events", new[] { new Column("at_time", sqlType) }),
        });

        var result = QueryAnalyzer.Analyze(schema, "SELECT at_time FROM events");

        Assert.Contains(result.Diagnostics, item => item.Code == "SQL205");
    }

    [Theory]
    [InlineData("integer(3)")]
    [InlineData("varchar(0)")]
    [InlineData("numeric(4, 5)")]
    [InlineData("date(3)")]
    public void RejectsInvalidPostgreSqlTypeModifiers(string sqlType)
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("events", new[] { new Column("value", sqlType) }),
        });

        var result = QueryAnalyzer.Analyze(schema, "SELECT value FROM events");

        Assert.Contains(result.Diagnostics, item => item.Code == "SQL205");
    }
}
