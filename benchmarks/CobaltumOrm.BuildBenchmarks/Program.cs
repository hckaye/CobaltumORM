using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace CobaltumOrm.BuildBenchmarks;

internal static class Program
{
    private static readonly WorkloadProfile[] DefaultProfiles =
    [
        new("small", 100, 100, 25, 4),
        new("medium", 500, 500, 100, 4),
        new("large", 1_000, 1_000, 200, 4),
    ];

    private static readonly BuildVariant[] Variants =
    [
        new("plain", Path.Combine("Plain", "Plain.csproj")),
        new("cobaltum", Path.Combine("Cobaltum", "Cobaltum.csproj")),
    ];

    private static readonly BuildScenario[] Scenarios =
    [
        BuildScenario.Clean,
        BuildScenario.NoChange,
        BuildScenario.OneFileChange,
    ];

    public static async Task<int> Main(string[] args)
    {
        BenchmarkOptions options;
        try
        {
            options = BenchmarkOptions.Parse(args, DefaultProfiles);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var repository = FindRepositoryRoot();
        var workRoot = options.WorkDirectory ?? Path.Combine(Path.GetTempPath(), "CobaltumOrm.BuildBenchmarks");
        var runDirectory = Path.Combine(
            workRoot,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Environment.ProcessId);
        Directory.CreateDirectory(runDirectory);

        Console.WriteLine($"Repository: {repository}");
        Console.WriteLine($"Work directory: {runDirectory}");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"Logical processors: {Environment.ProcessorCount}");
        Console.WriteLine($"Runs per measurement: {options.Runs}");
        Console.WriteLine();

        var results = new List<BenchmarkResult>();
        var succeeded = false;
        try
        {
            await PrepareDependencies(repository);
            foreach (var profile in options.Profiles)
            {
                Console.WriteLine(
                    $"Generating {profile.Name}: {profile.NamedQueryCount} [Query], " +
                    $"{profile.QueryMethodCount} Query methods, {profile.MigrationStatementCount} migration statements");
                var profileDirectory = Path.Combine(runDirectory, profile.Name);
                WorkloadWriter.Write(repository, profileDirectory, profile);

                foreach (var variant in Variants)
                {
                    var project = Path.Combine(profileDirectory, variant.ProjectFile);
                    Console.WriteLine($"  Preparing {variant.Name} project");
                    await RunDotnet(
                        profileDirectory,
                        "build", project,
                        "-c", "Release",
                        "--nologo",
                        "-v:q");

                    foreach (var scenario in Scenarios)
                    {
                        Console.Write($"  Measuring {variant.Name}, {ScenarioName(scenario)}");
                        await PrepareScenario(profileDirectory, project, scenario, 0);
                        await TimedBuild(profileDirectory, project);

                        var samples = new double[options.Runs];
                        for (var run = 0; run < options.Runs; run++)
                        {
                            await PrepareScenario(profileDirectory, project, scenario, run + 1);
                            samples[run] = await TimedBuild(profileDirectory, project);
                            Console.Write('.');
                        }

                        var result = BenchmarkResult.Create(profile, variant, scenario, samples);
                        results.Add(result);
                        Console.WriteLine($" {result.MedianMilliseconds:N0} ms median");
                    }
                }
            }

            succeeded = true;
        }
        catch (BenchmarkFailureException exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(exception.Output);
            Console.Error.WriteLine($"Generated workload retained at {runDirectory}");
            return 1;
        }
        finally
        {
            if (succeeded && !options.KeepWorkDirectory)
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }

        Console.WriteLine();
        PrintResults(results);
        return 0;
    }

