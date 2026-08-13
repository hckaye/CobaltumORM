namespace CobaltumOrm.Tool;

/// <summary>Where <c>cobaltum generate</c> writes its files.</summary>
internal enum GenerateOutputMode
{
    /// <summary>Under the project intermediate output directory, rewritten on every run.</summary>
    Intermediate,

    /// <summary>A durable directory that can be checked in.</summary>
    Directory,

    /// <summary>A directory that is compiled as its own C# library project.</summary>
    Library,
}

internal sealed class GenerateOptions
{
    public string? Project { get; set; }

    public string Configuration { get; set; } = "Debug";

    public string? Framework { get; set; }

    public string? Provider { get; set; }

    public string? GeneratedNamespace { get; set; }

    public GenerateOutputMode OutputMode { get; set; } = GenerateOutputMode.Intermediate;

    public string? Output { get; set; }

    public string? LibraryProject { get; set; }

    public string? LibraryName { get; set; }

    public bool NoRestore { get; set; }

    public bool Verbose { get; set; }

    public static GenerateOptions Parse(string[] args)
    {
        var options = new GenerateOptions();
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--project":
                case "-p":
                    options.Project = ReadValue(args, ref index);
                    break;

                case "--configuration":
                case "-c":
                    options.Configuration = ReadValue(args, ref index);
                    break;

                case "--framework":
                case "-f":
                    options.Framework = ReadValue(args, ref index);
                    break;

                case "--provider":
                    options.Provider = MigrationProviders.Normalize(ReadValue(args, ref index));
                    break;

                case "--generated-namespace":
                    options.GeneratedNamespace = ReadValue(args, ref index).Trim();
                    break;

                case "--output-mode":
                    options.OutputMode = ParseOutputMode(ReadValue(args, ref index));
                    break;

                case "--output":
                case "-o":
                    options.Output = ReadValue(args, ref index);
                    break;

                case "--library-project":
                    options.LibraryProject = ReadValue(args, ref index);
                    break;

                case "--library-name":
                    options.LibraryName = ReadValue(args, ref index);
                    break;

                case "--no-restore":
                    options.NoRestore = true;
                    break;

                case "--verbose":
                    options.Verbose = true;
                    break;

                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ToolUsageException($"Unknown option '{args[index]}'.");
                    }

                    throw new ToolUsageException(
                        $"The generate command does not accept positional arguments, but got '{args[index]}'.");
            }
        }

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Configuration))
        {
            throw new ToolUsageException("--configuration requires a non-empty value.");
        }

        if (GeneratedNamespace is not null && !IsValidNamespace(GeneratedNamespace))
        {
            throw new ToolUsageException(
                $"--generated-namespace '{GeneratedNamespace}' is not a valid C# namespace.");
        }

        if (OutputMode == GenerateOutputMode.Intermediate)
        {
            if (Output is not null)
            {
                throw new ToolUsageException(
                    "--output cannot be used with --output-mode intermediate; the project intermediate directory is used.");
            }
        }
        else if (LibraryProject is null && Output is null)
        {
            throw new ToolUsageException(
                $"--output is required with --output-mode {OutputMode.ToString().ToLowerInvariant()}.");
        }

        if (OutputMode != GenerateOutputMode.Library)
        {
            if (LibraryProject is not null)
            {
                throw new ToolUsageException("--library-project can only be used with --output-mode library.");
            }

            if (LibraryName is not null)
            {
                throw new ToolUsageException("--library-name can only be used with --output-mode library.");
            }
        }
        else if (LibraryProject is not null && LibraryName is not null)
        {
            throw new ToolUsageException(
                "--library-name writes a new library project, so it cannot be combined with --library-project.");
        }

        if (LibraryName is not null && !IsValidFileStem(LibraryName))
        {
            throw new ToolUsageException(
                $"--library-name '{LibraryName}' must be a file name without a directory or extension.");
        }
    }

    private static GenerateOutputMode ParseOutputMode(string value) => value.ToLowerInvariant() switch
    {
        "intermediate" => GenerateOutputMode.Intermediate,
        "directory" => GenerateOutputMode.Directory,
        "library" => GenerateOutputMode.Library,
        _ => throw new ToolUsageException(
            $"Unsupported output mode '{value}'. Supported modes: intermediate, directory, library."),
    };

    private static string ReadValue(string[] args, ref int index)
    {
        var option = args[index];
        index++;
        if (index == args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ToolUsageException($"{option} requires a value.");
        }

        return args[index];
    }

    private static bool IsValidNamespace(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0 || (!char.IsLetter(part[0]) && part[0] != '_'))
            {
                return false;
            }

            if (part.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidFileStem(string value) =>
        value.Length != 0 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains('/', StringComparison.Ordinal) &&
        !value.Contains('\\', StringComparison.Ordinal) &&
        !value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
}
