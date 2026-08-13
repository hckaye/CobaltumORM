using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace CobaltumOrm.Compiler;

/// <summary>
/// Rewrites raw query call sites before CoreCompile. The analysis and code generation happen in
/// <see cref="CobaltumOrmGenerationEngine"/>, which the command line tool also uses.
/// </summary>
public sealed class CobaltumOrmTransformTask : Task
{
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] AdditionalFiles { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] MigrationSources { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    public string? SuccessManifestPath { get; set; }

    public string? DefineConstants { get; set; }

    public string? LangVersion { get; set; }

    public string? Nullable { get; set; }

    public string? GeneratedNamespace { get; set; }

    public string? CobaltumOrmDatabaseProvider { get; set; }

    [Output]
    public ITaskItem[] ProcessedSources { get; private set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] TransformedSources { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        try
        {
            var succeeded = Transform();
            if (succeeded && !string.IsNullOrWhiteSpace(SuccessManifestPath))
            {
                CobaltumOrmTransformManifest.WriteSuccessManifest(
                    SuccessManifestPath!,
                    ProcessedSources,
                    TransformedSources);
            }

            return succeeded;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    private bool Transform()
    {
        var sourceItemSpecs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Sources)
        {
            var fullPath = ItemFullPath(item);
            if (!sourceItemSpecs.ContainsKey(fullPath))
            {
                sourceItemSpecs.Add(fullPath, item.ItemSpec);
            }
        }

        var result = CobaltumOrmGenerationEngine.Run(new GenerationRequest
        {
            SourcePaths = Sources.Select(ItemFullPath).ToArray(),
            ReferencePaths = References.Select(ItemFullPath).ToArray(),
            AdditionalFilePaths = AdditionalFiles.Select(ItemFullPath).ToArray(),
            MigrationSourcePaths = MigrationSources.Select(ItemFullPath).ToArray(),
            OutputDirectory = OutputDirectory,
            DefineConstants = DefineConstants,
            LangVersion = LangVersion,
            Nullable = Nullable,
            GeneratedNamespace = GeneratedNamespace,
            DatabaseProvider = CobaltumOrmDatabaseProvider,
            IncludeGeneratorOutput = false,
        });

        foreach (var diagnostic in result.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        if (!result.Succeeded)
        {
            return false;
        }

        if (result.Artifacts.Count == 0)
        {
            return true;
        }

        Directory.CreateDirectory(OutputDirectory);
        var outputItems = new List<ITaskItem>();
        var processedItems = new List<ITaskItem>();
        var activeTransformedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in result.Artifacts)
        {
            var outputPath = Path.Combine(OutputDirectory, artifact.FileName);
            WriteIfChanged(outputPath, artifact.Text);
            if (artifact.Kind == GeneratedArtifactKind.Transformed)
            {
                activeTransformedPaths.Add(Path.GetFullPath(outputPath));
                processedItems.Add(new TaskItem(
                    sourceItemSpecs.TryGetValue(artifact.SourcePath!, out var itemSpec)
                        ? itemSpec
                        : artifact.SourcePath!));
                outputItems.Add(CreateTransformedItem(outputPath));
            }
            else
            {
                outputItems.Add(CreateGeneratedItem(outputPath));
            }
        }

        RemoveStaleOutputs(activeTransformedPaths);
        ProcessedSources = processedItems.ToArray();
        TransformedSources = outputItems.ToArray();
        return true;
    }

    private void LogDiagnostic(GenerationDiagnostic diagnostic)
    {
        if (!diagnostic.IsError)
        {
            Log.LogWarning(
                "CobaltumOrm",
                diagnostic.Code,
                null,
                diagnostic.FilePath,
                diagnostic.StartLine,
                diagnostic.StartColumn,
                diagnostic.EndLine,
                diagnostic.EndColumn,
                diagnostic.Message);
            return;
        }

        Log.LogError(
            "CobaltumOrm",
            diagnostic.Code,
            null,
            diagnostic.FilePath,
            diagnostic.StartLine,
            diagnostic.StartColumn,
            diagnostic.EndLine,
            diagnostic.EndColumn,
            diagnostic.Message);
    }

    private static string ItemFullPath(ITaskItem item)
    {
        var fullPath = item.GetMetadata("FullPath");
        return string.IsNullOrWhiteSpace(fullPath)
            ? Path.GetFullPath(item.ItemSpec)
            : Path.GetFullPath(fullPath);
    }

    private static ITaskItem CreateGeneratedItem(string path)
    {
        var item = new TaskItem(path);
        item.SetMetadata("AutoGen", "true");
        item.SetMetadata("DesignTime", "true");
        item.SetMetadata("Visible", "false");
        return item;
    }

    private static ITaskItem CreateTransformedItem(string path)
    {
        var item = new TaskItem(path);
        item.SetMetadata("CobaltumOrmTransformed", "true");
        item.SetMetadata("Visible", "false");
        return item;
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private void RemoveStaleOutputs(ISet<string> activeTransformedPaths)
    {
        foreach (var path in Directory.EnumerateFiles(OutputDirectory, "*.cobaltum.cs", SearchOption.TopDirectoryOnly))
        {
            if (IsNumberedTransformOutput(path) && !activeTransformedPaths.Contains(Path.GetFullPath(path)))
            {
                File.Delete(path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(OutputDirectory, "*.g.cs", SearchOption.TopDirectoryOnly))
        {
            if (IsNumberedTransformOutput(path))
            {
                File.Delete(path);
            }
        }
    }

    private static bool IsNumberedTransformOutput(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 5 &&
               name[4] == '.' &&
               name.Take(4).All(character => character >= '0' && character <= '9');
    }
}
