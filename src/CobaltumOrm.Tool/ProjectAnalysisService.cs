using CobaltumOrm.Compiler;
using CobaltumOrm.Analysis;

namespace CobaltumOrm.Tool;

/// <summary>
/// Evaluates a project and runs CobaltumORM generation without publishing its artifacts. Commands
/// that need project facts use this service so they construct generation requests the same way as
/// explicit generation.
/// </summary>
internal sealed class ProjectAnalysisService
{
    private const string InspectionOutputDirectoryName = "CobaltumOrmInspection";

    private readonly IProjectEvaluator _evaluator;

    public ProjectAnalysisService(IProjectEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    public async Task<ProjectAnalysis> AnalyzeAsync(
        string projectPath,
        ProjectEvaluationOptions options,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var evaluation = await EvaluateAsync(projectPath, options, log, cancellationToken)
            .ConfigureAwait(false);
        return Analyze(evaluation);
    }

    public async Task<ProjectEvaluation> EvaluateAsync(
        string projectPath,
        ProjectEvaluationOptions options,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var absoluteProjectPath = Path.GetFullPath(projectPath);
        var evaluation = await _evaluator
            .EvaluateAsync(absoluteProjectPath, options, log, cancellationToken)
            .ConfigureAwait(false);
        return NormalizeEvaluation(evaluation, absoluteProjectPath);
    }

    public ProjectAnalysis Analyze(
        ProjectEvaluation evaluation,
        ProjectGenerationOptions? options = null)
    {
        var normalized = NormalizeEvaluation(evaluation, evaluation.ProjectPath);
        var requestOptions = options ?? new ProjectGenerationOptions();
        var generatedNamespace = FirstNonEmpty(
            requestOptions.GeneratedNamespace,
            normalized.GeneratedNamespace,
            "CobaltumOrm.Generated") ?? "CobaltumOrm.Generated";
        var databaseProvider = FirstNonEmpty(
            requestOptions.DatabaseProvider,
            normalized.DatabaseProvider,
            DatabaseDialects.DefaultProviderName) ?? DatabaseDialects.DefaultProviderName;
        var outputDirectory = string.IsNullOrWhiteSpace(requestOptions.OutputDirectory)
            ? DefaultInspectionOutputDirectory(normalized)
            : Path.GetFullPath(requestOptions.OutputDirectory!);
        var result = CobaltumOrmGenerationEngine.Run(CreateGenerationRequest(
            normalized,
            outputDirectory,
            generatedNamespace,
            databaseProvider));
        return new ProjectAnalysis(normalized, result, generatedNamespace, databaseProvider);
    }

    internal static GenerationRequest CreateGenerationRequest(
        ProjectEvaluation evaluation,
        string outputDirectory,
        string? generatedNamespace = null,
        string? databaseProvider = null) =>
        new()
        {
            SourcePaths = evaluation.CompileFiles,
            ReferencePaths = evaluation.References,
            AdditionalFilePaths = evaluation.AdditionalFiles,
            MigrationSourcePaths = evaluation.MigrationSources,
            OutputDirectory = outputDirectory,
            DefineConstants = evaluation.DefineConstants,
            LangVersion = evaluation.LangVersion,
            Nullable = evaluation.Nullable,
            GeneratedNamespace = generatedNamespace ?? NullIfEmpty(evaluation.GeneratedNamespace),
            DatabaseProvider = databaseProvider ?? NullIfEmpty(evaluation.DatabaseProvider),
            AnalysisCacheDirectory = NullIfEmpty(evaluation.AnalysisCacheDirectory),
            AnalysisCacheEnabled = evaluation.AnalysisCacheEnabled,
            IncludeGeneratorOutput = true,
        };

    private static ProjectEvaluation NormalizeEvaluation(ProjectEvaluation evaluation, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(evaluation.ProjectPath))
        {
            evaluation.ProjectPath = Path.GetFullPath(projectPath);
        }
        else
        {
            evaluation.ProjectPath = Path.GetFullPath(evaluation.ProjectPath);
        }

        if (string.IsNullOrWhiteSpace(evaluation.ProjectDirectory))
        {
            evaluation.ProjectDirectory = Path.GetDirectoryName(evaluation.ProjectPath)!;
        }
        else
        {
            evaluation.ProjectDirectory = Path.GetFullPath(evaluation.ProjectDirectory);
        }

        if (string.IsNullOrWhiteSpace(evaluation.TargetFramework))
        {
            throw new ToolExecutionException(
                $"Project '{evaluation.ProjectPath}' did not report a target framework. " +
                "Pass --framework when the project targets more than one framework.");
        }

        return evaluation;
    }

    private static string DefaultInspectionOutputDirectory(ProjectEvaluation evaluation)
    {
        var intermediate = string.IsNullOrWhiteSpace(evaluation.IntermediateOutputPath)
            ? Path.Combine(evaluation.ProjectDirectory, "obj")
            : evaluation.IntermediateOutputPath;
        return Path.GetFullPath(Path.Combine(intermediate, InspectionOutputDirectoryName));
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(
        value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

/// <summary>Overrides used when a caller needs a generation result without writing it.</summary>
internal sealed class ProjectGenerationOptions
{
    public string? OutputDirectory { get; init; }

    public string? GeneratedNamespace { get; init; }

    public string? DatabaseProvider { get; init; }
}

/// <summary>The evaluated project and the generation result produced from it.</summary>
internal sealed class ProjectAnalysis
{
    public ProjectAnalysis(
        ProjectEvaluation evaluation,
        GenerationResult generation,
        string generatedNamespace,
        string? databaseProvider)
    {
        Evaluation = evaluation;
        Generation = generation;
        GeneratedNamespace = generatedNamespace;
        DatabaseProvider = databaseProvider;
    }

    public ProjectEvaluation Evaluation { get; }

    public GenerationResult Generation { get; }

    public string GeneratedNamespace { get; }

    public string? DatabaseProvider { get; }
}
