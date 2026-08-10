using System;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.Analysis.Tests;

public sealed class DatabaseDialectTests
{
    [Fact]
    public void PostgreSqlDialectExposesAllAnalysisServices()
    {
        Assert.True(DatabaseDialects.TryResolve(null, out var dialect, out var error));
        Assert.Null(error);
        Assert.Equal(DatabaseProvider.PostgreSql, dialect.Provider);
        Assert.True(dialect.SchemaRules.SupportsSchemas);
        Assert.Equal("\"accounts\".\"users\"", dialect.IdentifierQuoter.QuoteQualifiedName("accounts", "users"));
        Assert.True(dialect.TypeMapper.TryMap("jsonb", out var kind));
        Assert.Equal(SqlValueKind.JsonBinary, kind);
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    public void PostgreSqlProviderNamesAreCaseInsensitive(string providerName)
    {
        Assert.True(DatabaseDialects.TryResolve(providerName, out var dialect, out _));
        Assert.Equal(DatabaseProvider.PostgreSql, dialect.Provider);
    }

    [Theory]
    [InlineData("MySql")]
    [InlineData("Sqlite")]
    [InlineData("SQLSERVER")]
    [InlineData("oracle")]
    public void RegisteredProviderNamesResolveToAnalysisServices(string providerName)
    {
        Assert.True(DatabaseDialects.TryResolve(providerName, out var dialect, out var error));
        Assert.Null(error);
        Assert.NotNull(dialect.QueryAnalyzer);
        Assert.NotNull(dialect.SchemaMigrationAnalyzer);
        Assert.NotNull(dialect.ScriptClassifier);
        Assert.NotNull(dialect.IdentifierQuoter);
        Assert.NotNull(dialect.TypeMapper);
        Assert.NotNull(dialect.MigrationSqlWriter);
        Assert.NotNull(dialect.SchemaRules);
    }
}
