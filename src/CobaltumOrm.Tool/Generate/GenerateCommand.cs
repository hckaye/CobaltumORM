using System.Globalization;
using CobaltumOrm.Compiler;

namespace CobaltumOrm.Tool;

/// <summary>
/// Runs explicit generation for one project. The analysis and code generation come from
/// <see cref="CobaltumOrmGenerationEngine"/>, which the MSBuild transform task also uses, so the
/// files written here match what a normal build produces.
/// </summary>
internal sealed class GenerateCommand
{
    private const string IntermediateDirectoryName = "CobaltumOrmGenerated";

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly IProjectEvaluator _evaluator;
    private readonly string _currentDirectory;

    public GenerateCommand(
        TextWriter output,
        TextWriter error,
        IProjectEvaluator evaluator,
        string currentDirectory)
    {
        _output = output;
        _error = error;
        _evaluator = evaluator;
        _currentDirectory = currentDirectory;
    }

    public async Task<int> RunAsync(GenerateOptions options, CancellationToken cancellationToken)
    {
        var projectPath = ResolveProject(options.Project);
        var libraryProjectPath = ResolveLibraryProject(options);
        ValidateOutputPath(options, Path.GetDirectoryName(projectPath)!, libraryProjectPath);
        var analysisService = new ProjectAnalysisService(_evaluator);
        var evaluation = await analysisService
            .EvaluateAsync(projectPath, options, _output, cancellationToken)
            .ConfigureAwait(false);

        var outputDirectory = ResolveOutputDirectory(options, evaluation, libraryProjectPath);
        var generatedNamespace = options.GeneratedNamespace ?? NullIfEmpty(evaluation.GeneratedNamespace);
        var provider = options.Provider ?? NullIfEmpty(evaluation.DatabaseProvider);
        var result = analysisService.Analyze(
            evaluation,
            new ProjectGenerationOptions
            {
                OutputDirectory = outputDirectory,
                GeneratedNamespace = generatedNamespace,
                DatabaseProvider = provider,
            }).Generation;

        foreach (var diagnostic in result.Diagnostics)
        {
            await WriteDiagnosticAsync(diagnostic).ConfigureAwait(false);
        }

        if (!result.Succeeded)
        {
            await _error.WriteLineAsync("error: generation failed; no files were written.").ConfigureAwait(false);
            return 1;
        }

        if (result.Artifacts.Count == 0)
        {
            await _output
                .WriteLineAsync($"Project '{projectPath}' has no C# sources to generate from.")
                .ConfigureAwait(false);
            return 0;
        }

        var summary = new GenerationOutputWriter(new GenerationOutputRequest
        {
            Mode = options.OutputMode,
            OutputDirectory = outputDirectory,
            Evaluation = evaluation,
            Artifacts = result.Artifacts,
            AnalyzedSources = result.AnalyzedSourcePaths,
            ProcessedSources = result.ProcessedSourcePaths,
            References = evaluation.References,
            LibraryName = ResolveLibraryName(options, evaluation, libraryProjectPath),
        }).Publish();

        await WriteSummaryAsync(options, outputDirectory, summary, libraryProjectPath).ConfigureAwait(false);
        return 0;
    }

    private async Task WriteSummaryAsync(
        GenerateOptions options,
        string outputDirectory,
        GenerationOutputSummary summary,
        string? libraryProjectPath)
    {
        await _output
            .WriteLineAsync(string.Format(
                CultureInfo.InvariantCulture,
                "Wrote {0} file(s) to {1}.",
                summary.WrittenFiles.Count,
                outputDirectory))
            .ConfigureAwait(false);
        foreach (var file in summary.WrittenFiles)
        {
            await _output.WriteLineAsync("  " + file).ConfigureAwait(false);
        }

        foreach (var file in summary.RemovedFiles)
        {
            await _output.WriteLineAsync("  removed " + file).ConfigureAwait(false);
        }

        if (options.OutputMode == GenerateOutputMode.Library && libraryProjectPath is not null)
        {
            var relative = Path.GetRelativePath(
                Path.GetDirectoryName(libraryProjectPath)!,
                Path.Combine(outputDirectory, GenerationOutputWriter.PropsFileName)).Replace('\\', '/');
            if (!File.ReadAllText(libraryProjectPath).Contains(
                    GenerationOutputWriter.PropsFileName,
                    StringComparison.Ordinal))
            {
                await _output.WriteLineAsync(
                    $"'{libraryProjectPath}' was not modified. Add these lines to compile the generated files:")
                    .ConfigureAwait(false);
                await _output.WriteLineAsync("  <PropertyGroup>").ConfigureAwait(false);
                await _output.WriteLineAsync("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>")
                    .ConfigureAwait(false);
                await _output.WriteLineAsync("  </PropertyGroup>").ConfigureAwait(false);
                await _output.WriteLineAsync($"  <Import Project=\"{relative}\" />").ConfigureAwait(false);
            }

            return;
        }

        if (options.OutputMode != GenerateOutputMode.Library)
        {
            await _output
                .WriteLineAsync("Import the props file from the project to compile the generated files.")
                .ConfigureAwait(false);
        }
    }

