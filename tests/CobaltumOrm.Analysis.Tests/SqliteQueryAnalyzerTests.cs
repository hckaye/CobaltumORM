using System;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class SqliteQueryAnalyzerTests
{
    [Fact]
    public void AcceptsAllSQLiteIdentifierDelimitersAndCaseVariants()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("Items", new[] { new Column("MixedCase", "INTEGER") }),
        });

        var result = new SqliteQueryAnalyzer().Analyze(
            schema,
            "SELECT [t].[mixedcase] FROM `ITEMS` AS \"t\"");

        SqliteAssertColumns(result, ("mixedcase", "long"));
    }

    [Theory]
    [InlineData("@value", "@value")]
    [InlineData(":value", ":value")]
    [InlineData("$value", "$value")]
    [InlineData("@1", "@1")]
    public void InfersNamedParametersWithEverySQLitePrefix(string parameter, string expectedName)
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[] { new Column("id", "INTEGER") }),
        });

        var result = new SqliteQueryAnalyzer().Analyze(
            schema,
            "SELECT id FROM items WHERE id = " + parameter);

        SqliteAssertSuccess(result);
        var actual = Assert.Single(result.Parameters);
        Assert.Equal(expectedName, actual.Name);
        Assert.Equal("long", actual.ClrType);
        Assert.Equal("INTEGER", actual.DatabaseTypeName);
    }

    [Fact]
    public void UsesSQLiteAffinityClrShapesAndParameterCaseInsensitivity()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[]
            {
                new Column("integer_value", "INTEGER"),
                new Column("text_value", "TEXT"),
                new Column("numeric_value", "NUMERIC"),
                new Column("real_value", "REAL"),
                new Column("blob_value", "BLOB"),
            }),
        });

        var result = new SqliteQueryAnalyzer().Analyze(
            schema,
            "SELECT integer_value, text_value, numeric_value, real_value, blob_value " +
            "FROM items WHERE integer_value = @Min AND integer_value < @min");

        SqliteAssertColumns(
            result,
            ("integer_value", "long"),
            ("text_value", "string"),
            ("numeric_value", "decimal"),
            ("real_value", "double"),
            ("blob_value", "byte[]"));
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("@Min", parameter.Name);
        Assert.Equal("long", parameter.ClrType);
    }

    [Theory]
    [InlineData("available = true")]
    [InlineData("available")]
    [InlineData("available IS TRUE")]
    public void PreservesBooleanAndInt32MigrationTypesInQueries(string predicate)
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[]
            {
                new Column("price_cents", "INT32"),
                new Column("available", "BOOLEAN"),
            }),
        });

        var result = new SqliteQueryAnalyzer().Analyze(
            schema,
            "SELECT price_cents, available FROM items WHERE " + predicate);

        SqliteAssertColumns(result, ("price_cents", "int"), ("available", "bool"));
    }

    [Fact]
    public void UsesSQLiteNumericAggregateResultRules()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[]
            {
                new Column("integer_value", "INTEGER"),
                new Column("numeric_value", "NUMERIC"),
                new Column("real_value", "REAL"),
            }),
        });

        var result = new SqliteQueryAnalyzer().Analyze(
            schema,
            "SELECT SUM(integer_value), AVG(integer_value), SUM(numeric_value), " +
            "AVG(numeric_value), AVG(real_value), COUNT(*) FROM items");

        SqliteAssertColumns(
            result,
            ("SUM", "long?"),
            ("AVG", "double?"),
            ("SUM", "decimal?"),
            ("AVG", "double?"),
            ("AVG", "double?"),
            ("COUNT", "long"));
    }

    [Fact]
    public void ReportsAnUnknownSchemaQualifiedTable()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("items", new[] { new Column("id", "INTEGER") }),
        });

        var result = new SqliteQueryAnalyzer().Analyze(schema, "SELECT id FROM main.items");

        Assert.Contains(result.Diagnostics, item => item.Code == "SQL200");
    }

    private static void SqliteAssertSuccess(AnalysisResult result)
    {
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
    }

    private static void SqliteAssertColumns(
        AnalysisResult result,
        params (string Name, string Type)[] expected)
    {
        SqliteAssertSuccess(result);
        Assert.Equal(expected.Length, result.Columns.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Name, result.Columns[index].Name);
            Assert.Equal(expected[index].Type, result.Columns[index].ClrType);
        }
    }
}
