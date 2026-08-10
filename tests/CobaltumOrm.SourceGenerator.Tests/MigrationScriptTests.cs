using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class MigrationScriptTests
{
    [Fact]
    public void GeneratesAnEmptyCatalogBeforeTheFirstMigrationIsAdded()
    {
        var generation = GeneratorTestHost.Run("namespace TestApp; public sealed class Empty { }");

        AssertNoWarningsOrErrors(generation);
        var assembly = generation.EmitAndLoad();
        var catalogType = assembly.GetType("TestApp.Generated.CobaltumMigrationCatalog", throwOnError: true)!;
        var migrationCatalog = Assert.IsAssignableFrom<IReadOnlyList<MigrationInfo>>(
            catalogType.GetProperty("All")!.GetValue(null));
        Assert.Empty(migrationCatalog);
    }

    [Fact]
    public async Task FlywayScriptMixesTableDdlAndSchemaNeutralStatementsWithoutChangingRuntimeSql()
    {
        const string flywaySql = """
            -- A leading comment contains a semicolon ; that is not a boundary.
            CREATE TABLE "audit;events" (
                id integer PRIMARY KEY,
                "message;text" text NULL
            );
            INSERT INTO "audit;events" (id, "message;text") VALUES
                (1, 'a semicolon; and -- inside a string'),
                (2, $seed$dollar; quote /* not a comment */$seed$),
                (3, E'escaped\'; semicolon');
            /* Nested comments may contain ; and /* another ; */ still continue. */
            UPDATE "audit;events" SET "message;text" = 'updated; value' WHERE id = 1;
            DELETE FROM "audit;events" WHERE id = -1;
            CREATE UNIQUE INDEX audit_events_id_idx ON "audit;events" (id);
            COMMENT ON COLUMN "audit;events"."message;text" IS 'comment; value';
            """;
        var generation = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[] { ("/db/V1__audit_events.sql", flywaySql) });

        AssertNoWarningsOrErrors(generation);
        Assert.Contains("record AuditEventsRow", generation.GeneratedText, StringComparison.Ordinal);

        var assembly = generation.EmitAndLoad();
        var catalogType = assembly.GetType("TestApp.Generated.CobaltumMigrationCatalog", throwOnError: true)!;
        var migrationCatalog = Assert.IsAssignableFrom<IReadOnlyList<MigrationInfo>>(
            catalogType.GetProperty("All")!.GetValue(null));
        _ = Assert.Single(migrationCatalog);
        var connection = new QueryFakeDbConnection();
        var runner = new MigrationRunner(new CapturingMigrationAdapter());

        await runner.MigrateUpAsync(connection, migrationCatalog);

        Assert.Contains(connection.Commands, command => command.CommandText == flywaySql);
    }

    [Fact]
    public void CSharpExecuteSqlAllowsSchemaNeutralUpdate()
    {
        const string source = """
            using CobaltumOrm.Migrations;
            [Migration(1)]
            public sealed class SeedUsers : Migration
            {
                public override void Up()
                {
                    Create.Table("users")
                        .WithColumn("id").AsInt32().PrimaryKey()
                        .WithColumn("name").AsText().Nullable();
                    Execute.Sql("UPDATE users SET name = 'value; -- text' WHERE id = 1; /* trailing ; */");
                }

                public override void Down() => Delete.Table("users");
            }
            """;

        var generation = GeneratorTestHost.Run(source);

        AssertNoWarningsOrErrors(generation);
        Assert.Contains("record UsersRow", generation.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedShapeChangingStatementIsDiagnosedAtItsSqlLocation()
    {
        const string sql = "CREATE TABLE users (id integer);\nCREATE VIEW user_ids AS SELECT id FROM users;";
        var generation = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[] { ("/db/V1__unsupported_view.sql", sql) });

        var diagnostic = Assert.Single(generation.AllDiagnostics, item => item.Id == "COB003");
        Assert.Contains("may change the queryable schema", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal("/db/V1__unsupported_view.sql", diagnostic.Location.GetLineSpan().Path);
        Assert.Equal("CREATE VIEW user_ids AS SELECT id FROM users;", sql.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length).Trim());
    }

    private static void AssertNoWarningsOrErrors(GeneratorTestResult result)
    {
        var problems = result.AllDiagnostics
            .Where(item => item.Severity == DiagnosticSeverity.Error || item.Severity == DiagnosticSeverity.Warning)
            .ToArray();
        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems.Select(item => item.ToString())));
    }
}

internal sealed class CapturingMigrationAdapter : IMigrationDatabaseAdapter
{
    public IReadOnlyList<MigrationCommand> GenerateCommands(MigrationOperation operation)
    {
        return operation is ExecuteSqlOperation executeSql
            ? new[] { new MigrationCommand(executeSql.Sql) }
            : new[] { new MigrationCommand("OPERATION " + operation.GetType().Name) };
    }

    public MigrationCommand CreateEnsureHistoryTableCommand(string? schemaName, string tableName) =>
        new MigrationCommand("ENSURE HISTORY");

    public MigrationCommand CreateReadHistoryCommand(string? schemaName, string tableName) =>
        new MigrationCommand("READ HISTORY");

    public MigrationCommand CreateInsertHistoryCommand(
        string? schemaName,
        string tableName,
        long version,
        string description,
        DateTimeOffset appliedUtc) =>
        new MigrationCommand("INSERT HISTORY " + version);

    public MigrationCommand CreateDeleteHistoryCommand(string? schemaName, string tableName, long version) =>
        new MigrationCommand("DELETE HISTORY " + version);
}
