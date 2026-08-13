using System;
using System.Data.Common;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CobaltumOrm.Migrations.PostgreSql;
using CobaltumOrm.Migrations.Tests.Fakes;
using Xunit;

namespace CobaltumOrm.Migrations.Tests;

public sealed class MigrationProjectHostTests
{
    [Fact]
    public async Task ListShowsMigrationsWithoutCreatingAConnection()
    {
        var project = new FakeMigrationProject();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            new[] { MigrationInfo.Create<CreateAuditLogMigration>(310, "Create Audit Log") },
            new[] { "list" },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(0, project.ConnectionCreateCount);
        Assert.Contains("310\treversible\tCreate Audit Log", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task StatusShowsAppliedAndPendingMigrations()
    {
        var project = new FakeMigrationProject();
        project.Connection.HistoryVersions.AddRange(new long[] { 100, 200, 310 });
        using var settings = new SettingsFile();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[] { "status", "--environment", "Staging", "--settings", settings.Path },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Contains("applied\t310\tCreate Audit Log", output.ToString());
        Assert.Contains("pending\t320\tAdd the audit actor", output.ToString());
        Assert.Contains("Environment: Staging", output.ToString());
        Assert.Equal("configured connection", project.Context!.ConnectionString);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task InvalidCommandReturnsUsageExitCode()
    {
        var project = new FakeMigrationProject();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[] { "down", "invalid" },
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Contains("non-negative target version", error.ToString());
        Assert.Equal(0, project.ConnectionCreateCount);
    }

    [Fact]
    public async Task UpAppliesAllCatalogMigrations()
    {
        var project = new FakeMigrationProject();
        using var settings = new SettingsFile();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[] { "up", "--settings", settings.Path },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(new long[] { 100, 200, 310, 320, 330, 340 }, project.Connection.HistoryVersions);
        Assert.Contains("Migrations are up to date.", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task DownRollsBackToTheRequestedVersion()
    {
        var project = new FakeMigrationProject();
        project.Connection.HistoryVersions.AddRange(new long[] { 100, 200, 310, 320, 330 });
        using var settings = new SettingsFile();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[] { "down", "320", "--settings", settings.Path },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(new long[] { 100, 200, 310, 320 }, project.Connection.HistoryVersions);
        Assert.Contains("Database is at migration version 320.", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task DryRunUpPrintsSqlAndFinalSchemaWithoutApplyingMigrations()
    {
        var project = new FakeDryRunMigrationProject();
        project.Connection.HistoryVersions.AddRange(new long[] { 100, 200, 310 });
        using var directory = new TemporaryMigrationDirectory();
        using var settings = new SettingsFile();
        var schemaPath = Path.Combine(directory.Path, "schema.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[]
            {
                "up", "--dry-run", "--write-schema", "--output", schemaPath,
                "--settings", settings.Path,
            },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(new long[] { 100, 200, 310 }, project.Connection.HistoryVersions);
        Assert.Empty(project.Connection.Transactions);
        Assert.Contains("Dry run: no database changes were made.", output.ToString());
        Assert.Contains("[up] 320 Add the audit actor", output.ToString());
        Assert.Contains("  UP-320", output.ToString());
        Assert.Contains("Final version: 340", output.ToString());
        Assert.Contains("Table: public.preview_users", output.ToString());
        Assert.Contains("  id bigint NOT NULL PRIMARY KEY", output.ToString());
        AssertSchemaJson(schemaPath);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task DryRunDownPrintsRollbackSqlWithoutRollingBackMigrations()
    {
        var project = new FakeDryRunMigrationProject();
        project.Connection.HistoryVersions.AddRange(new long[] { 100, 200, 310, 320, 330 });
        using var settings = new SettingsFile();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[] { "down", "310", "--dry-run", "--settings", settings.Path },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(new long[] { 100, 200, 310, 320, 330 }, project.Connection.HistoryVersions);
        Assert.Empty(project.Connection.Transactions);
        Assert.Contains("[down] 330 Finalize Audit", output.ToString());
        Assert.Contains("  DOWN-330", output.ToString());
        Assert.Contains("[down] 320 Add the audit actor", output.ToString());
        Assert.Contains("Final version: 310", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task SchemaWritesTheFinalSchemaWithoutCreatingAConnection()
    {
        var project = new FakeDryRunMigrationProject();
        using var directory = new TemporaryMigrationDirectory();
        var schemaPath = Path.Combine(directory.Path, "artifacts", "schema.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[] { "schema", "--output", schemaPath },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(0, project.ConnectionCreateCount);
        Assert.True(File.Exists(schemaPath));
        AssertSchemaJson(schemaPath);
        Assert.Contains("Final schema was written to", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task UpWritesTheFinalSchemaAfterApplyingMigrations()
    {
        var project = new FakeDryRunMigrationProject();
        using var directory = new TemporaryMigrationDirectory();
        using var settings = new SettingsFile();
        var schemaPath = Path.Combine(directory.Path, "schema.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await MigrationProjectHost.RunAsync(
            project,
            TestMigrationCatalog.All,
            new[] { "up", "--write-schema", "--output", schemaPath, "--settings", settings.Path },
            output,
            error);

        Assert.True(exitCode == 0, error.ToString());
        Assert.Equal(new long[] { 100, 200, 310, 320, 330, 340 }, project.Connection.HistoryVersions);
        AssertSchemaJson(schemaPath);
        Assert.Contains("Migrations are up to date.", output.ToString());
        Assert.Contains("Final schema was written to", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void MigrationSourceFilesAreResolvedFromTheFixedMigrationsDirectory()
    {
        using var directory = new TemporaryMigrationDirectory();
        directory.Write("Migrations/42_CreateUsers.cs", "[Migration(42, \"Create users\")] public sealed class M { }");
        directory.Write("Migrations/nested/V50__add_names.sql", "ALTER TABLE users ADD COLUMN name text;");

        var sources = MigrationProjectHost.FindMigrationSourceFiles(directory.Path);

        Assert.Equal(Path.Combine("Migrations", "42_CreateUsers.cs"), sources[42]);
        Assert.Equal(Path.Combine("Migrations", "nested", "V50__add_names.sql"), sources[50]);
    }

    private static void AssertSchemaJson(string schemaPath)
    {
        var schemaJson = File.ReadAllText(schemaPath);
        using var document = JsonDocument.Parse(schemaJson);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        var table = Assert.Single(root.GetProperty("tables").EnumerateArray());
        Assert.Equal("public", table.GetProperty("schema").GetString());
        Assert.Equal("preview_users", table.GetProperty("name").GetString());
        var columns = table.GetProperty("columns");
        Assert.Equal(2, columns.GetArrayLength());
        var column = columns[0];
        Assert.Equal("id", column.GetProperty("name").GetString());
        Assert.Equal("bigint", column.GetProperty("sqlType").GetString());
        Assert.False(column.GetProperty("nullable").GetBoolean());
        Assert.True(column.GetProperty("primaryKey").GetBoolean());
        Assert.False(column.GetProperty("identity").GetBoolean());
        Assert.Equal(JsonValueKind.Null, column.GetProperty("defaultExpression").ValueKind);
        var displayName = columns[1];
        Assert.Equal("表示名", displayName.GetProperty("name").GetString());
        Assert.Equal("'未設定'", displayName.GetProperty("defaultExpression").GetString());
        Assert.StartsWith("{\n  \"formatVersion\": 1,\n  \"tables\": [\n", schemaJson, StringComparison.Ordinal);
        Assert.Contains("          \"name\": \"表示名\",\n", schemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u8868", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("}\n", schemaJson, StringComparison.Ordinal);
    }

    private sealed class FakeMigrationProject : MigrationProject
    {
        internal FakeDbConnection Connection { get; } = new FakeDbConnection();

        internal int ConnectionCreateCount { get; private set; }

        internal MigrationProjectContext? Context { get; private set; }

        public override DbConnection CreateConnection(MigrationProjectContext context)
        {
            ConnectionCreateCount++;
            Context = context;
            return Connection;
        }

        public override IMigrationDatabaseAdapter CreateAdapter() => new PostgreSqlMigrationAdapter();

    }

    private sealed class FakeDryRunMigrationProject : MigrationProject
    {
        internal FakeDbConnection Connection { get; } = new FakeDbConnection();

        internal int ConnectionCreateCount { get; private set; }

        public override DbConnection CreateConnection(MigrationProjectContext context)
        {
            ConnectionCreateCount++;
            return Connection;
        }

        public override IMigrationDatabaseAdapter CreateAdapter() => new FakeDryRunAdapter();

    }

    private sealed class FakeDryRunAdapter : IMigrationDatabaseAdapter, IMigrationDryRunDatabaseAdapter
    {
        private readonly PostgreSqlMigrationAdapter _inner = new PostgreSqlMigrationAdapter();

        public IReadOnlyList<MigrationCommand> GenerateCommands(MigrationOperation operation) =>
            _inner.GenerateCommands(operation);

        public MigrationCommand CreateEnsureHistoryTableCommand(string? schemaName, string tableName) =>
            _inner.CreateEnsureHistoryTableCommand(schemaName, tableName);

        public MigrationCommand CreateReadHistoryCommand(string? schemaName, string tableName) =>
            _inner.CreateReadHistoryCommand(schemaName, tableName);

        public MigrationCommand CreateInsertHistoryCommand(
            string? schemaName,
            string tableName,
            long version,
            string description,
            System.DateTimeOffset appliedUtc) =>
            _inner.CreateInsertHistoryCommand(schemaName, tableName, version, description, appliedUtc);

        public MigrationCommand CreateDeleteHistoryCommand(string? schemaName, string tableName, long version) =>
            _inner.CreateDeleteHistoryCommand(schemaName, tableName, version);

        public MigrationCommand CreateHistoryTableExistsCommand(string? schemaName, string tableName) =>
            _inner.CreateHistoryTableExistsCommand(schemaName, tableName);

        public MigrationSchema BuildSchema(IReadOnlyList<MigrationCommand> commands) =>
            new MigrationSchema(new[]
            {
                new MigrationSchemaTable(
                    "public",
                    "preview_users",
                    new[]
                    {
                        new MigrationSchemaColumn("id", "bigint", false, true, null),
                        new MigrationSchemaColumn("表示名", "text", true, false, "'未設定'"),
                    }),
            });
    }

    private sealed class SettingsFile : System.IDisposable
    {
        public SettingsFile()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CobaltumOrm.Migrations.Tests." + System.Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(Path, "{\"ConnectionStrings\":{\"Cobaltum\":\"configured connection\"}}");
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class TemporaryMigrationDirectory : System.IDisposable
    {
        public TemporaryMigrationDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CobaltumOrm.Migrations.Host.Tests." + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string relativePath, string contents)
        {
            var path = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
