using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class McpBusinessTests
{
    private const string DiagnosticsUrl =
        "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md#";

    [Fact]
    public async Task InspectAndDoctorStructuredContentMatchesTheExistingJsonCommands()
    {
        using var fixture = new McpTestFixture();
        var tools = fixture.Tools();

        var inspectTool = await tools.InspectProject(CancellationToken.None);
        var inspectCommand = await RunCommandAsync(
            fixture,
            "inspect",
            "--project", fixture.ProjectPath,
            "--configuration", "Release",
            "--framework", "net10.0",
            "--no-restore",
            "--format", "json");
        var doctorTool = await tools.DoctorProject(CancellationToken.None);
        var doctorCommand = await RunCommandAsync(
            fixture,
            "doctor",
            "--project", fixture.ProjectPath,
            "--configuration", "Release",
            "--framework", "net10.0",
            "--no-restore",
            "--format", "json");

        AssertJsonEqual(inspectCommand.Output, Assert.IsType<JsonElement>(inspectTool.StructuredContent));
        AssertJsonEqual(doctorCommand.Output, Assert.IsType<JsonElement>(doctorTool.StructuredContent));
        Assert.Equal(0, inspectCommand.ExitCode);
        Assert.Equal(0, doctorCommand.ExitCode);
        Assert.Equal(string.Empty, inspectCommand.Error);
        Assert.Equal(string.Empty, doctorCommand.Error);
        Assert.True(fixture.Evaluator.LastOptions!.NoRestore);
        Assert.Equal("Release", fixture.Evaluator.LastOptions.Configuration);
        Assert.Equal("net10.0", fixture.Evaluator.LastOptions.Framework);
        Assert.Equal(0, fixture.ProcessRunner.CallCount);
        Assert.Single(inspectTool.Content.OfType<TextContentBlock>());
        Assert.Single(doctorTool.Content.OfType<TextContentBlock>());
    }

    [Fact]
    public async Task ArtifactListingIsDeterministicAndReadUsesOnlyAnAllowedInMemoryArtifact()
    {
        using var fixture = new McpTestFixture();
        var tools = fixture.Tools();

        var first = await tools.ListGeneratedArtifacts(CancellationToken.None);
        var second = await tools.ListGeneratedArtifacts(CancellationToken.None);
        Assert.Equal(
            first.StructuredContent!.Value.GetRawText(),
            second.StructuredContent!.Value.GetRawText());

        var artifacts = first.StructuredContent.Value.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.NotEmpty(artifacts);
        var names = artifacts.Select(artifact => artifact.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);

        var callsAfterList = fixture.Evaluator.CallCount;
        var selectedName = names[0];
        var read = await tools.ReadGeneratedArtifact(selectedName, CancellationToken.None);
        Assert.Equal(callsAfterList, fixture.Evaluator.CallCount);
        Assert.Equal(selectedName, read.StructuredContent!.Value.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(read.StructuredContent.Value.GetProperty("source").GetString()));
        Assert.Equal(0, fixture.ProcessRunner.CallCount);
    }

    [Fact]
    public async Task DoctorReturnsGenerationErrorsAsStructuredData()
    {
        using var fixture = new McpTestFixture();
        fixture.Evaluation.DatabaseProvider = "UnsupportedProvider";
        var tools = fixture.Tools();

        var result = await tools.DoctorProject(CancellationToken.None);

        Assert.False(result.IsError is true);
        Assert.Equal("error", result.StructuredContent!.Value.GetProperty("status").GetString());
        var diagnostic = Assert.Single(
            result.StructuredContent.Value.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("COB008", diagnostic.GetProperty("code").GetString());
        Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(0, fixture.ProcessRunner.CallCount);
    }

    [Fact]
    public async Task ProjectAnalysisCancellationReachesTheEvaluator()
    {
        using var fixture = new McpTestFixture();
        using var cancellation = new CancellationTokenSource();
        var evaluator = new BlockingProjectEvaluator();
        var tools = new CobaltumMcpTools(
            new CobaltumMcpProjectService(
                fixture.ProjectPath,
                fixture.Options(),
                evaluator,
                TextWriter.Null),
            McpDocumentation.Load());

        var call = tools.InspectProject(cancellation.Token);
        await evaluator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Theory]
    [InlineData("../secret.cs")]
    [InlineData("folder/secret.cs")]
    [InlineData("folder\\secret.cs")]
    [InlineData("..")]
    [InlineData("C:\\secret.cs")]
    public async Task ArtifactReadRejectsTraversalBeforeProjectAnalysis(string artifactName)
    {
        using var fixture = new McpTestFixture();
        var tools = fixture.Tools();
        var secretPath = Path.Combine(fixture.Root, "secret.cs");
        File.WriteAllText(secretPath, "must not be returned");

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.ReadGeneratedArtifact(artifactName, CancellationToken.None));

        Assert.Contains("Paths and '..' are not allowed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Evaluator.CallCount);
        Assert.Equal("must not be returned", File.ReadAllText(secretPath));
    }

    [Fact]
    public async Task ArtifactReadRejectsAFileNameNotReturnedByTheGenerator()
    {
        using var fixture = new McpTestFixture();
        var tools = fixture.Tools();
        var secretPath = Path.Combine(fixture.Root, "secret.cs");
        File.WriteAllText(secretPath, "must not be returned");

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.ReadGeneratedArtifact("secret.cs", CancellationToken.None));

        Assert.Contains("Unknown generated artifact", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("must not be returned", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Evaluator.CallCount);
    }

    [Theory]
    [InlineData("COB001", "en", "docs/ai/diagnostics.md")]
    [InlineData("COB109", "ja", "docs/ai/diagnostics.ja.md")]
    public void DiagnosticExplanationMatchesTheCheckedInSection(
        string code,
        string language,
        string relativeDocumentPath)
    {
        var documentation = McpDocumentation.Load();

        var explanation = documentation.ExplainDiagnostic(code, language);

        var checkedInDocument = File.ReadAllText(Path.Combine(RepositoryRoot(), relativeDocumentPath));
        Assert.Equal(ExtractSection(checkedInDocument, code), explanation.Section);
        Assert.Equal(DiagnosticsUrl + code.ToLowerInvariant(), explanation.HelpUri);
        Assert.Equal(ProjectInspectionOutput.FormatVersion, explanation.FormatVersion);
        Assert.Equal(code, explanation.Code);
        Assert.Equal(language, explanation.Language);
    }

    [Theory]
    [InlineData("COB000", "en", "not documented")]
    [InlineData("COB011", "en", "not documented")]
    [InlineData("COB110", "ja", "not documented")]
    [InlineData("COB001", "fr", "not supported")]
    public void DiagnosticExplanationRejectsUnsupportedInput(
        string code,
        string language,
        string message)
    {
        var documentation = McpDocumentation.Load();

        var exception = Assert.Throws<McpException>(
            () => documentation.ExplainDiagnostic(code, language));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpStartupOptionsUseTheExistingUsageErrorRouteBeforeProtocolOutput()
    {
        using var fixture = new McpTestFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(
            output,
            error,
            fixture.ProcessRunner,
            fixture.Root,
            fixture.Evaluator);

        var exitCode = await application.RunAsync(
            new[] { "mcp", "--project", "missing.csproj", "--format", "json" },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("Unknown option '--format'", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Evaluator.CallCount);
        Assert.Equal(0, fixture.ProcessRunner.CallCount);
    }

    [Fact]
    public async Task McpMissingProjectFailsBeforeProtocolOutput()
    {
        using var fixture = new McpTestFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(
            output,
            error,
            fixture.ProcessRunner,
            fixture.Root,
            fixture.Evaluator);

        var exitCode = await application.RunAsync(
            new[] { "mcp", "--project", "missing.csproj" },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("does not exist", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Run 'cobaltum --help'", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Evaluator.CallCount);
    }

    private static async Task<CommandResult> RunCommandAsync(
        McpTestFixture fixture,
        params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(
            output,
            error,
            fixture.ProcessRunner,
            fixture.Root,
            fixture.Evaluator);
        var exitCode = await application.RunAsync(args, CancellationToken.None);
        return new CommandResult(exitCode, output.ToString(), error.ToString());
    }

    private static void AssertJsonEqual(string expected, JsonElement actual)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual));
    }

    private static string ExtractSection(string document, string code)
    {
        var start = document.IndexOf("### " + code, StringComparison.Ordinal);
        var end = document.IndexOf("\n### ", start + code.Length + 4, StringComparison.Ordinal);
        if (end < 0)
        {
            end = document.Length;
        }

        return document.Substring(start, end - start).TrimEnd('\r', '\n');
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CobaltumOrm.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class BlockingProjectEvaluator : IProjectEvaluator
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProjectEvaluation> EvaluateAsync(
            string projectPath,
            ProjectEvaluationOptions options,
            TextWriter log,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The canceled evaluation continued running.");
        }
    }
}
