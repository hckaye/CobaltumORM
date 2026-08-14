using System.Text;

namespace CobaltumOrm.Tool;

internal enum AssistantTarget
{
    Auto,
    Agents,
    Claude,
    Cursor,
    Copilot,
    All,
}

internal sealed class AssistantInitOptions
{
    public string? Project { get; private set; }

    public AssistantTarget Target { get; private set; } = AssistantTarget.Auto;

    public static AssistantInitOptions Parse(string[] args)
    {
        var options = new AssistantInitOptions();
        var projectSpecified = false;
        var targetSpecified = false;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--project":
                case "-p":
                    if (projectSpecified)
                    {
                        throw new ToolUsageException("--project can only be specified once.");
                    }

                    options.Project = ReadValue(args, ref index);
                    projectSpecified = true;
                    break;

                case "--target":
                    if (targetSpecified)
                    {
                        throw new ToolUsageException("--target can only be specified once.");
                    }

                    options.Target = ParseTarget(ReadValue(args, ref index));
                    targetSpecified = true;
                    break;

                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ToolUsageException($"Unknown option '{args[index]}'.");
                    }

                    throw new ToolUsageException(
                        $"The assistant init command does not accept positional arguments, but got '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Project))
        {
            throw new ToolUsageException("The assistant init command requires --project <path>.");
        }

        return options;
    }

    private static AssistantTarget ParseTarget(string value) => value.ToLowerInvariant() switch
    {
        "auto" => AssistantTarget.Auto,
        "agents" => AssistantTarget.Agents,
        "claude" => AssistantTarget.Claude,
        "cursor" => AssistantTarget.Cursor,
        "copilot" => AssistantTarget.Copilot,
        "all" => AssistantTarget.All,
        _ => throw new ToolUsageException(
            $"Unsupported assistant target '{value}'. Supported targets: auto, agents, claude, cursor, copilot, all."),
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

internal sealed class AssistantInitCommand
{
    private const string BeginMarker = "<!-- BEGIN COBALTUMORM ASSISTANT MANAGED BLOCK -->";
    private const string EndMarker = "<!-- END COBALTUMORM ASSISTANT MANAGED BLOCK -->";
    private const string CanonicalPath = ".cobaltum/assistant.md";

    private const string CanonicalInstructions = """
        # CobaltumORM coding instructions

        First, run `cobaltum inspect --project <path> --format json` for the project being changed.

        Prefer compile-time checked `Query`, `Query<T>`, and `[Query]`. Use `NoCheckQuery` only when SQL is genuinely dynamic or uses syntax that CobaltumORM does not support.

        Follow every diagnostic `helpUri` returned by `inspect`. Then run `cobaltum doctor --project <path> --format json` and `dotnet build <project>`.

        Do not invent EF Core or `DbContext` APIs, and do not assume unsupported CobaltumORM APIs. Do not access a database or run migrations unless the user requests it.

        References:

        - [Quick reference](https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/quick-reference.md)
        - [Task recipes](https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/recipes.md)
        - [Build diagnostics](https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md)
        - [llms.txt](https://github.com/hckaye/CobaltumORM/blob/main/llms.txt)
        """;

    private const string AdapterInstructions =
        "Read and obey `.cobaltum/assistant.md` before changing CobaltumORM code.";

    private const string CursorPreamble = """
        ---
        description: CobaltumORM instructions
        alwaysApply: true
        ---
        """;

    private static readonly ManagedTarget CanonicalTarget =
        new(CanonicalPath, ManagedTargetKind.Canonical);

    private static readonly ManagedTarget[] AdapterTargets =
    {
        new("AGENTS.md", ManagedTargetKind.SharedAdapter),
        new("CLAUDE.md", ManagedTargetKind.SharedAdapter),
        new(".cursor/rules/cobaltum.mdc", ManagedTargetKind.CursorAdapter),
        new(".github/copilot-instructions.md", ManagedTargetKind.SharedAdapter),
    };

    private readonly TextWriter _output;
    private readonly string _currentDirectory;

    public AssistantInitCommand(TextWriter output, string currentDirectory)
    {
        _output = output;
        _currentDirectory = currentDirectory;
    }

    public void Run(AssistantInitOptions options)
    {
        var projectPath = ProjectPathResolver.Resolve(options.Project!, _currentDirectory);
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new ToolUsageException($"Project path '{projectPath}' has no parent directory.");
        var targets = SelectTargets(options.Target, projectDirectory);
        var plans = targets
            .Select(target => Plan(target, projectDirectory))
            .ToArray();

        foreach (var plan in plans)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Path)!);
        }

