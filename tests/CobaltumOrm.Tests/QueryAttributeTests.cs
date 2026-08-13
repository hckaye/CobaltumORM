using System;
using System.Linq;
using Xunit;

namespace CobaltumOrm.Tests;

public sealed class QueryAttributeTests
{
    [Fact]
    public void PartialClassCanDeclareMultipleNamedQueries()
    {
        var attributes = typeof(Queries)
            .GetCustomAttributes(typeof(QueryAttribute), false)
            .Cast<QueryAttribute>()
            .OrderBy(attribute => attribute.Name)
            .ToArray();

        Assert.Collection(
            attributes,
            first =>
            {
                Assert.Equal("ById", first.Name);
                Assert.Equal("select * from widgets where id = @id", first.Sql);
            },
            second =>
            {
                Assert.Equal("Recent", second.Name);
                Assert.Equal("select * from widgets order by created_utc desc", second.Sql);
            });
    }

    [Theory]
    [InlineData(null, "select 1")]
    [InlineData("", "select 1")]
    [InlineData("Query", null)]
    [InlineData("Query", "  ")]
    public void ConstructorRejectsMissingNameOrSql(string? name, string? sql)
    {
        Assert.Throws<ArgumentException>(() => new QueryAttribute(name!, sql!));
    }

    [Fact]
    public void RawQuerySqlParameterCarriesEditorSyntaxMetadata()
    {
        var method = typeof(CobaltumQueryExtensions)
            .GetMethods()
            .Single(candidate =>
                candidate.Name == "Query" &&
                !candidate.IsGenericMethod &&
                candidate.GetParameters().Length == 3 &&
                candidate.GetParameters()[1].ParameterType == typeof(string));

        Assert.Contains(
            method.GetParameters()[1].GetCustomAttributesData(),
            attribute => attribute.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.StringSyntaxAttribute" &&
                         Assert.IsType<string>(attribute.ConstructorArguments[0].Value) == "sql");
    }

    [Fact]
    public void GenericAttributeRetainsNameSqlAndResultType()
    {
        var attribute = Assert.Single(typeof(GenericQueries)
            .GetCustomAttributes(typeof(QueryAttribute<WidgetResult>), false)
            .Cast<QueryAttribute<WidgetResult>>());

        Assert.Equal("All", attribute.Name);
        Assert.Equal("select id, name from widgets", attribute.Sql);
    }

    [Fact]
    public void ResultColumnNameCanBeOmitted()
    {
        Assert.Null(new ResultColumnAttribute().Name);
        Assert.Equal("external_id", new ResultColumnAttribute("external_id").Name);
        Assert.Throws<ArgumentException>(() => new ResultColumnAttribute(" "));
    }
}

[Query("ById", "select * from widgets where id = @id")]
internal partial class Queries
{
}

internal sealed record WidgetResult(int Id, string Name);

[Query<WidgetResult>("All", "select id, name from widgets")]
internal partial class GenericQueries
{
}

[Query("Recent", "select * from widgets order by created_utc desc")]
internal partial class Queries
{
}
