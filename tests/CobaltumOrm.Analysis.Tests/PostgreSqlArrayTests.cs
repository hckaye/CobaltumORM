using System.Linq;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class PostgreSqlArrayTests
{
    [Fact]
    public void MapsPostgreSqlArrayColumnsToClrArrays()
    {
        var migration = PostgreSqlMigrationAnalyzer.Analyze(
            new DatabaseSchema(System.Array.Empty<Table>()),
            "CREATE TABLE array_values (numbers integer[] NOT NULL, labels text[], identifiers uuid[] NOT NULL);");

        Assert.Empty(migration.Diagnostics);
        var table = Assert.Single(migration.Schema.Tables);
        Assert.Equal(new[] { "integer[]", "text[]", "uuid[]" }, table.Columns.Select(column => column.SqlType));

        var result = QueryAnalyzer.Analyze(
            migration.Schema,
            "SELECT numbers, labels, identifiers FROM array_values");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "int[]", "string[]?", "Guid[]" }, result.Columns.Select(column => column.ClrType));
    }

    [Theory]
    [InlineData("smallint[]", "short[]", "smallint[]")]
    [InlineData("bigint[]", "long[]", "bigint[]")]
    [InlineData("numeric[]", "decimal[]", "numeric[]")]
    [InlineData("jsonb[]", "string[]", "jsonb[]")]
    [InlineData("bytea[]", "byte[][]", "bytea[]")]
    [InlineData("interval[]", "TimeSpan[]", "interval[]")]
    public void MapsSupportedArrayElementTypes(string sqlType, string clrType, string databaseTypeName)
    {
        var result = QueryAnalyzer.Analyze(EmptySchema(), $"SELECT @value::{sqlType} AS value");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(clrType, Assert.Single(result.Columns).ClrType);
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal(clrType, parameter.ClrType);
        Assert.Equal(databaseTypeName, parameter.DatabaseTypeName);
    }

    [Fact]
    public void InfersArrayConstructorsCastsAndSubscripts()
    {
        var result = QueryAnalyzer.Analyze(
            EmptySchema(),
            "SELECT ARRAY[1, 2, 3] AS numbers, ARRAY['a', 'b']::text[] AS labels, (ARRAY[4, 5])[1] AS first_item");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "int[]", "string[]", "int?" }, result.Columns.Select(column => column.ClrType));
    }

    [Fact]
    public void InfersArrayParametersForAnyAllAndArrayOperators()
    {
        var result = QueryAnalyzer.Analyze(
            ArraySchema(),
            @"SELECT 2 = ANY(numbers) AS any_match, 2 < ALL(numbers) AS all_match
              FROM array_values
              WHERE numbers @> @required
                AND numbers && @overlap
                AND numbers <@ ARRAY[1, 2, 3]");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "bool?", "bool?" }, result.Columns.Select(column => column.ClrType));
        Assert.Equal(new[] { "int[]", "int[]" }, result.Parameters.Select(parameter => parameter.ClrType));
        Assert.All(result.Parameters, parameter => Assert.Equal("integer[]", parameter.DatabaseTypeName));
    }

    [Fact]
    public void InfersAnArrayParameterFromAny()
    {
        var result = QueryAnalyzer.Analyze(EmptySchema(), "SELECT 7 = ANY(@values) AS found");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("bool?", Assert.Single(result.Columns).ClrType);
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("int[]", parameter.ClrType);
        Assert.Equal("integer[]", parameter.DatabaseTypeName);
    }

    [Fact]
    public void SupportsArrayTableFunctions()
    {
        var unnest = QueryAnalyzer.Analyze(
            ArraySchema(),
            "SELECT value FROM array_values CROSS JOIN unnest(numbers) AS element_values(value)");
        var subscripts = QueryAnalyzer.Analyze(
            ArraySchema(),
            "SELECT position FROM array_values CROSS JOIN generate_subscripts(numbers, 1) AS position");

        Assert.Empty(unnest.Diagnostics);
        Assert.Equal("int?", Assert.Single(unnest.Columns).ClrType);
        Assert.Empty(subscripts.Diagnostics);
        Assert.Equal("int", Assert.Single(subscripts.Columns).ClrType);
    }

    [Fact]
    public void PreservesArrayTypesThroughCommonExpressions()
    {
        var result = QueryAnalyzer.Analyze(
            ArraySchema(),
            "SELECT MIN(numbers) AS minimum, COALESCE(MIN(numbers), ARRAY[0]) AS available FROM array_values");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "int[]?", "int[]" }, result.Columns.Select(column => column.ClrType));
    }

    [Theory]
    [InlineData("SELECT ARRAY[1, 'two']")]
    [InlineData("SELECT 1 = ANY(2)")]
    [InlineData("SELECT 1 @> 2")]
    [InlineData("SELECT (ARRAY[1, 2])['first']")]
    public void RejectsInvalidArrayExpressions(string sql)
    {
        var result = QueryAnalyzer.Analyze(EmptySchema(), sql);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SQL207");
    }

    [Fact]
    public void RejectsMultidimensionalArrayTypes()
    {
        var result = PostgreSqlMigrationAnalyzer.Analyze(
            EmptySchema(),
            "CREATE TABLE matrix_values (matrix integer[][] NOT NULL);");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DDL205");
    }

    [Fact]
    public void DoesNotEnablePostgreSqlArrayInferenceForOtherDialects()
    {
        var result = new MySqlQueryAnalyzer().Analyze(EmptySchema(), "SELECT 1 = ANY(@values)");

        Assert.True(result.HasErrors);
        Assert.DoesNotContain(result.Parameters, parameter => parameter.ClrType == "int[]");
    }

    private static DatabaseSchema EmptySchema() => new DatabaseSchema(System.Array.Empty<Table>());

    private static DatabaseSchema ArraySchema() => new DatabaseSchema(new[]
    {
        new Table("array_values", new[]
        {
            new Column("numbers", "integer[]"),
        }),
    });
}
