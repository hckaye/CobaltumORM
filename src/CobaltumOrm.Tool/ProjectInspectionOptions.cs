namespace CobaltumOrm.Tool;

internal enum ProjectInspectionFormat
{
    Text,
    Json,
}

/// <summary>Options shared by the inspect and doctor commands.</summary>
internal sealed class ProjectInspectionOptions : ProjectEvaluationOptions
{
    public string? Project { get; set; }

    public ProjectInspectionFormat Format { get; set; } = ProjectInspectionFormat.Text;

    public static ProjectInspectionOptions Parse(string[] args, string command)
    {
        var options = new ProjectInspectionOptions();
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

                case "--no-restore":
                    options.NoRestore = true;
                    break;

                case "--format":
                    options.Format = ParseFormat(ReadValue(args, ref index));
                    break;

                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ToolUsageException($"Unknown option '{args[index]}'.");
                    }

                    throw new ToolUsageException(
                        $"The {command} command does not accept positional arguments, but got '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Project))
        {
            throw new ToolUsageException($"The {command} command requires --project <path>.");
        }

        if (string.IsNullOrWhiteSpace(options.Configuration))
        {
            throw new ToolUsageException("--configuration requires a non-empty value.");
        }

        return options;
    }

    private static ProjectInspectionFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "text" => ProjectInspectionFormat.Text,
        "json" => ProjectInspectionFormat.Json,
        _ => throw new ToolUsageException(
            $"Unsupported format '{value}'. Supported formats: text, json."),
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
}
