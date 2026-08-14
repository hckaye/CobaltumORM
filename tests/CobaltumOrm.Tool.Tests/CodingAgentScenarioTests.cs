using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using CobaltumOrm.Tool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class CodingAgentScenarioTests : IClassFixture<CodingAgentScenarioPackageFeed>
{
    private readonly CodingAgentScenarioPackageFeed _packageFeed;

    public CodingAgentScenarioTests(CodingAgentScenarioPackageFeed packageFeed)
    {
        _packageFeed = packageFeed;
    }

    [Fact]
    public async Task ExistingProjectCanBeOnboardedInspectedCheckedAndBuiltWithAgentGuidance()
    {
        using var fixture = new AgentScenarioFixture(_packageFeed, ValidScenarioSource);

        var add = await fixture.RunToolAsync(
            "add", "--project", fixture.ApplicationProjectPath,
            "--migration-project", fixture.MigrationProjectPath);
        Assert.True(add.ExitCode == 0, add.Output + add.Error);
        Assert.Equal(string.Empty, add.Error);
        Assert.Contains("CobaltumOrmMigrationProjectReference", File.ReadAllText(fixture.ApplicationProjectPath), StringComparison.Ordinal);

        var secondAdd = await fixture.RunToolAsync(
            "add", "--project", fixture.ApplicationProjectPath,
            "--migration-project", fixture.MigrationProjectPath);
        Assert.Equal(0, secondAdd.ExitCode);
        Assert.Contains("No changes needed", secondAdd.Output, StringComparison.Ordinal);
        var restore = fixture.Restore();
        Assert.Equal(0, restore.ExitCode);

        var init = await fixture.RunToolAsync("assistant", "init", "--project", fixture.ApplicationProjectPath);
        var secondInit = await fixture.RunToolAsync("assistant", "init", "--project", fixture.ApplicationProjectPath);
        Assert.Equal(0, init.ExitCode);
        Assert.Equal(0, secondInit.ExitCode);
        Assert.Contains("Unchanged .cobaltum/assistant.md", secondInit.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.ApplicationPath(".cobaltum/assistant.md")));
        Assert.True(File.Exists(fixture.ApplicationPath("AGENTS.md")));

        var instructions = File.ReadAllText(fixture.ApplicationPath(".cobaltum/assistant.md"));
        Assert.Contains("Do not invent EF Core or `DbContext` APIs", instructions, StringComparison.Ordinal);
        Assert.Contains("Prefer compile-time checked `Query`, `Query<T>`, and `[Query]`", instructions, StringComparison.Ordinal);
        Assert.Contains("`NoCheckQuery` only when SQL is genuinely dynamic", instructions, StringComparison.Ordinal);
        Assert.Contains("cobaltum doctor --project <path> --format json", instructions, StringComparison.Ordinal);
        Assert.Contains("dotnet build <project>", instructions, StringComparison.Ordinal);
        Assert.Contains("Do not access a database or run migrations unless the user requests it", instructions, StringComparison.Ordinal);

        var inspect = await fixture.RunToolAsync("inspect", "--project", fixture.ApplicationProjectPath, "--format", "json");
        var secondInspect = await fixture.RunToolAsync("inspect", "--project", fixture.ApplicationProjectPath, "--format", "json");
        var doctor = await fixture.RunToolAsync("doctor", "--project", fixture.ApplicationProjectPath, "--format", "json");
        Assert.Equal(0, inspect.ExitCode);
        Assert.Equal(0, secondInspect.ExitCode);
        Assert.Equal(inspect.Output, secondInspect.Output);
        Assert.Equal(0, doctor.ExitCode);

        using (var document = JsonDocument.Parse(inspect.Output))
        {
            var root = document.RootElement;
            Assert.Equal("PostgreSql", root.GetProperty("databaseProvider").GetString());
            Assert.Equal("Scenario.Migrations", root.GetProperty("generatedNamespace").GetString());
            Assert.Contains(
                root.GetProperty("migrationProjectReferencePaths").EnumerateArray().Select(item => item.GetString()),
                path => string.Equals(path, fixture.MigrationProjectPath, StringComparison.Ordinal));
            Assert.Contains(
                root.GetProperty("generatedArtifacts").EnumerateArray().Select(item => item.GetProperty("fileName").GetString()),
                name => name != null && name.StartsWith("CobaltumOrm.Queries.", StringComparison.Ordinal));
        }

        using (var document = JsonDocument.Parse(doctor.Output))
        {
            Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
            var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToArray();
            Assert.Equal("ok", checks.Single(check => check.GetProperty("id").GetString() == "database-provider").GetProperty("status").GetString());
            Assert.Equal("ok", checks.Single(check => check.GetProperty("id").GetString() == "generated-namespace").GetProperty("status").GetString());
        }

        var build = fixture.Build();
        Assert.Equal(0, build.ExitCode);
        Assert.Contains("Scenario.App ->", build.Output, StringComparison.Ordinal);
        await AssertMcpSurfaceAsync(fixture);
        AssertPublishedSafetyGuidance();
    }

    [Fact]
    public async Task IncorrectResultNullabilityReportsStableDiagnosticWithHelpUri()
    {
        using var fixture = new AgentScenarioFixture(_packageFeed, """
            using System.Data.Common;
            using CobaltumOrm;

            public sealed record UserResult(int Id, string Name);

            public static class NullabilityContract
            {
                public static object Read(DbConnection connection) =>
                    connection.Query<UserResult>("SELECT id, name FROM users").ReadAsync();
            }
            """);
        await fixture.ConfigureApplicationAsync();

        var inspect = await fixture.RunToolAsync("inspect", "--project", fixture.ApplicationProjectPath, "--format", "json");
        Assert.Equal(1, inspect.ExitCode);
        using var document = JsonDocument.Parse(inspect.Output);
        var diagnostic = document.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "COB109");
        Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
        var helpUri = Assert.IsType<string>(diagnostic.GetProperty("helpUri").GetString());
        Assert.Equal("https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md#cob109", helpUri);

        var build = fixture.Build();
        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("COB109", build.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicSqlIsRejectedByCheckedApiAndAllowedByNoCheckQueryInSeparateFixtures()
    {
        using var checkedFixture = new AgentScenarioFixture(_packageFeed, """
            using System.Data.Common;
            using CobaltumOrm;

            public static class CheckedDynamicSql
            {
                public static object Read(DbConnection connection, string sql) =>
                    connection.Query(sql).ReadAsync();
            }
            """);
        await checkedFixture.ConfigureApplicationAsync();
        var checkedBuild = checkedFixture.Build();
        Assert.NotEqual(0, checkedBuild.ExitCode);
        Assert.Contains("COB100", checkedBuild.Output, StringComparison.Ordinal);

        using var uncheckedFixture = new AgentScenarioFixture(_packageFeed, """
            using System.Data.Common;
            using CobaltumOrm;

            public static class UncheckedDynamicSql
            {
                public static object Read(DbConnection connection, string sql) =>
                    connection.NoCheckQuery(sql).ReadAsync();
            }
            """);
        await uncheckedFixture.ConfigureApplicationAsync();
        var uncheckedBuild = uncheckedFixture.Build();
        Assert.Equal(0, uncheckedBuild.ExitCode);
        Assert.Contains("Scenario.App ->", uncheckedBuild.Output, StringComparison.Ordinal);
    }

    private static async Task AssertMcpSurfaceAsync(AgentScenarioFixture fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "cobaltum-scenario", Version = "1.0.0" })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithCobaltumMcpSurface(
                new CobaltumMcpProjectService(
                    fixture.ApplicationProjectPath,
                    new ProjectInspectionOptions
                    {
                        Project = fixture.ApplicationProjectPath,
                        Configuration = "Debug",
                        Framework = "net10.0",
                        NoRestore = true,
                    },
                    new MsBuildProjectEvaluator(),
                    TextWriter.Null),
                McpDocumentation.Load());

        using var host = builder.Build();
        await host.StartAsync(timeout.Token);
        var transport = new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

        var inspect = await client.CallToolAsync("inspect_project", cancellationToken: timeout.Token);
        var doctor = await client.CallToolAsync("doctor_project", cancellationToken: timeout.Token);
        var list = await client.CallToolAsync("list_generated_artifacts", cancellationToken: timeout.Token);
        Assert.False(inspect.IsError is true);
        Assert.False(doctor.IsError is true);
        Assert.False(list.IsError is true);
        Assert.Equal("PostgreSql", inspect.StructuredContent!.Value.GetProperty("databaseProvider").GetString());
        Assert.Equal("Scenario.Migrations", inspect.StructuredContent.Value.GetProperty("generatedNamespace").GetString());
        Assert.Equal("ok", doctor.StructuredContent!.Value.GetProperty("status").GetString());

        var artifactName = list.StructuredContent!.Value.GetProperty("artifacts").EnumerateArray()
            .Select(artifact => artifact.GetProperty("name").GetString())
            .Single(name => name != null && name.StartsWith("CobaltumOrm.Queries.", StringComparison.Ordinal));
        var read = await client.CallToolAsync(
            "read_generated_artifact",
            new Dictionary<string, object?> { ["artifactName"] = artifactName },
            cancellationToken: timeout.Token);
        Assert.False(read.IsError is true);
        Assert.Contains("FindUserByIdAsync", read.StructuredContent!.Value.GetProperty("source").GetString(), StringComparison.Ordinal);

        var explain = await client.CallToolAsync(
            "explain_diagnostic",
            new Dictionary<string, object?> { ["code"] = "COB109", ["language"] = "ja" },
            cancellationToken: timeout.Token);
        Assert.False(explain.IsError is true);
        Assert.Equal("COB109", explain.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Equal("ja", explain.StructuredContent.Value.GetProperty("language").GetString());
        Assert.Equal(
            "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md#cob109",
            explain.StructuredContent.Value.GetProperty("helpUri").GetString());

        await host.StopAsync(timeout.Token);
    }

    private static void AssertPublishedSafetyGuidance()
    {
        var root = FindRepositoryRoot();
        var english = File.ReadAllText(Path.Combine(root, "docs", "ai", "agent-tools.md"));
        var japanese = File.ReadAllText(Path.Combine(root, "docs", "ai", "agent-tools.ja.md"));
        Assert.Contains("does not connect to a database or execute migrations", english, StringComparison.Ordinal);
        Assert.Contains("cobaltum://docs/diagnostics/en", english, StringComparison.Ordinal);
        Assert.Contains("データベースへの接続やマイグレーションの実行は行いません", japanese, StringComparison.Ordinal);
        Assert.Contains("cobaltum://docs/diagnostics/ja", japanese, StringComparison.Ordinal);
    }

    private static string ValidScenarioSource() => """
        using System.Data;
        using System.Data.Common;
        using System.Threading.Tasks;
        using CobaltumOrm;

        [Query("FindUserById", "SELECT id, name FROM users WHERE id = @id")]
        public static partial class AgentQueries
        {
        }

        public static class AgentScenario
        {
            public static async Task<string> Read(DbConnection connection, int id)
            {
                var named = await AgentQueries.FindUserByIdAsync(connection, id);
                var parameterized = await connection
                    .Query("SELECT id, name FROM users WHERE id = @id")
                    .WithParameter("@id", id, DbType.Int32)
                    .ReadAsync();
                return named[0].Name + parameterized[0].Name;
            }
        }
        """;

    internal static void CreatePackageFeed(
        string cobaltumVersion,
        MigrationProviders.RuntimePackage postgreSqlDriver,
        string feed)
    {
        var repository = FindRepositoryRoot();
        PackProject(repository, "src/CobaltumOrm/CobaltumOrm.csproj", feed);
        PackProject(repository, "src/CobaltumOrm.Migrations/CobaltumOrm.Migrations.csproj", feed);
        PackProject(repository, "src/CobaltumOrm.SourceGenerator/CobaltumOrm.SourceGenerator.csproj", feed);
        AddPackageAndDependencies(feed, "CobaltumOrm", cobaltumVersion);
        AddPackageAndDependencies(feed, "CobaltumOrm.Migrations", cobaltumVersion);
        AddPackageAndDependencies(feed, "CobaltumOrm.SourceGenerator", cobaltumVersion);
        AddPackageAndDependencies(feed, postgreSqlDriver.Id, postgreSqlDriver.Version);
    }

    private static void PackProject(string repository, string relativeProjectPath, string feed)
    {
        var result = RunDotnet(
            repository,
            "pack", Path.Combine(repository, relativeProjectPath),
            "--configuration", "Release", "--no-restore", "--output", feed, "--nologo");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static void AddPackageAndDependencies(string feed, string id, string version)
    {
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPackageAndDependencies(feed, id, version, copied);
    }

    private static void AddPackageAndDependencies(string feed, string id, string version, ISet<string> copied)
    {
        var key = id + "/" + version;
        if (!copied.Add(key))
        {
            return;
        }

        var package = FindPackage(feed, id, version) ?? PackageDirectories()
            .Select(directory => FindPackage(directory, id, version))
            .FirstOrDefault(path => path is not null);
        Assert.True(package is not null, $"Package '{id}' version '{version}' was not available after restore.");
        var destination = Path.Combine(feed, Path.GetFileName(package));
        if (!File.Exists(destination))
        {
            File.Copy(package, destination);
        }

        using var archive = ZipFile.OpenRead(destination);
        var nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = nuspec.Open();
        var document = XDocument.Load(stream);
        foreach (var dependency in document.Descendants()
                     .Where(element => element.Name.LocalName == "dependency")
                     .Where(IsModernDependency))
        {
            var dependencyId = dependency.Attribute("id")?.Value;
            if (dependencyId is null)
            {
                continue;
            }

            var versionConstraint = dependency.Attribute("version")?.Value;
            if (TryGetExactVersion(versionConstraint, out var dependencyVersion))
            {
                AddPackageAndDependencies(feed, dependencyId, dependencyVersion, copied);
                continue;
            }

            foreach (var availableVersion in AvailablePackageVersions(dependencyId))
            {
                AddPackageAndDependencies(feed, dependencyId, availableVersion, copied);
            }
        }
    }

    private static bool IsModernDependency(XElement dependency)
    {
        var group = dependency.Ancestors().FirstOrDefault(element => element.Name.LocalName == "group");
        var targetFramework = group?.Attribute("targetFramework")?.Value;
        return targetFramework is null || targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
            !targetFramework.Contains("standard", StringComparison.OrdinalIgnoreCase) &&
            !targetFramework.Contains("framework", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPackage(string directory, string id, string version)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var exact = Directory.EnumerateFiles(directory, "*.nupkg", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                id + "." + version,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var idDirectory = Directory.EnumerateDirectories(directory)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), id, StringComparison.OrdinalIgnoreCase));
        if (idDirectory is null)
        {
            return null;
        }

        var versionDirectory = Directory.EnumerateDirectories(idDirectory)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), version, StringComparison.OrdinalIgnoreCase));
        return versionDirectory is null
            ? null
            : Directory.EnumerateFiles(versionDirectory, "*.nupkg", SearchOption.TopDirectoryOnly).FirstOrDefault();
    }

    private static bool TryGetExactVersion(string? versionConstraint, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(versionConstraint))
        {
            return false;
        }

        var value = versionConstraint.Trim();
        if (value.IndexOf(',') >= 0 || value.IndexOf('*') >= 0)
        {
            return false;
        }

        var isExactRange = value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal);
        if (!isExactRange || value.StartsWith("(", StringComparison.Ordinal) || value.StartsWith(">", StringComparison.Ordinal) ||
            value.StartsWith("<", StringComparison.Ordinal))
        {
            return false;
        }

        version = value[1..^1].Trim();
        return version.Length != 0;
    }

    private static IEnumerable<string> AvailablePackageVersions(string id) => PackageDirectories()
        .SelectMany(directory => FindPackageVersions(directory, id))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> FindPackageVersions(string directory, string id)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var idDirectory = Directory.EnumerateDirectories(directory)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), id, StringComparison.OrdinalIgnoreCase));
        return idDirectory is null
            ? Array.Empty<string>()
            : Directory.EnumerateDirectories(idDirectory)
                .Where(path => Directory.EnumerateFiles(path, "*.nupkg", SearchOption.TopDirectoryOnly).Any())
                .Select(path => Path.GetFileName(path)!);
    }

    private static IEnumerable<string> PackageDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var repositoryPackages = Path.Combine(FindRepositoryRoot(), ".packages");
        if (Directory.Exists(repositoryPackages))
        {
            yield return repositoryPackages;
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    private static DotnetResult RunDotnet(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet did not finish within two minutes.");
        }

        Task.WaitAll(stdout, stderr);
        return new DotnetResult(process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CobaltumOrm.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate CobaltumOrm.sln.");
    }

    private sealed class AgentScenarioFixture : IDisposable
    {
        private readonly CodingAgentScenarioPackageFeed _packageFeed;

        public AgentScenarioFixture(CodingAgentScenarioPackageFeed packageFeed, Func<string> sourceFactory)
            : this(packageFeed, sourceFactory())
        {
        }

        public AgentScenarioFixture(CodingAgentScenarioPackageFeed packageFeed, string source)
        {
            _packageFeed = packageFeed;
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CobaltumOrm.CodingAgentScenarioTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ApplicationProjectPath = System.IO.Path.Combine(Root, "App", "Scenario.App.csproj");
            MigrationProjectPath = System.IO.Path.Combine(Root, "Migrations", "Scenario.Migrations.csproj");
            SourcePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ApplicationProjectPath)!, "AgentScenario.cs");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ApplicationProjectPath)!);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MigrationProjectPath)!);
            File.WriteAllText(SourcePath, source);
            File.WriteAllText(MigrationProjectPath, MigrationProject(_packageFeed.CobaltumVersion));
            Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(MigrationProjectPath)!, "Migrations"));
            File.WriteAllText(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(MigrationProjectPath)!, "Migrations", "CreateUsers.cs"),
                """
                using CobaltumOrm.Migrations;

                [Migration(1, "create users")]
                public sealed class CreateUsersMigration : Migration
                {
                    public override void Up()
                    {
                        Create.Table("users")
                            .WithColumn("id").AsInt32().NotNullable()
                            .WithColumn("name").AsString().Nullable();
                    }

                    public override void Down() => Delete.Table("users");
                }
                """);
            File.WriteAllText(ApplicationProjectPath, ApplicationProject());
        }

        public string Root { get; }

        public string ApplicationProjectPath { get; }

        public string MigrationProjectPath { get; }

        public string SourcePath { get; }

        public string Path(string relativePath) => System.IO.Path.Combine(Root, relativePath);

        public string ApplicationPath(string relativePath) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ApplicationProjectPath)!, relativePath);

        public async Task ConfigureApplicationAsync()
        {
            var add = await RunToolAsync(
                "add", "--project", ApplicationProjectPath,
                "--migration-project", MigrationProjectPath);
            Assert.True(add.ExitCode == 0, add.Output + add.Error);
            var restore = Restore();
            Assert.Equal(0, restore.ExitCode);
        }

        public async Task<ToolResult> RunToolAsync(params string[] arguments)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var application = new ToolApplication(output, error, new ThrowingProcessRunner(), Root);
            var exitCode = await application.RunAsync(arguments, CancellationToken.None);
            return new ToolResult(exitCode, output.ToString(), error.ToString());
        }

        public DotnetResult Restore()
        {
            var migration = RunDotnet(
                Root,
                "restore", MigrationProjectPath, "--source", _packageFeed.Path, "--disable-parallel", "--nologo");
            if (migration.ExitCode != 0)
            {
                return migration;
            }

            return RunDotnet(
                Root,
                "restore", ApplicationProjectPath, "--source", _packageFeed.Path, "--disable-parallel", "--nologo");
        }

        public DotnetResult Build() => RunDotnet(Root, "build", ApplicationProjectPath, "--no-restore", "--nologo");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private string ApplicationProject() => $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <RestoreSources>{{_packageFeed.Path}}</RestoreSources>
              </PropertyGroup>
            </Project>
            """;

        private static string MigrationProject(string cobaltumVersion) => $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <RootNamespace>Scenario.Migrations</RootNamespace>
                <CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>
                <CobaltumOrmDatabaseProvider>PostgreSql</CobaltumOrmDatabaseProvider>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="CobaltumOrm" Version="{{cobaltumVersion}}" />
                <PackageReference Include="CobaltumOrm.Migrations" Version="{{cobaltumVersion}}" />
                <PackageReference Include="CobaltumOrm.SourceGenerator" Version="{{cobaltumVersion}}" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """;
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Coding-agent scenarios must not execute migrations.");
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    private sealed record DotnetResult(int ExitCode, string Output);
}

public sealed class CodingAgentScenarioPackageFeed : IDisposable
{
    public CodingAgentScenarioPackageFeed()
    {
        CobaltumVersion = ResolveCobaltumVersion();
        PostgreSqlDriver = MigrationProviders.RuntimePackages("PostgreSql").Single();
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CobaltumOrm.CodingAgentScenarioTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path);
            CodingAgentScenarioTests.CreatePackageFeed(CobaltumVersion, PostgreSqlDriver, Path);
        }
        catch
        {
            DeleteFeed();
            throw;
        }
    }

    public string Path { get; }

    public string CobaltumVersion { get; }

    internal MigrationProviders.RuntimePackage PostgreSqlDriver { get; }

    public void Dispose()
    {
        DeleteFeed();
        if (Directory.Exists(Path))
        {
            throw new IOException($"Could not remove coding-agent scenario package feed '{Path}'.");
        }
    }

    private static string ResolveCobaltumVersion()
    {
        var informationalVersion = typeof(ToolApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            throw new InvalidOperationException("The CobaltumORM tool assembly has no informational version.");
        }

        return AddCommand.NormalizeInformationalVersion(informationalVersion);
    }

    private void DeleteFeed()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
