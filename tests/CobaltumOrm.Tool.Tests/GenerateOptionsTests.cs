using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class GenerateOptionsTests
{
    [Fact]
    public void DefaultsToIntermediateOutputAndDebug()
    {
        var options = GenerateOptions.Parse(new[] { "generate" });

        Assert.Equal(GenerateOutputMode.Intermediate, options.OutputMode);
        Assert.Equal("Debug", options.Configuration);
        Assert.Null(options.Project);
        Assert.Null(options.Output);
        Assert.Null(options.Provider);
        Assert.Null(options.GeneratedNamespace);
        Assert.False(options.NoRestore);
        Assert.False(options.Verbose);
    }

    [Fact]
    public void ReadsEveryOption()
    {
        var options = GenerateOptions.Parse(new[]
        {
            "generate",
            "--project", "src/App.csproj",
            "--configuration", "Release",
            "--framework", "net10.0",
            "--provider", "sqlite",
            "--generated-namespace", "App.Generated",
            "--output-mode", "directory",
            "--output", "Generated",
            "--no-restore",
            "--verbose",
        });

        Assert.Equal("src/App.csproj", options.Project);
        Assert.Equal("Release", options.Configuration);
        Assert.Equal("net10.0", options.Framework);
        Assert.Equal("Sqlite", options.Provider);
        Assert.Equal("App.Generated", options.GeneratedNamespace);
        Assert.Equal(GenerateOutputMode.Directory, options.OutputMode);
        Assert.Equal("Generated", options.Output);
        Assert.True(options.NoRestore);
        Assert.True(options.Verbose);
    }

    [Theory]
    [InlineData("-p", "src/App.csproj")]
    [InlineData("-c", "Release")]
    [InlineData("-f", "net10.0")]
    public void ShortOptionsMatchTheirLongForm(string option, string value)
    {
        var options = GenerateOptions.Parse(new[] { "generate", option, value });

        Assert.Equal(value, option switch
        {
            "-p" => options.Project,
            "-c" => options.Configuration,
            _ => options.Framework,
        });
    }

    [Fact]
    public void OutputIsRejectedForIntermediateMode()
    {
        var exception = Assert.Throws<ToolUsageException>(() =>
            GenerateOptions.Parse(new[] { "generate", "--output", "Generated" }));

        Assert.Contains("--output cannot be used", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("library")]
    public void OutputIsRequiredForDurableModes(string mode)
    {
        var exception = Assert.Throws<ToolUsageException>(() =>
            GenerateOptions.Parse(new[] { "generate", "--output-mode", mode }));

        Assert.Contains("--output is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryProjectSuppliesTheOutputDirectory()
    {
        var options = GenerateOptions.Parse(new[]
        {
            "generate", "--output-mode", "library", "--library-project", "Queries/Queries.csproj",
        });

        Assert.Equal("Queries/Queries.csproj", options.LibraryProject);
        Assert.Null(options.Output);
    }

    [Fact]
    public void LibraryOptionsRequireLibraryMode()
    {
        Assert.Throws<ToolUsageException>(() => GenerateOptions.Parse(new[]
        {
            "generate", "--output-mode", "directory", "--output", "Generated",
            "--library-project", "Queries.csproj",
        }));
        Assert.Throws<ToolUsageException>(() => GenerateOptions.Parse(new[]
        {
            "generate", "--library-name", "Queries",
        }));
    }

    [Fact]
    public void LibraryNameAndLibraryProjectAreExclusive()
    {
        var exception = Assert.Throws<ToolUsageException>(() => GenerateOptions.Parse(new[]
        {
            "generate", "--output-mode", "library",
            "--library-project", "Queries.csproj", "--library-name", "Queries",
        }));

        Assert.Contains("cannot be combined", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("Queries.csproj")]
    public void LibraryNameMustBeAPlainName(string name)
    {
        Assert.Throws<ToolUsageException>(() => GenerateOptions.Parse(new[]
        {
            "generate", "--output-mode", "library", "--output", "Generated", "--library-name", name,
        }));
    }

    [Fact]
    public void UnknownOutputModeIsRejected()
    {
        var exception = Assert.Throws<ToolUsageException>(() =>
            GenerateOptions.Parse(new[] { "generate", "--output-mode", "static" }));

        Assert.Contains("intermediate, directory, library", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("App..Generated")]
    [InlineData("1App")]
    [InlineData("App.Generated;rm")]
    [InlineData("App Generated")]
    public void InvalidGeneratedNamespacesAreRejected(string value)
    {
        var exception = Assert.Throws<ToolUsageException>(() =>
            GenerateOptions.Parse(new[] { "generate", "--generated-namespace", value }));

        Assert.Contains("is not a valid C# namespace", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOptionsAndPositionalsAreRejected()
    {
        Assert.Throws<ToolUsageException>(() => GenerateOptions.Parse(new[] { "generate", "--emit" }));
        Assert.Throws<ToolUsageException>(() => GenerateOptions.Parse(new[] { "generate", "all" }));
    }

    [Fact]
    public void OptionsThatTakeAValueRequireOne()
    {
        var exception = Assert.Throws<ToolUsageException>(() =>
            GenerateOptions.Parse(new[] { "generate", "--output-mode" }));

        Assert.Contains("requires a value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedProvidersAreRejected()
    {
        Assert.Throws<ToolUsageException>(() =>
            GenerateOptions.Parse(new[] { "generate", "--provider", "db2" }));
    }
}
