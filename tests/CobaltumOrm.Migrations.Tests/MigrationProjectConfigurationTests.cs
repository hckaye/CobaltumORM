using System;
using System.Data.Common;
using System.IO;
using CobaltumOrm.Migrations.Tests.Fakes;
using Xunit;

namespace CobaltumOrm.Migrations.Tests;

public sealed class MigrationProjectConfigurationTests
{
    [Fact]
    public void EnvironmentFileOverridesBaseSettings()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "appsettings.json"),
            "{\"ConnectionStrings\":{\"Cobaltum\":\"base\"},\"Cobaltum\":{\"Label\":\"base\"}}");
        File.WriteAllText(
            Path.Combine(directory.Path, "appsettings.Staging.json"),
            "{\"ConnectionStrings\":{\"Cobaltum\":\"staging\"},\"Cobaltum\":{\"Label\":\"staging\"}}");

        using var context = MigrationProjectConfiguration.Load(
            typeof(MigrationProjectConfigurationTests).Assembly,
            "Staging",
            null,
            directory.Path,
            includeEnvironmentVariables: false);

        Assert.Equal("Staging", context.EnvironmentName);
        Assert.Equal("staging", context.ConnectionString);
        Assert.Equal("staging", context.Configuration["Cobaltum:Label"]);
    }

    [Fact]
    public void ExplicitSettingsFileReplacesDefaultJsonFiles()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "appsettings.json"),
            "{\"ConnectionStrings\":{\"Cobaltum\":\"base\"},\"Cobaltum\":{\"Label\":\"base\"}}");
        var selected = Path.Combine(directory.Path, "selected.json");
        File.WriteAllText(
            selected,
            "{\"ConnectionStrings\":{\"Cobaltum\":\"selected\"},\"Cobaltum\":{\"Label\":\"selected\"}}");

        using var context = MigrationProjectConfiguration.Load(
            typeof(MigrationProjectConfigurationTests).Assembly,
            "Production",
            selected,
            directory.Path,
            includeEnvironmentVariables: false);

        Assert.Equal(selected, context.SettingsPath);
        Assert.Equal("selected", context.ConnectionString);
        Assert.Equal("selected", context.Configuration["Cobaltum:Label"]);
    }

    [Fact]
    public void ConnectionUsesTheMigrationProjectConfigurationAndFactory()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "appsettings.json"),
            "{\"ConnectionStrings\":{\"Cobaltum\":\"configured connection\"}}");
        var project = new RecordingMigrationProject();

        using var database = MigrationProjectConnection.Create(
            project,
            requestedEnvironment: "Staging",
            requestedSettingsPath: null,
            contentRootPath: directory.Path,
            includeEnvironmentVariables: false);

        Assert.Same(project.Connection, database.Connection);
        Assert.NotNull(project.Context);
        Assert.Equal("configured connection", project.Context.ConnectionString);
        Assert.Equal("Staging", project.Context.EnvironmentName);
    }

    private sealed class RecordingMigrationProject : MigrationProject
    {
        internal FakeDbConnection Connection { get; } = new FakeDbConnection();

        internal MigrationProjectContext? Context { get; private set; }

        public override DbConnection CreateConnection(MigrationProjectContext context)
        {
            Context = context;
            return Connection;
        }

        public override IMigrationDatabaseAdapter CreateAdapter() =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CobaltumOrm.Configuration.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
