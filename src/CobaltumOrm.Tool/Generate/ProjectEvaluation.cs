namespace CobaltumOrm.Tool;

/// <summary>The MSBuild-evaluated inputs a generation run needs.</summary>
internal sealed class ProjectEvaluation
{
    public string ProjectPath { get; set; } = string.Empty;

    public string ProjectDirectory { get; set; } = string.Empty;

    public string TargetFramework { get; set; } = string.Empty;

    public string Configuration { get; set; } = string.Empty;

    public string AssemblyName { get; set; } = string.Empty;

    public string RootNamespace { get; set; } = string.Empty;

    public string IntermediateOutputPath { get; set; } = string.Empty;

    public string LangVersion { get; set; } = string.Empty;

    public string Nullable { get; set; } = string.Empty;

    public string ImplicitUsings { get; set; } = string.Empty;

    public string DefineConstants { get; set; } = string.Empty;

    public string DatabaseProvider { get; set; } = string.Empty;

    public string GeneratedNamespace { get; set; } = string.Empty;

    public bool AnalysisCacheEnabled { get; set; } = true;

    public string AnalysisCacheDirectory { get; set; } = string.Empty;

    /// <summary>The evaluated CobaltumORM packages referenced by the project.</summary>
    public List<EvaluatedPackageReference> CobaltumOrmPackageReferences { get; } = new();

    /// <summary>The evaluated paths supplied through CobaltumOrmMigrationProjectReference.</summary>
    public List<string> MigrationProjectReferencePaths { get; } = new();

    /// <summary>The resolved CobaltumORM source-generator analyzer assemblies.</summary>
    public List<string> CobaltumOrmSourceGeneratorPaths { get; } = new();

    /// <summary>The transform task assembly configured by the CobaltumORM MSBuild targets.</summary>
    public string CompilerTaskAssembly { get; set; } = string.Empty;

    /// <summary>Whether the MSBuild transform is enabled for the evaluated target.</summary>
    public bool CompileTimeQueriesEnabled { get; set; } = true;

    /// <summary>Properties made visible to source generators by MSBuild.</summary>
    public List<string> CompilerVisibleProperties { get; } = new();

    public List<string> CompileFiles { get; } = new();

    public List<string> References { get; } = new();

    public List<string> AdditionalFiles { get; } = new();

    public List<string> MigrationSources { get; } = new();

    /// <summary>Migration inputs from referenced migration projects, including SQL files.</summary>
    public List<string> MigrationInputPaths { get; } = new();

