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
            "analysiscache=false",
            "analysiscachedirectory=/src/App/obj/Release/net10.0/CobaltumOrm/AnalysisCache",
            "cobaltumormpackagereference=CobaltumOrm|0.0.5",
            "cobaltumormpackagereference=CobaltumOrm.SourceGenerator|0.0.5",
            "migrationprojectreference=/src/Database/Database.csproj",
            "sourcegenerator=/packages/CobaltumOrm.SourceGenerator.dll",
            "compilertaskassembly=/packages/CobaltumOrm.Compiler.dll",
            "compiletimequeries=false",
            "explicitgeneration=true",
            "compilervisibleproperty=CobaltumOrmGeneratedNamespace",
            "compile=/src/App/Program.cs",
            "compile=/src/App/Queries.cs",
            "reference=/packages/CobaltumOrm.dll",
            "additionalfile=/src/App/Migrations/V1__init.sql",
            "migrationsource=/src/Database/Migrations/CreateUsers.cs",
            "migrationinput=/src/Database/Migrations/CreateUsers.cs",
            "migrationinput=/src/Database/Migrations/V2__add_email.sql",
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
        Assert.False(evaluation.AnalysisCacheEnabled);
        Assert.Equal(
            "/src/App/obj/Release/net10.0/CobaltumOrm/AnalysisCache",
            evaluation.AnalysisCacheDirectory);
        Assert.Equal(
            new[]
            {
                new EvaluatedPackageReference("CobaltumOrm", "0.0.5"),
                new EvaluatedPackageReference("CobaltumOrm.SourceGenerator", "0.0.5"),
            },
            evaluation.CobaltumOrmPackageReferences);
        Assert.Equal(new[] { "/src/Database/Database.csproj" }, evaluation.MigrationProjectReferencePaths);
        Assert.Equal(new[] { "/packages/CobaltumOrm.SourceGenerator.dll" }, evaluation.CobaltumOrmSourceGeneratorPaths);
        Assert.Equal("/packages/CobaltumOrm.Compiler.dll", evaluation.CompilerTaskAssembly);
        Assert.False(evaluation.CompileTimeQueriesEnabled);
        Assert.True(evaluation.ExplicitGeneration);
        Assert.Equal(new[] { "CobaltumOrmGeneratedNamespace" }, evaluation.CompilerVisibleProperties);
        Assert.Equal(
            new[] { "/src/App/Program.cs", "/src/App/Queries.cs" },
            evaluation.CompileFiles);
        Assert.Equal(new[] { "/packages/CobaltumOrm.dll" }, evaluation.References);
        Assert.Equal(new[] { "/src/App/Migrations/V1__init.sql" }, evaluation.AdditionalFiles);
        Assert.Equal(new[] { "/src/Database/Migrations/CreateUsers.cs" }, evaluation.MigrationSources);
        Assert.Equal(
            new[]
            {
                "/src/Database/Migrations/CreateUsers.cs",
                "/src/Database/Migrations/V2__add_email.sql",
            },
            evaluation.MigrationInputPaths);
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
    public void PackageReferencesPreferVersionOverrideAndRetainAnEvaluatedCentralVersion()
    {
        var evaluation = ProjectEvaluation.Parse(new[]
        {
            "cobaltumormpackagereference=CobaltumOrm.Migrations|0.0.4|1.0.0",
            "cobaltumormpackagereference=CobaltumOrm||",
            "cobaltumormpackagereference=CobaltumOrm.Analysis||",
            "cobaltumormcentralpackageversion=CobaltumOrm|1.0.0",
        });

        Assert.Equal(
            new[]
            {
                new EvaluatedPackageReference("CobaltumOrm.Migrations", "0.0.4"),
                new EvaluatedPackageReference("CobaltumOrm", "1.0.0"),
                new EvaluatedPackageReference("CobaltumOrm.Analysis", string.Empty),
            },
            evaluation.CobaltumOrmPackageReferences);
    }

    [Fact]
    public async Task MsBuildEvaluationReportsCentralPackageVersionsAndVersionOverrides()
    {
        using var fixture = new CentralPackageManagementFixture();
        var evaluation = await new MsBuildProjectEvaluator().EvaluateAsync(
            fixture.ProjectPath,
            new ProjectEvaluationOptions(),
            TextWriter.Null,
            CancellationToken.None);

        Assert.Contains(
            new EvaluatedPackageReference("CobaltumOrm", "0.0.5"),
            evaluation.CobaltumOrmPackageReferences);
        Assert.Contains(
            new EvaluatedPackageReference("CobaltumOrm.Migrations", "0.0.4"),
            evaluation.CobaltumOrmPackageReferences);
    }

    [Fact]
    public void TheEmbeddedTargetsFileReportsEveryInputTheEngineNeeds()
    {
        var targets = MsBuildProjectEvaluator.ReadTargets();

        Assert.Contains("<Target Name=\"CobaltumOrmWriteGenerationInputs\"", targets, StringComparison.Ordinal);
        Assert.Contains("DependsOnTargets=\"ResolveReferences\"", targets, StringComparison.Ordinal);
        Assert.Contains("CobaltumOrmGetMigrationInputs", targets, StringComparison.Ordinal);
        Assert.Contains("%(VersionOverride)|%(Version)", targets, StringComparison.Ordinal);
        Assert.Contains("cobaltumormcentralpackageversion=", targets, StringComparison.Ordinal);
        foreach (var key in new[]
        {
            "compile=", "reference=", "additionalfile=", "migrationsource=",
            "targetframework=", "defineconstants=", "databaseprovider=", "generatednamespace=",
            "analysiscache=", "analysiscachedirectory=",
            "cobaltumormpackagereference=", "migrationprojectreference=", "sourcegenerator=",
            "compilertaskassembly=", "compiletimequeries=", "compilervisibleproperty=", "migrationinput=",
        })
        {
            Assert.Contains(key, targets, StringComparison.Ordinal);
        }
    }

    private sealed class CentralPackageManagementFixture : IDisposable
    {
        public CentralPackageManagementFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CobaltumOrm.ProjectEvaluationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ProjectPath = Path.Combine(Root, "Fixture.csproj");
            File.WriteAllText(Path.Combine(Root, "Directory.Packages.props"), """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <CentralPackageVersionOverrideEnabled>true</CentralPackageVersionOverrideEnabled>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="CobaltumOrm" Version="0.0.5" />
                    <PackageVersion Include="CobaltumOrm.Migrations" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="CobaltumOrm" />
                    <PackageReference Include="CobaltumOrm.Migrations" VersionOverride="0.0.4" />
                  </ItemGroup>
                </Project>
                """);
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
