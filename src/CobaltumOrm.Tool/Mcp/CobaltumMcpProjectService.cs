using CobaltumOrm.Compiler;

namespace CobaltumOrm.Tool;

internal sealed class CobaltumMcpProjectService
{
    private readonly string _projectPath;
    private readonly ProjectEvaluationOptions _options;
    private readonly IProjectEvaluator _evaluator;
    private readonly TextWriter _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CobaltumMcpProjectSnapshot? _latest;

    public CobaltumMcpProjectService(
        string projectPath,
        ProjectEvaluationOptions options,
        IProjectEvaluator evaluator,
        TextWriter log)
    {
        _projectPath = projectPath;
        _options = options;
        _evaluator = evaluator;
        _log = log;
    }

    public async Task<CobaltumMcpProjectSnapshot> GetSnapshotAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _latest is not null)
            {
                return _latest;
            }

            var analysis = await new ProjectAnalysisService(_evaluator)
                .AnalyzeAsync(_projectPath, _options, _log, cancellationToken)
                .ConfigureAwait(false);
            _latest = CobaltumMcpProjectSnapshot.Create(analysis);
            return _latest;
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class CobaltumMcpProjectSnapshot
{
    private readonly IReadOnlyDictionary<string, GeneratedArtifact> _artifactsByName;

    private CobaltumMcpProjectSnapshot(
        ProjectAnalysis analysis,
        ProjectDoctorReport doctor,
        IReadOnlyList<GeneratedArtifact> artifacts,
        IReadOnlyDictionary<string, GeneratedArtifact> artifactsByName)
    {
        Analysis = analysis;
        Doctor = doctor;
        Artifacts = artifacts;
        _artifactsByName = artifactsByName;
        InspectJson = ProjectInspectionOutput.WriteInspectJson(analysis);
        DoctorJson = ProjectInspectionOutput.WriteDoctorJson(doctor);
    }

    public ProjectAnalysis Analysis { get; }

    public ProjectDoctorReport Doctor { get; }

    public IReadOnlyList<GeneratedArtifact> Artifacts { get; }

    public string InspectJson { get; }

    public string DoctorJson { get; }

    public bool TryGetArtifact(string name, out GeneratedArtifact artifact) =>
        _artifactsByName.TryGetValue(name, out artifact!);

    public static CobaltumMcpProjectSnapshot Create(ProjectAnalysis analysis)
    {
        var artifacts = ProjectInspectionOutput
            .OrderGeneratedArtifacts(analysis.Generation.Artifacts)
            .ToArray();
        var artifactsByName = new Dictionary<string, GeneratedArtifact>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (!CobaltumMcpTools.IsSafeArtifactName(artifact.FileName))
            {
                throw new ToolExecutionException(
                    $"Generation returned an invalid artifact name '{artifact.FileName}'.");
            }

            if (!artifactsByName.TryAdd(artifact.FileName, artifact))
            {
                throw new ToolExecutionException(
                    $"Generation returned the artifact name '{artifact.FileName}' more than once.");
            }
        }

        return new CobaltumMcpProjectSnapshot(
            analysis,
            ProjectDoctor.Diagnose(analysis),
            artifacts,
            artifactsByName);
    }
}
