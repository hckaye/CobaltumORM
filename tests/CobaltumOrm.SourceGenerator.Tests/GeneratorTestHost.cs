using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using CobaltumOrm.Migrations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace CobaltumOrm.SourceGenerator.Tests;

internal static class GeneratorTestHost
{
    internal static GeneratorTestResult Run(
        string source,
        IEnumerable<(string Path, string Text)>? additionalFiles = null,
        bool netStandard20 = false,
        string generatedNamespace = "TestApp.Generated",
        string? databaseProvider = null,
        string? analysisCacheDirectory = null,
        bool analysisCacheEnabled = true)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "Consumer.cs");
        var references = netStandard20 ? NetStandardReferences() : RuntimeReferences();
        var compilation = CSharpCompilation.Create(
            "GeneratedConsumer_" + Guid.NewGuid().ToString("N"),
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Release,
                warningLevel: 9999));
        var additionalTexts = (additionalFiles ?? Array.Empty<(string, string)>())
            .Select(file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Text))
            .ToImmutableArray();
        var globalOptions = new Dictionary<string, string>
        {
            ["build_property.CobaltumOrmGeneratedNamespace"] = generatedNamespace,
            ["build_property.CobaltumOrmAnalysisCache"] = analysisCacheEnabled ? "true" : "false",
        };
        if (databaseProvider != null)
        {
            globalOptions["build_property.CobaltumOrmDatabaseProvider"] = databaseProvider;
        }

        if (analysisCacheDirectory != null)
        {
            globalOptions["build_property._CobaltumOrmAnalysisCacheDirectory"] = analysisCacheDirectory;
        }

        var options = new TestAnalyzerConfigOptionsProvider(globalOptions);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new CobaltumOrmGenerator().AsSourceGenerator() },
            additionalTexts,
            parseOptions,
            options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        return new GeneratorTestResult(driver.GetRunResult(), outputCompilation, generatorDiagnostics);
    }

    private static ImmutableArray<MetadataReference> RuntimeReferences()
    {
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
        return trusted.Concat(ProjectReferences(false)).Distinct(MetadataReferencePathComparer.Instance).ToImmutableArray();
    }

    private static ImmutableArray<MetadataReference> NetStandardReferences()
    {
        var packageRoot = ResolveNetStandardLibraryRefRoot();
        var references = Directory.EnumerateFiles(packageRoot, "*.dll")
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
        return references.Concat(ProjectReferences(true)).Distinct(MetadataReferencePathComparer.Instance).ToImmutableArray();
    }

    private static string ResolveNetStandardLibraryRefRoot()
    {
        // The repository NuGet.Config overrides the global packages folder to a local .packages directory,
        // and the restored NETStandard.Library version may differ from the one originally pinned (e.g. 2.0.3 vs 2.0.0).
        // Search each candidate packages root for any version that provides the netstandard2.0 ref assemblies.
        var candidatePackagesRoots = new[]
        {
            Path.Combine(FindRepositoryRoot(), ".packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        };

        foreach (var packagesRoot in candidatePackagesRoots)
        {
            var libraryRoot = Path.Combine(packagesRoot, "netstandard.library");
            if (!Directory.Exists(libraryRoot))
            {
                continue;
            }

            foreach (var versionDir in Directory.EnumerateDirectories(libraryRoot)
                         .OrderByDescending(dir => dir, StringComparer.OrdinalIgnoreCase))
            {
                var refRoot = Path.Combine(versionDir, "build", "netstandard2.0", "ref");
                if (Directory.Exists(refRoot) && Directory.EnumerateFiles(refRoot, "*.dll").Any())
                {
                    return refRoot;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the NETStandard.Library ref assemblies for netstandard2.0. " +
            $"Searched under: {string.Join(", ", candidatePackagesRoots)}. " +
            "Run 'dotnet restore tests/CobaltumOrm.SourceGenerator.Tests' first.");
    }

    private static IEnumerable<MetadataReference> ProjectReferences(bool netStandard20)
    {
        yield return MetadataReference.CreateFromFile(typeof(QueryAttribute).Assembly.Location);
        var migrationPath = typeof(Migration).Assembly.Location;
        if (netStandard20)
        {
            migrationPath = Path.Combine(
                FindRepositoryRoot(),
                "src",
                "CobaltumOrm.Migrations",
                "bin",
                "Release",
                "netstandard2.0",
                "CobaltumOrm.Migrations.dll");
        }

        yield return MetadataReference.CreateFromFile(migrationPath);
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

    private sealed class MetadataReferencePathComparer : IEqualityComparer<MetadataReference>
    {
        internal static readonly MetadataReferencePathComparer Instance = new MetadataReferencePathComparer();

        public bool Equals(MetadataReference? x, MetadataReference? y) =>
            string.Equals(x?.Display, y?.Display, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(MetadataReference obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Display ?? string.Empty);
    }
}

internal sealed class GeneratorTestResult
{
    internal GeneratorTestResult(
        GeneratorDriverRunResult runResult,
        Compilation compilation,
        ImmutableArray<RoslynDiagnostic> generatorDiagnostics)
    {
        RunResult = runResult;
        Compilation = compilation;
        GeneratorDiagnostics = generatorDiagnostics;
    }

    internal GeneratorDriverRunResult RunResult { get; }
    internal Compilation Compilation { get; }
    internal ImmutableArray<RoslynDiagnostic> GeneratorDiagnostics { get; }

    internal string GeneratedText => string.Join(
        "\n",
        RunResult.Results.SelectMany(result => result.GeneratedSources).Select(source => source.SourceText.ToString()));

    internal ImmutableArray<RoslynDiagnostic> AllDiagnostics =>
        GeneratorDiagnostics.AddRange(Compilation.GetDiagnostics());

    internal Assembly EmitAndLoad()
    {
        using var stream = new MemoryStream();
        var result = Compilation.Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
        }

        return Assembly.Load(stream.ToArray());
    }
}

internal sealed class InMemoryAdditionalText : AdditionalText
{
    private readonly SourceText _text;

    internal InMemoryAdditionalText(string path, string text)
    {
        Path = path;
        _text = SourceText.From(text, Encoding.UTF8);
    }

    public override string Path { get; }

    public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
}

internal sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions _global;

    internal TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> global)
    {
        _global = new TestAnalyzerConfigOptions(global);
    }

    public override AnalyzerConfigOptions GlobalOptions => _global;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestAnalyzerConfigOptions.Empty;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestAnalyzerConfigOptions.Empty;
}

internal sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
{
    internal static readonly TestAnalyzerConfigOptions Empty =
        new TestAnalyzerConfigOptions(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _values;

    internal TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
}
