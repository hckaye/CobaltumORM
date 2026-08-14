using System.ComponentModel;
using System.Text.Json;
using CobaltumOrm.Compiler;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CobaltumOrm.Tool;

[McpServerToolType]
internal sealed class CobaltumMcpTools
{
    private readonly CobaltumMcpProjectService _project;
    private readonly McpDocumentation _documentation;

    public CobaltumMcpTools(
        CobaltumMcpProjectService project,
        McpDocumentation documentation)
    {
        _project = project;
        _documentation = documentation;
    }

    [McpServerTool(
        Name = "inspect_project",
        ReadOnly = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpInspectProjectResult))]
    [Description(
        "Returns formatVersion 1 project and generated-artifact metadata for the project selected at startup. " +
        "Analysis uses project sources without connecting to a database or publishing generated files.")]
    public async Task<CallToolResult> InspectProject(CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var analysis = snapshot.Analysis;
        var summary =
            $"Project analysis {(analysis.Generation.Succeeded ? "succeeded" : "failed")} for " +
            $"'{analysis.Evaluation.ProjectPath}'. " +
            $"Returned {analysis.Generation.Artifacts.Count} generated artifact(s) and " +
            $"{analysis.Generation.Diagnostics.Count} diagnostic(s).";
        return Success(ParseJson(snapshot.InspectJson), summary);
    }

    [McpServerTool(
        Name = "doctor_project",
        ReadOnly = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDoctorProjectResult))]
    [Description(
        "Returns formatVersion 1 project checks and generation diagnostics, including error statuses as data. " +
        "Analysis does not connect to a database or publish generated files.")]
    public async Task<CallToolResult> DoctorProject(CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var summary =
            $"Project doctor status is {DoctorStatusText(snapshot.Doctor.Status)}. " +
            $"Returned {snapshot.Doctor.Checks.Count} check(s) and " +
            $"{snapshot.Analysis.Generation.Diagnostics.Count} diagnostic(s).";
        return Success(ParseJson(snapshot.DoctorJson), summary);
    }

    [McpServerTool(
        Name = "list_generated_artifacts",
        ReadOnly = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpGeneratedArtifactListResult))]
    [Description(
        "Returns deterministic formatVersion 1 metadata for source artifacts generated from the startup project. " +
        "It does not connect to a database or write the generated sources to disk.")]
    public async Task<CallToolResult> ListGeneratedArtifacts(CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        var artifacts = snapshot.Artifacts.Select(ArtifactMetadata).ToArray();
        var result = new McpGeneratedArtifactListResult
        {
            FormatVersion = ProjectInspectionOutput.FormatVersion,
            Artifacts = artifacts,
        };
        var text = artifacts.Length == 0
            ? "No generated artifacts were returned."
            : "Generated artifacts:\n" + string.Join(
                "\n",
                artifacts.Select(artifact => $"- {artifact.Name} ({artifact.Kind})"));
        return Success(JsonSerializer.SerializeToElement(result), text);
    }

    [McpServerTool(
        Name = "read_generated_artifact",
        ReadOnly = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpReadGeneratedArtifactResult))]
    [Description(
        "Returns the generated source and metadata for one exact name from list_generated_artifacts. " +
        "It reads only the in-memory generation result and does not read a caller-supplied path or write files.")]
    public async Task<CallToolResult> ReadGeneratedArtifact(
        [Description("Exact artifacts[].name value returned by list_generated_artifacts.")]
        string artifactName,
        CancellationToken cancellationToken)
    {
        if (!IsSafeArtifactName(artifactName))
        {
            throw new McpException(
                "artifactName must be an exact file name returned by list_generated_artifacts. " +
                "Paths and '..' are not allowed.");
        }

        var snapshot = await SnapshotAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        if (!snapshot.TryGetArtifact(artifactName, out var artifact))
        {
            throw new McpException(
                $"Unknown generated artifact '{artifactName}'. Call list_generated_artifacts and use one returned name.");
        }

        var result = new McpReadGeneratedArtifactResult
        {
            FormatVersion = ProjectInspectionOutput.FormatVersion,
            Name = artifact.FileName,
            Kind = ProjectInspectionOutput.ArtifactKindText(artifact.Kind),
            SourcePath = artifact.SourcePath,
            Source = artifact.Text,
        };
        return Success(
            JsonSerializer.SerializeToElement(result),
            $"Generated artifact '{artifact.FileName}' ({result.Kind}):\n\n{artifact.Text}");
    }

    [McpServerTool(
        Name = "explain_diagnostic",
        ReadOnly = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpExplainDiagnosticResult))]
    [Description(
        "Returns the checked-in English or Japanese section for a documented CobaltumORM diagnostic and its canonical help URI. " +
        "It reads only documentation embedded in the installed tool.")]
    public CallToolResult ExplainDiagnostic(
        [Description("Documented code COB001-COB010 or COB100-COB109.")]
        string code,
        [Description("Documentation language: en or ja.")]
        string language)
    {
        var explanation = _documentation.ExplainDiagnostic(code, language);
        var result = new McpExplainDiagnosticResult
        {
            FormatVersion = explanation.FormatVersion,
            Code = explanation.Code,
            Language = explanation.Language,
            HelpUri = explanation.HelpUri,
            Section = explanation.Section,
        };
        return Success(
            JsonSerializer.SerializeToElement(result),
            explanation.Section + "\n\nHelp: " + explanation.HelpUri);
    }

    internal static bool IsSafeArtifactName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name != "." &&
        !name.Contains("..", StringComparison.Ordinal) &&
        name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0 &&
        !Path.IsPathRooted(name);

    private async Task<CobaltumMcpProjectSnapshot> SnapshotAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _project.GetSnapshotAsync(refresh, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new McpException("Project analysis failed: " + exception.Message, exception);
        }
    }

    private static McpGeneratedArtifactMetadataResult ArtifactMetadata(GeneratedArtifact artifact) => new()
    {
        Name = artifact.FileName,
        Kind = ProjectInspectionOutput.ArtifactKindText(artifact.Kind),
        SourcePath = artifact.SourcePath,
    };

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CallToolResult Success(JsonElement structuredContent, string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = structuredContent,
    };

    private static string DoctorStatusText(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "ok",
        DoctorStatus.Warning => "warning",
        DoctorStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
