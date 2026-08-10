using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CobaltumOrm.Tool;

internal sealed class MigrationScaffolder
{
    private static readonly Regex CSharpVersionPattern = new(
        @"\bMigration(?:Attribute)?\s*\(\s*([0-9]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex SqlVersionPattern = new(
        @"^V([0-9]+)__.+\.sql$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly TextWriter _output;

    public MigrationScaffolder(TextWriter output)
    {
        _output = output;
    }

    public void Add(string projectPath, string description, long? requestedVersion)
    {
        ValidateDescription(description);
        var project = ReadProject(projectPath);
        var existingVersions = FindVersions(project.Directory);
        var version = requestedVersion ?? NextVersion(existingVersions);

        if (version <= 0)
        {
            throw new ToolUsageException("Migration versions must be positive.");
        }

        if (existingVersions.Contains(version))
        {
            throw new ToolUsageException($"Migration version {version} already exists in the project.");
        }

        if (existingVersions.Count > 0 && version <= existingVersions.Max())
        {
            throw new ToolUsageException(
                $"Migration version {version} must be greater than the current latest version {existingVersions.Max()}.");
        }

        var className = MakeClassName(description);
        var migrationsDirectory = Path.Combine(project.Directory, "Migrations");
        Directory.CreateDirectory(migrationsDirectory);
        var filePath = Path.Combine(migrationsDirectory, $"{version}_{className}.cs");
        var source = WriteSource(project.RootNamespace, className, version, description);

        try
        {
            using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(source);
        }
        catch (IOException exception)
        {
            throw new ToolUsageException($"Could not create migration file '{filePath}': {exception.Message}");
        }

        _output.WriteLine($"Created {filePath}");
    }

    internal static MigrationProjectDefinition ReadProject(string projectPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(projectPath, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is IOException || exception is XmlException)
        {
            throw new ToolUsageException($"Could not read migration project '{projectPath}': {exception.Message}");
        }

        var rootNamespace = Property(document, "RootNamespace");
        if (string.IsNullOrWhiteSpace(rootNamespace))
        {
            throw new ToolUsageException(
                "The migration project must define a non-empty RootNamespace property.");
        }

        var outputType = Property(document, "OutputType");
        if (!string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException("The migration project must set OutputType to Exe.");
        }

        if (!string.Equals(
                Property(document, "CobaltumOrmMigrationProject"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException(
                "The migration project must set CobaltumOrmMigrationProject to true.");
        }

        return new MigrationProjectDefinition(
            Path.GetDirectoryName(projectPath)
                ?? throw new ToolUsageException("The migration project has no parent directory."),
            rootNamespace.Trim());
    }

    internal static bool IsMigrationProject(string projectPath)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(projectPath, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            return string.Equals(
                Property(document, "CobaltumOrmMigrationProject"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException || exception is XmlException)
        {
            return false;
        }
    }

    private static string? Property(XDocument document, string name) =>
        document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == name &&
                element.Parent?.Name.LocalName == "PropertyGroup" &&
                element.Parent.Attribute("Condition") is null &&
                element.Attribute("Condition") is null)
            ?.Value;

    private static HashSet<long> FindVersions(string projectDirectory)
    {
        var versions = new HashSet<long>();
        foreach (var file in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(projectDirectory, file);
            if (IsBuildOutput(relativePath))
            {
                continue;
            }

            if (string.Equals(Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                var source = File.ReadAllText(file);
                foreach (Match match in CSharpVersionPattern.Matches(source))
                {
                    AddVersion(versions, match.Groups[1].Value, file);
                }
            }
            else
            {
                var match = SqlVersionPattern.Match(Path.GetFileName(file));
                if (match.Success)
                {
                    AddVersion(versions, match.Groups[1].Value, file);
                }
            }
        }

        return versions;
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var firstSeparator = relativePath.IndexOfAny(new[]
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
        });
        var firstSegment = firstSeparator < 0 ? relativePath : relativePath.Substring(0, firstSeparator);
        return string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstSegment, ".git", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddVersion(HashSet<long> versions, string value, string file)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
        {
            throw new ToolUsageException($"File '{file}' contains an invalid migration version '{value}'.");
        }

        if (!versions.Add(version))
        {
            throw new ToolUsageException($"Migration version {version} is defined more than once in the project.");
        }
    }

    private static long NextVersion(HashSet<long> existingVersions)
    {
        var timestamp = long.Parse(
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);
        if (existingVersions.Count == 0)
        {
            return timestamp;
        }

        var latest = existingVersions.Max();
        if (latest == long.MaxValue)
        {
            throw new ToolUsageException("No migration version is available after Int64.MaxValue.");
        }

        return Math.Max(timestamp, latest + 1);
    }

    private static void ValidateDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ToolUsageException("A non-empty migration name is required.");
        }

        if (description.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
        {
            throw new ToolUsageException("A migration name cannot contain null characters or line breaks.");
        }
    }

    private static string MakeClassName(string description)
    {
        var builder = new StringBuilder();
        var capitalize = true;
        foreach (var character in description)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                capitalize = true;
                continue;
            }

            builder.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }

        if (builder.Length == 0)
        {
            builder.Append("Generated");
        }

        if (!char.IsLetter(builder[0]) && builder[0] != '_')
        {
            builder.Insert(0, '_');
        }

        if (!builder.ToString().EndsWith("Migration", StringComparison.Ordinal))
        {
            builder.Append("Migration");
        }

        return builder.ToString();
    }

    private static string WriteSource(
        string rootNamespace,
        string className,
        long version,
        string description) =>
        $$"""
        using CobaltumOrm.Migrations;

        namespace {{rootNamespace}}.Migrations;

        [Migration({{version.ToString(CultureInfo.InvariantCulture)}}, "{{EscapeString(description)}}")]
        public sealed class {{className}} : Migration
        {
            public override void Up()
            {
            }

            public override void Down()
            {
            }
        }
        """ + Environment.NewLine;

    private static string EscapeString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}

internal sealed class MigrationProjectDefinition
{
    public MigrationProjectDefinition(string directory, string rootNamespace)
    {
        Directory = directory;
        RootNamespace = rootNamespace;
    }

    public string Directory { get; }

    public string RootNamespace { get; }
}
