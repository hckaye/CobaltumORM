using System.Diagnostics;
using System.Security;
using System.Text.Json;
using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

/// <summary>
/// Drives the generate command through real MSBuild evaluation and compares its files with what a
/// normal build produces.
/// </summary>
public sealed class GenerateIntegrationTests
{
    private const string MigrationSource = """
        using CobaltumOrm.Migrations;

        [Migration(1, "create users")]
        public sealed class CreateUsersMigration : Migration
        {
            public override void Up()
            {
                Create.Table("users")
                    .WithColumn("id").AsInt32().NotNullable()
                    .WithColumn("name").AsString().NotNullable();
            }

            public override void Down()
            {
                Delete.Table("users");
            }
        }
        """;

    private const string QueriesSource = """
        using CobaltumOrm;

        [Query("FindUsers", "SELECT id, name FROM users")]
        public static partial class UserQueries
        {
        }
        """;

    private const string ConsumerSource = """
        using System.Data.Common;
        using System.Threading.Tasks;
        using CobaltumOrm;

        public static class Consumer
        {
            public static async Task<string> Read(DbConnection connection)
            {
                var rows = await connection.Query("SELECT id, name, email FROM users").ReadAsync();
                return rows[0].Name + rows[0].Email;
            }
        }
        """;

    [Fact]
    public async Task ExplicitGenerationProducesTheSameFilesAsTheBuildAndCompiles()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources();

        var build = fixture.RunDotnet("build", fixture.ProjectPath, "-c", "Release", "--nologo");
        Assert.True(build.ExitCode == 0, string.Join("\n", build.Output));

        var output = Path.Combine(fixture.Root, "ExplicitOutput");
        var run = await fixture.GenerateAsync("--output-mode", "directory", "--output", output);
        Assert.True(run.ExitCode == 0, run.Output + run.Error);

        var fromBuild = fixture.ReadBuildGeneratedFiles();
        var fromCommand = ReadGeneratedFiles(output);
        Assert.Equal(
            fromBuild.Keys.OrderBy(name => name, StringComparer.Ordinal),
            fromCommand.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach (var file in fromBuild)
        {
            Assert.Equal(file.Value, fromCommand[file.Key]);
        }

        Assert.Contains("CobaltumOrm.Models.g.cs", fromCommand.Keys);
        Assert.Contains("CobaltumOrm.SqlSchema.g.cs", fromCommand.Keys);
        Assert.Contains("CobaltumOrm.RawQueries.g.cs", fromCommand.Keys);
        Assert.Contains("CobaltumOrm.FlywayMigrations.g.cs", fromCommand.Keys);
        Assert.Contains(fromCommand.Keys, name => name.EndsWith(".cobaltum.cs", StringComparison.Ordinal));
        Assert.Contains(fromCommand.Keys, name => name.StartsWith("CobaltumOrm.Queries.", StringComparison.Ordinal));

        var cacheDirectory = Path.Combine(
            fixture.Root,
            "obj",
            "Release",
            "net10.0",
            "CobaltumOrm",
            "AnalysisCache");
        var cacheFiles = Directory.GetFiles(cacheDirectory, "*.xml");
        Assert.NotEmpty(cacheFiles);
        Assert.Contains(cacheFiles, path =>
            Path.GetFileName(path).StartsWith("schema-", StringComparison.Ordinal));
        Assert.Contains(cacheFiles, path =>
            Path.GetFileName(path).StartsWith("query-", StringComparison.Ordinal));
        var preservedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        foreach (var cacheFile in cacheFiles)
        {
            File.SetLastWriteTimeUtc(cacheFile, preservedTimestamp);
        }

        var repeatedRun = await fixture.GenerateAsync(
            "--output-mode", "directory", "--output", output);
        Assert.True(repeatedRun.ExitCode == 0, repeatedRun.Output + repeatedRun.Error);
        Assert.All(cacheFiles, cacheFile =>
            Assert.Equal(preservedTimestamp, File.GetLastWriteTimeUtc(cacheFile)));

        fixture.Clean();
        var explicitBuild = fixture.RunDotnet(
            "build",
            fixture.ProjectPath,
            "-c",
            "Release",
            "--nologo",
            "-p:CobaltumOrmGeneratedProps=" + Path.Combine(output, GenerationOutputWriter.PropsFileName));
        Assert.True(explicitBuild.ExitCode == 0, string.Join("\n", explicitBuild.Output));
        Assert.False(
            Directory.Exists(Path.Combine(fixture.Root, "obj", "Release", "net10.0", "CobaltumOrm")),
            "The MSBuild transform must not run when the generated props file is imported.");
    }

