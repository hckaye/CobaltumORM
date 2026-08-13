using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class ProjectEvaluationTests
{
    [Fact]
    public void ReadsPropertiesAndItemsFromTheMsBuildReport()
    {
        var evaluation = ProjectEvaluation.Parse(new[]
        {
            "project=/src/App/App.csproj",
            "projectdirectory=/src/App",
            "targetframework=net10.0",
            "configuration=Release",
            "assemblyname=App",
            "rootnamespace=App.Root",
            "intermediateoutputpath=/src/App/obj/Release/net10.0/",
            "langversion=12.0",
            "nullable=enable",
            "implicitusings=enable",
            "defineconstants=TRACE;RELEASE;NET10_0",
            "databaseprovider=Sqlite",
            "generatednamespace=App.Database",
            "compile=/src/App/Program.cs",
            "compile=/src/App/Queries.cs",
            "reference=/packages/CobaltumOrm.dll",
            "additionalfile=/src/App/Migrations/V1__init.sql",
            "migrationsource=/src/Database/Migrations/CreateUsers.cs",
        });

        Assert.Equal("/src/App/App.csproj", evaluation.ProjectPath);
        Assert.Equal("/src/App", evaluation.ProjectDirectory);
        Assert.Equal("net10.0", evaluation.TargetFramework);
        Assert.Equal("Release", evaluation.Configuration);
        Assert.Equal("App", evaluation.AssemblyName);
        Assert.Equal("App.Root", evaluation.RootNamespace);
        Assert.Equal("/src/App/obj/Release/net10.0/", evaluation.IntermediateOutputPath);
        Assert.Equal("12.0", evaluation.LangVersion);
        Assert.Equal("enable", evaluation.Nullable);
        Assert.Equal("enable", evaluation.ImplicitUsings);
        Assert.Equal("TRACE;RELEASE;NET10_0", evaluation.DefineConstants);
        Assert.Equal("Sqlite", evaluation.DatabaseProvider);
        Assert.Equal("App.Database", evaluation.GeneratedNamespace);
        Assert.Equal(
            new[] { "/src/App/Program.cs", "/src/App/Queries.cs" },
            evaluation.CompileFiles);
        Assert.Equal(new[] { "/packages/CobaltumOrm.dll" }, evaluation.References);
        Assert.Equal(new[] { "/src/App/Migrations/V1__init.sql" }, evaluation.AdditionalFiles);
        Assert.Equal(new[] { "/src/Database/Migrations/CreateUsers.cs" }, evaluation.MigrationSources);
    }

    [Fact]
    public void EmptyValuesAndDuplicatesAreDropped()
    {
        var evaluation = ProjectEvaluation.Parse(new[]
        {
            "targetframework=",
            "compile=/src/App/Program.cs",
            "compile=/src/App/Program.cs",
            "additionalfile=",
            "unknown=value",
            "no separator",
            string.Empty,
        });

        Assert.Equal(string.Empty, evaluation.TargetFramework);
        Assert.Single(evaluation.CompileFiles);
        Assert.Empty(evaluation.AdditionalFiles);
    }

    [Fact]
    public void TheEmbeddedTargetsFileReportsEveryInputTheEngineNeeds()
    {
        var targets = MsBuildProjectEvaluator.ReadTargets();

        Assert.Contains("<Target Name=\"CobaltumOrmWriteGenerationInputs\"", targets, StringComparison.Ordinal);
        Assert.Contains("DependsOnTargets=\"ResolveReferences\"", targets, StringComparison.Ordinal);
        Assert.Contains("CobaltumOrmGetMigrationInputs", targets, StringComparison.Ordinal);
        foreach (var key in new[]
        {
            "compile=", "reference=", "additionalfile=", "migrationsource=",
            "targetframework=", "defineconstants=", "databaseprovider=", "generatednamespace=",
        })
        {
            Assert.Contains(key, targets, StringComparison.Ordinal);
        }
    }
}
