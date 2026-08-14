using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CobaltumOrm.Tests;

/// <summary>
/// Keeps docs/ai in step with the code it documents. Every code block in the recipes is copied
/// from a region of the sample project that CI compiles, every COB code emitted by the source
/// generator or the build transform has a section in both diagnostics pages, and every link in
/// llms.txt resolves to a file in the repository.
/// </summary>
public class AiDocumentationTests
{
    private const string RepositoryUrl = "https://github.com/hckaye/CobaltumORM/blob/main/";

    private static readonly string[] SnippetSources =
    {
        "samples/CobaltumOrm.Consumer/AiGuideSamples.cs",
        "samples/CobaltumOrm.Consumer/Migrations.cs",
        "samples/CobaltumOrm.Consumer/Migrations/V20__add_display_name.sql",
    };

    private static readonly string[] RecipeDocuments =
    {
        "docs/ai/recipes.md",
        "docs/ai/recipes.ja.md",
    };

    private static readonly string[] PairedDocuments =
    {
        "docs/ai/agent-tools.md",
        "docs/ai/agent-tools.ja.md",
        "docs/ai/quick-reference.md",
        "docs/ai/quick-reference.ja.md",
        "docs/ai/recipes.md",
        "docs/ai/recipes.ja.md",
        "docs/ai/diagnostics.md",
        "docs/ai/diagnostics.ja.md",
    };

    private static readonly Regex SnippetStart =
        new Regex(@"^(?://|--) <snippet ([a-z0-9-]+)>$", RegexOptions.Compiled);

    private static readonly Regex SnippetEnd =
        new Regex(@"^(?://|--) </snippet>$", RegexOptions.Compiled);

    private static readonly Regex SnippetMarker =
        new Regex(@"^<!-- snippet: ([a-z0-9-]+) -->$", RegexOptions.Compiled);

    private static readonly Regex DiagnosticCode =
        new Regex(@"""(COB[0-9]{3})""", RegexOptions.Compiled);

    [Fact]
    public void EveryDocumentedSnippetMatchesTheCompiledSample()
    {
        var root = FindRepositoryRoot();
        var snippets = ReadSnippets(root);
        Assert.NotEmpty(snippets);

        foreach (var document in RecipeDocuments)
        {
            foreach (var (name, documented) in ReadDocumentedSnippets(Path.Combine(root, document)))
            {
                Assert.True(
                    snippets.ContainsKey(name),
                    $"{document} references snippet '{name}', which no sample source declares.");
                Assert.Equal(snippets[name], documented);
            }
        }
    }

    [Fact]
    public void EnglishAndJapaneseRecipesUseTheSameSnippets()
    {
        var root = FindRepositoryRoot();
        var english = ReadDocumentedSnippets(Path.Combine(root, "docs/ai/recipes.md"))
            .Select(entry => entry.Name)
            .ToArray();
        var japanese = ReadDocumentedSnippets(Path.Combine(root, "docs/ai/recipes.ja.md"))
            .Select(entry => entry.Name)
            .ToArray();

        Assert.NotEmpty(english);
        Assert.Equal(english, japanese);
    }

