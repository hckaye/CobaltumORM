namespace CobaltumOrm.Tool;

internal sealed class DoctorCommand
{
    private readonly TextWriter _output;
    private readonly IProjectEvaluator _evaluator;
    private readonly string _currentDirectory;

    public DoctorCommand(
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
        var report = ProjectDoctor.Diagnose(analysis);
        if (options.Format == ProjectInspectionFormat.Json)
        {
            await _output.WriteLineAsync(ProjectInspectionOutput.WriteDoctorJson(report)).ConfigureAwait(false);
        }
        else
        {
            await ProjectInspectionOutput.WriteDoctorTextAsync(_output, report).ConfigureAwait(false);
        }

        return report.Status == DoctorStatus.Error ? 1 : 0;
    }
}
