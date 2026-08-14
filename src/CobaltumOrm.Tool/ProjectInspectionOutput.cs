using System.Buffers;
using System.Text;
using System.Text.Json;
using CobaltumOrm.Compiler;

namespace CobaltumOrm.Tool;

/// <summary>Writes the versioned machine and text reports for project inspection commands.</summary>
internal static class ProjectInspectionOutput
{
    public const int FormatVersion = 1;

    public static string WriteInspectJson(ProjectAnalysis analysis) => WriteJson(writer =>
    {
        var evaluation = analysis.Evaluation;
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("projectPath", evaluation.ProjectPath);
        writer.WriteString("targetFramework", evaluation.TargetFramework);
        writer.WriteString("configuration", evaluation.Configuration);
        writer.WriteString("assemblyName", evaluation.AssemblyName);
        writer.WriteString("rootNamespace", evaluation.RootNamespace);
        writer.WriteString("generatedNamespace", analysis.GeneratedNamespace);
        WriteNullableString(writer, "databaseProvider", analysis.DatabaseProvider);
        writer.WriteBoolean("analysisSucceeded", analysis.Generation.Succeeded);
        WriteStringArray(writer, "sourcePaths", evaluation.CompileFiles);
        WriteStringArray(writer, "additionalFilePaths", evaluation.AdditionalFiles);
        WriteStringArray(writer, "migrationSourcePaths", evaluation.MigrationSources);
        WriteStringArray(writer, "migrationInputPaths", evaluation.MigrationInputPaths);
        WritePackageReferences(writer, evaluation.CobaltumOrmPackageReferences);
        WriteStringArray(writer, "migrationProjectReferencePaths", evaluation.MigrationProjectReferencePaths);
        WriteStringArray(writer, "sourceGeneratorPaths", evaluation.CobaltumOrmSourceGeneratorPaths);
        WriteGeneratedArtifacts(writer, analysis.Generation.Artifacts);
        WriteStringArray(writer, "analyzedSourcePaths", analysis.Generation.AnalyzedSourcePaths);
        WriteStringArray(writer, "processedSourcePaths", analysis.Generation.ProcessedSourcePaths);
        WriteDiagnostics(writer, analysis.Generation.Diagnostics);
        writer.WriteEndObject();
    });

