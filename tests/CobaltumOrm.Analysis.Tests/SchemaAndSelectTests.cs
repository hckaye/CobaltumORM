using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class SchemaAndSelectTests
{
    [Theory]
    [InlineData("active", "bool")]
    [InlineData("age", "short?")]
    [InlineData("id", "int")]
    [InlineData("payments.id", "long")]
    [InlineData("score", "float")]
    [InlineData("ratio", "double?")]
    [InlineData("balance", "decimal?")]
    [InlineData("name", "string")]
    [InlineData("nickname", "string?")]
    [InlineData("code", "string")]
    [InlineData("external_id", "Guid")]
    [InlineData("birth_date", "DateOnly?")]
    [InlineData("wake_time", "TimeOnly")]
    [InlineData("created_at", "DateTime")]
    [InlineData("updated_at", "DateTimeOffset?")]
    [InlineData("avatar", "byte[]?")]
    public void MapsEverySupportedSchemaType(string selection, string expectedType)
    {
        var from = selection.StartsWith("payments.", System.StringComparison.Ordinal) ? "payments" : "users";
        var result = TestSchema.Analyze($"SELECT {selection} FROM {from}");

        TestSchema.AssertColumns(result, (selection.Substring(selection.LastIndexOf('.') + 1), expectedType));
    }

    [Fact]
    public void ExpandsUnqualifiedAndQualifiedWildcardsInScopeOrder()
    {
        var result = TestSchema.Analyze("SELECT u.*, o.total FROM users u JOIN orders o ON o.user_id = u.id");

        TestSchema.AssertSuccess(result);
        Assert.Equal(16, result.Columns.Count);
        Assert.Equal("id", result.Columns[0].Name);
        Assert.Equal("int", result.Columns[0].ClrType);
        Assert.Equal("total", result.Columns[15].Name);
        Assert.Equal("decimal", result.Columns[15].ClrType);
    }

    [Fact]
    public void UnqualifiedWildcardExpandsEveryTable()
    {
        var result = TestSchema.Analyze("SELECT * FROM users u LEFT JOIN orders o ON o.user_id = u.id");

        TestSchema.AssertSuccess(result);
        Assert.Equal(20, result.Columns.Count);
        Assert.Equal("int", result.Columns[0].ClrType);
        Assert.Equal("int?", result.Columns[15].ClrType);
        Assert.Equal("decimal?", result.Columns[17].ClrType);
    }

    [Fact]
    public void InfersEveryLiteralKind()
    {
        var result = TestSchema.Analyze("SELECT 1 integer_value, 1.5 decimal_value, 'text' string_value, TRUE bool_value, CAST(NULL AS text) null_value");

        TestSchema.AssertColumns(
            result,
            ("integer_value", "int"),
            ("decimal_value", "decimal"),
            ("string_value", "string"),
            ("bool_value", "bool"),
            ("null_value", "string?"));
    }

    [Fact]
    public void InfersPostgreSqlIntegerLiteralWidths()
    {
        var result = TestSchema.Analyze(
            "SELECT 2147483647 int_value, 2147483648 bigint_value, 9223372036854775808 numeric_value");

        TestSchema.AssertColumns(
            result,
            ("int_value", "int"),
            ("bigint_value", "long"),
            ("numeric_value", "decimal"));
    }

    [Fact]
    public void HonorsExplicitAndImplicitAliasesAndDeterministicDefaults()
    {
        var result = TestSchema.Analyze(
            "SELECT id AS user_id, name display_name, LOWER(name), CASE WHEN active THEN 1 ELSE 0 END, CAST(id AS bigint), 42 FROM users");

        TestSchema.AssertColumns(
            result,
            ("user_id", "int"),
            ("display_name", "string"),
            ("lower", "string"),
            ("case", "int"),
            ("cast", "long"),
            ("?column?", "int"));
    }

    [Fact]
    public void UnquotedIdentifiersAreCaseInsensitive()
    {
        var result = TestSchema.Analyze("SELECT U.ID FROM USERS U");

        TestSchema.AssertColumns(result, ("ID", "int"));
    }

    [Fact]
    public void QuotedIdentifiersAreCaseSensitiveAndSupportEscapes()
    {
        var schema = new DatabaseSchema(new[]
        {
            new Table("Odd\"Table", new[] { new Column("MixedCase", "text") }),
        });

        var result = QueryAnalyzer.Analyze(schema, "SELECT \"t\".\"MixedCase\" FROM \"Odd\"\"Table\" AS \"t\"");

        TestSchema.AssertColumns(result, ("MixedCase", "string"));
    }

    [Fact]
    public void SchemaCollectionsAreDefensivelyCopied()
    {
        var columns = new[] { new Column("id", "integer") };
        var tables = new[] { new Table("items", columns) };
        var schema = new DatabaseSchema(tables);
        columns[0] = new Column("changed", "text");
        tables[0] = new Table("changed", columns);

        var result = QueryAnalyzer.Analyze(schema, "SELECT id FROM items");

        TestSchema.AssertColumns(result, ("id", "int"));
    }
}
