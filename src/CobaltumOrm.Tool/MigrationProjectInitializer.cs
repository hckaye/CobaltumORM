using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CobaltumOrm.Tool;

internal sealed class MigrationProjectInitializer
{
    private const string SourceName = "CobaltumMigrations";
    private const string DefaultFramework = "net8.0";
    private const string TemplateUserSecretsId = "5b04a918-37d5-4fbf-b1d2-a58081ff96d8";

    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    private static readonly HashSet<string> SupportedFrameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "net8.0",
        "net9.0",
        "net10.0",
    };

    private static readonly TemplateFile[] TemplateFiles =
    {
        new("CobaltumOrm.Tool.Templates.CobaltumMigrations.csproj", ProjectFileName),
        new("CobaltumOrm.Tool.Templates.Program.cs", _ => "Program.cs"),
        new("CobaltumOrm.Tool.Templates.appsettings.json", _ => "appsettings.json"),
        new("CobaltumOrm.Tool.Templates.README.md", _ => "README.md"),
        new("CobaltumOrm.Tool.Templates.Migrations.README.md", _ => Path.Combine("Migrations", "README.md")),
    };

    private readonly TextWriter _output;

    public MigrationProjectInitializer(TextWriter output)
    {
        _output = output;
    }

    public void Create(
        string projectName,
        string outputDirectory,
        string? requestedFramework,
        string? requestedProvider = null)
    {
        var plan = Plan(projectName, outputDirectory, requestedFramework, requestedProvider);
        plan.Write(_output);
    }

    public MigrationProjectPlan Plan(
        string projectName,
        string outputDirectory,
        string? requestedFramework,
        string? requestedProvider,
        MigrationProjectPackageConfiguration? packageConfiguration = null)
    {
        ValidateProjectName(projectName);
        var provider = MigrationProviders.Normalize(requestedProvider);
        var framework = string.IsNullOrWhiteSpace(requestedFramework)
            ? DefaultFramework
            : requestedFramework.Trim().ToLowerInvariant();
        if (!SupportedFrameworks.Contains(framework))
        {
            throw new ToolUsageException(
                $"Unsupported target framework '{framework}'. Use net8.0, net9.0, or net10.0.");
        }

        EnsureOutputDirectoryIsAvailable(outputDirectory);

        var userSecretsId = Guid.NewGuid().ToString("D");
        var contents = TemplateFiles.ToDictionary(
            file => file,
            file => Transform(
                ReadResource(file.ResourceName),
                projectName,
                framework,
                provider,
                userSecretsId,
                file.ResourceName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? packageConfiguration
                    : null));

        return new MigrationProjectPlan(
            outputDirectory,
            Path.Combine(outputDirectory, ProjectFileName(projectName)),
            contents.Select(pair => new MigrationProjectFilePlan(
                Path.Combine(outputDirectory, pair.Key.OutputPath(projectName)),
                pair.Value))
            .ToArray());
    }

    private static void ValidateProjectName(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ToolUsageException("The init command requires a non-empty project name.");
        }

        var segments = projectName.Split('.');
        if (segments.Any(segment => !IsIdentifier(segment) || ReservedKeywords.Contains(segment)))
        {
            throw new ToolUsageException(
                "The project name must be a dot-separated C# namespace, such as MyApp.Database.");
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || (!char.IsLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsLetterOrDigit(value[index]) && value[index] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureOutputDirectoryIsAvailable(string outputDirectory)
    {
        if (File.Exists(outputDirectory))
        {
            throw new ToolUsageException($"Output path '{outputDirectory}' is an existing file.");
        }

        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new ToolUsageException(
                $"Output directory '{outputDirectory}' is not empty. Choose an empty or new directory.");
        }
    }

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(MigrationProjectInitializer).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration template '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string Transform(
        string content,
        string projectName,
        string framework,
        string provider,
        string userSecretsId,
        MigrationProjectPackageConfiguration? packageConfiguration)
    {
        var transformed = SelectProvider(content, provider)
            .Replace(SourceName, projectName, StringComparison.Ordinal)
            .Replace(DefaultFramework, framework, StringComparison.Ordinal)
            .Replace(TemplateUserSecretsId, userSecretsId, StringComparison.Ordinal);

        return packageConfiguration is null
            ? transformed
            : ConfigurePackageReferences(transformed, packageConfiguration);
    }

    private static string ConfigurePackageReferences(
        string content,
        MigrationProjectPackageConfiguration configuration)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new ToolUsageException($"Could not configure the migration project template: {exception.Message}");
        }

        var requirements = configuration.Packages
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        foreach (var reference in document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference"))
        {
            var packageId = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
            if (packageId is null || !requirements.TryGetValue(packageId, out var requirement))
            {
                throw new ToolUsageException(
                    $"The migration project template contains an unsupported PackageReference '{packageId ?? ""}'.");
            }

            RemoveMetadata(reference, "VersionOverride");
            var versionAttribute = reference.Attribute("Version");
            foreach (var child in reference.Elements()
                .Where(element => element.Name.LocalName == "Version")
                .ToArray())
            {
                child.Remove();
            }

            if (configuration.UseCentralPackageManagement)
            {
                versionAttribute?.Remove();
            }

            if (!configuration.UseCentralPackageManagement)
            {
                if (requirement.Version is null)
                {
                    throw new ToolUsageException(
                        $"No version was resolved for migration package '{requirement.Id}'.");
                }

                if (versionAttribute is null)
                {
                    reference.Add(new XAttribute("Version", requirement.Version));
                }
                else
                {
                    versionAttribute.Value = requirement.Version;
                }
            }
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void RemoveMetadata(XElement reference, string name)
    {
        reference.Attribute(name)?.Remove();
        foreach (var child in reference.Elements()
            .Where(element => element.Name.LocalName == name)
            .ToArray())
        {
            child.Remove();
        }
    }

    private static string SelectProvider(string content, string provider)
    {
        var lines = content.Split('\n');
        var output = new StringBuilder(content.Length);
        var frames = new Stack<ConditionalFrame>();
        var include = true;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var directive = ReadConditionalDirective(line);
            if (directive is not null)
            {
                switch (directive.Kind)
                {
                    case ConditionalKind.If:
                    {
                        var parentIncluded = include;
                        var matches = EvaluateProviderCondition(directive.Expression!, provider);
                        var currentIncluded = parentIncluded && matches;
                        frames.Push(new ConditionalFrame(parentIncluded, matches, currentIncluded));
                        include = currentIncluded;
                        break;
                    }

                    case ConditionalKind.ElseIf:
                    {
                        if (frames.Count == 0)
                        {
                            throw new InvalidOperationException("A template has an unmatched #elseif directive.");
                        }

                        var frame = frames.Peek();
                        var matches = EvaluateProviderCondition(directive.Expression!, provider);
                        var currentIncluded = frame.ParentIncluded && !frame.BranchMatched && matches;
                        frame.BranchMatched |= matches;
                        frame.CurrentIncluded = currentIncluded;
                        include = currentIncluded;
                        break;
                    }

                    case ConditionalKind.Else:
                    {
                        if (frames.Count == 0)
                        {
                            throw new InvalidOperationException("A template has an unmatched #else directive.");
                        }

                        var frame = frames.Peek();
                        frame.CurrentIncluded = frame.ParentIncluded && !frame.BranchMatched;
                        frame.BranchMatched = true;
                        include = frame.CurrentIncluded;
                        break;
                    }

                    case ConditionalKind.EndIf:
                    {
                        if (frames.Count == 0)
                        {
                            throw new InvalidOperationException("A template has an unmatched #endif directive.");
                        }

                        frames.Pop();
                        include = frames.Count == 0 || frames.Peek().CurrentIncluded;
                        break;
                    }
                }

                continue;
            }

            if (include)
            {
                output.Append(line);
                if (index < lines.Length - 1)
                {
                    output.Append('\n');
                }
            }
        }

        if (frames.Count != 0)
        {
            throw new InvalidOperationException("A template has an unterminated provider condition.");
        }

        return output.ToString();
    }

    private static ConditionalDirective? ReadConditionalDirective(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("<!--", StringComparison.Ordinal) &&
            text.EndsWith("-->", StringComparison.Ordinal))
        {
            text = text[4..^3].Trim();
        }

        if (!text.StartsWith('#'))
        {
            return null;
        }

        if (text.StartsWith("#if", StringComparison.Ordinal))
        {
            return new ConditionalDirective(
                ConditionalKind.If,
                text[3..].Trim());
        }

        if (text.StartsWith("#elseif", StringComparison.Ordinal))
        {
            return new ConditionalDirective(
                ConditionalKind.ElseIf,
                text[7..].Trim());
        }

        if (text.StartsWith("#else", StringComparison.Ordinal))
        {
            return new ConditionalDirective(ConditionalKind.Else, null);
        }

        if (text.StartsWith("#endif", StringComparison.Ordinal))
        {
            return new ConditionalDirective(ConditionalKind.EndIf, null);
        }

        return null;
    }

    private static bool EvaluateProviderCondition(string expression, string provider)
    {
        var condition = expression.Trim();
        if (condition.StartsWith('(') && condition.EndsWith(')'))
        {
            condition = condition[1..^1].Trim();
        }

        foreach (var name in MigrationProviders.Names)
        {
            if (string.Equals(condition, $"provider == \"{name}\"", StringComparison.Ordinal))
            {
                return string.Equals(provider, name, StringComparison.Ordinal);
            }
        }

        throw new InvalidOperationException($"Unsupported provider condition '{expression}'.");
    }

    private static string ProjectFileName(string projectName) => $"{projectName}.csproj";

    private enum ConditionalKind
    {
        If,
        ElseIf,
        Else,
        EndIf,
    }

    private sealed record ConditionalDirective(ConditionalKind Kind, string? Expression);

    private sealed class ConditionalFrame
    {
        public ConditionalFrame(bool parentIncluded, bool branchMatched, bool currentIncluded)
        {
            ParentIncluded = parentIncluded;
            BranchMatched = branchMatched;
            CurrentIncluded = currentIncluded;
        }

        public bool ParentIncluded { get; }

        public bool BranchMatched { get; set; }

        public bool CurrentIncluded { get; set; }
    }

    private sealed record TemplateFile(string ResourceName, Func<string, string> OutputPath);
}

internal sealed record MigrationProjectPackageConfiguration(
    bool UseCentralPackageManagement,
    IReadOnlyList<PackageRequirement> Packages);

internal sealed record MigrationProjectFilePlan(string Path, string Content);

internal sealed class MigrationProjectPlan
{
    public MigrationProjectPlan(
        string outputDirectory,
        string projectPath,
        IReadOnlyList<MigrationProjectFilePlan> files)
    {
        OutputDirectory = outputDirectory;
        ProjectPath = projectPath;
        Files = files;
    }

    public string OutputDirectory { get; }

    public string ProjectPath { get; }

    public IReadOnlyList<MigrationProjectFilePlan> Files { get; }

    public void Write(TextWriter output)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            foreach (var file in Files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                using var stream = new FileStream(file.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(file.Content);
            }
        }
        catch (IOException exception)
        {
            throw new ToolUsageException(
                $"Could not create migration project in '{OutputDirectory}': {exception.Message}");
        }

        output.WriteLine($"Created migration project {ProjectPath}");
    }
}
