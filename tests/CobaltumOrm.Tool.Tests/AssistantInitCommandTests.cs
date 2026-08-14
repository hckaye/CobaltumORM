using System.Diagnostics;
using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class AssistantInitCommandTests
{
    private const string BeginMarker = "<!-- BEGIN COBALTUMORM ASSISTANT MANAGED BLOCK -->";
    private const string EndMarker = "<!-- END COBALTUMORM ASSISTANT MANAGED BLOCK -->";

    [Fact]
    public async Task InitResolvesAnExistingProjectDirectory()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();

        var result = await fixture.RunAsync(
            "assistant", "init", "--project", Path.GetRelativePath(fixture.Root, fixture.ProjectDirectory));

        Assert.Equal(0, result.ExitCode);
        var instructions = File.ReadAllText(fixture.Path(".cobaltum/assistant.md"));
        Assert.Contains("cobaltum inspect --project <path> --format json", instructions, StringComparison.Ordinal);
        Assert.Contains("Query<T>", instructions, StringComparison.Ordinal);
        Assert.Contains("[Query]", instructions, StringComparison.Ordinal);
        Assert.Contains("NoCheckQuery", instructions, StringComparison.Ordinal);
        Assert.Contains("diagnostic `helpUri`", instructions, StringComparison.Ordinal);
        Assert.Contains("cobaltum doctor --project <path> --format json", instructions, StringComparison.Ordinal);
        Assert.Contains("dotnet build <project>", instructions, StringComparison.Ordinal);
        Assert.Contains("EF Core", instructions, StringComparison.Ordinal);
        Assert.Contains("DbContext", instructions, StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/quick-reference.md",
            instructions,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/recipes.md",
            instructions,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md",
            instructions,
            StringComparison.Ordinal);
        Assert.Contains("https://github.com/hckaye/CobaltumORM/blob/main/llms.txt", instructions, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.Path("AGENTS.md")));
        Assert.Contains("Created .cobaltum/assistant.md", result.Output, StringComparison.Ordinal);
        Assert.Contains("Created AGENTS.md", result.Output, StringComparison.Ordinal);
        Assert.Contains($"dotnet build {fixture.ProjectPath}", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Theory]
    [MemberData(nameof(ExplicitTargets))]
    public async Task InitCreatesTheSelectedAdapter(string target, string[] expectedAdapters)
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();

        var result = await fixture.RunAsync(
            "assistant", "init", "-p", fixture.ProjectPath, "--target", target);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(fixture.Path(".cobaltum/assistant.md")));
        foreach (var expectedAdapter in expectedAdapters)
        {
            var content = File.ReadAllText(fixture.Path(expectedAdapter));
            Assert.Contains("Read and obey `.cobaltum/assistant.md`", content, StringComparison.Ordinal);
        }

        foreach (var adapter in AllAdapters.Except(expectedAdapters, StringComparer.Ordinal))
        {
            Assert.False(File.Exists(fixture.Path(adapter)));
        }

        if (string.Equals(target, "cursor", StringComparison.Ordinal))
        {
            var cursorRule = File.ReadAllText(fixture.Path(".cursor/rules/cobaltum.mdc"));
            Assert.StartsWith("---\n", cursorRule, StringComparison.Ordinal);
            Assert.Contains("alwaysApply: true", cursorRule, StringComparison.Ordinal);
        }

        Assert.Equal(string.Empty, result.Error);
    }

    public static IEnumerable<object[]> ExplicitTargets => new[]
    {
        new object[] { "agents", new[] { "AGENTS.md" } },
        new object[] { "claude", new[] { "CLAUDE.md" } },
        new object[] { "cursor", new[] { ".cursor/rules/cobaltum.mdc" } },
        new object[] { "copilot", new[] { ".github/copilot-instructions.md" } },
        new object[] { "all", AllAdapters },
    };

    [Fact]
    public async Task AutoUpdatesDetectedAdaptersWithoutCreatingOthers()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();
        fixture.WriteFile("CLAUDE.md", "# Existing Claude guidance\n");
        fixture.WriteFile(".github/copilot-instructions.md", "# Existing Copilot guidance\n");

        var result = await fixture.RunAsync("assistant", "init", "--project", fixture.ProjectPath);

        Assert.Equal(0, result.ExitCode);
        var claude = File.ReadAllText(fixture.Path("CLAUDE.md"));
        Assert.StartsWith("# Existing Claude guidance\n", claude, StringComparison.Ordinal);
        Assert.Contains(BeginMarker, claude, StringComparison.Ordinal);
        var copilot = File.ReadAllText(fixture.Path(".github/copilot-instructions.md"));
        Assert.StartsWith("# Existing Copilot guidance\n", copilot, StringComparison.Ordinal);
        Assert.Contains(BeginMarker, copilot, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.Path(".cobaltum/assistant.md")));
        Assert.False(File.Exists(fixture.Path("AGENTS.md")));
        Assert.False(File.Exists(fixture.Path(".cursor/rules/cobaltum.mdc")));
        Assert.Contains("Updated CLAUDE.md", result.Output, StringComparison.Ordinal);
        Assert.Contains("Updated .github/copilot-instructions.md", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task AutoCreatesAgentsWhenNoAdapterIsPresent()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();

        var result = await fixture.RunAsync("assistant", "init", "--project", fixture.ProjectPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(fixture.Path("AGENTS.md")));
        Assert.Contains("Created AGENTS.md", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task SharedAdapterPreservesContentOutsideTheManagedBlock()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();
        fixture.WriteFile(
            "AGENTS.md",
            $"""
            # Local instructions

            {BeginMarker}
            Old generated instruction.
            {EndMarker}

            Keep this user instruction.
            """);

        var result = await fixture.RunAsync(
            "assistant", "init", "--project", fixture.ProjectPath, "--target", "agents");

        Assert.Equal(0, result.ExitCode);
        var agents = File.ReadAllText(fixture.Path("AGENTS.md"));
        Assert.Contains("# Local instructions", agents, StringComparison.Ordinal);
        Assert.Contains("Keep this user instruction.", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("Old generated instruction.", agents, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(agents, BeginMarker));
        Assert.Equal(1, CountOccurrences(agents, EndMarker));
        Assert.Contains("Read and obey `.cobaltum/assistant.md`", agents, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task DedicatedAdapterRefusesAnUnrecognizedFileBeforeWriting()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();
        fixture.WriteFile(".cursor/rules/cobaltum.mdc", "---\nalwaysApply: true\n---\nUser cursor rule\n");
        var before = File.ReadAllBytes(fixture.Path(".cursor/rules/cobaltum.mdc"));

        var result = await fixture.RunAsync(
            "assistant", "init", "--project", fixture.ProjectPath, "--target", "cursor");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Refusing to overwrite unrecognized", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path(".cursor/rules/cobaltum.mdc")));
        Assert.False(File.Exists(fixture.Path(".cobaltum/assistant.md")));
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task AllTargetPreflightsEveryFileBeforeWriting()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();
        fixture.WriteFile("CLAUDE.md", "# Existing Claude guidance\n");
        fixture.WriteFile(".cursor/rules/cobaltum.mdc", "User cursor rule\n");
        var claudeBefore = File.ReadAllBytes(fixture.Path("CLAUDE.md"));
        var cursorBefore = File.ReadAllBytes(fixture.Path(".cursor/rules/cobaltum.mdc"));

        var result = await fixture.RunAsync(
            "assistant", "init", "--project", fixture.ProjectPath, "--target", "all");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Refusing to overwrite unrecognized", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Path(".cobaltum/assistant.md")));
        Assert.False(File.Exists(fixture.Path("AGENTS.md")));
        Assert.Equal(claudeBefore, File.ReadAllBytes(fixture.Path("CLAUDE.md")));
        Assert.Equal(cursorBefore, File.ReadAllBytes(fixture.Path(".cursor/rules/cobaltum.mdc")));
        Assert.False(File.Exists(fixture.Path(".github/copilot-instructions.md")));
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task RepeatingTheSameOptionsLeavesEverySelectedFileByteForByteUnchanged()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();
        var args = new[] { "assistant", "init", "--project", fixture.ProjectPath, "--target", "all" };

        var first = await fixture.RunAsync(args);
        Assert.Equal(0, first.ExitCode);
        var paths = new[] { ".cobaltum/assistant.md" }.Concat(AllAdapters).ToArray();
        var bytes = paths.ToDictionary(path => path, path => File.ReadAllBytes(fixture.Path(path)), StringComparer.Ordinal);

        var second = await fixture.RunAsync(args);

        Assert.Equal(0, second.ExitCode);
        foreach (var path in paths)
        {
            Assert.Equal(bytes[path], File.ReadAllBytes(fixture.Path(path)));
            Assert.Contains($"Unchanged {path}", second.Output, StringComparison.Ordinal);
        }

        Assert.Equal(string.Empty, second.Error);
    }

    [Theory]
    [InlineData("AGENTS.md", "agents", "# Local instructions\r\n", "\r\n")]
    [InlineData("CLAUDE.md", "claude", "# Local instructions\n", "\n")]
    public async Task UpdatesManagedBlocksWithTheExistingLineEnding(
        string adapter,
        string target,
        string existingContent,
        string expectedNewline)
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();
        fixture.WriteFile(adapter, existingContent);

        var result = await fixture.RunAsync(
            "assistant", "init", "--project", fixture.ProjectPath, "--target", target);

        Assert.Equal(0, result.ExitCode);
        var content = File.ReadAllText(fixture.Path(adapter));
        Assert.Contains(expectedNewline, content, StringComparison.Ordinal);
        if (string.Equals(expectedNewline, "\r\n", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("\n", content.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("\r", content, StringComparison.Ordinal);
        }

        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task HelpDescribesAssistantInitOptionsAndRepeatBehavior()
    {
        using var fixture = new AssistantFixture();

        var result = await fixture.RunAsync("assistant", "init", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "cobaltum assistant init --project <path> [--target auto|agents|claude|cursor|copilot|all]",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains("auto creates .cobaltum/assistant.md", result.Output, StringComparison.Ordinal);
        Assert.Contains("creates AGENTS.md when no adapter is present", result.Output, StringComparison.Ordinal);
        Assert.Contains("Re-running the same options", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task InvalidTargetUsesTheUsageErrorRouteWithoutWritingFiles()
    {
        using var fixture = new AssistantFixture();
        fixture.WriteProject();

        var result = await fixture.RunAsync(
            "assistant", "init", "--project", fixture.ProjectPath, "--target", "windsurf");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unsupported assistant target 'windsurf'", result.Error, StringComparison.Ordinal);
        Assert.Contains("auto, agents, claude, cursor, copilot, all", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Path(".cobaltum/assistant.md")));
        Assert.False(File.Exists(fixture.Path("AGENTS.md")));
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task InitRequiresAnExistingProjectPath()
    {
        using var fixture = new AssistantFixture();

        var result = await fixture.RunAsync("assistant", "init", "--project", "missing.csproj");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Path(".cobaltum")));
        Assert.Equal(string.Empty, result.Output);
    }

    private static readonly string[] AllAdapters =
    {
        "AGENTS.md",
        "CLAUDE.md",
        ".cursor/rules/cobaltum.mdc",
        ".github/copilot-instructions.md",
    };

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed class AssistantFixture : IDisposable
    {
        public AssistantFixture()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CobaltumOrm.Tool.Tests",
                Guid.NewGuid().ToString("N"));
            ProjectDirectory = System.IO.Path.Combine(Root, "App");
            ProjectPath = System.IO.Path.Combine(ProjectDirectory, "Example.App.csproj");
            Directory.CreateDirectory(ProjectDirectory);
        }

        public string Root { get; }

        public string ProjectDirectory { get; }

        public string ProjectPath { get; }

        public void WriteProject() =>
            File.WriteAllText(ProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");

        public string Path(string relativePath) =>
            System.IO.Path.Combine(ProjectDirectory, relativePath);

        public void WriteFile(string relativePath, string content)
        {
            var path = Path(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public async Task<RunResult> RunAsync(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var application = new ToolApplication(output, error, new ThrowingProcessRunner(), Root);
            var exitCode = await application.RunAsync(args, CancellationToken.None);
            return new RunResult(exitCode, output.ToString(), error.ToString());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("assistant init must not start a process.");
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);
}
