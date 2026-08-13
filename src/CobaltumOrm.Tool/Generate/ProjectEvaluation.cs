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

    public List<string> CompileFiles { get; } = new();

    public List<string> References { get; } = new();

    public List<string> AdditionalFiles { get; } = new();

    public List<string> MigrationSources { get; } = new();

    /// <summary>Reads the file written by the CobaltumOrmWriteGenerationInputs target.</summary>
    public static ProjectEvaluation Parse(IEnumerable<string> lines)
    {
        var evaluation = new ProjectEvaluation();
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
            }
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
}

/// <summary>Reads evaluated project inputs.</summary>
internal interface IProjectEvaluator
{
    Task<ProjectEvaluation> EvaluateAsync(
        string projectPath,
        GenerateOptions options,
        TextWriter log,
        CancellationToken cancellationToken);
}