    [Fact]
    public void EverySampleSnippetIsDocumented()
    {
        var root = FindRepositoryRoot();
        var documented = ReadDocumentedSnippets(Path.Combine(root, "docs/ai/recipes.md"))
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in ReadSnippets(root).Keys)
        {
            Assert.Contains(name, documented);
        }
    }

    [Fact]
    public void EveryEmittedDiagnosticCodeIsDocumented()
    {
        var root = FindRepositoryRoot();
        var codes = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => DiagnosticCode.Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(codes);
        foreach (var fileName in new[] { "docs/ai/diagnostics.md", "docs/ai/diagnostics.ja.md" })
        {
            var lines = File.ReadAllLines(Path.Combine(root, fileName));
            foreach (var code in codes)
            {
                Assert.True(
                    lines.Any(line => line == "### " + code),
                    $"{fileName} has no section for {code}.");
            }
        }
    }

    [Fact]
    public void DiagnosticsPagesDocumentTheSameCodes()
    {
        var root = FindRepositoryRoot();
        var english = ReadDiagnosticSections(Path.Combine(root, "docs/ai/diagnostics.md"));
        var japanese = ReadDiagnosticSections(Path.Combine(root, "docs/ai/diagnostics.ja.md"));

        Assert.NotEmpty(english);
        Assert.Equal(english, japanese);
    }

    [Fact]
    public void LlmsIndexLinksResolveToRepositoryPaths()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "llms.txt"));
        var links = Regex.Matches(index, @"\]\((" + Regex.Escape(RepositoryUrl) + @"[^)]*)\)")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            var relative = link.Substring(RepositoryUrl.Length).Replace('/', Path.DirectorySeparatorChar);
            var target = Path.Combine(root, relative);
            Assert.True(
                File.Exists(target) || Directory.Exists(target),
                $"llms.txt links to '{link}', which does not exist in the repository.");
        }

        foreach (var document in PairedDocuments)
        {
            Assert.Contains(RepositoryUrl + document.Replace(Path.DirectorySeparatorChar, '/'), index, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RelativeDocumentationLinksResolve()
    {
        var root = FindRepositoryRoot();
        foreach (var document in PairedDocuments)
        {
            var path = Path.Combine(root, document);
            var directory = Path.GetDirectoryName(path)!;
            var links = Regex.Matches(File.ReadAllText(path), @"\]\(([^)#:]+)(?:#[^)]*)?\)")
                .Select(match => match.Groups[1].Value)
                .Where(target => target.Length != 0)
                .Where(target => !Uri.TryCreate(target, UriKind.Absolute, out _))
                .Distinct(StringComparer.Ordinal);

            foreach (var link in links)
            {
                var target = Path.GetFullPath(Path.Combine(directory, link.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(
                    File.Exists(target) || Directory.Exists(target),
                    $"{document} links to '{link}', which does not exist.");
            }
        }
    }

    private static IReadOnlyDictionary<string, string[]> ReadSnippets(string root)
    {
        var snippets = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var source in SnippetSources)
        {
            string? name = null;
            var body = new List<string>();
            foreach (var line in File.ReadAllLines(Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar))))
            {
                var trimmed = line.Trim();
                var start = SnippetStart.Match(trimmed);
                if (start.Success)
                {
                    name = start.Groups[1].Value;
                    body.Clear();
                    continue;
                }

                if (name is null)
                {
                    continue;
                }

                if (SnippetEnd.IsMatch(trimmed))
                {
                    snippets.Add(name, Dedent(body));
                    name = null;
                    continue;
                }

                body.Add(line);
            }

            Assert.Null(name);
        }

        return snippets;
    }

    private static string[] Dedent(IReadOnlyList<string> body)
    {
        var indent = body
            .Where(line => line.Trim().Length != 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        return body
            .Select(line => line.Trim().Length == 0 ? string.Empty : line.Substring(indent))
            .ToArray();
    }

    private static IReadOnlyList<(string Name, string[] Body)> ReadDocumentedSnippets(string path)
    {
        var lines = File.ReadAllLines(path);
        var documented = new List<(string, string[])>();
        for (var index = 0; index < lines.Length; index++)
        {
            var marker = SnippetMarker.Match(lines[index]);
            if (!marker.Success)
            {
                continue;
            }

            Assert.StartsWith("```", lines[index + 1], StringComparison.Ordinal);
            var body = new List<string>();
            var cursor = index + 2;
            while (lines[cursor] != "```")
            {
                body.Add(lines[cursor]);
                cursor++;
            }

            documented.Add((marker.Groups[1].Value, body.ToArray()));
            index = cursor;
        }

        return documented;
    }

    private static string[] ReadDiagnosticSections(string path) =>
        File.ReadAllLines(path)
            .Where(line => line.StartsWith("### COB", StringComparison.Ordinal))
            .Select(line => line.Substring(4))
            .ToArray();

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
