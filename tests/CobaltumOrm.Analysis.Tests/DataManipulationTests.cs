using System;
using System.Linq;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class DataManipulationTests
{
    private static readonly DatabaseSchema Schema = new DatabaseSchema(new[]
    {
        new Table("users", new[]
        {
            new Column("id", "integer"),
            new Column("name", "text", true),
            new Column("document", "jsonb"),
        }, "app"),
    });

    [Fact]
    public void ValidatesUpdateSyntaxNamesAndParameterTypes()
    {
        var result = QueryAnalyzer.Analyze(
            Schema,
            "UPDATE app.users AS u SET name = @name, document = @document WHERE u.id = @id;");

        AssertSuccess(result);
        Assert.Empty(result.Columns);
        Assert.Equal(
            new[]
            {
                ("@name", "string", (string?)null),
                ("@document", "string", "jsonb"),
                ("@id", "int", (string?)null),
            },
            result.Parameters.Select(parameter =>
                (parameter.Name, parameter.ClrType, parameter.DatabaseTypeName)).ToArray());
    }

    [Fact]
    public void ValidatesInsertRowsColumnsAndDefaults()
    {
        var values = QueryAnalyzer.Analyze(
            Schema,
            "INSERT INTO app.users (id, name, document) VALUES (@id, @name, @document), (2, DEFAULT, '{}');");
        var defaults = QueryAnalyzer.Analyze(Schema, "INSERT INTO app.users DEFAULT VALUES;");

        AssertSuccess(values);
        AssertSuccess(defaults);
        Assert.Equal(new[] { "@id", "@name", "@document" }, values.Parameters.Select(item => item.Name));
        Assert.Empty(defaults.Parameters);
    }

    [Fact]
    public void ValidatesDeleteTargetAndPredicate()
    {
        var result = QueryAnalyzer.Analyze(
            Schema,
            "/* checked */ DELETE FROM app.users WHERE id = @id; -- trailing comment");

        AssertSuccess(result);
        Assert.Equal("int", Assert.Single(result.Parameters).ClrType);
    }

    [Fact]
    public void ValidatesPostgreSqlTruncate()
    {
        var result = QueryAnalyzer.Analyze(
            Schema,
            "TRUNCATE TABLE ONLY app.users RESTART IDENTITY CASCADE;");

        AssertSuccess(result);
        Assert.Empty(result.Columns);
        Assert.Empty(result.Parameters);
    }

    [Theory]
    [InlineData("UPDATE app.users name = 'x'", "SQL100")]
    [InlineData("INSERT INTO app.users (id) VALUE (1)", "SQL100")]
    [InlineData("DELETE app.users WHERE id = 1", "SQL100")]
    [InlineData("UPDATE audit.users SET name = 'x'", "SQL200")]
    [InlineData("DELETE FROM app.missing WHERE id = 1", "SQL200")]
    [InlineData("UPDATE app.users SET missing = 1", "SQL203")]
    [InlineData("UPDATE app.users SET name = 'x' WHERE missing = 1", "SQL203")]
    [InlineData("INSERT INTO app.users (missing) VALUES (1)", "SQL203")]
    [InlineData("INSERT INTO app.users (id, name) VALUES (1)", "SQL219")]
    [InlineData("UPDATE app.users SET name = 'x', name = 'y'", "SQL219")]
    [InlineData("TRUNCATE TABLE app.missing", "SQL200")]
    public void RejectsInvalidDataManipulation(string sql, string expectedCode)
    {
        var result = QueryAnalyzer.Analyze(Schema, sql);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static void AssertSuccess(AnalysisResult result)
    {
        Assert.False(
            result.HasErrors,
            string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.ToString())));
    }
}
