using System.Text.Json.Serialization;

namespace CobaltumOrm.Tool;

internal sealed class McpInspectProjectResult
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; init; } = string.Empty;

    [JsonPropertyName("targetFramework")]
    public string TargetFramework { get; init; } = string.Empty;

    [JsonPropertyName("configuration")]
    public string Configuration { get; init; } = string.Empty;

    [JsonPropertyName("assemblyName")]
    public string AssemblyName { get; init; } = string.Empty;

    [JsonPropertyName("rootNamespace")]
    public string RootNamespace { get; init; } = string.Empty;

    [JsonPropertyName("generatedNamespace")]
    public string GeneratedNamespace { get; init; } = string.Empty;

    [JsonPropertyName("databaseProvider")]
    public string? DatabaseProvider { get; init; }

    [JsonPropertyName("analysisSucceeded")]
    public bool AnalysisSucceeded { get; init; }

    [JsonPropertyName("sourcePaths")]
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("additionalFilePaths")]
    public IReadOnlyList<string> AdditionalFilePaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("migrationSourcePaths")]
    public IReadOnlyList<string> MigrationSourcePaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("migrationInputPaths")]
    public IReadOnlyList<string> MigrationInputPaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("cobaltumOrmPackageReferences")]
    public IReadOnlyList<McpPackageReferenceResult> CobaltumOrmPackageReferences { get; init; } =
        Array.Empty<McpPackageReferenceResult>();

    [JsonPropertyName("migrationProjectReferencePaths")]
    public IReadOnlyList<string> MigrationProjectReferencePaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("sourceGeneratorPaths")]
    public IReadOnlyList<string> SourceGeneratorPaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("generatedArtifacts")]
    public IReadOnlyList<McpInspectArtifactResult> GeneratedArtifacts { get; init; } =
        Array.Empty<McpInspectArtifactResult>();

    [JsonPropertyName("analyzedSourcePaths")]
    public IReadOnlyList<string> AnalyzedSourcePaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("processedSourcePaths")]
    public IReadOnlyList<string> ProcessedSourcePaths { get; init; } = Array.Empty<string>();

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<McpGenerationDiagnosticResult> Diagnostics { get; init; } =
        Array.Empty<McpGenerationDiagnosticResult>();
}

internal sealed class McpDoctorProjectResult
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; init; } = string.Empty;

    [JsonPropertyName("targetFramework")]
    public string TargetFramework { get; init; } = string.Empty;

    [JsonPropertyName("configuration")]
    public string Configuration { get; init; } = string.Empty;

    [JsonPropertyName("checks")]
    public IReadOnlyList<McpDoctorCheckResult> Checks { get; init; } = Array.Empty<McpDoctorCheckResult>();

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<McpGenerationDiagnosticResult> Diagnostics { get; init; } =
        Array.Empty<McpGenerationDiagnosticResult>();
}

internal sealed class McpPackageReferenceResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

internal sealed class McpInspectArtifactResult
{
    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }
}

internal sealed class McpGenerationDiagnosticResult
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    [JsonPropertyName("startLine")]
    public int StartLine { get; init; }

    [JsonPropertyName("startColumn")]
    public int StartColumn { get; init; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; init; }

    [JsonPropertyName("endColumn")]
    public int EndColumn { get; init; }

    [JsonPropertyName("helpUri")]
    public string? HelpUri { get; init; }
}

internal sealed class McpDoctorCheckResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("nextAction")]
    public string? NextAction { get; init; }

    [JsonPropertyName("helpUri")]
    public string? HelpUri { get; init; }
}

internal sealed class McpGeneratedArtifactListResult
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<McpGeneratedArtifactMetadataResult> Artifacts { get; init; } =
        Array.Empty<McpGeneratedArtifactMetadataResult>();
}

internal sealed class McpGeneratedArtifactMetadataResult
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }
}

internal sealed class McpReadGeneratedArtifactResult
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

internal sealed class McpExplainDiagnosticResult
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; init; } = string.Empty;

    [JsonPropertyName("helpUri")]
    public string HelpUri { get; init; } = string.Empty;

    [JsonPropertyName("section")]
    public string Section { get; init; } = string.Empty;
}