    private static async Task PrepareDependencies(string repository)
    {
        Console.WriteLine("Preparing repository dependencies (not measured)");
        await RunDotnet(
            repository,
            "build", Path.Combine(repository, "src", "CobaltumOrm.SourceGenerator", "CobaltumOrm.SourceGenerator.csproj"),
            "-c", "Release",
            "--nologo",
            "-v:q");
        await RunDotnet(
            repository,
            "build", Path.Combine(repository, "src", "CobaltumOrm.Migrations", "CobaltumOrm.Migrations.csproj"),
            "-c", "Release",
            "--nologo",
            "-v:q");
        Console.WriteLine();
    }

    private static async Task PrepareScenario(
        string workingDirectory,
        string project,
        BuildScenario scenario,
        int iteration)
    {
        switch (scenario)
        {
            case BuildScenario.Clean:
                await RunDotnet(
                    workingDirectory,
                    "clean", project,
                    "-c", "Release",
                    "-p:BuildProjectReferences=false",
                    "--nologo",
                    "-v:q");
                break;
            case BuildScenario.NoChange:
                break;
            case BuildScenario.OneFileChange:
                File.WriteAllText(
                    Path.Combine(workingDirectory, "Sources", "BuildMarker.cs"),
                    $"namespace BuildStress; internal static class BuildMarker {{ internal const int Version = {iteration}; }}\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static async Task<double> TimedBuild(string workingDirectory, string project)
    {
        var stopwatch = Stopwatch.StartNew();
        await RunDotnet(
            workingDirectory,
            "build", project,
            "-c", "Release",
            "--no-restore",
            "--no-dependencies",
            "--nologo",
            "-v:q");
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static async Task RunDotnet(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await standardOutput) + (await standardError);
        if (process.ExitCode != 0)
        {
            throw new BenchmarkFailureException(
                $"dotnet {string.Join(' ', arguments)} exited with code {process.ExitCode}.",
                output);
        }
    }

    private static void PrintResults(IReadOnlyList<BenchmarkResult> results)
    {
        Console.WriteLine("| Profile | Variant | Build | Median (ms) | Min (ms) | Max (ms) |");
        Console.WriteLine("| --- | --- | --- | ---: | ---: | ---: |");
        foreach (var result in results)
        {
            Console.WriteLine(
                $"| {result.Profile.Name} | {result.Variant.Name} | {ScenarioName(result.Scenario)} | " +
                $"{result.MedianMilliseconds:N0} | {result.MinimumMilliseconds:N0} | {result.MaximumMilliseconds:N0} |");
        }

        Console.WriteLine();
        Console.WriteLine("CobaltumORM overhead compared with the matching plain C# project:");
        Console.WriteLine("| Profile | Build | Added median (ms) | Ratio |");
        Console.WriteLine("| --- | --- | ---: | ---: |");
        foreach (var cobaltum in results.Where(result => result.Variant.Name == "cobaltum"))
        {
            var plain = results.Single(result =>
                result.Profile.Name == cobaltum.Profile.Name &&
                result.Variant.Name == "plain" &&
                result.Scenario == cobaltum.Scenario);
            Console.WriteLine(
                $"| {cobaltum.Profile.Name} | {ScenarioName(cobaltum.Scenario)} | " +
                $"{cobaltum.MedianMilliseconds - plain.MedianMilliseconds:N0} | " +
                $"{cobaltum.MedianMilliseconds / plain.MedianMilliseconds:N2}x |");
        }
    }

    private static string ScenarioName(BuildScenario scenario) => scenario switch
    {
        BuildScenario.Clean => "clean",
        BuildScenario.NoChange => "no-change",
        BuildScenario.OneFileChange => "one-file-change",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CobaltumOrm.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the CobaltumORM repository root.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -c Release -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --profile <small|medium|large|all>  Workload profile. May be repeated. Default: all");
        Console.WriteLine("  --runs <count>                      Recorded builds per measurement. Default: 3");
        Console.WriteLine("  --work-directory <path>             Parent directory for generated projects");
        Console.WriteLine("  --keep                               Keep generated projects after a successful run");
        Console.WriteLine("  --help                               Show help");
    }

    private sealed record WorkloadProfile(
        string Name,
        int NamedQueryCount,
        int QueryMethodCount,
        int TableCount,
        int AlterStatementsPerTable)
    {
        internal int MigrationStatementCount => TableCount * (1 + AlterStatementsPerTable);
    }

    private sealed record BuildVariant(string Name, string ProjectFile);

    private enum BuildScenario
    {
        Clean,
        NoChange,
        OneFileChange,
    }

    private sealed record BenchmarkResult(
        WorkloadProfile Profile,
        BuildVariant Variant,
        BuildScenario Scenario,
        double MedianMilliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds)
    {
        internal static BenchmarkResult Create(
            WorkloadProfile profile,
            BuildVariant variant,
            BuildScenario scenario,
            IReadOnlyCollection<double> samples)
        {
            var sorted = samples.Order().ToArray();
            var middle = sorted.Length / 2;
            var median = sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2
                : sorted[middle];
            return new BenchmarkResult(profile, variant, scenario, median, sorted[0], sorted[^1]);
        }
    }

    private sealed class BenchmarkOptions
    {
        private BenchmarkOptions()
        {
        }

        internal IReadOnlyList<WorkloadProfile> Profiles { get; private init; } = [];

        internal int Runs { get; private init; }

        internal string? WorkDirectory { get; private init; }

        internal bool KeepWorkDirectory { get; private init; }

        internal bool ShowHelp { get; private init; }

        internal static BenchmarkOptions Parse(string[] args, IReadOnlyList<WorkloadProfile> profiles)
        {
            var selected = new List<WorkloadProfile>();
            var runs = 3;
            string? workDirectory = null;
            var keep = false;
            var showHelp = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--profile":
                        {
                            var value = ReadValue(args, ref index, "--profile");
                            if (value == "all")
                            {
                                selected.Clear();
                                selected.AddRange(profiles);
                                break;
                            }

                            var profile = profiles.FirstOrDefault(candidate => candidate.Name == value)
                                ?? throw new ArgumentException($"Unknown profile '{value}'.");
                            if (!selected.Contains(profile))
                            {
                                selected.Add(profile);
                            }

                            break;
                        }
                    case "--runs":
                        {
                            var value = ReadValue(args, ref index, "--runs");
                            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out runs) || runs < 1)
                            {
                                throw new ArgumentException("--runs must be a positive integer.");
                            }

                            break;
                        }
                    case "--work-directory":
                        workDirectory = Path.GetFullPath(ReadValue(args, ref index, "--work-directory"));
                        break;
                    case "--keep":
                        keep = true;
                        break;
                    case "--help":
                    case "-h":
                        showHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[index]}'.");
                }
            }

