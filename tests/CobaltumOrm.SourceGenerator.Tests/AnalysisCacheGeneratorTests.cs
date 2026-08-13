using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class AnalysisCacheGeneratorTests
{
    [Fact]
    public void EnabledAndDisabledCacheProduceIdenticalSourcesAndDiagnostics()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "CobaltumOrm.AnalysisCacheGeneratorTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            const string source = """
                using CobaltumOrm;
                using CobaltumOrm.Migrations;

                [Migration(1, "create users")]
                public sealed class CreateUsers : Migration
                {
                    public override void Up()
                    {
                        Execute.Sql("CREATE TABLE users (id bigint NOT NULL, name text NOT NULL)");
                    }

                    public override void Down() { }
                }

                [Query("Users", "SELECT id, name FROM users")]
                [Query("Broken", "SELECT missing FROM users")]
                public static partial class Queries { }
                """;

            var enabled = GeneratorTestHost.Run(
                source,
                analysisCacheDirectory: directory,
                analysisCacheEnabled: true);
            var enabledAgain = GeneratorTestHost.Run(
                source,
                analysisCacheDirectory: directory,
                analysisCacheEnabled: true);
            var disabled = GeneratorTestHost.Run(
                source,
                analysisCacheDirectory: directory,
                analysisCacheEnabled: false);

            Assert.Equal(Sources(enabled), Sources(enabledAgain));
            Assert.Equal(Sources(enabled), Sources(disabled));
            Assert.Equal(Diagnostics(enabled), Diagnostics(enabledAgain));
            Assert.Equal(Diagnostics(enabled), Diagnostics(disabled));
            Assert.Contains(Diagnostics(enabled), diagnostic => diagnostic.Contains("missing", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string[] Sources(GeneratorTestResult result) => result.RunResult.Results
        .SelectMany(generator => generator.GeneratedSources)
        .Select(source => source.HintName + "\0" + source.SourceText)
        .OrderBy(source => source, StringComparer.Ordinal)
        .ToArray();

    private static string[] Diagnostics(GeneratorTestResult result) => result.GeneratorDiagnostics
        .Select(DiagnosticValue)
        .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
        .ToArray();

    private static string DiagnosticValue(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return string.Join(
            "\0",
            diagnostic.Id,
            diagnostic.Severity,
            diagnostic.GetMessage(),
            span.Path,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character);
    }
}