    private async Task WriteDiagnosticAsync(GenerationDiagnostic diagnostic)
    {
        var severity = diagnostic.IsError ? "error" : "warning";
        var location = diagnostic.FilePath is null
            ? string.Empty
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}({1},{2}): ",
                diagnostic.FilePath,
                diagnostic.StartLine,
                diagnostic.StartColumn);
        var writer = diagnostic.IsError ? _error : _output;
        await writer
            .WriteLineAsync($"{location}{severity} {diagnostic.Code}: {diagnostic.Message}")
            .ConfigureAwait(false);
    }

    private string ResolveProject(string? project)
    {
        if (project is not null)
        {
            return ProjectPathResolver.Resolve(project, _currentDirectory);
        }

        var candidates = Directory
            .EnumerateFiles(_currentDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new ToolUsageException(
                $"No project file was found in '{_currentDirectory}'. Specify one with --project.");
        }

        if (candidates.Length > 1)
        {
            throw new ToolUsageException(
                $"More than one project file was found in '{_currentDirectory}'. Specify one with --project.");
        }

        return candidates[0];
    }

    private string? ResolveLibraryProject(GenerateOptions options)
    {
        if (options.LibraryProject is null)
        {
            return null;
        }

        var resolved = Path.GetFullPath(options.LibraryProject, _currentDirectory);
        if (!File.Exists(resolved))
        {
            throw new ToolUsageException($"Library project '{resolved}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(resolved), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException($"Library project '{resolved}' is not a .csproj file.");
        }

        return resolved;
    }

    private string ResolveOutputDirectory(
        GenerateOptions options,
        ProjectEvaluation evaluation,
        string? libraryProjectPath)
    {
        if (options.OutputMode == GenerateOutputMode.Intermediate)
        {
            var intermediate = evaluation.IntermediateOutputPath.Length != 0
                ? evaluation.IntermediateOutputPath
                : Path.Combine(evaluation.ProjectDirectory, "obj");
            return Path.GetFullPath(Path.Combine(intermediate, IntermediateDirectoryName));
        }

        return ValidateOutputPath(options, evaluation.ProjectDirectory, libraryProjectPath)!;
    }

    private string? ValidateOutputPath(
        GenerateOptions options,
        string projectDirectory,
        string? libraryProjectPath)
    {
        if (options.OutputMode == GenerateOutputMode.Intermediate)
        {
            return null;
        }

        var output = options.Output is not null
            ? Path.GetFullPath(options.Output, _currentDirectory)
            : Path.GetDirectoryName(libraryProjectPath!)!;
        if (File.Exists(output))
        {
            throw new ToolUsageException($"Output path '{output}' is a file.");
        }

        var resolvedProjectDirectory = Path.GetFullPath(projectDirectory);
        if (string.Equals(output, resolvedProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
            IsAncestor(output, resolvedProjectDirectory))
        {
            throw new ToolUsageException(
                $"Output path '{output}' contains the project directory. Choose a directory that only holds generated files.");
        }

        return output;
    }

    private static bool IsAncestor(string candidate, string path)
    {
        var prefix = candidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveLibraryName(
        GenerateOptions options,
        ProjectEvaluation evaluation,
        string? libraryProjectPath)
    {
        if (options.OutputMode != GenerateOutputMode.Library || libraryProjectPath is not null)
        {
            return null;
        }

        if (options.LibraryName is not null)
        {
            return options.LibraryName;
        }

        var assemblyName = evaluation.AssemblyName.Length != 0
            ? evaluation.AssemblyName
            : Path.GetFileNameWithoutExtension(evaluation.ProjectPath);
        return assemblyName + ".Generated";
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
