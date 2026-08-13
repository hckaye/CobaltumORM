using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class GenerateCommandTests
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

    private const string ConsumerSource = """
        using System.Data.Common;
        using System.Threading.Tasks;
        using CobaltumOrm;

        public static class Consumer
        {
            public static async Task<string> Read(DbConnection connection)
            {
                var rows = await connection.Query("SELECT id, name FROM users").ReadAsync();
                return rows[0].Name;
            }
        }
        """;

    [Fact]
    public async Task IntermediateModeWritesUnderTheProjectIntermediateDirectory()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        fixture.AddSource("Consumer.cs", ConsumerSource);

        var run = await fixture.RunAsync("generate", "--project", fixture.ProjectPath);

        Assert.Equal(0, run.ExitCode);
        var outputDirectory = Path.Combine(fixture.IntermediateOutputPath, "CobaltumOrmGenerated");
        Assert.True(Directory.Exists(outputDirectory), run.Error);
        Assert.Contains("CobaltumOrm.Models.g.cs", FileNames(outputDirectory));
        Assert.Contains("CobaltumOrm.SqlSchema.g.cs", FileNames(outputDirectory));
        Assert.Contains("CobaltumOrm.RawQueries.g.cs", FileNames(outputDirectory));
        Assert.Contains("0000.Consumer.cobaltum.cs", FileNames(outputDirectory));
        Assert.Contains(GenerationOutputWriter.ManifestFileName, FileNames(outputDirectory));
    }

    [Fact]
    public async Task DirectoryModeWritesGeneratedFilesAndAPropsFile()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        fixture.AddSource("Consumer.cs", ConsumerSource);
        var output = Path.Combine(fixture.Root, "Generated");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);

        Assert.Equal(0, run.ExitCode);
        var props = File.ReadAllText(Path.Combine(output, GenerationOutputWriter.PropsFileName));
        Assert.Contains("<CobaltumOrmCompileTimeQueries>false</CobaltumOrmCompileTimeQueries>", props, StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Consumer.cs\" />", props, StringComparison.Ordinal);
        Assert.Contains(
            "<Compile Include=\"$(MSBuildThisFileDirectory)CobaltumOrm.Models.g.cs\" />",
            props,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Compile Include=\"$(MSBuildThisFileDirectory)0000.Consumer.cobaltum.cs\" />",
            props,
            StringComparison.Ordinal);
        Assert.Contains("CobaltumOrm.SourceGenerator", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<Compile Remove=\"Migrations.cs\" />", props, StringComparison.Ordinal);

        var manifest = File.ReadAllText(Path.Combine(output, GenerationOutputWriter.ManifestFileName));
        Assert.Contains("mode=directory", manifest, StringComparison.Ordinal);
        Assert.Contains("file=CobaltumOrm.Models.g.cs", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("sha", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LibraryModeWritesAProjectThatCompilesTheGeneratedFiles()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        fixture.AddSource("Consumer.cs", ConsumerSource);
        var output = Path.Combine(fixture.Root, "QueryLibrary");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "library",
            "--output", output, "--library-name", "Fixture.Queries");

        Assert.Equal(0, run.ExitCode);
        var project = File.ReadAllText(Path.Combine(output, "Fixture.Queries.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>Fixture.Queries</AssemblyName>", project, StringComparison.Ordinal);
        Assert.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", project, StringComparison.Ordinal);
        Assert.Contains($"<Import Project=\"{GenerationOutputWriter.PropsFileName}\" />", project, StringComparison.Ordinal);

        var props = File.ReadAllText(Path.Combine(output, GenerationOutputWriter.PropsFileName));
        Assert.Contains("../Migrations.cs", props, StringComparison.Ordinal);
        Assert.DoesNotContain("../Consumer.cs", props, StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"$(MSBuildThisFileDirectory)0000.Consumer.cobaltum.cs\" />", props, StringComparison.Ordinal);
        Assert.Contains("<HintPath>", props, StringComparison.Ordinal);
        Assert.DoesNotContain("<Compile Remove=", props, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibraryModeNeverEditsAnExistingProject()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        fixture.AddSource("Consumer.cs", ConsumerSource);
        var libraryDirectory = Path.Combine(fixture.Root, "Queries");
        Directory.CreateDirectory(libraryDirectory);
        var libraryProject = Path.Combine(libraryDirectory, "Queries.csproj");
        const string original = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
        File.WriteAllText(libraryProject, original);

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "library",
            "--library-project", libraryProject);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(original, File.ReadAllText(libraryProject));
        Assert.True(File.Exists(Path.Combine(libraryDirectory, GenerationOutputWriter.PropsFileName)));
        Assert.False(File.Exists(Path.Combine(libraryDirectory, "Queries.Generated.csproj")));
        Assert.Contains("was not modified", run.Output, StringComparison.Ordinal);
        Assert.Contains("<Import Project=\"CobaltumOrm.Generated.props\" />", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSqlFailsWithoutWritingOutput()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        fixture.AddSource("Consumer.cs", """
            using System.Data.Common;
            using CobaltumOrm;

            public static class Consumer
            {
                public static object Read(DbConnection connection) =>
                    connection.Query("SELECT missing FROM users").ReadAsync();
            }
            """);
        var output = Path.Combine(fixture.Root, "Generated");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("SQL203", run.Error, StringComparison.Ordinal);
        Assert.Contains("Consumer.cs(", run.Error, StringComparison.Ordinal);
        Assert.Contains("no files were written", run.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task InvalidMigrationSqlReportsTheMigrationLocation()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", """
            using CobaltumOrm.Migrations;

            [Migration(1, "create users")]
            public sealed class CreateUsersMigration : Migration
            {
                public override void Up() => Execute.Sql("CREATE TABLE");

                public override void Down() => Execute.Sql("DROP TABLE users");
            }
            """);
        var output = Path.Combine(fixture.Root, "Generated");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Migrations.cs(", run.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task StaleGeneratedFilesAreRemovedAndUserFilesAreKept()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        fixture.AddSource("Consumer.cs", ConsumerSource);
        var output = Path.Combine(fixture.Root, "Generated");
        var first = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("0000.Consumer.cobaltum.cs", FileNames(output));

        var keep = Path.Combine(output, "notes.txt");
        File.WriteAllText(keep, "user owned");
        File.Delete(Path.Combine(fixture.Root, "Consumer.cs"));
        fixture.RemoveSource("Consumer.cs");

        var second = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);

        Assert.Equal(0, second.ExitCode);
        Assert.DoesNotContain("0000.Consumer.cobaltum.cs", FileNames(output));
        Assert.Contains("removed 0000.Consumer.cobaltum.cs", second.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(keep));
        Assert.Contains("CobaltumOrm.Models.g.cs", FileNames(output));
    }

    [Fact]
    public async Task UntrackedFilesInTheOutputAreNeverRemoved()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        var output = Path.Combine(fixture.Root, "Generated");
        Directory.CreateDirectory(output);
        var stray = Path.Combine(output, "CobaltumOrm.Handwritten.g.cs");
        File.WriteAllText(stray, "// kept");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);

        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task ManifestEntriesThatAreNotPlainFileNamesAreIgnored()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        var output = Path.Combine(fixture.Root, "Generated");
        Directory.CreateDirectory(Path.Combine(output, "subdir"));
        var nested = Path.Combine(output, "subdir", "user.cs");
        File.WriteAllText(nested, "// user owned");
        var outside = Path.Combine(fixture.Root, "escaped.cs");
        File.WriteAllText(outside, "// user owned");
        File.WriteAllText(
            Path.Combine(output, GenerationOutputWriter.ManifestFileName),
            string.Join(
                Environment.NewLine,
                "# CobaltumORM explicit generation output",
                "mode=directory",
                "file=subdir/user.cs",
                "file=../escaped.cs",
                "file=CobaltumOrm.Stale.g.cs"));
        var stale = Path.Combine(output, "CobaltumOrm.Stale.g.cs");
        File.WriteAllText(stale, "// stale");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);

        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(nested));
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(stale));
        Assert.Contains("removed CobaltumOrm.Stale.g.cs", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("removed subdir", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task OutputCannotContainTheProjectDirectory(string relative)
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory",
            "--output", Path.Combine(fixture.Root, relative));

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("contains the project directory", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputCannotBeAFile()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        var output = Path.Combine(fixture.Root, "Generated");
        Directory.CreateDirectory(output);
        var file = Path.Combine(output, "taken");
        File.WriteAllText(file, string.Empty);

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", file);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("is a file", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProjectsAndNonProjectPathsAreRejected()
    {
        using var fixture = new GenerateFixture();
        var missing = await fixture.RunAsync("generate", "--project", Path.Combine(fixture.Root, "None.csproj"));
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("does not exist", missing.Error, StringComparison.Ordinal);

        var notAProject = Path.Combine(fixture.Root, "Consumer.cs");
        File.WriteAllText(notAProject, string.Empty);
        var wrongExtension = await fixture.RunAsync("generate", "--project", notAProject);
        Assert.Equal(2, wrongExtension.ExitCode);
        Assert.Contains("is not a .csproj file", wrongExtension.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedNamespaceAndProviderOverridesAreApplied()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);
        var output = Path.Combine(fixture.Root, "Generated");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output,
            "--generated-namespace", "Fixture.Data", "--provider", "MySql");

        Assert.Equal(0, run.ExitCode);
        var schema = File.ReadAllText(Path.Combine(output, "CobaltumOrm.SqlSchema.g.cs"));
        Assert.Contains("namespace Fixture.Data", schema, StringComparison.Ordinal);
        Assert.Contains("`users`", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectPropertiesSupplyTheProviderAndNamespace()
    {
        using var fixture = new GenerateFixture();
        fixture.Evaluation.DatabaseProvider = "SqlServer";
        fixture.Evaluation.GeneratedNamespace = "Fixture.FromProject";
        fixture.AddSource("Migrations.cs", MigrationSource);
        var output = Path.Combine(fixture.Root, "Generated");

        var run = await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--output-mode", "directory", "--output", output);

        Assert.Equal(0, run.ExitCode);
        var schema = File.ReadAllText(Path.Combine(output, "CobaltumOrm.SqlSchema.g.cs"));
        Assert.Contains("namespace Fixture.FromProject", schema, StringComparison.Ordinal);
        Assert.Contains("[dbo].[users]", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingTargetFrameworkAsksForTheFrameworkOption()
    {
        using var fixture = new GenerateFixture();
        fixture.Evaluation.TargetFramework = string.Empty;
        fixture.AddSource("Migrations.cs", MigrationSource);

        var run = await fixture.RunAsync("generate", "--project", fixture.ProjectPath);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("--framework", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEvaluatorReceivesTheParsedOptions()
    {
        using var fixture = new GenerateFixture();
        fixture.AddSource("Migrations.cs", MigrationSource);

        await fixture.RunAsync(
            "generate", "--project", fixture.ProjectPath, "--configuration", "Release", "--framework", "net10.0");

        Assert.Equal("Release", fixture.Evaluator.LastOptions!.Configuration);
        Assert.Equal("net10.0", fixture.Evaluator.LastOptions.Framework);
        Assert.Equal(fixture.ProjectPath, fixture.Evaluator.LastProjectPath);
    }

    private static string[] FileNames(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)).ToArray()
            : Array.Empty<string>();

    private sealed class RecordingEvaluator : IProjectEvaluator
    {
        private readonly ProjectEvaluation _evaluation;

        public RecordingEvaluator(ProjectEvaluation evaluation)
        {
            _evaluation = evaluation;
        }

        public GenerateOptions? LastOptions { get; private set; }

        public string? LastProjectPath { get; private set; }

        public Task<ProjectEvaluation> EvaluateAsync(
            string projectPath,
            GenerateOptions options,
            TextWriter log,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            LastProjectPath = projectPath;
            return Task.FromResult(_evaluation);
        }
    }

    private sealed class GenerateFixture : IDisposable
    {
        private readonly StringWriter _output = new();
        private readonly StringWriter _error = new();

        public GenerateFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "CobaltumOrm.GenerateTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "Fixture.csproj");
            File.WriteAllText(ProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            IntermediateOutputPath = Path.Combine(Root, "obj", "Debug", "net10.0");
            Evaluation = new ProjectEvaluation
            {
                ProjectPath = ProjectPath,
                ProjectDirectory = Root,
                TargetFramework = "net10.0",
                Configuration = "Debug",
                AssemblyName = "Fixture",
                RootNamespace = "Fixture",
                IntermediateOutputPath = IntermediateOutputPath,
                LangVersion = "latest",
                Nullable = "enable",
            };
            Evaluation.References.AddRange(ReferencePaths());
            Evaluator = new RecordingEvaluator(Evaluation);
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string IntermediateOutputPath { get; }

        public ProjectEvaluation Evaluation { get; }

        public RecordingEvaluator Evaluator { get; }

        public void AddSource(string fileName, string text)
        {
            var path = Path.Combine(Root, fileName);
            File.WriteAllText(path, text);
            Evaluation.CompileFiles.Add(path);
        }

        public void RemoveSource(string fileName) =>
            Evaluation.CompileFiles.Remove(Path.Combine(Root, fileName));

        public async Task<RunResult> RunAsync(params string[] args)
        {
            _output.GetStringBuilder().Clear();
            _error.GetStringBuilder().Clear();
            var application = new ToolApplication(
                _output,
                _error,
                new ThrowingProcessRunner(),
                Root,
                Evaluator);
            var exitCode = await application.RunAsync(args, CancellationToken.None);
            return new RunResult(exitCode, _output.ToString(), _error.ToString());
        }

        public void Dispose()
        {
            _output.Dispose();
            _error.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static IEnumerable<string> ReferencePaths() =>
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<int> RunAsync(
            System.Diagnostics.ProcessStartInfo startInfo,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("generate must not start a migration process.");
    }
}
