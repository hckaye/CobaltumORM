using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class JoinAndAggregateTests
{
    [Fact]
    public void InnerJoinPreservesBaseNullability()
    {
        var result = TestSchema.Analyze("SELECT u.id, o.total FROM users u INNER JOIN orders o ON o.user_id = u.id");

        TestSchema.AssertColumns(result, ("id", "int"), ("total", "decimal"));
    }

    [Fact]
    public void LeftJoinMakesRightTableNullable()
    {
        var result = TestSchema.Analyze("SELECT u.id, o.id, o.note FROM users u LEFT OUTER JOIN orders o ON o.user_id = u.id");

        TestSchema.AssertColumns(result, ("id", "int"), ("id", "int?"), ("note", "string?"));
    }

    [Fact]
    public void RightJoinMakesEveryPriorTableNullable()
    {
        var result = TestSchema.Analyze("SELECT u.id, o.id FROM users u RIGHT JOIN orders o ON o.user_id = u.id");

        TestSchema.AssertColumns(result, ("id", "int?"), ("id", "int"));
    }

    [Fact]
    public void FullJoinMakesBothSidesNullable()
    {
        var result = TestSchema.Analyze("SELECT u.id, o.id FROM users u FULL OUTER JOIN orders o ON o.user_id = u.id");

        TestSchema.AssertColumns(result, ("id", "int?"), ("id", "int?"));
    }

    [Fact]
    public void OuterJoinNullabilityPropagatesThroughChainedJoins()
    {
        var result = TestSchema.Analyze(
            "SELECT u.id, o.id, p.id FROM users u LEFT JOIN orders o ON o.user_id = u.id RIGHT JOIN payments p ON p.order_id = o.id");

        TestSchema.AssertColumns(result, ("id", "int?"), ("id", "int?"), ("id", "long"));
    }

    [Fact]
    public void CountIsAlwaysLongAndNonNullable()
    {
        var result = TestSchema.Analyze("SELECT COUNT(*), COUNT(nickname) FROM users");

        TestSchema.AssertColumns(result, ("count", "long"), ("count", "long"));
    }

    [Fact]
    public void AggregatesWithoutGroupByAreNullableExceptCount()
    {
        var result = TestSchema.Analyze("SELECT SUM(id), SUM(balance), AVG(id), AVG(balance), AVG(score), MIN(name), MAX(age), COUNT(id) FROM users");

        TestSchema.AssertColumns(
            result,
            ("sum", "long?"),
            ("sum", "decimal?"),
            ("avg", "decimal?"),
            ("avg", "decimal?"),
            ("avg", "double?"),
            ("min", "string?"),
            ("max", "short?"),
            ("count", "long"));
    }

    [Fact]
    public void SumOfBigintUsesPostgreSqlNumericResultType()
    {
        var result = TestSchema.Analyze("SELECT SUM(id) FROM payments");

        TestSchema.AssertColumns(result, ("sum", "decimal?"));
    }

    [Fact]
    public void GroupedAggregatesFollowArgumentNullability()
    {
        var result = TestSchema.Analyze(
            "SELECT active, SUM(id), SUM(balance), AVG(score), MIN(name), MAX(age) FROM users GROUP BY active");

        TestSchema.AssertColumns(
            result,
            ("active", "bool"),
            ("sum", "long"),
            ("sum", "decimal?"),
            ("avg", "double"),
            ("min", "string"),
            ("max", "short?"));
    }

    [Fact]
    public void EndToEndExampleInfersShapeAndParameter()
    {
        var result = TestSchema.Analyze(@"
            SELECT u.id, u.name, COUNT(o.id) AS order_count, SUM(o.total) AS total_spent
            FROM users u LEFT JOIN orders o ON o.user_id = u.id
            WHERE u.created_at > @since
            GROUP BY u.id, u.name");

        TestSchema.AssertColumns(
            result,
            ("id", "int"),
            ("name", "string"),
            ("order_count", "long"),
            ("total_spent", "decimal?"));
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal("@since", parameter.Name);
        Assert.Equal("DateTime", parameter.ClrType);
    }

    [Fact]
    public void ParsesAllNonSelectClauses()
    {
        var result = TestSchema.Analyze(@"
            SELECT active, COUNT(*) AS count
            FROM users
            WHERE id > 0
            GROUP BY active
            HAVING COUNT(*) > 0
            ORDER BY count DESC
            LIMIT 10 OFFSET 2;");

        TestSchema.AssertColumns(result, ("active", "bool"), ("count", "long"));
    }
}
