using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class ExpressionTests
{
    [Theory]
    [InlineData("CAST(1 AS smallint) + 1", "int")]
    [InlineData("id + CAST(1 AS bigint)", "long")]
    [InlineData("id + 1.5", "decimal")]
    [InlineData("score + 1", "double")]
    [InlineData("ratio + balance", "double?")]
    [InlineData("id / 2", "int")]
    [InlineData("id - 2", "int")]
    [InlineData("age * 2", "int?")]
    [InlineData("-balance", "decimal?")]
    public void InfersArithmeticUsingNumericWidening(string expression, string expectedType)
    {
        var result = TestSchema.Analyze($"SELECT {expression} AS value FROM users");

        TestSchema.AssertColumns(result, ("value", expectedType));
    }

    [Fact]
    public void InfersStringConcatenationAndNullability()
    {
        var result = TestSchema.Analyze("SELECT name || '!', name || nickname FROM users");

        TestSchema.AssertColumns(result, ("?column?", "string"), ("?column?", "string?"));
    }

    [Fact]
    public void InfersComparisonLogicalAndNullPredicates()
    {
        var result = TestSchema.Analyze(
            "SELECT id > 0, nickname LIKE 'A%', active AND true, active OR false, NOT active, nickname IS NULL, nickname IS NOT NULL, id IN (1, 2), age BETWEEN 1 AND 10 FROM users");

        TestSchema.AssertColumns(
            result,
            ("?column?", "bool"),
            ("?column?", "bool?"),
            ("?column?", "bool"),
            ("?column?", "bool"),
            ("?column?", "bool"),
            ("?column?", "bool"),
            ("?column?", "bool"),
            ("?column?", "bool"),
            ("?column?", "bool?"));
    }

    [Theory]
    [InlineData("=")]
    [InlineData("<>")]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void SupportsEveryComparisonOperator(string comparisonOperator)
    {
        var result = TestSchema.Analyze($"SELECT id {comparisonOperator} 1 AS value FROM users");

        TestSchema.AssertColumns(result, ("value", "bool"));
    }

    [Fact]
    public void SupportsNegatedLikeInAndBetweenAndParentheses()
    {
        var result = TestSchema.Analyze(
            "SELECT name NOT LIKE 'x', id NOT IN (1, 2), id NOT BETWEEN 1 AND 2, (id + 1) * 2 FROM users");

        TestSchema.AssertColumns(
            result,
            ("?column?", "bool"),
            ("?column?", "bool"),
            ("?column?", "bool"),
            ("?column?", "int"));
    }

    [Fact]
    public void InfersSearchedAndSimpleCase()
    {
        var result = TestSchema.Analyze(
            "SELECT CASE WHEN active THEN id ELSE CAST(1 AS bigint) END searched, CASE id WHEN 1 THEN name WHEN 2 THEN nickname ELSE 'x' END simple FROM users");

        TestSchema.AssertColumns(result, ("searched", "long"), ("simple", "string?"));
    }

    [Fact]
    public void CaseWithoutElseIsNullable()
    {
        var result = TestSchema.Analyze("SELECT CASE WHEN active THEN id END value FROM users");

        TestSchema.AssertColumns(result, ("value", "int?"));
    }

    [Fact]
    public void CoalesceIsNullableOnlyWhenAllArgumentsAreNullable()
    {
        var result = TestSchema.Analyze(
            "SELECT COALESCE(nickname, name), COALESCE(nickname, CAST(NULL AS text)), COALESCE(NULL, id) FROM users");

        TestSchema.AssertColumns(result, ("coalesce", "string"), ("coalesce", "string?"), ("coalesce", "int"));
    }

    [Fact]
    public void NullIfUsesFirstArgumentTypeAndIsAlwaysNullable()
    {
        var result = TestSchema.Analyze("SELECT NULLIF(id, 0), NULLIF(name, nickname) FROM users");

        TestSchema.AssertColumns(result, ("nullif", "int?"), ("nullif", "string?"));
    }

    [Theory]
    [InlineData("CAST(NULL AS integer)", "int?")]
    [InlineData("CAST(name AS varchar(12))", "string")]
    [InlineData("CAST(id AS double precision)", "double")]
    [InlineData("CAST(id AS date)", "DateOnly")]
    public void CastProvidesAnExplicitResultType(string expression, string expectedType)
    {
        var result = TestSchema.Analyze($"SELECT {expression} AS value FROM users");

        TestSchema.AssertColumns(result, ("value", expectedType));
    }

    [Fact]
    public void StandardStringAndNumericFunctionsPropagateNullability()
    {
        var result = TestSchema.Analyze(
            "SELECT LOWER(name), UPPER(nickname), LENGTH(name), ABS(age), ABS(score) FROM users");

        TestSchema.AssertColumns(
            result,
            ("lower", "string"),
            ("upper", "string?"),
            ("length", "int"),
            ("abs", "short?"),
            ("abs", "float"));
    }

    [Fact]
    public void ParsesEscapedStringLiterals()
    {
        var result = TestSchema.Analyze("SELECT 'can''t' AS text_value");

        TestSchema.AssertColumns(result, ("text_value", "string"));
    }
}
