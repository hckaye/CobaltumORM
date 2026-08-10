using System;
using System.Linq;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class MySqlQueryTests
{
    private static DatabaseSchema Schema => new DatabaseSchema(new[]
    {
        new Table("tenant`one", new[]
        {
            new Column("id", "bigint"),
            new Column("display`name", "varchar(80)"),
            new Column("amount", "decimal(18,4)"),
            new Column("enabled", "tinyint(1)"),
            new Column("payload", "json", true),
            new Column("created", "datetime(6)"),
        }, "app`data"),
    });

    [Fact]
    public void AnalyzesBacktickEscapesAndAtParameters()
    {
        var result = new MySqlQueryAnalyzer().Analyze(
            Schema,
            "SELECT `display``name`, @id FROM `app``data`.`tenant``one` WHERE `id` = @id");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        Assert.Equal("display`name", result.Columns[0].Name);
        Assert.Equal("string", result.Columns[0].ClrType);
        Assert.Equal("?column?", result.Columns[1].Name);
        Assert.Equal("long", result.Columns[1].ClrType);
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("@id", parameter.Name);
        Assert.Equal("long", parameter.ClrType);
    }

    [Fact]
    public void UsesMySqlExactNumericAggregateRules()
    {
        var result = new MySqlQueryAnalyzer().Analyze(
            Schema,
            "SELECT SUM(id), SUM(amount), AVG(id), AVG(amount), COUNT(*) FROM `app``data`.`tenant``one`");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        Assert.Equal(new[] { "decimal?", "decimal?", "decimal?", "decimal?", "long" },
            result.Columns.Select(item => item.ClrType).ToArray());
    }

    [Fact]
    public void MapsJsonAndTemporalParametersFromMySqlColumns()
    {
        var result = new MySqlQueryAnalyzer().Analyze(
            Schema,
            "SELECT payload, created FROM `app``data`.`tenant``one` WHERE payload = @payload AND created > @created");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        Assert.Equal("string?", result.Columns[0].ClrType);
        Assert.Equal("DateTime", result.Columns[1].ClrType);
        Assert.Equal(new[] { "@payload", "@created" }, result.Parameters.Select(item => item.Name).ToArray());
        Assert.Equal(new[] { "string", "DateTime" }, result.Parameters.Select(item => item.ClrType).ToArray());
        Assert.Equal("json", result.Parameters[0].DatabaseTypeName);
        Assert.Equal("datetime", result.Parameters[1].DatabaseTypeName);
    }

    [Fact]
    public void TreatsUnquotedIdentifiersAsCaseInsensitive()
    {
        var result = new MySqlQueryAnalyzer().Analyze(
            Schema,
            "SELECT ID FROM `app``data`.`tenant``one`");

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(item => item.ToString())));
        Assert.Equal("long", Assert.Single(result.Columns).ClrType);
    }
}
