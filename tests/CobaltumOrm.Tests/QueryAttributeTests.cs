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
                candidate.GetParameters().Length == 3 &&
                candidate.GetParameters()[1].ParameterType == typeof(string));

        Assert.Contains(
            method.GetParameters()[1].GetCustomAttributesData(),
            attribute => attribute.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.StringSyntaxAttribute" &&
                         Assert.IsType<string>(attribute.ConstructorArguments[0].Value) == "sql");
    }
}

[Query("ById", "select * from widgets where id = @id")]
internal partial class Queries
{
}

[Query("Recent", "select * from widgets order by created_utc desc")]
internal partial class Queries
{
}
