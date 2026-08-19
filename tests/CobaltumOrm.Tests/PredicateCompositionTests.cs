using System;
using System.Collections.Generic;
using System.Data;
using Xunit;

namespace CobaltumOrm.Tests;

/// <summary>
/// Covers the SQL that composed predicates produce: the comparison each member writes, the
/// parentheses around combined conditions, and the numbering of the parameters they bind.
/// </summary>
public sealed class PredicateCompositionTests
{
    private const string Select = "SELECT 1 FROM test_rows";

    private static readonly TestTable Table = new TestTable();

    [Fact]
    public void CombinesTwoPredicatesWithAnd()
    {
        var sql = Table.Where(Table.Id.Equal(1).And(Table.Name.Equal("alice"))).Sql;

        Assert.Equal(
            Select + " WHERE (\"id\" = @__cobaltum_where_0 AND \"name\" = @__cobaltum_where_1)",
            sql);
    }

    [Fact]
    public void CombinesTwoPredicatesWithOr()
    {
        var sql = Table.Where(Table.Id.Equal(1) | Table.Id.Equal(2)).Sql;

        Assert.Equal(
            Select + " WHERE (\"id\" = @__cobaltum_where_0 OR \"id\" = @__cobaltum_where_1)",
            sql);
    }

    [Fact]
    public void ConditionalOperatorsCombineTheSameWayAsTheirNonConditionalForms()
    {
        Assert.Equal(
            Table.Where(Table.Id.Equal(1) & Table.Id.Equal(2)).Sql,
            Table.Where(Table.Id.Equal(1) && Table.Id.Equal(2)).Sql);
        Assert.Equal(
            Table.Where(Table.Id.Equal(1) | Table.Id.Equal(2)).Sql,
            Table.Where(Table.Id.Equal(1) || Table.Id.Equal(2)).Sql);
    }

    [Fact]
    public void ConditionalOperatorsReadBothSides()
    {
        var evaluated = 0;

        CobaltumPredicate<TestRecord> Count(int id)
        {
            evaluated++;
            return Table.Id.Equal(id);
        }

        var predicate = Count(1) && Count(2) || Count(3);

        Assert.Equal(3, evaluated);
        Assert.Equal(
            Select + " WHERE ((\"id\" = @__cobaltum_where_0 AND \"id\" = @__cobaltum_where_1)" +
            " OR \"id\" = @__cobaltum_where_2)",
            Table.Where(predicate).Sql);
    }

    [Fact]
    public void ParenthesizesNestedCombinations()
    {
        var predicate = (Table.Id.Equal(1) || Table.Id.Equal(2)) && Table.Name.Equal("alice");

        Assert.Equal(
            Select + " WHERE ((\"id\" = @__cobaltum_where_0 OR \"id\" = @__cobaltum_where_1)" +
            " AND \"name\" = @__cobaltum_where_2)",
            Table.Where(predicate).Sql);
    }

    [Fact]
    public void NumbersParametersAcrossChainedWhereClauses()
    {
        var sql = Table
            .Where(Table.Id.Equal(1) | Table.Id.Equal(2))
            .Where(Table.Name.Equal("alice"))
            .Sql;

        Assert.Equal(
            Select + " WHERE (\"id\" = @__cobaltum_where_0 OR \"id\" = @__cobaltum_where_1)" +
            " AND \"name\" = @__cobaltum_where_2",
            sql);
    }

    [Fact]
    public void SkipsParameterNumbersForNullTests()
    {
        var sql = Table
            .Where(Table.Name.IsNull().Or(Table.Name.IsNotNull()))
            .Where(Table.Id.Equal(7))
            .Sql;

        Assert.Equal(
            Select + " WHERE (\"name\" IS NULL OR \"name\" IS NOT NULL)" +
            " AND \"id\" = @__cobaltum_where_0",
            sql);
    }

    [Fact]
    public void ComparesWithNullThroughEqualAndNotEqual()
    {
        Assert.Equal(
            Select + " WHERE \"name\" IS NULL",
            Table.Where(Table.Name.Equal(null)).Sql);
        Assert.Equal(
            Select + " WHERE \"name\" IS NOT NULL",
            Table.Where(Table.Name.NotEqual(null)).Sql);
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("<>")]
    public void WritesRelationalComparisons(string comparison)
    {
        var predicate = comparison switch
        {
            "<" => Table.Id < 5,
            "<=" => Table.Id <= 5,
            ">" => Table.Id > 5,
            ">=" => Table.Id >= 5,
            _ => Table.Id.NotEqual(5),
        };

        Assert.Equal(
            Select + " WHERE \"id\" " + comparison + " @__cobaltum_where_0",
            Table.Where(predicate).Sql);
    }

    [Fact]
    public void OperatorsAndMethodsProduceTheSameSql()
    {
        Assert.Equal(
            Table.Where(Table.Id.LessThan(5)).Sql,
            Table.Where(Table.Id < 5).Sql);
        Assert.Equal(
            Table.Where(Table.Id.GreaterThanOrEqual(5)).Sql,
            Table.Where(Table.Id >= 5).Sql);
    }

