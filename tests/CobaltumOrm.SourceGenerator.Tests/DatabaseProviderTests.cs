using System;
using System.Linq;
using CobaltumOrm.Analysis;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class DatabaseProviderTests
{
    [Fact]
    public void PostgreSqlIsTheDefaultProviderAndKeepsPostgreSqlQuoting()
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[] { ("/db/V1__create_users.sql", "CREATE TABLE users (id integer);") });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "COB008");
        Assert.Contains("Name = \"\\\"users\\\"\";", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitPostgreSqlProviderUsesThePostgreSqlDialect()
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[] { ("/db/V1__create_users.sql", "CREATE TABLE users (id integer);") },
            databaseProvider: "PostgreSql");

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "COB008");
        Assert.Contains("Name = \"\\\"users\\\"\";", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidProviderProducesOneConfigurationDiagnostic()
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            databaseProvider: "InvalidProvider");

        var diagnostics = result.AllDiagnostics.Where(diagnostic => diagnostic.Id == "COB008").ToArray();
        Assert.Single(diagnostics);
        Assert.Contains("CobaltumOrmDatabaseProvider", diagnostics[0].GetMessage(), StringComparison.Ordinal);
        Assert.Contains("PostgreSql, MySql, Sqlite, SqlServer, or Oracle", diagnostics[0].GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POSTGRESQL", DatabaseProvider.PostgreSql)]
    [InlineData("MYSQL", DatabaseProvider.MySql)]
    [InlineData("Sqlite", DatabaseProvider.Sqlite)]
    [InlineData("SQLSERVER", DatabaseProvider.SqlServer)]
    [InlineData("oracle", DatabaseProvider.Oracle)]
    public void RegisteredProvidersResolveCaseInsensitivelyToSingletonDialects(
        string providerName,
        DatabaseProvider expectedProvider)
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            databaseProvider: providerName);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "COB008");
        Assert.True(DatabaseDialects.TryResolve(providerName, out var dialect, out var error));
        Assert.Null(error);
        Assert.Equal(expectedProvider, dialect.Provider);
        Assert.NotNull(dialect.QueryAnalyzer);
        Assert.NotNull(dialect.SchemaMigrationAnalyzer);
        Assert.NotNull(dialect.ScriptClassifier);
        Assert.NotNull(dialect.IdentifierQuoter);
        Assert.NotNull(dialect.TypeMapper);
        Assert.NotNull(dialect.MigrationSqlWriter);
        Assert.NotNull(dialect.SchemaRules);
        Assert.Same(dialect, DialectProperty(expectedProvider));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankProviderUsesPostgreSql(string? providerName)
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            databaseProvider: providerName);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "COB008");
        Assert.True(DatabaseDialects.TryResolve(providerName, out var dialect, out _));
        Assert.Same(DatabaseDialects.PostgreSqlDialect, dialect);
    }

    private static IDatabaseDialect DialectProperty(DatabaseProvider provider)
    {
        return provider switch
        {
            DatabaseProvider.PostgreSql => DatabaseDialects.PostgreSqlDialect,
            DatabaseProvider.MySql => DatabaseDialects.MySqlDialect,
            DatabaseProvider.Sqlite => DatabaseDialects.SqliteDialect,
            DatabaseProvider.SqlServer => DatabaseDialects.SqlServerDialect,
            DatabaseProvider.Oracle => DatabaseDialects.OracleDialect,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }
}
