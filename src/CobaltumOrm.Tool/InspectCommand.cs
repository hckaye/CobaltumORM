namespace CobaltumOrm.Tool;

internal sealed class InspectCommand
{
    private readonly TextWriter _output;
    private readonly IProjectEvaluator _evaluator;
    private readonly string _currentDirectory;

    public InspectCommand(
        TextWriter output,
        IProjectEvaluator evaluator,
        string currentDirectory)
    {
        _output = output;
        _evaluator = evaluator;
        _currentDirectory = currentDirectory;
    }

    public async Task<int> RunAsync(ProjectInspectionOptions options, CancellationToken cancellationToken)
    {
        var projectPath = ProjectPathResolver.Resolve(options.Project!, _currentDirectory);
        var analysis = await new ProjectAnalysisService(_evaluator)
            .AnalyzeAsync(projectPath, options, TextWriter.Null, cancellationToken)
            .ConfigureAwait(false);
        if (options.Format == ProjectInspectionFormat.Json)
        {
            await _output.WriteLineAsync(ProjectInspectionOutput.WriteInspectJson(analysis)).ConfigureAwait(false);
        }
        else
        {
            await ProjectInspectionOutput.WriteInspectTextAsync(_output, analysis).ConfigureAwait(false);
        }

        return analysis.Generation.Succeeded ? 0 : 1;
    }
}
