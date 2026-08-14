using System.Text.Json;
using CobaltumOrm.Compiler;
using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class ProjectInspectionCommandTests
{
    private const string DiagnosticsUrl =
        "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md#";

    [Fact]
    public void DocumentedGenerationDiagnosticCodesHaveCanonicalHelpUris()
    {
        foreach (var number in Enumerable.Range(1, 10).Concat(Enumerable.Range(100, 10)))
        {
            var code = "COB" + number.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(DiagnosticsUrl + code.ToLowerInvariant(), GenerationDiagnosticHelpLinks.ForCode(code));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cob100")]
    [InlineData("COB000")]
    [InlineData("COB011")]
    [InlineData("COB099")]
    [InlineData("COB110")]
    [InlineData("COB1000")]
    [InlineData("COB1O0")]
    public void UndocumentedOrMalformedGenerationDiagnosticCodesHaveNoHelpUri(string? code)
    {
        Assert.Null(GenerationDiagnosticHelpLinks.ForCode(code));
    }

    [Fact]
    public async Task HelpListsInspectAndDoctorWithTheirEvaluationOptions()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new ThrowingProcessRunner());

        var exitCode = await application.RunAsync(new[] { "--help" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("cobaltum inspect --project <path>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("cobaltum doctor --project <path>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("[--configuration <name>] [--no-restore]", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task InspectJsonIsStableAndDoesNotWriteProjectOrSourceFiles()
    {
        using var fixture = new InspectionFixture();
        var projectBefore = File.ReadAllText(fixture.ProjectPath);
        var sourceBefore = File.ReadAllText(fixture.SourcePath);

        var first = await fixture.RunAsync(
            "inspect", "--project", fixture.ProjectPath, "--configuration", "Release",
            "--framework", "net10.0", "--no-restore", "--format", "json");
        var second = await fixture.RunAsync(
            "inspect", "--project", fixture.ProjectPath, "--configuration", "Release",
            "--framework", "net10.0", "--no-restore", "--format", "json");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.Output, second.Output);
        Assert.Equal(string.Empty, first.Error);
        Assert.Equal(projectBefore, File.ReadAllText(fixture.ProjectPath));
        Assert.Equal(sourceBefore, File.ReadAllText(fixture.SourcePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obj", "CobaltumOrmInspection")));
        Assert.True(fixture.Evaluator.LastOptions!.NoRestore);
        Assert.Equal("Release", fixture.Evaluator.LastOptions.Configuration);
        Assert.Equal("net10.0", fixture.Evaluator.LastOptions.Framework);

        using var document = JsonDocument.Parse(first.Output);
        var root = document.RootElement;
        Assert.Equal(ProjectInspectionOutput.FormatVersion, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(fixture.ProjectPath, root.GetProperty("projectPath").GetString());
        Assert.Equal("net10.0", root.GetProperty("targetFramework").GetString());
        Assert.Equal("Release", root.GetProperty("configuration").GetString());
        Assert.True(root.GetProperty("analysisSucceeded").GetBoolean());
        Assert.Equal(
            new[]
            {
                "formatVersion", "projectPath", "targetFramework", "configuration", "assemblyName",
                "rootNamespace", "generatedNamespace", "databaseProvider", "analysisSucceeded",
                "sourcePaths", "additionalFilePaths", "migrationSourcePaths", "migrationInputPaths",
                "cobaltumOrmPackageReferences", "migrationProjectReferencePaths", "sourceGeneratorPaths",
                "generatedArtifacts", "analyzedSourcePaths", "processedSourcePaths", "diagnostics",
            },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            new[] { fixture.SourcePath },
            root.GetProperty("sourcePaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            new[] { fixture.MigrationProjectPath },
            root.GetProperty("migrationProjectReferencePaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            new[] { "CobaltumOrm", "CobaltumOrm.SourceGenerator" },
            root.GetProperty("cobaltumOrmPackageReferences")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task InspectTextSummarizesTheEvaluatedProject()
    {
        using var fixture = new InspectionFixture();

        var result = await fixture.RunAsync("inspect", "--project", fixture.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Project: " + fixture.ProjectPath, result.Output, StringComparison.Ordinal);
        Assert.Contains("Target: net10.0 (Debug)", result.Output, StringComparison.Ordinal);
        Assert.Contains("Generated namespace: Fixture.Generated", result.Output, StringComparison.Ordinal);
        Assert.Contains("Analysis: succeeded", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task DoctorReturnsZeroForWarningsAndReportsTheNextAction()
    {
        using var fixture = new InspectionFixture();
        fixture.Evaluation.MigrationProjectReferencePaths.Clear();
        fixture.Evaluation.MigrationInputPaths.Clear();

        var result = await fixture.RunAsync("doctor", "--project", fixture.ProjectPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Doctor: warning", result.Output, StringComparison.Ordinal);
        Assert.Contains("[warning] migration-inputs", result.Output, StringComparison.Ordinal);
        Assert.Contains("Next action:", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task DoctorReportsMissingSourceGeneratorWiringAsAnError()
    {
        using var fixture = new InspectionFixture();
        fixture.Evaluation.CobaltumOrmSourceGeneratorPaths.Clear();
        fixture.Evaluation.CompilerTaskAssembly = string.Empty;

        var result = await fixture.RunAsync("doctor", "--project", fixture.ProjectPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Doctor: error", result.Output, StringComparison.Ordinal);
        Assert.Contains("[error] cobaltumorm-wiring", result.Output, StringComparison.Ordinal);
        Assert.Contains("Add CobaltumOrm.SourceGenerator", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task DoctorReportsAnInvalidTransformTaskAssemblyAsAnError()
    {
        using var fixture = new InspectionFixture();
        fixture.Evaluation.CompilerTaskAssembly = Path.Combine(fixture.Root, "missing", "CobaltumOrm.Compiler.dll");

        var result = await fixture.RunAsync("doctor", "--project", fixture.ProjectPath, "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var wiring = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("id").GetString() == "cobaltumorm-wiring");
        Assert.Equal("error", wiring.GetProperty("status").GetString());
        Assert.Contains("does not exist", wiring.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidProviderProducesJsonDiagnosticsAndDoctorErrorExitCode()
    {
        using var fixture = new InspectionFixture();
        fixture.Evaluation.DatabaseProvider = "UnsupportedProvider";

        var inspect = await fixture.RunAsync(
            "inspect", "--project", fixture.ProjectPath, "--format", "json");
        var doctor = await fixture.RunAsync(
            "doctor", "--project", fixture.ProjectPath, "--format", "json");

        Assert.Equal(1, inspect.ExitCode);
        Assert.Equal(string.Empty, inspect.Error);
        using (var document = JsonDocument.Parse(inspect.Output))
        {
            Assert.False(document.RootElement.GetProperty("analysisSucceeded").GetBoolean());
            var diagnostic = Assert.Single(document.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal("COB008", diagnostic.GetProperty("code").GetString());
            Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
            Assert.NotNull(diagnostic.GetProperty("helpUri").GetString());
        }

        Assert.Equal(1, doctor.ExitCode);
        Assert.Equal(string.Empty, doctor.Error);
        using var doctorDocument = JsonDocument.Parse(doctor.Output);
        Assert.Equal(ProjectInspectionOutput.FormatVersion, doctorDocument.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("error", doctorDocument.RootElement.GetProperty("status").GetString());
        var providerCheck = doctorDocument.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("id").GetString() == "database-provider");
        Assert.Equal("error", providerCheck.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DoctorReportsAMigrationProviderPackageMismatch()
    {
        using var fixture = new InspectionFixture();
        fixture.Evaluation.DatabaseProvider = "Sqlite";
        fixture.Evaluation.CobaltumOrmPackageReferences.Add(
            new EvaluatedPackageReference("CobaltumOrm.Migrations.PostgreSql", "0.0.5"));

        var result = await fixture.RunAsync("doctor", "--project", fixture.ProjectPath, "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var provider = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("id").GetString() == "database-provider");
        Assert.Equal("error", provider.GetProperty("status").GetString());
        Assert.Contains("does not match", provider.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("CobaltumOrm.Migrations.PostgreSql", provider.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectReportsSqlDiagnosticsWithoutWritingGeneratedFiles()
    {
        using var fixture = new InspectionFixture();
        File.WriteAllText(fixture.SourcePath, """
            using System.Data.Common;
            using CobaltumOrm;

            public static class Input
            {
                public static object Read(DbConnection connection) =>
                    connection.Query("SELECT missing FROM users").ReadAsync();
            }
            """);

        var result = await fixture.RunAsync(
            "inspect", "--project", fixture.ProjectPath, "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obj", "CobaltumOrmInspection")));
        using var document = JsonDocument.Parse(result.Output);
        var diagnostics = document.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.StartsWith("SQL", diagnostic.GetProperty("code").GetString(), StringComparison.Ordinal);
            Assert.Equal(fixture.SourcePath, diagnostic.GetProperty("filePath").GetString());
            Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
        });
    }

    [Fact]
    public async Task InspectJsonLinksCompilerTaskDiagnosticsToTheGuide()
    {
        using var fixture = new InspectionFixture();
        File.WriteAllText(fixture.SourcePath, """
            using System.Data.Common;
            using CobaltumOrm;

            public static class Input
            {
                public static object Read(DbConnection connection, string sql) =>
                    connection.Query(sql).ReadAsync();
            }
            """);

        var result = await fixture.RunAsync(
            "inspect", "--project", fixture.ProjectPath, "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var diagnostic = document.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "COB100");
        Assert.Equal(DiagnosticsUrl + "cob100", diagnostic.GetProperty("helpUri").GetString());
    }

    [Fact]
    public async Task AmbiguousTargetFrameworkUsesTheExistingErrorRoute()
    {
        using var fixture = new InspectionFixture();
        fixture.Evaluation.TargetFramework = string.Empty;

        var result = await fixture.RunAsync("inspect", "--project", fixture.ProjectPath, "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("--framework", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidInspectionOptionsUseTheUsageErrorRouteWithoutJsonOutput()
    {
        using var fixture = new InspectionFixture();

        var result = await fixture.RunAsync(
            "inspect", "--project", fixture.ProjectPath, "--format", "yaml");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("Unsupported format 'yaml'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRequiresAProjectPath()
    {
        using var fixture = new InspectionFixture();

        var result = await fixture.RunAsync("inspect", "--format", "json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Contains("requires --project", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectJsonEscapesValuesAndKeepsArtifactsAndDiagnosticsOrdered()
    {
        var evaluation = new ProjectEvaluation
        {
            ProjectPath = "/tmp/Inspection/Fixture.csproj",
            ProjectDirectory = "/tmp/Inspection",
            TargetFramework = "net10.0",
            Configuration = "Debug",
            AssemblyName = "Fixture",
            RootNamespace = "Fixture",
        };
        evaluation.CompileFiles.AddRange(new[] { "/tmp/Inspection/Z.cs", "/tmp/Inspection/A.cs" });
        var result = new GenerationResult(
            false,
            new[]
            {
                new GenerationDiagnostic(
                    "COB008",
                    "Provider \"invalid\"\nchoose another",
                    "/tmp/Inspection/Z.cs",
                    4,
                    3,
                    4,
                    8,
                    true,
                    "https://example.test/diagnostics#cob008"),
            },
            new[]
            {
                new GeneratedArtifact("z.g.cs", "", GeneratedArtifactKind.Generated, null),
                new GeneratedArtifact("a.cobaltum.cs", "", GeneratedArtifactKind.Transformed, "/tmp/Inspection/A.cs"),
            },
            evaluation.CompileFiles,
            new[] { "/tmp/Inspection/Z.cs" });

        var json = ProjectInspectionOutput.WriteInspectJson(
            new ProjectAnalysis(evaluation, result, "Fixture.Generated", "UnsupportedProvider"));

        Assert.Contains("\\n", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[] { "/tmp/Inspection/A.cs", "/tmp/Inspection/Z.cs" },
            document.RootElement.GetProperty("sourcePaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            new[] { "a.cobaltum.cs", "z.g.cs" },
            document.RootElement.GetProperty("generatedArtifacts")
                .EnumerateArray()
                .Select(item => item.GetProperty("fileName").GetString()));
        var diagnostic = Assert.Single(document.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("Provider \"invalid\"\nchoose another", diagnostic.GetProperty("message").GetString());
        Assert.Equal(4, diagnostic.GetProperty("startLine").GetInt32());
        Assert.Equal("https://example.test/diagnostics#cob008", diagnostic.GetProperty("helpUri").GetString());
    }

    private sealed class InspectionFixture : IDisposable
    {
        private readonly StringWriter _output = new();
        private readonly StringWriter _error = new();

        public InspectionFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "CobaltumOrm.InspectionTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "Fixture.csproj");
            SourcePath = Path.Combine(Root, "Input.cs");
            MigrationProjectPath = Path.Combine(Root, "Fixture.Migrations.csproj");
            File.WriteAllText(ProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.WriteAllText(SourcePath, "public sealed class Input { public int Id { get; set; } }");
            Evaluation = new ProjectEvaluation
            {
                ProjectPath = ProjectPath,
                ProjectDirectory = Root,
                TargetFramework = "net10.0",
                Configuration = "Debug",
                AssemblyName = "Fixture",
                RootNamespace = "Fixture",
                GeneratedNamespace = "Fixture.Generated",
                DatabaseProvider = "PostgreSql",
                AnalysisCacheEnabled = false,
            };
            Evaluation.CompileFiles.Add(SourcePath);
            Evaluation.References.AddRange(ReferencePaths());
            Evaluation.References.Add(typeof(CobaltumOrm.CobaltumQueryDefinition<>).Assembly.Location);
            Evaluation.CobaltumOrmPackageReferences.Add(new EvaluatedPackageReference("CobaltumOrm.SourceGenerator", "0.0.5"));
            Evaluation.CobaltumOrmPackageReferences.Add(new EvaluatedPackageReference("CobaltumOrm", "0.0.5"));
            Evaluation.CobaltumOrmSourceGeneratorPaths.Add(typeof(ProjectAnalysisService).Assembly.Location);
            Evaluation.CompilerTaskAssembly = typeof(ProjectAnalysisService).Assembly.Location;
            Evaluation.CompilerVisibleProperties.Add("CobaltumOrmGeneratedNamespace");
            Evaluation.CompilerVisibleProperties.Add("CobaltumOrmDatabaseProvider");
            Evaluation.MigrationProjectReferencePaths.Add(MigrationProjectPath);
            Evaluation.MigrationInputPaths.Add(Path.Combine(Root, "Migrations", "Create.cs"));
            Evaluator = new RecordingEvaluator(Evaluation);
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string SourcePath { get; }

        public string MigrationProjectPath { get; }

        public ProjectEvaluation Evaluation { get; }

        public RecordingEvaluator Evaluator { get; }

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

    private sealed class RecordingEvaluator : IProjectEvaluator
    {
        private readonly ProjectEvaluation _evaluation;

        public RecordingEvaluator(ProjectEvaluation evaluation)
        {
            _evaluation = evaluation;
        }

        public ProjectEvaluationOptions? LastOptions { get; private set; }

        public Task<ProjectEvaluation> EvaluateAsync(
            string projectPath,
            ProjectEvaluationOptions options,
            TextWriter log,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            _evaluation.Configuration = options.Configuration;
            if (options.Framework is not null)
            {
                _evaluation.TargetFramework = options.Framework;
            }

            return Task.FromResult(_evaluation);
        }
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<int> RunAsync(
            System.Diagnostics.ProcessStartInfo startInfo,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("inspection must not start a migration process.");
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);
}