    [Fact]
    public void WritesLikePredicates()
    {
        Assert.Equal(
            Select + " WHERE \"name\" LIKE @__cobaltum_where_0",
            Table.Where(Table.Name.Like("ali%")).Sql);
        Assert.Equal(
            Select + " WHERE \"name\" NOT LIKE @__cobaltum_where_0",
            Table.Where(Table.Name.NotLike("ali%")).Sql);
    }

    [Fact]
    public void WritesOneParameterPerValueInAnInList()
    {
        Assert.Equal(
            Select + " WHERE \"id\" IN (@__cobaltum_where_0, @__cobaltum_where_1, @__cobaltum_where_2)",
            Table.Where(Table.Id.In(1, 2, 3)).Sql);
        Assert.Equal(
            Select + " WHERE \"id\" NOT IN (@__cobaltum_where_0, @__cobaltum_where_1)",
            Table.Where(Table.Id.NotIn(new List<int> { 1, 2 })).Sql);
    }

    [Fact]
    public void WritesBothBoundsOfARange()
    {
        Assert.Equal(
            Select + " WHERE \"id\" BETWEEN @__cobaltum_where_0 AND @__cobaltum_where_1",
            Table.Where(Table.Id.Between(1, 9)).Sql);
        Assert.Equal(
            Select + " WHERE \"id\" NOT BETWEEN @__cobaltum_where_0 AND @__cobaltum_where_1",
            Table.Where(Table.Id.NotBetween(1, 9)).Sql);
    }

    [Fact]
    public void CombinesListsOfPredicates()
    {
        var predicates = new[] { Table.Id.Equal(1), Table.Id.Equal(2), Table.Id.Equal(3) };

        Assert.Equal(
            Select + " WHERE ((\"id\" = @__cobaltum_where_0 OR \"id\" = @__cobaltum_where_1)" +
            " OR \"id\" = @__cobaltum_where_2)",
            Table.Where(CobaltumPredicate.Any(predicates)).Sql);
        Assert.Equal(
            Select + " WHERE ((\"id\" = @__cobaltum_where_0 AND \"id\" = @__cobaltum_where_1)" +
            " AND \"id\" = @__cobaltum_where_2)",
            Table.Where(CobaltumPredicate.All(predicates)).Sql);
    }

    [Fact]
    public void AddsConditionsOnlyWhenTheConditionHolds()
    {
        var predicate = Table.Id.Equal(1)
            .AndIf(false, () => Table.Name.Equal("alice"))
            .OrIf(true, Table.Id.Equal(2));

        Assert.Equal(
            Select + " WHERE (\"id\" = @__cobaltum_where_0 OR \"id\" = @__cobaltum_where_1)",
            Table.Where(predicate).Sql);
    }

    [Fact]
    public void DeletesWithACombinedCondition()
    {
        var command = Table.DeleteWhere(Table.Id.Equal(1) & Table.Name.Like("a%"));

        Assert.Equal(
            "DELETE FROM test_rows WHERE (\"id\" = @__cobaltum_where_0" +
            " AND \"name\" LIKE @__cobaltum_where_1)",
            command.Sql);
    }

    [Fact]
    public void RejectsAnEmptyValueList()
    {
        var exception = Assert.Throws<ArgumentException>(() => Table.Id.In());

        Assert.Equal("values", exception.ParamName);
    }

    [Fact]
    public void RejectsNullInsideAValueList()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Table.Name.In(new string?[] { "alice", null }));

        Assert.Equal("values", exception.ParamName);
    }

    [Fact]
    public void RejectsNullForComparisonsThatAreNotEquality()
    {
        Assert.Throws<ArgumentNullException>(() => Table.Name.LessThan(null));
        Assert.Throws<ArgumentNullException>(() => Table.Name.Like(null));
        Assert.Throws<ArgumentNullException>(() => Table.Name.Between(null, "b"));
    }

    [Fact]
    public void RejectsAnEmptyPredicateList()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CobaltumPredicate.All<TestRecord>());

        Assert.Equal("predicates", exception.ParamName);
    }

    [Fact]
    public void RejectsNullPredicates()
    {
        Assert.Throws<ArgumentNullException>(() => Table.Id.Equal(1).And(null!));
        Assert.Throws<ArgumentNullException>(() => Table.Id.Equal(1).Or(null!));
    }

    private sealed record TestRecord(int Id, string? Name);

    private sealed class TestTable : CobaltumTable<TestRecord>
    {
        internal TestTable()
            : base(Select, static _ => new TestRecord(1, null), "DELETE FROM test_rows")
        {
            Id = new CobaltumColumn<TestRecord, int>("\"id\"", DbType.Int32);
            Name = new CobaltumColumn<TestRecord, string?>("\"name\"", DbType.String);
        }

        internal CobaltumColumn<TestRecord, int> Id { get; }

        internal CobaltumColumn<TestRecord, string?> Name { get; }
    }
}
