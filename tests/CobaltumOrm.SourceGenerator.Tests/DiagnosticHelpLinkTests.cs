using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public class DiagnosticHelpLinkTests
{
    private const string DocumentationUrl =
        "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md";

    [Fact]
    public void EveryDescriptorHasAStableHelpLink()
    {
        Assert.NotEmpty(GeneratorDiagnostics.All);
        Assert.Equal(DocumentationUrl + "#", GeneratorDiagnostics.HelpLinkPrefix);
        foreach (var descriptor in GeneratorDiagnostics.All)
        {
            Assert.Equal(
                DocumentationUrl + "#" + descriptor.Id.ToLowerInvariant(),
                descriptor.HelpLinkUri);
        }
    }

    [Fact]
    public void DescriptorIdentifiersAreUnique()
    {
        var ids = GeneratorDiagnostics.All.Select(descriptor => descriptor.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("diagnostics.md")]
    [InlineData("diagnostics.ja.md")]
    public void HelpLinkPointsAtAnExistingSection(string fileName)
    {
        var lines = File.ReadAllLines(
            Path.Combine(FindRepositoryRoot(), "docs", "ai", fileName));
        foreach (var descriptor in GeneratorDiagnostics.All)
        {
            Assert.Contains(lines, line => line == "### " + descriptor.Id);
        }
    }

    [Fact]
    public void QuerySqlDiagnosticCarriesTheHelpLink()
    {
        var result = GeneratorTestHost.Run(
            """
            using CobaltumOrm;

            [Query("Missing", "SELECT id FROM app.missing")]
            public static partial class MissingQueries
            {
            }
            """);

        var diagnostic = result.AllDiagnostics.First(item => item.Id == "COB004");
        Assert.Equal(DocumentationUrl + "#cob004", diagnostic.Descriptor.HelpLinkUri);
    }

    [Fact]
    public void ProviderConfigurationDiagnosticCarriesTheHelpLink()
    {
        var result = GeneratorTestHost.Run(
            """
            public static class Empty
            {
            }
            """,
            databaseProvider: "NotAProvider");

        var diagnostic = result.AllDiagnostics.First(item => item.Id == "COB008");
        Assert.Equal(DocumentationUrl + "#cob008", diagnostic.Descriptor.HelpLinkUri);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CobaltumOrm.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate CobaltumOrm.sln.");
    }
}