    /// <summary>Reads the file written by the CobaltumOrmWriteGenerationInputs target.</summary>
    public static ProjectEvaluation Parse(IEnumerable<string> lines)
    {
        var evaluation = new ProjectEvaluation();
        var packageReferences = new List<ReportedPackageReference>();
        var centralPackageVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator);
            var value = line.Substring(separator + 1).Trim();
            switch (key)
            {
                case "project":
                    evaluation.ProjectPath = value;
                    break;
                case "projectdirectory":
                    evaluation.ProjectDirectory = value;
                    break;
                case "targetframework":
                    evaluation.TargetFramework = value;
                    break;
                case "configuration":
                    evaluation.Configuration = value;
                    break;
                case "assemblyname":
                    evaluation.AssemblyName = value;
                    break;
                case "rootnamespace":
                    evaluation.RootNamespace = value;
                    break;
                case "intermediateoutputpath":
                    evaluation.IntermediateOutputPath = value;
                    break;
                case "langversion":
                    evaluation.LangVersion = value;
                    break;
                case "nullable":
                    evaluation.Nullable = value;
                    break;
                case "implicitusings":
                    evaluation.ImplicitUsings = value;
                    break;
                case "defineconstants":
                    evaluation.DefineConstants = value;
                    break;
                case "databaseprovider":
                    evaluation.DatabaseProvider = value;
                    break;
                case "generatednamespace":
                    evaluation.GeneratedNamespace = value;
                    break;
                case "analysiscache":
                    evaluation.AnalysisCacheEnabled = !string.Equals(
                        value,
                        "false",
                        StringComparison.OrdinalIgnoreCase);
                    break;
                case "analysiscachedirectory":
                    evaluation.AnalysisCacheDirectory = value;
                    break;
                case "cobaltumormpackagereference":
                    AddReportedPackageReference(packageReferences, value);
                    break;
                case "cobaltumormcentralpackageversion":
                    AddCentralPackageVersion(centralPackageVersions, value);
                    break;
                case "migrationprojectreference":
                    Add(evaluation.MigrationProjectReferencePaths, value);
                    break;
                case "sourcegenerator":
                    Add(evaluation.CobaltumOrmSourceGeneratorPaths, value);
                    break;
                case "compilertaskassembly":
                    evaluation.CompilerTaskAssembly = value;
                    break;
                case "compiletimequeries":
                    evaluation.CompileTimeQueriesEnabled = !string.Equals(
                        value,
                        "false",
                        StringComparison.OrdinalIgnoreCase);
                    break;
                case "compilervisibleproperty":
                    Add(evaluation.CompilerVisibleProperties, value);
                    break;
                case "compile":
                    Add(evaluation.CompileFiles, value);
                    break;
                case "reference":
                    Add(evaluation.References, value);
                    break;
                case "additionalfile":
                    Add(evaluation.AdditionalFiles, value);
                    break;
                case "migrationsource":
                    Add(evaluation.MigrationSources, value);
                    break;
                case "migrationinput":
                    Add(evaluation.MigrationInputPaths, value);
                    break;
            }
        }

        foreach (var packageReference in packageReferences)
        {
            centralPackageVersions.TryGetValue(packageReference.Id, out var centralVersion);
            AddPackageReference(
                evaluation.CobaltumOrmPackageReferences,
                packageReference.Id,
                FirstNonEmpty(
                    packageReference.VersionOverride,
                    packageReference.Version,
                    centralVersion ?? string.Empty));
        }

        return evaluation;
    }

    private static void Add(List<string> values, string value)
    {
        if (value.Length != 0 && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }

    private static void AddReportedPackageReference(List<ReportedPackageReference> values, string value)
    {
        var metadata = value.Split('|');
        var id = metadata[0].Trim();
        if (id.Length == 0)
        {
            return;
        }

        var (versionOverride, version) = metadata.Length switch
        {
            1 => (string.Empty, string.Empty),
            2 => (string.Empty, metadata[1]),
            _ => (metadata[1], metadata[2]),
        };

        values.Add(new ReportedPackageReference(id, versionOverride, version));
    }

    private static void AddCentralPackageVersion(IDictionary<string, string> versions, string value)
    {
        var separator = value.IndexOf('|', StringComparison.Ordinal);
        var id = (separator < 0 ? value : value.Substring(0, separator)).Trim();
        var version = separator < 0 ? string.Empty : value.Substring(separator + 1).Trim();
        if (id.Length != 0 && version.Length != 0)
        {
            versions[id] = version;
        }
    }

    private static void AddPackageReference(
        List<EvaluatedPackageReference> values,
        string id,
        string version)
    {
        if (id.Length == 0 || values.Any(existing =>
                string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Version, version, StringComparison.Ordinal)))
        {
            return;
        }

        values.Add(new EvaluatedPackageReference(id, version));
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private sealed record ReportedPackageReference(string Id, string VersionOverride, string Version);
}

/// <summary>A CobaltumORM package reference after MSBuild has evaluated its metadata.</summary>
internal sealed record EvaluatedPackageReference(string Id, string Version);

/// <summary>Options shared by commands that evaluate a project through MSBuild.</summary>
internal class ProjectEvaluationOptions
{
    public string Configuration { get; set; } = "Debug";

    public string? Framework { get; set; }

    public bool NoRestore { get; set; }

    public bool Verbose { get; set; }
}

/// <summary>Reads evaluated project inputs.</summary>
internal interface IProjectEvaluator
{
    Task<ProjectEvaluation> EvaluateAsync(
        string projectPath,
        ProjectEvaluationOptions options,
        TextWriter log,
        CancellationToken cancellationToken);
}
