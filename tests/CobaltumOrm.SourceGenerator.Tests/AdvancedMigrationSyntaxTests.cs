using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class AdvancedMigrationSyntaxTests
{
    [Fact]
    public void AdvancedFluentMigrationSyntaxBuildsTheCompileTimeSchema()
    {
        const string source = """
            using CobaltumOrm.Migrations;

            [Migration(1)]
            public sealed class CreateUsers : Migration
            {
                public override void Up()
                {
                    Create.Schema("app");
                    Create.Table("users")
                        .InSchema("app")
                        .IfNotExists()
                        .WithColumn("id").AsInt64().Identity().PrimaryKey("PK_users")
                        .WithColumn("name").AsAnsiString(100).NotNullable().WithColumnDescription("Display name")
                            .WithColumnAdditionalDescription("Format", "Plain text").Indexed()
                        .WithColumn("code").AsFixedLengthAnsiString(8).Unique()
                        .WithColumn("created_at").AsDateTime2().WithDefault(SystemMethods.CurrentDateTime);
                    Create.Column("note").OnTable("users").InSchema("app").AsString(200).Nullable();
                    Alter.Column("name").OnTable("users").InSchema("app").AsString(160).NotNullable();
                    Create.Index("IX_users_name").OnTable("users").InSchema("app").OnColumn("name");
                    Insert.IntoTable("users").InSchema("app").Row(new { name = "Ada", code = "00000001" });
                    Update.Table("users").InSchema("app").Set(new { name = "Grace" }).Where(new { name = "Ada" });
                    Delete.FromTable("users").InSchema("app").Where(new { name = "Grace" });
                    IfDatabase("PostgreSQL").Create.Table("postgres_only").WithColumn("id").AsInt32();
                    IfDatabase("SqlServer").Create.Table("sql_server_only").WithColumn("id").AsInt32();
                }

                public override void Down()
                {
                    Delete.Table("users").InSchema("app").IfExists();
                    Delete.Schema("app");
                }
            }
            """;

        var result = GeneratorTestHost.Run(source);
        var errors = result.AllDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.Empty(errors);
        Assert.Contains("record AppUsersRow", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.String Name", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.String? Note", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("CURRENT_TIMESTAMP", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record PostgresOnlyRow", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("record SqlServerOnlyRow", result.GeneratedText, StringComparison.Ordinal);
    }
}