    public static string WriteDoctorJson(ProjectDoctorReport report) => WriteJson(writer =>
    {
        var evaluation = report.Analysis.Evaluation;
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("status", StatusText(report.Status));
        writer.WriteString("projectPath", evaluation.ProjectPath);
        writer.WriteString("targetFramework", evaluation.TargetFramework);
        writer.WriteString("configuration", evaluation.Configuration);
        writer.WritePropertyName("checks");
        writer.WriteStartArray();
        foreach (var check in report.Checks)
        {
            writer.WriteStartObject();
            writer.WriteString("id", check.Id);
            writer.WriteString("status", StatusText(check.Status));
            writer.WriteString("message", check.Message);
            WriteNullableString(writer, "nextAction", check.NextAction);
            WriteNullableString(writer, "helpUri", check.HelpUri);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteDiagnostics(writer, report.Analysis.Generation.Diagnostics);
        writer.WriteEndObject();
    });

    public static async Task WriteInspectTextAsync(TextWriter writer, ProjectAnalysis analysis)
    {
        var evaluation = analysis.Evaluation;
        await writer.WriteLineAsync("Project: " + evaluation.ProjectPath).ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Target: " + evaluation.TargetFramework + " (" + evaluation.Configuration + ")").ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Assembly: " + Display(evaluation.AssemblyName) + " | Root namespace: " + Display(evaluation.RootNamespace))
            .ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Generated namespace: " + analysis.GeneratedNamespace + " | Provider: " + Display(analysis.DatabaseProvider))
            .ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Inputs: " + analysis.Evaluation.CompileFiles.Count + " source, " +
            analysis.Evaluation.AdditionalFiles.Count + " additional, " +
            analysis.Evaluation.MigrationInputPaths.Count + " migration").ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Artifacts: " + analysis.Generation.Artifacts.Count + " | Analysis: " +
            (analysis.Generation.Succeeded ? "succeeded" : "failed")).ConfigureAwait(false);
        await WriteDiagnosticsTextAsync(writer, analysis.Generation.Diagnostics).ConfigureAwait(false);
    }

    public static async Task WriteDoctorTextAsync(TextWriter writer, ProjectDoctorReport report)
    {
        await writer.WriteLineAsync("Project: " + report.Analysis.Evaluation.ProjectPath).ConfigureAwait(false);
        await writer.WriteLineAsync("Doctor: " + StatusText(report.Status)).ConfigureAwait(false);
        foreach (var check in report.Checks)
        {
            await writer.WriteLineAsync(
                "[" + StatusText(check.Status) + "] " + check.Id + ": " + check.Message).ConfigureAwait(false);
            if (check.NextAction is not null)
            {
                await writer.WriteLineAsync("  Next action: " + check.NextAction).ConfigureAwait(false);
            }
        }

        await WriteDiagnosticsTextAsync(writer, report.Analysis.Generation.Diagnostics).ConfigureAwait(false);
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in Ordered(values))
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WritePackageReferences(
        Utf8JsonWriter writer,
        IEnumerable<EvaluatedPackageReference> references)
    {
        writer.WritePropertyName("cobaltumOrmPackageReferences");
        writer.WriteStartArray();
        foreach (var reference in references
                     .OrderBy(reference => reference.Id, StringComparer.Ordinal)
                     .ThenBy(reference => reference.Version, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", reference.Id);
            writer.WriteString("version", reference.Version);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteGeneratedArtifacts(
        Utf8JsonWriter writer,
        IReadOnlyList<GeneratedArtifact> artifacts)
    {
        writer.WritePropertyName("generatedArtifacts");
        writer.WriteStartArray();
        foreach (var artifact in artifacts
                     .OrderBy(item => item.FileName, StringComparer.Ordinal)
                     .ThenBy(item => item.Kind)
                     .ThenBy(item => item.SourcePath, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("fileName", artifact.FileName);
            writer.WriteString("kind", ArtifactKindText(artifact.Kind));
            WriteNullableString(writer, "sourcePath", artifact.SourcePath);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDiagnostics(
        Utf8JsonWriter writer,
        IEnumerable<GenerationDiagnostic> diagnostics)
    {
        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();
        foreach (var diagnostic in OrderedDiagnostics(diagnostics))
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("severity", diagnostic.IsError ? "error" : "warning");
            writer.WriteString("message", diagnostic.Message);
            WriteNullableString(writer, "filePath", diagnostic.FilePath);
            writer.WriteNumber("startLine", diagnostic.StartLine);
            writer.WriteNumber("startColumn", diagnostic.StartColumn);
            writer.WriteNumber("endLine", diagnostic.EndLine);
            writer.WriteNumber("endColumn", diagnostic.EndColumn);
            WriteNullableString(writer, "helpUri", diagnostic.HelpUri);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static async Task WriteDiagnosticsTextAsync(
        TextWriter writer,
        IEnumerable<GenerationDiagnostic> diagnostics)
    {
        var ordered = OrderedDiagnostics(diagnostics).ToArray();
        if (ordered.Length == 0)
        {
            return;
        }

        await writer.WriteLineAsync("Diagnostics:").ConfigureAwait(false);
        foreach (var diagnostic in ordered)
        {
            var location = diagnostic.FilePath is null
                ? string.Empty
                : diagnostic.FilePath + "(" + diagnostic.StartLine + "," + diagnostic.StartColumn + "): ";
            await writer.WriteLineAsync(
                "  " + location + (diagnostic.IsError ? "error" : "warning") + " " +
                diagnostic.Code + ": " + diagnostic.Message).ConfigureAwait(false);
            if (diagnostic.HelpUri is not null)
            {
                await writer.WriteLineAsync("    Help: " + diagnostic.HelpUri).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<string> Ordered(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal);

    private static IEnumerable<GenerationDiagnostic> OrderedDiagnostics(
        IEnumerable<GenerationDiagnostic> diagnostics) => diagnostics
        .OrderBy(diagnostic => diagnostic.FilePath ?? string.Empty, StringComparer.Ordinal)
        .ThenBy(diagnostic => diagnostic.StartLine)
        .ThenBy(diagnostic => diagnostic.StartColumn)
        .ThenBy(diagnostic => diagnostic.EndLine)
        .ThenBy(diagnostic => diagnostic.EndColumn)
        .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
        .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
        .ThenBy(diagnostic => diagnostic.IsError);

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static string ArtifactKindText(GeneratedArtifactKind kind) => kind switch
    {
        GeneratedArtifactKind.Generated => "generated",
        GeneratedArtifactKind.Transformed => "transformed",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string StatusText(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "ok",
        DoctorStatus.Warning => "warning",
        DoctorStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "(not set)" : value;
}
