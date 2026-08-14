using System.Diagnostics;
using System.Reflection;

namespace CobaltumOrm.Tool;

/// <summary>
/// Evaluates a project by running a dedicated MSBuild target through <c>dotnet msbuild</c>. The
/// target reports the Compile items, references, AdditionalFiles, migration project inputs, and
/// compiler properties the normal build uses, so nothing here parses csproj conditions by hand.
/// </summary>
internal sealed class MsBuildProjectEvaluator : IProjectEvaluator
{
    private const string TargetsResourceName = "CobaltumOrm.Tool.Templates.CobaltumOrm.Generate.targets";

    public async Task<ProjectEvaluation> EvaluateAsync(
        string projectPath,
        ProjectEvaluationOptions options,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "cobaltum-generate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var targetsPath = Path.Combine(workingDirectory, "CobaltumOrm.Generate.targets");
            File.WriteAllText(targetsPath, ReadTargets());
            var inputsPath = Path.Combine(workingDirectory, "inputs.txt");

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-verbosity:quiet");
            startInfo.ArgumentList.Add("-nodeReuse:false");
            if (!options.NoRestore)
            {
                startInfo.ArgumentList.Add("-restore");
            }

            startInfo.ArgumentList.Add("-target:CobaltumOrmWriteGenerationInputs");
            startInfo.ArgumentList.Add("-property:CustomAfterMicrosoftCommonTargets=" + targetsPath);
            startInfo.ArgumentList.Add("-property:CobaltumOrmGenerationInputsFile=" + inputsPath);
            startInfo.ArgumentList.Add("-property:Configuration=" + options.Configuration);
            if (options.Framework is not null)
            {
                startInfo.ArgumentList.Add("-property:TargetFramework=" + options.Framework);
            }

            if (options.Verbose)
            {
                await log.WriteLineAsync(
                    "dotnet " + string.Join(" ", startInfo.ArgumentList)).ConfigureAwait(false);
            }

            var (exitCode, output) = await RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                if (options.Framework is null && await HasAmbiguousTargetFrameworkAsync(
                        projectPath,
                        options.Configuration,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new ToolExecutionException(
                        $"Project '{projectPath}' targets more than one framework. " +
                        "Pass --framework to select one target framework.");
                }

                throw new ToolExecutionException(
                    "MSBuild could not evaluate '" + projectPath + "'." +
                    Environment.NewLine + output.Trim());
            }

            if (!File.Exists(inputsPath))
            {
                throw new ToolExecutionException(
                    "MSBuild did not report generation inputs for '" + projectPath + "'." +
                    Environment.NewLine + output.Trim());
            }

            if (options.Verbose && output.Trim().Length != 0)
            {
                await log.WriteLineAsync(output.Trim()).ConfigureAwait(false);
            }

            return ProjectEvaluation.Parse(File.ReadAllLines(inputsPath));
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    internal static string ReadTargets()
    {
        using var stream = typeof(MsBuildProjectEvaluator).GetTypeInfo().Assembly
            .GetManifestResourceStream(TargetsResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{TargetsResourceName}' is missing from the tool.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new ToolExecutionException("The dotnet process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return (process.ExitCode, await standardOutput.ConfigureAwait(false) +
            await standardError.ConfigureAwait(false));
    }

    private static async Task<bool> HasAmbiguousTargetFrameworkAsync(
        string projectPath,
        string configuration,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-getProperty:TargetFrameworks");
        startInfo.ArgumentList.Add("-property:Configuration=" + configuration);
        var (exitCode, output) = await RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return false;
        }

        return output
            .Trim()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length > 1;
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

/// <summary>An error that stops a command without being a usage mistake.</summary>
internal sealed class ToolExecutionException : Exception
{
    public ToolExecutionException(string message)
        : base(message)
    {
    }
}
