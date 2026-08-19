using System.Diagnostics;
using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class ToolApplicationTests
{
    [Fact]
    public async Task InitCreatesAReadyToConfigureMigrationProject()
    {
        using var directory = new TemporaryDirectory();
        var processRunner = new RecordingProcessRunner();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, processRunner, directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", "MyApp.Database", "--framework", "net10.0" },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var projectDirectory = Path.Combine(directory.Path, "MyApp.Database");
        var projectPath = Path.Combine(projectDirectory, "MyApp.Database.csproj");
        Assert.True(File.Exists(projectPath));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "appsettings.json")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "README.md")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "Migrations", "README.md")));
        Assert.False(Directory.Exists(Path.Combine(projectDirectory, ".template.config")));

        var project = File.ReadAllText(projectPath);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project);
        Assert.Contains("<RootNamespace>MyApp.Database</RootNamespace>", project);
        Assert.Contains("<CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>", project);
        Assert.Contains("<CobaltumOrmDatabaseProvider>PostgreSql</CobaltumOrmDatabaseProvider>", project);
        Assert.Contains("<CompilerVisibleProperty Include=\"CobaltumOrmDatabaseProvider\" />", project);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm\" Version=\"1.0.1\" />", project);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.Migrations.PostgreSql\"", project);
        Assert.Contains("<PackageReference Include=\"Npgsql\" Version=\"10.0.3\" />", project);
        Assert.Contains("CopyToOutputDirectory=\"PreserveNewest\"", project);
        Assert.Contains("CobaltumOrm.Migrations/$(AssemblyName)", project);
        Assert.DoesNotContain("5b04a918-37d5-4fbf-b1d2-a58081ff96d8", project);
        var program = File.ReadAllText(Path.Combine(projectDirectory, "Program.cs"));
        Assert.Contains("namespace MyApp.Database;", program);
        Assert.Contains("using CobaltumOrm.Migrations.PostgreSql;", program);
        Assert.Contains("using Npgsql;", program);
        Assert.Contains("new NpgsqlConnection", program);
        Assert.Contains("new PostgreSqlMigrationAdapter", program);
        Assert.Contains("Generated.CobaltumMigrationCatalog.All", program);
        Assert.DoesNotContain("#if", program);
        var readme = File.ReadAllText(Path.Combine(projectDirectory, "README.md"));
        Assert.Contains("../MyApp.Database/MyApp.Database.csproj", readme);
        Assert.Contains("migrations schema", readme);
        Assert.Contains(projectPath, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.Null(processRunner.StartInfo);
    }

    [Theory]
    [MemberData(nameof(ProviderCases))]
    public async Task InitGeneratesTheSelectedProvider(
        string provider,
        string adapterPackage,
        string driverPackage,
        string driverNamespace,
        string connectionType,
        string adapterType)
    {
        using var directory = new TemporaryDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner(), directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", "MyApp.Database", "--provider", provider },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var projectDirectory = Path.Combine(directory.Path, "MyApp.Database");
        var project = File.ReadAllText(Path.Combine(projectDirectory, "MyApp.Database.csproj"));
        var program = File.ReadAllText(Path.Combine(projectDirectory, "Program.cs"));
        Assert.Contains($"<CobaltumOrmDatabaseProvider>{provider}</CobaltumOrmDatabaseProvider>", project);
        Assert.Contains("<CompilerVisibleProperty Include=\"CobaltumOrmDatabaseProvider\" />", project);
        Assert.Contains($"<PackageReference Include=\"{adapterPackage}\"", project);
        Assert.Contains($"<PackageReference Include=\"{driverPackage}\"", project);
        Assert.Contains($"using {driverNamespace};", program);
        Assert.Contains($"new {connectionType}", program);
        Assert.Contains($"new {adapterType}", program);
        Assert.DoesNotContain("#if", project);
        Assert.DoesNotContain("#if", program);

        foreach (var otherProvider in ProviderCases.Select(item => item[0]).Cast<string>())
        {
            if (string.Equals(otherProvider, provider, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain($"CobaltumOrm.Migrations.{otherProvider}", project);
            Assert.DoesNotContain($"CobaltumOrm.Migrations.{otherProvider}", program);
        }

        Assert.Equal(string.Empty, error.ToString());
    }

    public static IEnumerable<object[]> ProviderCases => new[]
    {
        new object[]
        {
            "PostgreSql", "CobaltumOrm.Migrations.PostgreSql", "Npgsql",
            "CobaltumOrm.Migrations.PostgreSql", "NpgsqlConnection", "PostgreSqlMigrationAdapter",
        },
        new object[]
        {
            "MySql", "CobaltumOrm.Migrations.MySql", "MySqlConnector",
            "CobaltumOrm.Migrations.MySql", "MySqlConnection", "MySqlMigrationAdapter",
        },
        new object[]
        {
            "Sqlite", "CobaltumOrm.Migrations.Sqlite", "Microsoft.Data.Sqlite",
            "Microsoft.Data.Sqlite", "SqliteConnection", "SqliteMigrationAdapter",
        },
        new object[]
        {
            "SqlServer", "CobaltumOrm.Migrations.SqlServer", "Microsoft.Data.SqlClient",
            "Microsoft.Data.SqlClient", "SqlConnection", "SqlServerMigrationAdapter",
        },
        new object[]
        {
            "Oracle", "CobaltumOrm.Migrations.Oracle", "Oracle.ManagedDataAccess.Core",
            "Oracle.ManagedDataAccess.Client", "OracleConnection", "OracleMigrationAdapter",
        },
    };

    [Fact]
    public async Task InitAcceptsProviderNamesWithoutCaseSensitivity()
    {
        using var directory = new TemporaryDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner(), directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", "MyApp.Database", "--provider", "sQLsErVeR" },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var project = File.ReadAllText(Path.Combine(directory.Path, "MyApp.Database", "MyApp.Database.csproj"));
        Assert.Contains("<CobaltumOrmDatabaseProvider>SqlServer</CobaltumOrmDatabaseProvider>", project);
        Assert.Contains("CobaltumOrm.Migrations.SqlServer", project);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task InitRejectsAnUnsupportedProvider()
    {
        using var directory = new TemporaryDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner(), directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", "MyApp.Database", "--provider", "MongoDb" },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unsupported provider 'MongoDb'", error.ToString());
        Assert.Contains("PostgreSql, MySql, Sqlite, SqlServer, Oracle", error.ToString());
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "MyApp.Database")));
    }

    [Fact]
    public async Task HelpListsTheProviderOptionAndAllSupportedProviders()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner());

        var exitCode = await application.RunAsync(
            new[] { "migrations", "--help" },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("--provider <name>", output.ToString());
        Assert.Contains("default: PostgreSql", output.ToString());
        Assert.Contains("migrations schema [--output <path>]", output.ToString());
        Assert.Contains("--write-schema", output.ToString());
        Assert.Contains("schema.generated.json", output.ToString());
        foreach (var provider in ProviderCases.Select(item => item[0]).Cast<string>())
        {
            Assert.Contains(provider, output.ToString());
        }

        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task InitSupportsAnExplicitEmptyOutputDirectory()
    {
        using var directory = new TemporaryDirectory();
        var target = Directory.CreateDirectory(Path.Combine(directory.Path, "database"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner(), directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", "MyApp.Database", "--output", target.FullName },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(target.FullName, "MyApp.Database.csproj")));
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", File.ReadAllText(Path.Combine(target.FullName, "MyApp.Database.csproj")));
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task InitDoesNotWriteIntoANonEmptyDirectory()
    {
        using var directory = new TemporaryDirectory();
        var target = Directory.CreateDirectory(Path.Combine(directory.Path, "database"));
        var existingFile = Path.Combine(target.FullName, "keep.txt");
        File.WriteAllText(existingFile, "keep");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner(), directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", "MyApp.Database", "--output", target.FullName },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("is not empty", error.ToString());
        Assert.Equal("keep", File.ReadAllText(existingFile));
        Assert.False(File.Exists(Path.Combine(target.FullName, "MyApp.Database.csproj")));
    }

    [Theory]
    [InlineData("My-App.Database")]
    [InlineData("MyApp.class")]
    [InlineData("MyApp..Database")]
    public async Task InitRejectsNamesThatCannotBeUsedAsCSharpNamespaces(string projectName)
    {
        using var directory = new TemporaryDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner(), directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", projectName },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("dot-separated C# namespace", error.ToString());
    }

    [Fact]
    public async Task InitRejectsUnsupportedTargetFrameworks()
    {
        using var directory = new TemporaryDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner(), directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "init", "MyApp.Database", "--framework", "net7.0" },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Use net8.0, net9.0, or net10.0", error.ToString());
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "MyApp.Database")));
    }

    [Fact]
    public async Task AddCreatesMigrationInTheFixedProjectDirectory()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.WriteProject();
        var processRunner = new RecordingProcessRunner();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, processRunner, directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "add", "create users", "--version", "42", "--project", project },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var migration = Path.Combine(directory.Path, "Migrations", "42_CreateUsersMigration.cs");
        Assert.True(File.Exists(migration));
        var source = File.ReadAllText(migration);
        Assert.Contains("namespace Example.Database.Migrations;", source);
        Assert.Contains("[Migration(42, \"create users\")]", source);
        Assert.Contains("public sealed class CreateUsersMigration : Migration", source);
        Assert.Null(processRunner.StartInfo);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task AddRequiresANewerVersionThanExistingMigrations()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.WriteProject();
        var migrations = Directory.CreateDirectory(Path.Combine(directory.Path, "Migrations"));
        File.WriteAllText(
            Path.Combine(migrations.FullName, "Existing.cs"),
            "[Migration(42, \"existing\")] public sealed class ExistingMigration { }");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner());

        var exitCode = await application.RunAsync(
            new[] { "migrations", "add", "older", "--version", "41", "--project", project },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must be greater than the current latest version 42", error.ToString());
        Assert.False(File.Exists(Path.Combine(migrations.FullName, "41_OlderMigration.cs")));
    }

    [Fact]
    public async Task AddDetectsVersionsUsedByFlywayCompatibleSql()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.WriteProject();
        var migrations = Directory.CreateDirectory(Path.Combine(directory.Path, "Migrations"));
        File.WriteAllText(Path.Combine(migrations.FullName, "V50__create_accounts.sql"), "SELECT 1;");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner());

        var exitCode = await application.RunAsync(
            new[] { "migrations", "add", "duplicate", "--version", "50", "--project", project },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Migration version 50 already exists", error.ToString());
    }

    [Fact]
    public async Task UpRunsTheMigrationProjectWithSelectedBuildOptions()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.WriteProject();
        var processRunner = new RecordingProcessRunner { ExitCode = 7 };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, processRunner, directory.Path);

        var exitCode = await application.RunAsync(
            new[]
            {
                "migrations", "up", "--project", directory.Path,
                "--configuration", "Release", "--framework", "net10.0", "--no-build",
                "--environment", "Staging", "--settings", "settings/staging.json",
                "--dry-run", "--write-schema",
            },
            CancellationToken.None);

        Assert.Equal(7, exitCode);
        var startInfo = Assert.IsType<ProcessStartInfo>(processRunner.StartInfo);
        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(directory.Path, startInfo.WorkingDirectory);
        Assert.Equal(
            new[]
            {
                "run", "--project", project, "--configuration", "Release", "--no-launch-profile",
                "--no-build", "--framework", "net10.0", "--", "up",
                "--output", Path.Combine(directory.Path, "schema.generated.json"), "--environment", "Staging",
                "--settings", Path.Combine(directory.Path, "settings", "staging.json"),
                "--dry-run", "--write-schema",
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public async Task DryRunIsRejectedForCommandsThatDoNotChangeMigrations()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.WriteProject();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner());

        var exitCode = await application.RunAsync(
            new[] { "migrations", "status", "--project", project, "--dry-run" },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("--dry-run can only be used", error.ToString());
    }

    [Fact]
    public async Task SchemaRunsTheMigrationProjectWithAnAbsoluteOutputPath()
    {
        using var directory = new TemporaryDirectory();
        var project = directory.WriteProject();
        var processRunner = new RecordingProcessRunner();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, processRunner, directory.Path);

        var exitCode = await application.RunAsync(
            new[]
            {
                "migrations", "schema", "--project", project,
                "--output", "artifacts/schema.json",
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var startInfo = Assert.IsType<ProcessStartInfo>(processRunner.StartInfo);
        Assert.Equal(
            new[]
            {
                "run", "--project", project, "--configuration", "Debug", "--no-launch-profile",
                "--", "schema", "--output", Path.Combine(directory.Path, "artifacts", "schema.json"),
            },
            startInfo.ArgumentList);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task SchemaDefaultsToTheDiscoveredMigrationProjectDirectory()
    {
        using var directory = new TemporaryDirectory();
        var projectDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "src", "Database"));
        var project = directory.WriteProject(projectDirectory.FullName);
        var processRunner = new RecordingProcessRunner();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, processRunner, directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "schema" },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var startInfo = Assert.IsType<ProcessStartInfo>(processRunner.StartInfo);
        Assert.Equal(projectDirectory.FullName, startInfo.WorkingDirectory);
        Assert.Equal(
            new[]
            {
                "run", "--project", project, "--configuration", "Debug", "--no-launch-profile",
                "--", "schema", "--output", Path.Combine(projectDirectory.FullName, "schema.generated.json"),
            },
            startInfo.ArgumentList);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ProjectMustUseTheFixedExecutableDefinition()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "Broken.csproj");
        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>Example</RootNamespace></PropertyGroup></Project>");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner());

        var exitCode = await application.RunAsync(
            new[] { "migrations", "list", "--project", project },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("must set OutputType to Exe", error.ToString());
    }

    [Fact]
    public async Task OmittedProjectIsDiscoveredRecursively()
    {
        using var directory = new TemporaryDirectory();
        var nested = Directory.CreateDirectory(Path.Combine(directory.Path, "src", "Database"));
        var project = directory.WriteProject(nested.FullName);
        var processRunner = new RecordingProcessRunner();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, processRunner, directory.Path);

        var exitCode = await application.RunAsync(
            new[] { "migrations", "list" },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(project, processRunner.StartInfo!.ArgumentList);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public int ExitCode { get; set; }

        public ProcessStartInfo? StartInfo { get; private set; }

        public Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            StartInfo = startInfo;
            return Task.FromResult(ExitCode);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CobaltumOrm.Tool.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteProject(string? directory = null)
        {
            var project = System.IO.Path.Combine(directory ?? Path, "Example.Migrations.csproj");
            File.WriteAllText(
                project,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>Example.Database</RootNamespace>
                    <CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>
                  </PropertyGroup>
                </Project>
                """);
            return project;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
