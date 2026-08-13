using System.Security;
using System.Text;
using CobaltumOrm.Compiler;

namespace CobaltumOrm.Tool;

/// <summary>
/// Publishes a generation run into the selected output directory. Files are written to a staging
/// directory first, so a failed run never leaves half of an output behind, and only files recorded
/// in the tool-owned manifest are removed.
/// </summary>
internal sealed class GenerationOutputWriter
{
    internal const string ManifestFileName = "CobaltumOrm.generated.manifest";
    internal const string PropsFileName = "CobaltumOrm.Generated.props";

    private readonly GenerationOutputRequest _request;

    public GenerationOutputWriter(GenerationOutputRequest request)
    {
        _request = request;
    }

    public GenerationOutputSummary Publish()
    {
        var staging = Path.Combine(
            Path.GetTempPath(),
            "cobaltum-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var artifact in _request.Artifacts)
            {
                files[SafeFileName(artifact.FileName)] = artifact.Text;
            }

            files[PropsFileName] = WriteProps();
            var libraryProjectFileName = LibraryProjectFileName();
            if (libraryProjectFileName is not null)
            {
                files[libraryProjectFileName] = WriteLibraryProject();
            }

            foreach (var file in files)
            {
                var stagedPath = Path.Combine(staging, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                File.WriteAllText(stagedPath, file.Value, new UTF8Encoding(false));
            }

            Directory.CreateDirectory(_request.OutputDirectory);
            var removed = RemoveStaleFiles(files.Keys);
            foreach (var file in files.Keys)
            {
                var destination = Path.Combine(_request.OutputDirectory, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(staging, file), destination, overwrite: true);
            }

            File.WriteAllText(
                Path.Combine(_request.OutputDirectory, ManifestFileName),
                WriteManifest(files.Keys),
                new UTF8Encoding(false));
            return new GenerationOutputSummary(files.Keys.ToArray(), removed, libraryProjectFileName);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Reads the file names an earlier run recorded in the output directory.</summary>
    internal static IReadOnlyList<string> ReadManifestFiles(string outputDirectory)
    {
        var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(manifestPath)
            .Where(line => line.StartsWith("file=", StringComparison.Ordinal))
            .Select(line => line.Substring("file=".Length).Trim())
            .Where(value => value.Length != 0)
            .ToArray();
    }

    private IReadOnlyList<string> RemoveStaleFiles(IEnumerable<string> currentFiles)
    {
        var current = new HashSet<string>(currentFiles, StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var recorded in ReadManifestFiles(_request.OutputDirectory))
        {
            if (current.Contains(recorded) || !IsInsideOutput(recorded))
            {
                continue;
            }

            var path = Path.Combine(_request.OutputDirectory, recorded);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed.Add(recorded);
            }
        }

        return removed;
    }

    private bool IsInsideOutput(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var combined = Path.GetFullPath(Path.Combine(_request.OutputDirectory, relativePath));
        return combined.StartsWith(
            _request.OutputDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private string WriteManifest(IEnumerable<string> files)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# CobaltumORM explicit generation output");
        builder.AppendLine("# Written by cobaltum generate. Every file listed here is owned by the tool and is");
        builder.AppendLine("# rewritten or removed on the next run. Do not add your own files to this list.");
        builder.Append("mode=").AppendLine(_request.Mode.ToString().ToLowerInvariant());
        builder.Append("project=").AppendLine(_request.Evaluation.ProjectPath);
        builder.Append("targetframework=").AppendLine(_request.Evaluation.TargetFramework);
        builder.Append("configuration=").AppendLine(_request.Evaluation.Configuration);
        foreach (var file in files)
        {
            builder.Append("file=").AppendLine(file);
        }

        return builder.ToString();
    }

    private string WriteProps()
    {
        var builder = new StringBuilder();
        builder.AppendLine("<Project>");
        builder.AppendLine("  <!-- Written by cobaltum generate. Do not edit; it is replaced on the next run. -->");
        builder.AppendLine("  <PropertyGroup>");
        builder.AppendLine("    <CobaltumOrmExplicitGeneration>true</CobaltumOrmExplicitGeneration>");
        builder.AppendLine("    <CobaltumOrmCompileTimeQueries>false</CobaltumOrmCompileTimeQueries>");
        builder.AppendLine("  </PropertyGroup>");

        if (_request.Mode != GenerateOutputMode.Library)
        {
            var removed = _request.ProcessedSources
                .Select(path => ProjectRelative(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (removed.Length != 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var path in removed)
                {
                    builder.Append("    <Compile Remove=\"").Append(Escape(path)).AppendLine("\" />");
                }

                builder.AppendLine("  </ItemGroup>");
            }
        }

        builder.AppendLine("  <ItemGroup>");
        if (_request.Mode == GenerateOutputMode.Library)
        {
            var processed = new HashSet<string>(_request.ProcessedSources, StringComparer.OrdinalIgnoreCase);
            foreach (var source in _request.AnalyzedSources.Where(path => !processed.Contains(path)))
            {
                builder.Append("    <Compile Include=\"$(MSBuildThisFileDirectory)")
                    .Append(Escape(OutputRelative(source)))
                    .AppendLine("\" />");
            }
        }

        foreach (var file in _request.Artifacts.Select(artifact => artifact.FileName).OrderBy(name => name, StringComparer.Ordinal))
        {
            builder.Append("    <Compile Include=\"$(MSBuildThisFileDirectory)")
                .Append(Escape(file))
                .AppendLine("\" />");
        }

        builder.AppendLine("  </ItemGroup>");

        if (_request.Mode == GenerateOutputMode.Library)
        {
            builder.AppendLine("  <ItemGroup>");
            foreach (var reference in _request.References.Where(path => !IsFrameworkReference(path)))
            {
                builder.Append("    <Reference Include=\"")
                    .Append(Escape(Path.GetFileNameWithoutExtension(reference)))
                    .AppendLine("\">");
                builder.Append("      <HintPath>").Append(Escape(reference)).AppendLine("</HintPath>");
                builder.AppendLine("      <Private>true</Private>");
                builder.AppendLine("    </Reference>");
            }

            builder.AppendLine("  </ItemGroup>");
        }

        builder.AppendLine("  <Target Name=\"CobaltumOrmDisableGeneratorForExplicitOutput\" BeforeTargets=\"CoreCompile\">");
        builder.AppendLine("    <ItemGroup>");
        builder.AppendLine("      <_CobaltumOrmGeneratorAnalyzer Include=\"@(Analyzer)\" Condition=\"'%(Filename)' == 'CobaltumOrm.SourceGenerator'\" />");
        builder.AppendLine("      <Analyzer Remove=\"@(_CobaltumOrmGeneratorAnalyzer)\" />");
        builder.AppendLine("    </ItemGroup>");
        builder.AppendLine("  </Target>");
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    private string WriteLibraryProject()
    {
        var evaluation = _request.Evaluation;
        var builder = new StringBuilder();
        builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        builder.AppendLine("  <!-- Written by cobaltum generate. Do not edit; it is replaced on the next run. -->");
        builder.AppendLine("  <PropertyGroup>");
        builder.Append("    <TargetFramework>").Append(Escape(evaluation.TargetFramework)).AppendLine("</TargetFramework>");
        builder.Append("    <AssemblyName>").Append(Escape(_request.LibraryName!)).AppendLine("</AssemblyName>");
        if (evaluation.RootNamespace.Length != 0)
        {
            builder.Append("    <RootNamespace>").Append(Escape(evaluation.RootNamespace)).AppendLine("</RootNamespace>");
        }

        builder.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        if (evaluation.Nullable.Length != 0)
        {
            builder.Append("    <Nullable>").Append(Escape(evaluation.Nullable)).AppendLine("</Nullable>");
        }

        if (evaluation.ImplicitUsings.Length != 0)
        {
            builder.Append("    <ImplicitUsings>").Append(Escape(evaluation.ImplicitUsings)).AppendLine("</ImplicitUsings>");
        }

        if (evaluation.LangVersion.Length != 0)
        {
            builder.Append("    <LangVersion>").Append(Escape(evaluation.LangVersion)).AppendLine("</LangVersion>");
        }

        builder.AppendLine("  </PropertyGroup>");
        builder.Append("  <Import Project=\"").Append(PropsFileName).AppendLine("\" />");
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    private string? LibraryProjectFileName() =>
        _request.Mode == GenerateOutputMode.Library && _request.LibraryName is not null
            ? _request.LibraryName + ".csproj"
            : null;

    private string ProjectRelative(string path) =>
        Normalize(Path.GetRelativePath(_request.Evaluation.ProjectDirectory, path));

    private string OutputRelative(string path) =>
        Normalize(Path.GetRelativePath(_request.OutputDirectory, path));

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Keeps a generated file name inside the output directory.</summary>
    private static string SafeFileName(string fileName)
    {
        if (fileName.Length == 0 ||
            Path.IsPathRooted(fileName) ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Contains('/', StringComparison.Ordinal) ||
            fileName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ToolExecutionException(
                $"Generated file name '{fileName}' must be a plain file name inside the output directory.");
        }

        return fileName;
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? value;

    private static bool IsFrameworkReference(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/packs/", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class GenerationOutputRequest
{
    public GenerateOutputMode Mode { get; set; }

    public string OutputDirectory { get; set; } = string.Empty;

    public ProjectEvaluation Evaluation { get; set; } = new();

    public IReadOnlyList<GeneratedArtifact> Artifacts { get; set; } = Array.Empty<GeneratedArtifact>();

    public IReadOnlyList<string> AnalyzedSources { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> ProcessedSources { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> References { get; set; } = Array.Empty<string>();

    public string? LibraryName { get; set; }
}

internal sealed record GenerationOutputSummary(
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> RemovedFiles,
    string? LibraryProjectFileName);
