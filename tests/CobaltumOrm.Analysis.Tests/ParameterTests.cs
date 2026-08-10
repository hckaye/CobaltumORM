using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class ParameterTests
{
    [Theory]
    [InlineData("SELECT id FROM users WHERE age > @value", "short")]
    [InlineData("SELECT id FROM users WHERE @value = name", "string")]
    [InlineData("SELECT id FROM users WHERE active = @value", "bool")]
    [InlineData("SELECT id FROM users WHERE id IN (@value, 2)", "int")]
    [InlineData("SELECT id FROM users WHERE id BETWEEN @value AND 10", "int")]
    [InlineData("SELECT LOWER(@value) FROM users", "string")]
    [InlineData("SELECT CAST(@value AS uuid)", "Guid")]
    [InlineData("SELECT id FROM users LIMIT @value", "long")]
    [InlineData("SELECT id FROM users OFFSET @value", "long")]
    public void InfersParametersFromTypedContexts(string sql, string expectedType)
    {
        var result = TestSchema.Analyze(sql);

        TestSchema.AssertSuccess(result);
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("@value", parameter.Name);
        Assert.Equal(expectedType, parameter.ClrType);
    }

    [Fact]
    public void CoalesceInfersParameterFromOtherArguments()
    {
        var result = TestSchema.Analyze("SELECT COALESCE(@fallback, name) FROM users");

        TestSchema.AssertColumns(result, ("coalesce", "string"));
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("string", parameter.ClrType);
    }

    [Fact]
    public void AParameterIsCollectedOnceAndKeepsItsFirstSpelling()
    {
        var result = TestSchema.Analyze("SELECT id FROM users WHERE id > @Min AND id < @min");

        TestSchema.AssertSuccess(result);
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("@Min", parameter.Name);
        Assert.Equal("int", parameter.ClrType);
    }

    [Fact]
    public void LaterClauseCanTypeAParameterSelectedDirectly()
    {
        var result = TestSchema.Analyze("SELECT @value AS echoed FROM users WHERE id = @value");

        TestSchema.AssertColumns(result, ("echoed", "int"));
        Assert.Equal("int", Assert.Single(result.Parameters).ClrType);
    }
}
