using System.Diagnostics;
using System.Globalization;

namespace CobaltumOrm.Tool;

internal sealed class ToolApplication
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly IProcessRunner _processRunner;
    private readonly IProjectEvaluator _projectEvaluator;
    private readonly string _currentDirectory;

    public ToolApplication(
        TextWriter output,
        TextWriter error,
        IProcessRunner processRunner,
        string? currentDirectory = null,
        IProjectEvaluator? projectEvaluator = null)
    {
        _output = output;
        _error = error;
        _processRunner = processRunner;
        _projectEvaluator = projectEvaluator ?? new MsBuildProjectEvaluator();
        _currentDirectory = Path.GetFullPath(currentDirectory ?? Directory.GetCurrentDirectory());
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                WriteHelp(_output);
                return 0;
            }

            if (string.Equals(args[0], "generate", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length > 1 && IsHelp(args[1]))
                {
                    WriteHelp(_output);
                    return 0;
                }

                return await new GenerateCommand(_output, _error, _projectEvaluator, _currentDirectory)
                    .RunAsync(GenerateOptions.Parse(args), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(args[0], "migrations", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(args[0], "migration", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolUsageException($"Unknown command '{args[0]}'.");
            }

            if (args.Length == 1 || IsHelp(args[1]))
            {
                WriteHelp(_output);
                return 0;
            }

            var options = Parse(args);
            if (options.Command == "init")
            {
                var outputDirectory = options.Output is null
                    ? Path.Combine(_currentDirectory, options.Positionals[0])
                    : Path.GetFullPath(options.Output, _currentDirectory);
                new MigrationProjectInitializer(_output).Create(
                    options.Positionals[0],
                    outputDirectory,
                    options.Framework,
                    options.Provider);
                return 0;
            }

            if (options.SettingsPath is not null)
            {
                options.SettingsPath = Path.GetFullPath(options.SettingsPath, _currentDirectory);
            }

            var projectPath = ResolveProject(options.Project);
            if (options.Command == "schema" || options.WriteSchema)
            {
                options.Output = options.Output is null
                    ? Path.Combine(Path.GetDirectoryName(projectPath)!, "schema.generated.json")
                    : Path.GetFullPath(options.Output, _currentDirectory);
            }

            if (options.Command == "add")
            {
                new MigrationScaffolder(_output).Add(projectPath, options.Positionals[0], options.Version);
                return 0;
            }

            MigrationScaffolder.ReadProject(projectPath);
            var startInfo = CreateStartInfo(projectPath, options);
            return await _processRunner.RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolUsageException exception)
        {
            await _error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            await _error.WriteLineAsync("Run 'cobaltum --help' for usage.").ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _error.WriteLineAsync("Command was canceled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await _error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static ToolOptions Parse(string[] args)
    {
        var command = args[1].ToLowerInvariant();
        if (command != "init" && command != "add" && command != "list" && command != "status" &&
            command != "up" && command != "down" && command != "schema")
        {
            throw new ToolUsageException($"Unknown migrations command '{args[1]}'.");
        }

        var options = new ToolOptions(command);
        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--project":
                case "-p":
                    options.Project = ReadOptionValue(args, ref index, args[index]);
                    break;

                case "--configuration":
                case "-c":
                    options.Configuration = ReadOptionValue(args, ref index, args[index]);
                    options.ConfigurationSpecified = true;
                    break;

                case "--framework":
                case "-f":
                    options.Framework = ReadOptionValue(args, ref index, args[index]);
                    break;

                case "--provider":
                    options.Provider = MigrationProviders.Normalize(
                        ReadOptionValue(args, ref index, args[index]));
                    options.ProviderSpecified = true;
                    break;

                case "--environment":
                case "-e":
                    options.EnvironmentName = ReadOptionValue(args, ref index, args[index]);
                    break;

                case "--output":
                case "-o":
                    options.Output = ReadOptionValue(args, ref index, args[index]);
                    break;

                case "--settings":
                    options.SettingsPath = ReadOptionValue(args, ref index, args[index]);
                    break;

                case "--version":
                    var value = ReadOptionValue(args, ref index, args[index]);
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
                    {
                        throw new ToolUsageException("--version requires a positive 64-bit integer.");
                    }

                    options.Version = version;
                    break;

                case "--no-build":
                    options.NoBuild = true;
                    break;

                case "--dry-run":
                    options.DryRun = true;
                    break;

                case "--write-schema":
                    options.WriteSchema = true;
                    break;

                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ToolUsageException($"Unknown option '{args[index]}'.");
                    }

                    options.Positionals.Add(args[index]);
                    break;
            }
        }

        Validate(options);
        return options;
    }

    private static void Validate(ToolOptions options)
    {
        if (options.Command == "init")
        {
            if (options.Positionals.Count != 1)
            {
                throw new ToolUsageException("The init command requires one project name.");
            }

            if (options.Project is not null || options.ConfigurationSpecified ||
                options.EnvironmentName is not null || options.SettingsPath is not null ||
                options.Version.HasValue || options.NoBuild || options.DryRun || options.WriteSchema)
            {
                throw new ToolUsageException(
                    "The init command only accepts --output, --framework, and --provider.");
            }

            return;
        }

        var expectedPositionals = options.Command == "add" || options.Command == "down" ? 1 : 0;
        if (options.Positionals.Count != expectedPositionals)
        {
            throw new ToolUsageException(options.Command switch
            {
                "add" => "The add command requires one migration name.",
                "down" => "The down command requires one target version.",
                _ => $"The {options.Command} command does not accept positional arguments.",
            });
        }

        if (options.ProviderSpecified)
        {
            throw new ToolUsageException("--provider can only be used with migrations init.");
        }

        if (options.Command == "down" &&
            (!long.TryParse(
                options.Positionals[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var target) || target < 0))
        {
            throw new ToolUsageException("The down target must be a non-negative 64-bit integer.");
        }

        if (options.Command != "add" && options.Version.HasValue)
        {
            throw new ToolUsageException("--version can only be used with migrations add.");
        }

        if (options.Output is not null && options.Command != "schema" &&
            !(options.Command == "up" && options.WriteSchema))
        {
            throw new ToolUsageException(
                "--output can only be used with migrations init, migrations schema, or migrations up --write-schema.");
        }

        if (options.WriteSchema && options.Command != "up")
        {
            throw new ToolUsageException("--write-schema can only be used with migrations up.");
        }

        if (options.Command == "add" && (options.NoBuild || options.Framework is not null))
        {
            throw new ToolUsageException("--no-build and --framework cannot be used with migrations add.");
        }

        if (options.DryRun && options.Command != "up" && options.Command != "down")
        {
            throw new ToolUsageException("--dry-run can only be used with migrations up and migrations down.");
        }

        if (options.Command == "add" &&
            (options.EnvironmentName is not null || options.SettingsPath is not null))
        {
            throw new ToolUsageException("--environment and --settings cannot be used with migrations add.");
        }

        if (options.Command == "schema" &&
            (options.EnvironmentName is not null || options.SettingsPath is not null))
        {
            throw new ToolUsageException("--environment and --settings cannot be used with migrations schema.");
        }

        if (string.IsNullOrWhiteSpace(options.Configuration))
        {
            throw new ToolUsageException("--configuration requires a non-empty value.");
        }
    }

    private static string ReadOptionValue(string[] args, ref int index, string option)
    {
        index++;
        if (index == args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ToolUsageException($"{option} requires a value.");
        }

        return args[index];
    }

    private string ResolveProject(string? project)
    {
        if (project is null)
        {
            var discovered = FindMigrationProjects(_currentDirectory, SearchOption.AllDirectories);
            return SelectSingleProject(discovered, _currentDirectory);
        }

        var candidate = Path.GetFullPath(project, _currentDirectory);
        if (File.Exists(candidate))
        {
            if (!string.Equals(Path.GetExtension(candidate), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolUsageException($"Project path '{candidate}' is not a .csproj file.");
            }

            return candidate;
        }

        if (!Directory.Exists(candidate))
        {
            throw new ToolUsageException($"Project path '{candidate}' does not exist.");
        }

        return SelectSingleProject(FindMigrationProjects(candidate, SearchOption.TopDirectoryOnly), candidate);
    }

    private static string[] FindMigrationProjects(string directory, SearchOption searchOption) =>
        Directory
            .EnumerateFiles(directory, "*.csproj", searchOption)
            .Where(path => !HasIgnoredDirectory(path, directory))
            .Where(MigrationScaffolder.IsMigrationProject)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool HasIgnoredDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part =>
                string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectSingleProject(string[] projects, string searchRoot)
    {
        if (projects.Length == 0)
        {
            throw new ToolUsageException(
                $"No CobaltumORM migration project was found under '{searchRoot}'.");
        }

        if (projects.Length > 1)
        {
            throw new ToolUsageException(
                $"More than one CobaltumORM migration project was found under '{searchRoot}'. Specify one with --project.");
        }

        return projects[0];
    }

    private static ProcessStartInfo CreateStartInfo(string projectPath, ToolOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(options.Configuration);
        startInfo.ArgumentList.Add("--no-launch-profile");
        if (options.NoBuild)
        {
            startInfo.ArgumentList.Add("--no-build");
        }

        if (options.Framework is not null)
        {
            startInfo.ArgumentList.Add("--framework");
            startInfo.ArgumentList.Add(options.Framework);
        }

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(options.Command);
        foreach (var positional in options.Positionals)
        {
            startInfo.ArgumentList.Add(positional);
        }

        if (options.Output is not null)
        {
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(options.Output);
        }

        if (options.EnvironmentName is not null)
        {
            startInfo.ArgumentList.Add("--environment");
            startInfo.ArgumentList.Add(options.EnvironmentName);
        }

        if (options.SettingsPath is not null)
        {
            startInfo.ArgumentList.Add("--settings");
            startInfo.ArgumentList.Add(options.SettingsPath);
        }

        if (options.DryRun)
        {
            startInfo.ArgumentList.Add("--dry-run");
        }

        if (options.WriteSchema)
        {
            startInfo.ArgumentList.Add("--write-schema");
        }

        return startInfo;
    }

    private static bool IsHelp(string value) =>
        value == "--help" || value == "-h" || string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("CobaltumORM tool");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  cobaltum generate [--project <path>] [--output-mode <mode>] [--output <dir>]");
        writer.WriteLine("  cobaltum migrations init <project-name> [--provider <name>] [--output <path>] [--framework <tfm>]");
        writer.WriteLine("  cobaltum migrations add <name> [--version <number>] [--project <path>]");
        writer.WriteLine("  cobaltum migrations list [--project <path>]");
        writer.WriteLine("  cobaltum migrations status [--project <path>]");
        writer.WriteLine("  cobaltum migrations schema [--output <path>] [--project <path>]");
        writer.WriteLine("  cobaltum migrations up [--dry-run] [--write-schema] [--output <path>] [--project <path>]");
        writer.WriteLine("  cobaltum migrations down <target-version> [--dry-run] [--project <path>]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -o, --output <path>         Output directory for init or JSON file for schema");
        writer.WriteLine("                             schema defaults to schema.generated.json in the migration project");
        writer.WriteLine("  -p, --project <path>        Migration .csproj file or its directory");
        writer.WriteLine("  -c, --configuration <name> Build configuration (default: Debug)");
        writer.WriteLine("  -f, --framework <tfm>      Target framework for init or dotnet run");
        writer.WriteLine("      --provider <name>     Database provider for init (default: PostgreSql)");
        writer.WriteLine("                            PostgreSql, MySql, Sqlite, SqlServer, or Oracle");
        writer.WriteLine("  -e, --environment <name>  Environment name (default: DOTNET_ENVIRONMENT or Production)");
        writer.WriteLine("      --settings <path>      JSON settings file used instead of appsettings files");
        writer.WriteLine("      --no-build             Do not build the migration project");
        writer.WriteLine("      --dry-run              Show migration files, SQL, and final schema without changes");
        writer.WriteLine("      --write-schema         Write final schema JSON after migrations up");
        writer.WriteLine("      --version <number>     Version for a new migration");
        writer.WriteLine();
        writer.WriteLine("Generate options:");
        writer.WriteLine("      --output-mode <mode>   intermediate (default), directory, or library");
        writer.WriteLine("                             intermediate writes under the project obj directory");
        writer.WriteLine("                             directory writes a durable directory you can check in");
        writer.WriteLine("                             library writes a directory that compiles as its own project");
        writer.WriteLine("  -o, --output <dir>         Output directory for directory and library modes");
        writer.WriteLine("      --library-project <path> Existing destination csproj; it is never modified");
        writer.WriteLine("      --library-name <name>  Name of the library project the tool writes");
        writer.WriteLine("      --generated-namespace <ns> Namespace for generated code");
        writer.WriteLine("      --provider <name>      Database provider when it is not set in the project");
        writer.WriteLine("      --no-restore           Do not restore before evaluating the project");
        writer.WriteLine("      --verbose              Print the MSBuild command and its output");
    }
}

internal sealed class ToolOptions
{
    public ToolOptions(string command)
    {
        Command = command;
    }

    public string Command { get; }

    public List<string> Positionals { get; } = new();

    public string? Project { get; set; }

    public string? Output { get; set; }

    public string Configuration { get; set; } = "Debug";

    public bool ConfigurationSpecified { get; set; }

    public string? Framework { get; set; }

    public string Provider { get; set; } = MigrationProviders.Default;

    public bool ProviderSpecified { get; set; }

    public string? EnvironmentName { get; set; }

    public string? SettingsPath { get; set; }

    public long? Version { get; set; }

    public bool NoBuild { get; set; }

    public bool DryRun { get; set; }

    public bool WriteSchema { get; set; }
}

internal sealed class ToolUsageException : Exception
{
    public ToolUsageException(string message)
        : base(message)
    {
    }
}