    [Fact]
    public async Task LibraryModeOutputCompilesOnItsOwn()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources();
        var restore = fixture.RunDotnet("build", fixture.ProjectPath, "-c", "Release", "--nologo");
        Assert.True(restore.ExitCode == 0, string.Join("\n", restore.Output));

        var output = Path.Combine(fixture.Root, "QueryLibrary");
        var run = await fixture.GenerateAsync(
            "--output-mode", "library", "--output", output, "--library-name", "Fixture.Queries");
        Assert.True(run.ExitCode == 0, run.Output + run.Error);

        var libraryProject = Path.Combine(output, "Fixture.Queries.csproj");
        Assert.True(File.Exists(libraryProject), run.Output + run.Error);
        var build = fixture.RunDotnet("build", libraryProject, "-c", "Release", "--nologo");
        Assert.True(build.ExitCode == 0, string.Join("\n", build.Output));
        Assert.True(File.Exists(Path.Combine(output, "bin", "Release", "net10.0", "Fixture.Queries.dll")));
    }

    [Fact]
    public async Task InvalidSqlReportsALocationAndFailsWithANonZeroExitCode()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources();
        File.WriteAllText(Path.Combine(fixture.Root, "Consumer.cs"), """
            using System.Data.Common;
            using CobaltumOrm;

            public static class Consumer
            {
                public static object Read(DbConnection connection) =>
                    connection.Query("SELECT id FROM").ReadAsync();
            }
            """);

        var output = Path.Combine(fixture.Root, "ExplicitOutput");
        var run = await fixture.GenerateAsync("--output-mode", "directory", "--output", output);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Consumer.cs(", run.Error, StringComparison.Ordinal);
        Assert.Contains("error SQL", run.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task MigrationProjectReferenceInputsAreEvaluatedThroughMsBuild()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources(includeLocalMigration: false);
        fixture.WriteMigrationProject(MigrationSource);

        var output = Path.Combine(fixture.Root, "ExplicitOutput");
        var run = await fixture.GenerateAsync("--output-mode", "directory", "--output", output);

        Assert.True(run.ExitCode == 0, run.Output + run.Error);
        var models = File.ReadAllText(Path.Combine(output, "CobaltumOrm.Models.g.cs"));
        Assert.Contains("UsersRow", models, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectUsesRealMsBuildEvaluationWithoutPublishingGeneratedFiles()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources();
        var build = fixture.RunDotnet("build", fixture.ProjectPath, "-c", "Release", "--nologo");
        Assert.True(build.ExitCode == 0, string.Join("\n", build.Output));
        var projectBefore = File.ReadAllText(fixture.ProjectPath);
        var consumerPath = Path.Combine(fixture.Root, "Consumer.cs");
        var consumerBefore = File.ReadAllText(consumerPath);

        var inspect = await fixture.InspectAsync("inspect", "--format", "json", "--no-restore");

        Assert.Equal(0, inspect.ExitCode);
        Assert.Equal(string.Empty, inspect.Error);
        Assert.Equal(projectBefore, File.ReadAllText(fixture.ProjectPath));
        Assert.Equal(consumerBefore, File.ReadAllText(consumerPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obj", "Release", "net10.0", "CobaltumOrmInspection")));
        using var document = JsonDocument.Parse(inspect.Output);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(fixture.ProjectPath, root.GetProperty("projectPath").GetString());
        Assert.Equal("net10.0", root.GetProperty("targetFramework").GetString());
        Assert.Equal("Release", root.GetProperty("configuration").GetString());
        Assert.True(root.GetProperty("analysisSucceeded").GetBoolean());
        Assert.NotEmpty(root.GetProperty("sourceGeneratorPaths").EnumerateArray());
        Assert.Contains(
            root.GetProperty("generatedArtifacts").EnumerateArray(),
            artifact => artifact.GetProperty("fileName").GetString() == "CobaltumOrm.SqlSchema.g.cs");
    }

    [Fact]
    public async Task DoctorReportsRealMigrationProjectReferencesAndInputs()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources(includeLocalMigration: false);
        fixture.WriteMigrationProject(MigrationSource);
        var build = fixture.RunDotnet("build", fixture.ProjectPath, "-c", "Release", "--nologo");
        Assert.True(build.ExitCode == 0, string.Join("\n", build.Output));

        var inspect = await fixture.InspectAsync("inspect", "--format", "json", "--no-restore");
        var doctor = await fixture.InspectAsync("doctor", "--format", "json", "--no-restore");

        Assert.Equal(0, inspect.ExitCode);
        using (var document = JsonDocument.Parse(inspect.Output))
        {
            var root = document.RootElement;
            Assert.Equal(
                new[] { Path.Combine(fixture.Root, "Migrations.Project", "Migrations.Project.csproj") },
                root.GetProperty("migrationProjectReferencePaths").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains(
                root.GetProperty("migrationSourcePaths").EnumerateArray().Select(item => item.GetString()),
                path => path!.EndsWith("Migrations/CreateUsers.cs", StringComparison.Ordinal));
            Assert.Contains(
                root.GetProperty("migrationInputPaths").EnumerateArray().Select(item => item.GetString()),
                path => path!.EndsWith("Migrations/CreateUsers.cs", StringComparison.Ordinal));
        }

        Assert.Equal(0, doctor.ExitCode);
        using var doctorDocument = JsonDocument.Parse(doctor.Output);
        Assert.Equal("ok", doctorDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            new[]
            {
                "target-framework", "cobaltumorm-wiring", "database-provider", "generated-namespace",
                "migration-inputs", "generation-diagnostics",
            },
            doctorDocument.RootElement.GetProperty("checks").EnumerateArray()
                .Select(check => check.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task DoctorUsesTheProviderFromAnExternalMigrationProject()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources(includeLocalMigration: false);
        fixture.WriteMigrationProject(MigrationSource, "Sqlite");
        var build = fixture.RunDotnet("build", fixture.ProjectPath, "-c", "Release", "--nologo");
        Assert.True(build.ExitCode == 0, string.Join("\n", build.Output));

        var doctor = await fixture.InspectAsync("doctor", "--format", "json", "--no-restore");

        Assert.True(doctor.ExitCode == 0, doctor.Output + doctor.Error);
        using var document = JsonDocument.Parse(doctor.Output);
        var provider = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("id").GetString() == "database-provider");
        Assert.Equal("ok", provider.GetProperty("status").GetString());
        Assert.Contains("Sqlite", provider.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorRecognizesAnImportedExplicitGenerationPropsFile()
    {
        using var fixture = new BuildFixture();
        fixture.WriteDefaultSources();
        var output = Path.Combine(fixture.Root, "ExplicitOutput");
        var generation = await fixture.GenerateAsync("--output-mode", "directory", "--output", output);
        Assert.Equal(0, generation.ExitCode);

        var project = File.ReadAllText(fixture.ProjectPath);
        File.WriteAllText(
            fixture.ProjectPath,
            project.Replace(
                "</Project>",
                $"  <Import Project=\"{SecurityElement.Escape(Path.Combine(output, GenerationOutputWriter.PropsFileName))}\" />{Environment.NewLine}</Project>",
                StringComparison.Ordinal));

        var doctor = await fixture.InspectAsync("doctor", "--format", "json", "--no-restore");

        Assert.Equal(0, doctor.ExitCode);
        using var document = JsonDocument.Parse(doctor.Output);
        var wiring = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("id").GetString() == "cobaltumorm-wiring");
        Assert.Equal("ok", wiring.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InspectRequiresFrameworkForARealMultiTargetProject()
    {
        using var fixture = new BuildFixture();
        fixture.WriteMultiTargetProject();

        var ambiguous = await fixture.InspectAsync("inspect", "--format", "json");
        var selected = await fixture.InspectAsync(
            "inspect", "--framework", "net10.0", "--format", "json", "--no-restore");

        Assert.Equal(1, ambiguous.ExitCode);
        Assert.Equal(string.Empty, ambiguous.Output);
        Assert.Contains("--framework", ambiguous.Error, StringComparison.Ordinal);
        Assert.Equal(0, selected.ExitCode);
        Assert.Equal(string.Empty, selected.Error);
        using var document = JsonDocument.Parse(selected.Output);
        Assert.Equal("net10.0", document.RootElement.GetProperty("targetFramework").GetString());
    }

    private static Dictionary<string, string> ReadGeneratedFiles(string directory) =>
        Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => Path.GetFileName(path), ReadText, StringComparer.Ordinal);

    private static string ReadText(string path) => File.ReadAllText(path).TrimStart('﻿');

    private sealed class BuildFixture : IDisposable
    {
        private readonly string _repository = FindRepositoryRoot();

        public BuildFixture()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "CobaltumOrm.GenerateIntegration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            // The macOS temporary directory sits under a symbolic link. Compare the two
            // generation paths on the resolved directory so the file contents can be compared.
            Root = ResolveLinks(root);
            ProjectPath = Path.Combine(Root, "Fixture.csproj");
        }

        private static string ResolveLinks(string path)
        {
            var segments = new List<string>();
            for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            {
                var target = current.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    segments.Reverse();
                    return Path.Combine(new[] { target.FullName }.Concat(segments).ToArray());
                }

                segments.Add(current.Name);
            }

            return path;
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public void WriteDefaultSources(bool includeLocalMigration = true)
        {
            if (includeLocalMigration)
            {
                File.WriteAllText(Path.Combine(Root, "Migrations.cs"), MigrationSource);
            }

            File.WriteAllText(Path.Combine(Root, "Queries.cs"), QueriesSource);
            File.WriteAllText(Path.Combine(Root, "Consumer.cs"), ConsumerSource);
            Directory.CreateDirectory(Path.Combine(Root, "Migrations"));
            File.WriteAllText(
                Path.Combine(Root, "Migrations", "V2__add_email.sql"),
                "ALTER TABLE users ADD COLUMN email text NOT NULL;");
            File.WriteAllText(ProjectPath, ProjectText(migrationProjectReference: false));
        }

        public void WriteMultiTargetProject()
        {
            File.WriteAllText(ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                  </PropertyGroup>
                </Project>
                """);
        }

        public void WriteMigrationProject(string migrationSource, string provider = "PostgreSql")
        {
            var directory = Path.Combine(Root, "Migrations.Project");
            Directory.CreateDirectory(Path.Combine(directory, "Migrations"));
            File.WriteAllText(Path.Combine(directory, "Migrations", "CreateUsers.cs"), migrationSource);
            File.WriteAllText(Path.Combine(directory, "Migrations.Project.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>
                    <CobaltumOrmDatabaseProvider>{provider}</CobaltumOrmDatabaseProvider>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Escape(RepositoryProject("CobaltumOrm.Migrations"))}" />
                  </ItemGroup>
                  <Import Project="{Escape(SourceGeneratorTargets())}" />
                </Project>
                """);
            File.WriteAllText(ProjectPath, ProjectText(migrationProjectReference: true));
        }

        public void Clean()
        {
            var obj = Path.Combine(Root, "obj");
            if (Directory.Exists(obj))
            {
                foreach (var directory in Directory.EnumerateDirectories(obj, "CobaltumOrm", SearchOption.AllDirectories))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        public Dictionary<string, string> ReadBuildGeneratedFiles()
        {
            var transformed = Path.Combine(Root, "obj", "Release", "net10.0", "CobaltumOrm");
            var generatorDirectories = Directory.EnumerateDirectories(
                Path.Combine(Root, "obj"),
                "*.CobaltumOrmGenerator",
                SearchOption.AllDirectories);
            var files = Directory
                .EnumerateFiles(transformed, "*.cs", SearchOption.TopDirectoryOnly)
                .Concat(generatorDirectories.SelectMany(directory =>
                    Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)));
            return files.ToDictionary(path => Path.GetFileName(path), ReadText, StringComparer.Ordinal);
        }

        public async Task<RunResult> GenerateAsync(params string[] arguments)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var application = new ToolApplication(output, error, new DotNetProcessRunner(), Root);
            var args = new List<string> { "generate", "--project", ProjectPath, "--configuration", "Release" };
            args.AddRange(arguments);
            var exitCode = await application.RunAsync(args.ToArray(), CancellationToken.None);
            return new RunResult(exitCode, output.ToString(), error.ToString());
        }

        public async Task<RunResult> InspectAsync(string command, params string[] arguments)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var application = new ToolApplication(output, error, new DotNetProcessRunner(), Root);
            var args = new List<string> { command, "--project", ProjectPath, "--configuration", "Release" };
            args.AddRange(arguments);
            var exitCode = await application.RunAsync(args.ToArray(), CancellationToken.None);
            return new RunResult(exitCode, output.ToString(), error.ToString());
        }

        public ProcessResult RunDotnet(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Root,
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

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start dotnet.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(300_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("dotnet did not finish within five minutes.");
            }

            Task.WaitAll(standardOutput, standardError);
            return new ProcessResult(
                process.ExitCode,
                (standardOutput.Result + standardError.Result).Split('\n'));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string ProjectText(bool migrationProjectReference)
        {
            var migrationReference = migrationProjectReference
                ? "    <CobaltumOrmMigrationProjectReference Include=\"Migrations.Project/Migrations.Project.csproj\" />\n"
                : string.Empty;
            return $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <CobaltumOrmGeneratedNamespace>Fixture.Generated</CobaltumOrmGeneratedNamespace>
                    <CobaltumOrmCompilerTaskAssembly>{Escape(CompilerTaskAssembly())}</CobaltumOrmCompilerTaskAssembly>
                    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
                    <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Escape(RepositoryProject("CobaltumOrm"))}" />
                    <ProjectReference Include="{Escape(RepositoryProject("CobaltumOrm.Migrations"))}" />
                    <ProjectReference Include="{Escape(RepositoryProject("CobaltumOrm.SourceGenerator"))}"
                                      OutputItemType="Analyzer"
                                      ReferenceOutputAssembly="false" />
                    <AdditionalFiles Include="Migrations/V*__*.sql" />
                    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
                {migrationReference}  </ItemGroup>
                  <Import Project="{Escape(SourceGeneratorTargets())}" />
                  <Import Project="$(CobaltumOrmGeneratedProps)" Condition="'$(CobaltumOrmGeneratedProps)' != ''" />
                </Project>
                """;
        }

        private string RepositoryProject(string name) =>
            Path.Combine(_repository, "src", name, name + ".csproj");

        private string SourceGeneratorTargets() => Path.Combine(
            _repository,
            "src",
            "CobaltumOrm.SourceGenerator",
            "buildTransitive",
            "CobaltumOrm.SourceGenerator.targets");

        private string CompilerTaskAssembly() => Path.Combine(
            _repository,
            "src",
            "CobaltumOrm.Compiler",
            "bin",
            "Release",
            "netstandard2.0",
            "CobaltumOrm.Compiler.dll");

        private static string Escape(string value) => SecurityElement.Escape(value) ?? value;

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CobaltumOrm.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Could not locate CobaltumOrm.sln.");
        }
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);

    private sealed record ProcessResult(int ExitCode, string[] Output);
}