            if (selected.Count == 0)
            {
                selected.AddRange(profiles);
            }

            return new BenchmarkOptions
            {
                Profiles = selected,
                Runs = runs,
                WorkDirectory = workDirectory,
                KeepWorkDirectory = keep,
                ShowHelp = showHelp,
            };
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            index++;
            if (index >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            return args[index];
        }
    }

    private static class WorkloadWriter
    {
        private const int QueriesPerClass = 25;
        private const int ClassesPerFile = 4;
        private const int MethodsPerFile = 100;

        internal static void Write(string repository, string directory, WorkloadProfile profile)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            var sourcesDirectory = Path.Combine(directory, "Sources");
            var migrationsDirectory = Path.Combine(directory, "Migrations");
            Directory.CreateDirectory(sourcesDirectory);
            Directory.CreateDirectory(migrationsDirectory);
            WriteProjects(repository, directory);
            WriteNamedQueries(sourcesDirectory, profile);
            WriteQueryMethods(sourcesDirectory, profile);
            WriteMigration(migrationsDirectory, profile);
            File.WriteAllText(
                Path.Combine(sourcesDirectory, "BuildMarker.cs"),
                "namespace BuildStress; internal static class BuildMarker { internal const int Version = -1; }\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void WriteProjects(string repository, string directory)
        {
            var plainDirectory = Path.Combine(directory, "Plain");
            var cobaltumDirectory = Path.Combine(directory, "Cobaltum");
            Directory.CreateDirectory(plainDirectory);
            Directory.CreateDirectory(cobaltumDirectory);
            var runtimeProject = Xml(Path.Combine(repository, "src", "CobaltumOrm", "CobaltumOrm.csproj"));
            var migrationsProject = Xml(Path.Combine(repository, "src", "CobaltumOrm.Migrations", "CobaltumOrm.Migrations.csproj"));
            var generatorProject = Xml(Path.Combine(repository, "src", "CobaltumOrm.SourceGenerator", "CobaltumOrm.SourceGenerator.csproj"));
            var generatorTargets = Xml(Path.Combine(
                repository,
                "src", "CobaltumOrm.SourceGenerator", "buildTransitive", "CobaltumOrm.SourceGenerator.targets"));
            var compilerAssembly = Xml(Path.Combine(
                repository,
                "src", "CobaltumOrm.Compiler", "bin", "Release", "netstandard2.0", "CobaltumOrm.Compiler.dll"));

            var commonStart = $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <CobaltumOrmGeneratedNamespace>BuildStress.Generated</CobaltumOrmGeneratedNamespace>
                    <CobaltumOrmDatabaseProvider>PostgreSql</CobaltumOrmDatabaseProvider>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="../Sources/**/*.cs" />
                    <AdditionalFiles Include="../Migrations/V*__*.sql" />
                    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
                    <CompilerVisibleProperty Include="CobaltumOrmDatabaseProvider" />
                    <ProjectReference Include="{{runtimeProject}}" />
                    <ProjectReference Include="{{migrationsProject}}" />
                  </ItemGroup>
                """;

            File.WriteAllText(
                Path.Combine(plainDirectory, "Plain.csproj"),
                commonStart + """
                  <PropertyGroup>
                    <AssemblyName>BuildStress.Plain</AssemblyName>
                  </PropertyGroup>
                </Project>
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.WriteAllText(
                Path.Combine(cobaltumDirectory, "Cobaltum.csproj"),
                commonStart + $$"""
                  <PropertyGroup>
                    <AssemblyName>BuildStress.Cobaltum</AssemblyName>
                    <CobaltumOrmCompilerTaskAssembly>{{compilerAssembly}}</CobaltumOrmCompilerTaskAssembly>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{generatorProject}}"
                                      OutputItemType="Analyzer"
                                      ReferenceOutputAssembly="false" />
                  </ItemGroup>
                  <Import Project="{{generatorTargets}}" />
                </Project>
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void WriteNamedQueries(string directory, WorkloadProfile profile)
        {
            var classCount = (profile.NamedQueryCount + QueriesPerClass - 1) / QueriesPerClass;
            var fileCount = (classCount + ClassesPerFile - 1) / ClassesPerFile;
            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                var builder = new StringBuilder();
                builder.AppendLine("using CobaltumOrm;");
                builder.AppendLine();
                builder.AppendLine("namespace BuildStress;");
                builder.AppendLine();
                var firstClass = fileIndex * ClassesPerFile;
                var lastClass = Math.Min(classCount, firstClass + ClassesPerFile);
                for (var classIndex = firstClass; classIndex < lastClass; classIndex++)
                {
                    var firstQuery = classIndex * QueriesPerClass;
                    var lastQuery = Math.Min(profile.NamedQueryCount, firstQuery + QueriesPerClass);
                    for (var queryIndex = firstQuery; queryIndex < lastQuery; queryIndex++)
                    {
                        var tableIndex = queryIndex % profile.TableCount;
                        builder.Append("[Query(\"Find")
                            .Append(queryIndex.ToString("D4", CultureInfo.InvariantCulture))
                            .Append("\", \"SELECT id, value FROM bench_table_")
                            .Append(tableIndex.ToString("D4", CultureInfo.InvariantCulture))
                            .AppendLine(" WHERE id = @id\")]");
                    }

                    builder.Append("public static partial class NamedQueries")
                        .Append(classIndex.ToString("D4", CultureInfo.InvariantCulture))
                        .AppendLine();
                    builder.AppendLine("{");
                    builder.AppendLine("}");
                    builder.AppendLine();
                }

                File.WriteAllText(
                    Path.Combine(directory, $"NamedQueries.{fileIndex:D4}.cs"),
                    builder.ToString(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        private static void WriteQueryMethods(string directory, WorkloadProfile profile)
        {
            var fileCount = (profile.QueryMethodCount + MethodsPerFile - 1) / MethodsPerFile;
            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                var builder = new StringBuilder();
                builder.AppendLine("using System.Data.Common;");
                builder.AppendLine("using CobaltumOrm;");
                builder.AppendLine();
                builder.AppendLine("namespace BuildStress;");
                builder.AppendLine();
                builder.Append("public static class QueryMethods")
                    .Append(fileIndex.ToString("D4", CultureInfo.InvariantCulture))
                    .AppendLine();
                builder.AppendLine("{");
                var firstMethod = fileIndex * MethodsPerFile;
                var lastMethod = Math.Min(profile.QueryMethodCount, firstMethod + MethodsPerFile);
                for (var methodIndex = firstMethod; methodIndex < lastMethod; methodIndex++)
                {
                    var tableIndex = methodIndex % profile.TableCount;
                    builder.Append("    public static object Read")
                        .Append(methodIndex.ToString("D4", CultureInfo.InvariantCulture))
                        .AppendLine("(DbConnection connection, int id) =>");
                    builder.Append("        connection.Query($\"SELECT id, value FROM bench_table_")
                        .Append(tableIndex.ToString("D4", CultureInfo.InvariantCulture))
                        .AppendLine(" WHERE id = {id}\").ReadAsync();");
                    builder.AppendLine();
                }

                builder.AppendLine("}");
                File.WriteAllText(
                    Path.Combine(directory, $"QueryMethods.{fileIndex:D4}.cs"),
                    builder.ToString(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        private static void WriteMigration(string directory, WorkloadProfile profile)
        {
            var builder = new StringBuilder();
            for (var tableIndex = 0; tableIndex < profile.TableCount; tableIndex++)
            {
                var table = tableIndex.ToString("D4", CultureInfo.InvariantCulture);
                builder.Append("CREATE TABLE bench_table_").Append(table).AppendLine(" (");
                builder.AppendLine("    id integer NOT NULL,");
                builder.AppendLine("    tenant_id integer NOT NULL,");
                builder.AppendLine("    value text NOT NULL,");
                builder.AppendLine("    status integer NOT NULL,");
                builder.AppendLine("    amount numeric(18, 2) NOT NULL,");
                builder.AppendLine("    created_at timestamp with time zone NOT NULL,");
                builder.AppendLine("    updated_at timestamp with time zone NULL,");
                builder.AppendLine("    PRIMARY KEY (id)");
                builder.AppendLine(");");
                for (var alterIndex = 0; alterIndex < profile.AlterStatementsPerTable; alterIndex++)
                {
                    builder.Append("ALTER TABLE bench_table_").Append(table)
                        .Append(" ADD COLUMN extra_").Append(alterIndex.ToString("D2", CultureInfo.InvariantCulture))
                        .AppendLine(" text NULL;");
                }

                builder.AppendLine();
            }

            File.WriteAllText(
                Path.Combine(directory, "V1__large_schema.sql"),
                builder.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string Xml(string value) => SecurityElement.Escape(value) ?? value;
    }

    private sealed class BenchmarkFailureException(string message, string output) : Exception(message)
    {
        internal string Output { get; } = output;
    }
}