        foreach (var plan in plans)
        {
            if (plan.Change == AssistantFileChange.Unchanged)
            {
                continue;
            }

            File.WriteAllText(plan.Path, plan.Content, plan.Encoding);
        }

        foreach (var plan in plans)
        {
            _output.WriteLine($"{plan.Change} {plan.DisplayPath}");
        }

        WriteNextCommands(projectPath);
    }

    private static IReadOnlyList<ManagedTarget> SelectTargets(AssistantTarget target, string projectDirectory)
    {
        var targets = new List<ManagedTarget> { CanonicalTarget };

        switch (target)
        {
            case AssistantTarget.Auto:
                var detected = AdapterTargets
                    .Where(adapter => PathExists(Path.Combine(projectDirectory, adapter.RelativePath)))
                    .ToArray();
                targets.AddRange(detected.Length == 0 ? new[] { AdapterTargets[0] } : detected);
                break;

            case AssistantTarget.Agents:
                targets.Add(AdapterTargets[0]);
                break;

            case AssistantTarget.Claude:
                targets.Add(AdapterTargets[1]);
                break;

            case AssistantTarget.Cursor:
                targets.Add(AdapterTargets[2]);
                break;

            case AssistantTarget.Copilot:
                targets.Add(AdapterTargets[3]);
                break;

            case AssistantTarget.All:
                targets.AddRange(AdapterTargets);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported assistant target.");
        }

        return targets;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static PlannedFile Plan(ManagedTarget target, string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, target.RelativePath);
        EnsureTargetParentCanBeCreated(path, target.RelativePath);
        if (Directory.Exists(path))
        {
            throw new ToolUsageException($"Assistant target '{target.RelativePath}' is a directory.");
        }

        if (!File.Exists(path))
        {
            var newline = "\n";
            return new PlannedFile(
                path,
                target.RelativePath,
                CreateNewContent(target, newline),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                AssistantFileChange.Created);
        }

        var existing = ReadTextFile(path, target.RelativePath);
        var newlineForFile = DetectNewline(existing.Content);
        var managedBlock = FindManagedBlock(existing.Content, target.RelativePath);
        if (managedBlock is null)
        {
            if (target.Kind != ManagedTargetKind.SharedAdapter)
            {
                throw new ToolUsageException(
                    $"Refusing to overwrite unrecognized dedicated assistant file '{target.RelativePath}'.");
            }

            var appended = AppendManagedBlock(existing.Content, CreateManagedBlock(AdapterInstructions, newlineForFile), newlineForFile);
            return CreateUpdatedPlan(path, target.RelativePath, appended, existing);
        }

        var replacement = CreateManagedBlock(InstructionsFor(target), newlineForFile);
        var updated = existing.Content[..managedBlock.Start] + replacement + existing.Content[managedBlock.End..];
        return CreateUpdatedPlan(path, target.RelativePath, updated, existing);
    }

    private static PlannedFile CreateUpdatedPlan(
        string path,
        string displayPath,
        string content,
        TextFileContent existing) =>
        new(
            path,
            displayPath,
            content,
            existing.Encoding,
            string.Equals(content, existing.Content, StringComparison.Ordinal)
                ? AssistantFileChange.Unchanged
                : AssistantFileChange.Updated);

    private static void EnsureTargetParentCanBeCreated(string path, string displayPath)
    {
        for (var directory = Path.GetDirectoryName(path);
             directory is not null && !Directory.Exists(directory);
             directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(directory))
            {
                throw new ToolUsageException(
                    $"Cannot create assistant target '{displayPath}' because '{directory}' is a file.");
            }
        }
    }

    private static TextFileContent ReadTextFile(string path, string displayPath)
    {
        var bytes = File.ReadAllBytes(path);
        if (StartsWith(bytes, 0xFF, 0xFE) || StartsWith(bytes, 0xFE, 0xFF) ||
            StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF) || StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00))
        {
            throw new ToolUsageException(
                $"Cannot safely update assistant target '{displayPath}' because it is not UTF-8 text.");
        }

        var hasBom = StartsWith(bytes, 0xEF, 0xBB, 0xBF);
        var offset = hasBom ? 3 : 0;
        string content;
        try
        {
            content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            throw new ToolUsageException(
                $"Cannot safely update assistant target '{displayPath}' because it is not UTF-8 text.");
        }

        if (content.IndexOf('\0') >= 0)
        {
            throw new ToolUsageException(
                $"Cannot safely update assistant target '{displayPath}' because it is not text.");
        }

        return new TextFileContent(content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom));
    }

    private static bool StartsWith(byte[] value, params byte[] prefix)
    {
        if (value.Length < prefix.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (value[index] != prefix[index])
            {
                return false;
            }
        }

        return true;
    }

    private static ManagedBlock? FindManagedBlock(string content, string displayPath)
    {
        var begins = FindMarkerOffsets(content, BeginMarker);
        var ends = FindMarkerOffsets(content, EndMarker);
        if (begins.Count == 0 && ends.Count == 0)
        {
            return null;
        }

        if (begins.Count != 1 || ends.Count != 1 ||
            !IsMarkerLine(content, begins[0], BeginMarker) ||
            !IsMarkerLine(content, ends[0], EndMarker) ||
            ends[0] < begins[0])
        {
            throw new ToolUsageException(
                $"Cannot safely update assistant target '{displayPath}' because its CobaltumORM-managed block is malformed.");
        }

        return new ManagedBlock(begins[0], ends[0] + EndMarker.Length);
    }

    private static List<int> FindMarkerOffsets(string content, string marker)
    {
        var offsets = new List<int>();
        var searchStart = 0;
        while (searchStart < content.Length)
        {
            var offset = content.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (offset < 0)
            {
                break;
            }

            offsets.Add(offset);
            searchStart = offset + marker.Length;
        }

        return offsets;
    }

    private static bool IsMarkerLine(string content, int offset, string marker)
    {
        var startsLine = offset == 0 || content[offset - 1] is '\r' or '\n';
        var after = offset + marker.Length;
        var endsLine = after == content.Length || content[after] is '\r' or '\n';
        return startsLine && endsLine;
    }

    private static string CreateNewContent(ManagedTarget target, string newline)
    {
        var managedBlock = CreateManagedBlock(InstructionsFor(target), newline);
        if (target.Kind == ManagedTargetKind.CursorAdapter)
        {
            return NormalizeLineEndings(CursorPreamble, newline).TrimEnd('\r', '\n') + newline + newline +
                managedBlock + newline;
        }

        return managedBlock + newline;
    }

    private static string InstructionsFor(ManagedTarget target) =>
        target.Kind == ManagedTargetKind.Canonical ? CanonicalInstructions : AdapterInstructions;

    private static string CreateManagedBlock(string instructions, string newline)
    {
        var normalized = NormalizeLineEndings(instructions, newline).TrimEnd('\r', '\n');
        return BeginMarker + newline + normalized + newline + EndMarker;
    }

    private static string AppendManagedBlock(string existing, string managedBlock, string newline)
    {
        if (existing.Length == 0)
        {
            return managedBlock + newline;
        }

        var separator = EndsWithNewline(existing) ? newline : newline + newline;
        return existing + separator + managedBlock + newline;
    }

    private static bool EndsWithNewline(string value) =>
        value.EndsWith('\r') || value.EndsWith('\n');

    private static string DetectNewline(string content)
    {
        var lineFeed = content.IndexOf('\n');
        if (lineFeed >= 0)
        {
            return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";
        }

        return content.Contains('\r') ? "\r" : "\n";
    }

    private static string NormalizeLineEndings(string value, string newline) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", newline, StringComparison.Ordinal);

    private void WriteNextCommands(string projectPath)
    {
        _output.WriteLine();
        _output.WriteLine("Next:");
        _output.WriteLine($"  cobaltum inspect --project {FormatCommandPath(projectPath)} --format json");
        _output.WriteLine($"  cobaltum doctor --project {FormatCommandPath(projectPath)} --format json");
        _output.WriteLine($"  dotnet build {FormatCommandPath(projectPath)}");
    }

    private static string FormatCommandPath(string path) =>
        path.Any(char.IsWhiteSpace) ? $"\"{path.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : path;

    private sealed record ManagedTarget(string RelativePath, ManagedTargetKind Kind);

    private sealed record TextFileContent(string Content, Encoding Encoding);

    private sealed record ManagedBlock(int Start, int End);

    private sealed record PlannedFile(
        string Path,
        string DisplayPath,
        string Content,
        Encoding Encoding,
        AssistantFileChange Change);

    private enum ManagedTargetKind
    {
        Canonical,
        SharedAdapter,
        CursorAdapter,
    }

    private enum AssistantFileChange
    {
        Created,
        Updated,
        Unchanged,
    }
}
