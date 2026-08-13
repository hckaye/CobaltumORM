using System;
using System.Linq;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class ProviderIntegrationTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public void SourceGeneratorUsesTheConfiguredProviderForMigrationsSchemaAndQueries(
        ProviderCase provider)
    {
        var schemaName = provider.Name == "Sqlite" ? string.Empty : ".InSchema(\"app\")";
        var qualifiedTable = provider.Name == "Sqlite"
            ? provider.Quote("users")
            : provider.Quote("app") + "." + provider.Quote("users");
        var query = "SELECT " + provider.Quote("id") + ", " + provider.Quote("payload") +
            " FROM " + qualifiedTable + " WHERE " + provider.Quote("id") + " = " + provider.Parameter;
        var queryLiteral = CSharpLiteral(query);
        var source = $$"""
            using System.Data.Common;
            using CobaltumOrm;
            using CobaltumOrm.Migrations;

            [Migration(1, "create users")]
            public sealed class CreateUsersMigration : Migration
            {
                public override void Up()
                {
                    Create.Table("users"){{schemaName}}
                        .WithColumn("id").{{provider.IdMigrationMethod}}.PrimaryKey().Identity()
                        .WithColumn("payload").AsJsonb().Nullable()
                        .WithColumn("amount").AsDecimal(12, 2).NotNullable();
                }

                public override void Down()
                {
                }
            }

            [Query("FindUser", {{queryLiteral}})]
            public static partial class UserQueries
            {
            }

            public static class Consumer
            {
                public static object Read(DbConnection connection, {{provider.QueryParameterType}} id) =>
                    UserQueries.FindUserAsync(connection, id);
            }
            """;

        var result = GeneratorTestHost.Run(source, databaseProvider: provider.Name);
        AssertNoErrors(result);
        Assert.NotEmpty(result.RunResult.Results.SelectMany(run => run.GeneratedSources));
        var rowName = provider.Name == "Sqlite" ? "UsersRow" : "AppUsersRow";
        Assert.Contains("record " + rowName, result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("Name = " + CSharpLiteral(provider.QualifiedTable) + ";", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains(
            "CobaltumColumn(\"payload\", " + CSharpLiteral(provider.PayloadSqlType),
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CobaltumColumn(\"id\", " + CSharpLiteral(provider.IdSqlType),
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.Contains(queryLiteral, result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains(provider.Parameter, result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains(provider.ExpectedDbType, result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains(", '" + provider.Parameter[0] + "');", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains(
            (provider.QueryParameterType == "long"
                ? "global::System.Int64? id"
                : "global::System.Int32? id"),
            result.GeneratedText,
            StringComparison.Ordinal);

        if (provider.PayloadClrType != null)
        {
            Assert.Contains(provider.PayloadClrType, result.GeneratedText, StringComparison.Ordinal);
        }

        result.EmitAndLoad();
    }

    [Theory]
    [InlineData("Create.Table(\"users\").InSchema(\"main\").WithColumn(\"id\").AsInt32();")]
    [InlineData("Alter.Table(\"users\").InSchema(\"main\").AddColumn(\"id\").AsInt32();")]
    [InlineData("Delete.Table(\"users\").InSchema(\"main\");")]
    [InlineData("Rename.Table(\"users\").InSchema(\"main\").To(\"accounts\");")]
    public void SqliteSchemaViolationIsReportedAtInSchemaWithoutThrowing(string operation)
    {
        var source = $$"""
            using CobaltumOrm.Migrations;

            [Migration(1)]
            public sealed class CreateUsersMigration : Migration
            {
                public override void Up()
                {
                    {{operation}}
                }

                public override void Down()
                {
                }
            }
            """;

        var result = GeneratorTestHost.Run(source, databaseProvider: "sqlite");
        AssertMigrationDiagnostic(result, ".InSchema(\"main\")", "does not support named schemas");
        var diagnostic = result.AllDiagnostics.Single(item => item.Id == "COB001");

        Assert.Contains("Sqlite", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("does not support named schemas", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void OracleStringLengthLimitIsReportedAtTheAsStringCall()
    {
        var result = GeneratorTestHost.Run(
            """
            using CobaltumOrm.Migrations;

            [Migration(1)]
            public sealed class CreateUsersMigration : Migration
            {
                public override void Up()
                {
                    Create.Table("users").WithColumn("name").AsString(40000);
                }

                public override void Down()
                {
                }
            }
            """,
            databaseProvider: "Oracle");

        AssertMigrationDiagnostic(result, ".AsString(40000)", "32767");
    }

    [Fact]
    public void SqliteNonInt64IdentityIsReportedAtTheIdentityCall()
    {
        var result = GeneratorTestHost.Run(
            """
            using CobaltumOrm.Migrations;

            [Migration(1)]
            public sealed class CreateUsersMigration : Migration
            {
                public override void Up()
                {
                    Create.Table("users").WithColumn("id").AsInt32().PrimaryKey().Identity();
                }

                public override void Down()
                {
                }
            }
            """,
            databaseProvider: "Sqlite");

        AssertMigrationDiagnostic(result, ".Identity()", "Int64 primary key");
    }

    [Theory]
    [InlineData("Alter.Table(\"users\").AddColumn(\"id\").AsInt64().PrimaryKey();", ".PrimaryKey()")]
    [InlineData("Alter.Table(\"users\").AddColumn(\"id\").AsInt64().Identity();", ".Identity()")]
    public void SqliteAddedColumnConstraintsAreReportedWithoutThrowing(
        string operation,
        string userCall)
    {
        var source = $$"""
            using CobaltumOrm.Migrations;

            [Migration(1)]
            public sealed class AddUsersColumnMigration : Migration
            {
                public override void Up()
                {
                    {{operation}}
                }

                public override void Down()
                {
                }
            }
            """;

        var result = GeneratorTestHost.Run(source, databaseProvider: "Sqlite");

        AssertMigrationDiagnostic(result, userCall, "cannot add primary-key or identity");
    }

    public static TheoryData<ProviderCase> Providers => new()
    {
        new ProviderCase("PostgreSql", "\"app\".\"users\"", "AsInt32()", "int", "@id", "integer", "jsonb", "DbType.Int32", "global::System.String?"),
        new ProviderCase("MySql", "`app`.`users`", "AsInt32()", "int", "@id", "int", "json", "DbType.Int32", "global::System.String?"),
        new ProviderCase("Sqlite", "\"users\"", "AsInt64()", "long", "@id", "INTEGER", "BLOB", "DbType.Int64", "global::System.Byte[]?"),
        new ProviderCase("SqlServer", "[app].[users]", "AsInt32()", "int", "@id", "int", "nvarchar(max)", "DbType.Int32", "global::System.String?"),
        new ProviderCase("Oracle", "\"app\".\"users\"", "AsInt32()", "int", ":id", "NUMBER(10,0)", "BLOB", "DbType.Int32", "global::System.Byte[]?"),
    };

    private static void AssertNoErrors(GeneratorTestResult result)
    {
        var errors = result.AllDiagnostics
            .Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(item => item.ToString())));
    }

    private static void AssertMigrationDiagnostic(
        GeneratorTestResult result,
        string userCall,
        string message)
    {
        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB001");
        Assert.Contains(message, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal("Consumer.cs", diagnostic.Location.GetLineSpan().Path);
        var sourceText = result.Compilation.SyntaxTrees.Single(tree => tree.FilePath == "Consumer.cs").GetText().ToString();
        Assert.Contains(
            userCall,
            sourceText.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.AllDiagnostics,
            item => item.Id == "CS8785" ||
                item.GetMessage().Contains("Unhandled", StringComparison.OrdinalIgnoreCase) ||
                item.GetMessage().Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    private static string CSharpLiteral(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public sealed class ProviderCase
    {
        public ProviderCase(
            string name,
            string qualifiedTable,
            string idMigrationMethod,
            string queryParameterType,
            string parameter,
            string idSqlType,
            string payloadSqlType,
            string expectedDbType,
            string payloadClrType)
        {
            Name = name;
            QualifiedTable = qualifiedTable;
            IdMigrationMethod = idMigrationMethod;
            QueryParameterType = queryParameterType;
            Parameter = parameter;
            IdSqlType = idSqlType;
            PayloadSqlType = payloadSqlType;
            ExpectedDbType = expectedDbType;
            PayloadClrType = payloadClrType;
        }

        public string Name { get; }
        public string QualifiedTable { get; }
        public string IdMigrationMethod { get; }
        public string QueryParameterType { get; }
        public string Parameter { get; }
        public string IdSqlType { get; }
        public string PayloadSqlType { get; }
        public string ExpectedDbType { get; }
        public string? PayloadClrType { get; }

        public string Quote(string value)
        {
            return Name switch
            {
                "MySql" => "`" + value + "`",
                "SqlServer" => "[" + value + "]",
                _ => "\"" + value + "\"",
            };
        }

        public override string ToString() => Name;
    }
}
