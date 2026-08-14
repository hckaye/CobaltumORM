using CobaltumOrm.Tool;

namespace CobaltumOrm.Tool.Tests;

internal sealed class McpTestFixture : IDisposable
{
    public McpTestFixture()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "CobaltumOrm.McpTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ProjectPath = Path.Combine(Root, "Fixture.csproj");
        SourcePath = Path.Combine(Root, "Input.cs");
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
        Evaluation.CobaltumOrmPackageReferences.Add(new EvaluatedPackageReference("CobaltumOrm", "0.0.5"));
        Evaluation.CobaltumOrmPackageReferences.Add(
            new EvaluatedPackageReference("CobaltumOrm.SourceGenerator", "0.0.5"));
        Evaluation.CobaltumOrmSourceGeneratorPaths.Add(typeof(ProjectAnalysisService).Assembly.Location);
        Evaluation.CompilerTaskAssembly = typeof(ProjectAnalysisService).Assembly.Location;
        Evaluation.CompilerVisibleProperties.Add("CobaltumOrmDatabaseProvider");
        Evaluation.CompilerVisibleProperties.Add("CobaltumOrmGeneratedNamespace");

        Evaluator = new FakeProjectEvaluator(Evaluation);
        ProcessRunner = new RecordingProcessRunner();
    }

    public string Root { get; }

    public string ProjectPath { get; }

    public string SourcePath { get; }

    public ProjectEvaluation Evaluation { get; }

    public FakeProjectEvaluator Evaluator { get; }

    public RecordingProcessRunner ProcessRunner { get; }

    public ProjectInspectionOptions Options(
        string configuration = "Release",
        string? framework = "net10.0",
        bool noRestore = true) => new()
        {
            Project = ProjectPath,
            Configuration = configuration,
            Framework = framework,
            NoRestore = noRestore,
        };

    public CobaltumMcpTools Tools(ProjectInspectionOptions? options = null) => new(
        new CobaltumMcpProjectService(
            ProjectPath,
            options ?? Options(),
            Evaluator,
            TextWriter.Null),
        McpDocumentation.Load());

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

    private static IEnumerable<string> ReferencePaths() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}

internal sealed class FakeProjectEvaluator : IProjectEvaluator
{
    private readonly ProjectEvaluation _evaluation;

    public FakeProjectEvaluator(ProjectEvaluation evaluation)
    {
        _evaluation = evaluation;
    }

    public int CallCount { get; private set; }

    public ProjectEvaluationOptions? LastOptions { get; private set; }

    public Task<ProjectEvaluation> EvaluateAsync(
        string projectPath,
        ProjectEvaluationOptions options,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastOptions = options;
        _evaluation.ProjectPath = projectPath;
        _evaluation.ProjectDirectory = Path.GetDirectoryName(projectPath)!;
        _evaluation.Configuration = options.Configuration;
        if (options.Framework is not null)
        {
            _evaluation.TargetFramework = options.Framework;
        }

        return Task.FromResult(_evaluation);
    }
}

internal sealed class RecordingProcessRunner : IProcessRunner
{
    public int CallCount { get; private set; }

    public Task<int> RunAsync(
        System.Diagnostics.ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("MCP analysis must not start a migration process.");
    }
}
