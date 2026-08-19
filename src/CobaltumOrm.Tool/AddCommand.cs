using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CobaltumOrm.Tool;

internal sealed class AddCommand
{
    private readonly TextWriter _output;
    private readonly string _currentDirectory;

    public AddCommand(TextWriter output, string currentDirectory)
    {
        _output = output;
        _currentDirectory = currentDirectory;
    }

    public void Run(AddOptions options)
    {
        var projectPath = ResolveProjectPath(options.Project!, "Project");
        var migrationProjectPath = ResolveMigrationProjectPath(
            options.MigrationProject!,
            options.CreateMigrationProject);
        if (PathsEqual(projectPath, migrationProjectPath))
        {
            throw new ToolUsageException("The project and migration project must be different .csproj files.");
        }

        var project = ProjectFile.Load(projectPath);
        if (project.HasProperty("CobaltumOrmMigrationProject", "true"))
        {
            throw new ToolUsageException(
                $"Project '{projectPath}' is already a CobaltumORM migration project. Specify an application or query project.");
        }

        var targetPackages = LoadNearestCentralPackages(projectPath);
        var migration = ReadMigrationProject(migrationProjectPath, options.CreateMigrationProject, targetPackages);
        var provider = ResolveProvider(project, migration, options);
        var generatedNamespace = options.GeneratedNamespace ??
            DefaultGeneratedNamespace(project, projectPath, migration);
        ValidateNamespace(generatedNamespace, "generated namespace");
        var targetUsesCentralManagement = UsesCentralPackageManagement(project, targetPackages);
        var cobaltumVersion = ResolveCobaltumVersion(
            project,
            targetPackages,
            targetUsesCentralManagement,
            migration,
            PackageVersion());
        var driverPackages = ResolveDriverPackages(
            migration.GetDriverPackages(provider),
            targetPackages,
            targetUsesCentralManagement);
        var targetPackageRequirements = targetUsesCentralManagement
            ? CreatePackageRequirements(cobaltumVersion, driverPackages)
            : Array.Empty<PackageRequirement>();
        var migrationPackageRequirements = options.CreateMigrationProject
            ? CreateMigrationPackageRequirements(provider, cobaltumVersion, driverPackages)
            : Array.Empty<PackageRequirement>();

        var plan = project.PlanChanges(
            migrationProjectPath,
            provider,
            generatedNamespace,
            driverPackages,
            cobaltumVersion,
            targetUsesCentralManagement);
        var centralPlans = PlanCentralPackages(
            targetPackages,
            targetUsesCentralManagement,
            targetPackageRequirements,
            migration.Packages,
            options.CreateMigrationProject && migration.UsesCentralPackageManagement,
            migrationPackageRequirements);
        MigrationProjectPlan? migrationProjectPlan = null;

        if (options.CreateMigrationProject)
        {
            var packageConfiguration = new MigrationProjectPackageConfiguration(
                migration.UsesCentralPackageManagement,
                migrationPackageRequirements);
            migrationProjectPlan = new MigrationProjectInitializer(_output).Plan(
                migration.ProjectName!,
                migration.OutputDirectory!,
                options.Framework,
                provider,
                packageConfiguration);
        }

        foreach (var (centralFile, centralPlan) in centralPlans)
        {
            if (centralPlan.Changes.Count == 0)
            {
                continue;
            }

            centralFile.Write(centralPlan.Content!);
            _output.WriteLine($"Updated {centralFile.Path}");
            foreach (var change in centralPlan.Changes)
            {
                _output.WriteLine($"  {change}");
            }
        }

        if (plan.Changes.Count == 0 && centralPlans.All(item => item.Plan.Changes.Count == 0))
        {
            if (migrationProjectPlan is null)
            {
                _output.WriteLine($"No changes needed in {projectPath}");
                return;
            }
        }

        if (plan.Changes.Count != 0)
        {
            project.Write(plan.Content!);
            _output.WriteLine($"Updated {projectPath}");
            foreach (var change in plan.Changes)
            {
                _output.WriteLine($"  {change}");
            }
        }

        migrationProjectPlan?.Write(_output);
    }

    private static IReadOnlyList<(CentralPackageFile File, CentralPackageChangePlan Plan)> PlanCentralPackages(
        CentralPackageFile? targetPackages,
        bool targetUsesCentralManagement,
        IReadOnlyList<PackageRequirement> targetRequirements,
        CentralPackageFile? migrationPackages,
        bool migrationUsesCentralManagement,
        IReadOnlyList<PackageRequirement> migrationRequirements)
    {
        var plans = new List<(CentralPackageFile File, CentralPackageChangePlan Plan)>();
        if (targetUsesCentralManagement && migrationUsesCentralManagement &&
            PathsEqual(targetPackages!.Path, migrationPackages!.Path))
        {
            var requirements = targetRequirements
                .Concat(migrationRequirements)
                .GroupBy(requirement => requirement.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            plans.Add((targetPackages, targetPackages.PlanPackageVersions(requirements)));
            return plans;
        }

        if (targetUsesCentralManagement)
        {
            var centralFile = targetPackages
                ?? throw new ToolUsageException("Target central package management has no package file.");
            plans.Add((centralFile, centralFile.PlanPackageVersions(targetRequirements)));
        }

        if (migrationUsesCentralManagement)
        {
            var centralFile = migrationPackages
                ?? throw new ToolUsageException("Migration central package management has no package file.");
            plans.Add((centralFile, centralFile.PlanPackageVersions(migrationRequirements)));
        }

        return plans;
    }

    private string ResolveProjectPath(string value, string label)
    {
        var path = Path.GetFullPath(value, _currentDirectory);
        if (!File.Exists(path))
        {
            throw new ToolUsageException($"{label} path '{path}' does not exist.");
        }

        if (!string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException($"{label} path '{path}' is not a .csproj file.");
        }

        return path;
    }

    private string ResolveMigrationProjectPath(string value, bool creating)
    {
        var path = Path.GetFullPath(value, _currentDirectory);
        if (!string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException($"Migration project path '{path}' is not a .csproj file.");
        }

        if (!creating && !File.Exists(path))
        {
            throw new ToolUsageException(
                $"Migration project path '{path}' does not exist. Add --create-migration-project to create it.");
        }

        return path;
    }

    private static MigrationProjectInput ReadMigrationProject(
        string path,
        bool creating,
        CentralPackageFile? targetPackages)
    {
        if (!File.Exists(path))
        {
            if (!creating)
            {
                throw new ToolUsageException(
                    $"Migration project path '{path}' does not exist. Add --create-migration-project to create it.");
            }

            var projectName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(projectName))
            {
                throw new ToolUsageException($"Migration project path '{path}' has no project name.");
            }

            var migrationPackages = LoadNearestCentralPackages(path, targetPackages);
            return MigrationProjectInput.ForCreation(
                projectName,
                Path.GetDirectoryName(path)
                    ?? throw new ToolUsageException("The migration project path has no parent directory."),
                migrationPackages,
                migrationPackages?.ReadManagePackageVersionsCentrally() == true);
        }

        if (creating)
        {
            throw new ToolUsageException(
                $"Migration project '{path}' already exists. The add command never replaces existing files.");
        }

        var definition = MigrationScaffolder.ReadProject(path);
        var document = ProjectFile.Load(path);
        var packages = LoadNearestCentralPackages(path, targetPackages);
        var usesCentralManagement = UsesCentralPackageManagement(document, packages);
        var provider = ReadProvider(document);
        return MigrationProjectInput.ForExisting(
            definition.RootNamespace,
            document.ReadOptionalProperty("CobaltumOrmGeneratedNamespace")?.Trim(),
            provider,
            document,
            packages,
            usesCentralManagement);
    }

    private static string ResolveProvider(
        ProjectFile project,
        MigrationProjectInput migration,
        AddOptions options)
    {
        var projectProvider = project.ReadOptionalProvider();
        var requestedProvider = options.Provider;
        var migrationProvider = migration.Provider;
        var provider = requestedProvider ?? migrationProvider ?? projectProvider ?? MigrationProviders.Default;

        if (requestedProvider is not null && migrationProvider is not null &&
            !string.Equals(requestedProvider, migrationProvider, StringComparison.Ordinal))
        {
            throw new ToolUsageException(
                $"The requested provider '{requestedProvider}' conflicts with the migration project's provider '{migrationProvider}'.");
        }

        if (projectProvider is not null &&
            !string.Equals(projectProvider, provider, StringComparison.Ordinal))
        {
            throw new ToolUsageException(
                $"The target project's CobaltumOrmDatabaseProvider '{projectProvider}' conflicts with '{provider}'.");
        }

        return provider;
    }

    private static string? ReadProvider(ProjectFile project)
    {
        var value = project.ReadOptionalProperty("CobaltumOrmDatabaseProvider");
        return value is null ? null : MigrationProviders.Normalize(value);
    }

    // The application gets its own namespace, not the migration project's. Sharing the
    // migration project's namespace puts the application's generated Tables and row records
    // next to the migration project's generated types, which makes their names ambiguous
    // when the migration assembly is also referenced.
    private static string DefaultGeneratedNamespace(
        ProjectFile project,
        string projectPath,
        MigrationProjectInput migration)
    {
        var rootNamespace = project.ReadOptionalProperty("RootNamespace")?.Trim();
        if (string.IsNullOrWhiteSpace(rootNamespace))
        {
            rootNamespace = SanitizeRootNamespace(Path.GetFileNameWithoutExtension(projectPath));
        }

        var candidate = rootNamespace + ".Generated";
        var migrationGeneratedNamespace = migration.GeneratedNamespace ??
            migration.RootNamespace + ".Generated";
        if (string.Equals(candidate, migration.RootNamespace, StringComparison.Ordinal) ||
            string.Equals(candidate, migrationGeneratedNamespace, StringComparison.Ordinal))
        {
            throw new ToolUsageException(
                $"The default generated namespace '{candidate}' collides with the migration project. " +
                "Pass --generated-namespace <namespace> to choose a different namespace.");
        }

        return candidate;
    }

    private static string SanitizeRootNamespace(string projectName)
    {
        var builder = new StringBuilder(projectName.Length);
        foreach (var character in projectName)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' || character == '.'
                ? character
                : '_');
        }

        return builder.ToString();
    }

    internal static IReadOnlyList<PackageRequirement> ReadDriverPackages(
        ProjectFile project,
        string provider,
        CentralPackageFile? packages,
        bool usesCentralManagement)
    {
        return MigrationProviders.RuntimePackages(provider)
            .Select(package =>
            {
                var version = ReadEffectivePackageVersion(
                    project,
                    package.Id,
                    packages,
                    usesCentralManagement);
                return new PackageRequirement(package.Id, version ?? package.Version, version is not null);
            })
            .ToArray();
    }

    private static IReadOnlyList<PackageRequirement> ResolveDriverPackages(
        IReadOnlyList<PackageRequirement> migrationPackages,
        CentralPackageFile? targetPackages,
        bool targetUsesCentralManagement)
    {
        return migrationPackages
            .Select(requirement =>
            {
                var targetCentralVersion = targetUsesCentralManagement
                    ? targetPackages!.ReadPackageVersion(requirement.Id)
                    : null;
                if (targetCentralVersion is not null && requirement.IsExplicit && requirement.Version is not null &&
                    !string.Equals(
                        targetCentralVersion,
                        requirement.Version,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolUsageException(
                        $"The provider driver package '{requirement.Id}' has central version '{targetCentralVersion}', which conflicts with the migration project's version '{requirement.Version}'.");
                }

                return new PackageRequirement(
                    requirement.Id,
                    targetCentralVersion ?? requirement.Version,
                    requirement.IsExplicit || targetCentralVersion is not null);
            })
            .ToArray();
    }

    private static string? ReadEffectivePackageVersion(
        ProjectFile project,
        string packageId,
        CentralPackageFile? packages,
        bool usesCentralManagement)
    {
        var projectVersion = project.ReadPackageVersion(packageId);
        var centralVersion = usesCentralManagement
            ? packages!.ReadPackageVersion(packageId)
            : null;
        if (projectVersion is not null && centralVersion is not null &&
            !string.Equals(projectVersion, centralVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolUsageException(
                $"The migration project's PackageReference '{packageId}' conflicts with its central PackageVersion.");
        }

        return projectVersion ?? centralVersion;
    }

    private static CentralPackageFile? LoadNearestCentralPackages(
        string projectPath,
        CentralPackageFile? knownPackages = null)
    {
        var path = CentralPackageFile.FindNearest(projectPath);
        if (path is null)
        {
            return null;
        }

        if (knownPackages is not null && PathsEqual(path, knownPackages.Path))
        {
            return knownPackages;
        }

        return CentralPackageFile.Load(path);
    }

    private static bool UsesCentralPackageManagement(
        ProjectFile project,
        CentralPackageFile? packages)
    {
        var projectValue = project.ReadOptionalProperty("ManagePackageVersionsCentrally");
        if (projectValue is not null)
        {
            if (!bool.TryParse(projectValue, out var enabled))
            {
                throw new ToolUsageException(
                    $"Project '{project.FilePath}' has an invalid ManagePackageVersionsCentrally value '{projectValue}'.");
            }

            if (enabled && packages is null)
            {
                throw new ToolUsageException(
                    $"Project '{project.FilePath}' enables central package management but no Directory.Packages.props was found.");
            }

            return enabled;
        }

        if (packages is null)
        {
            return false;
        }

        return packages.ReadManagePackageVersionsCentrally();
    }

    private static string ResolveCobaltumVersion(
        ProjectFile project,
        CentralPackageFile? projectPackages,
        bool projectUsesCentralManagement,
        MigrationProjectInput migration,
        string fallback)
    {
        var versions = new List<string>();
        AddExistingCobaltumVersions(project, projectPackages, projectUsesCentralManagement, versions);
        if (migration.Project is not null)
        {
            AddExistingCobaltumVersions(
                migration.Project,
                migration.Packages,
                migration.UsesCentralPackageManagement,
                versions);
        }
        else if (migration.UsesCentralPackageManagement)
        {
            AddExistingCobaltumCentralVersions(migration.Packages!, versions);
        }

        var distinctVersions = versions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctVersions.Length > 1)
        {
            throw new ToolUsageException(
                $"Existing CobaltumORM package versions conflict: {string.Join(", ", distinctVersions)}.");
        }

        return distinctVersions.SingleOrDefault() ?? fallback;
    }

    private static void AddExistingCobaltumVersions(
        ProjectFile project,
        CentralPackageFile? packages,
        bool usesCentralManagement,
        ICollection<string> versions)
    {
        foreach (var packageId in CobaltumPackageIds)
        {
            foreach (var version in project.ReadPackageReferenceVersions(packageId))
            {
                if (version is not null)
                {
                    versions.Add(version);
                }
            }

            if (usesCentralManagement)
            {
                AddExistingCobaltumCentralVersion(packages!, packageId, versions);
            }
        }
    }

    private static void AddExistingCobaltumCentralVersions(
        CentralPackageFile packages,
        ICollection<string> versions)
    {
        foreach (var packageId in CobaltumPackageIds)
        {
            AddExistingCobaltumCentralVersion(packages, packageId, versions);
        }
    }

    private static void AddExistingCobaltumCentralVersion(
        CentralPackageFile packages,
        string packageId,
        ICollection<string> versions)
    {
        var centralVersion = packages.ReadPackageVersion(packageId);
        if (centralVersion is not null)
        {
            versions.Add(centralVersion);
        }
    }

    private static IReadOnlyList<PackageRequirement> CreatePackageRequirements(
        string cobaltumVersion,
        IReadOnlyList<PackageRequirement> driverPackages)
    {
        return new[]
        {
            new PackageRequirement("CobaltumOrm", cobaltumVersion),
            new PackageRequirement("CobaltumOrm.Migrations", cobaltumVersion),
            new PackageRequirement("CobaltumOrm.SourceGenerator", cobaltumVersion),
        }
        .Concat(driverPackages)
        .GroupBy(requirement => requirement.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();
    }

    private static IReadOnlyList<PackageRequirement> CreateMigrationPackageRequirements(
        string provider,
        string cobaltumVersion,
        IReadOnlyList<PackageRequirement> driverPackages)
    {
        return new[]
        {
            new PackageRequirement("CobaltumOrm", cobaltumVersion),
            new PackageRequirement("CobaltumOrm.Migrations", cobaltumVersion),
            new PackageRequirement("CobaltumOrm.SourceGenerator", cobaltumVersion),
            new PackageRequirement($"CobaltumOrm.Migrations.{provider}", cobaltumVersion),
        }
        .Concat(driverPackages)
        .GroupBy(requirement => requirement.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();
    }

    private static readonly string[] CobaltumPackageIds =
    {
        "CobaltumOrm",
        "CobaltumOrm.Migrations",
        "CobaltumOrm.SourceGenerator",
    };

    private static void ValidateNamespace(string value, string description)
    {
        if (!IsNamespace(value))
        {
            throw new ToolUsageException($"The {description} '{value}' is not a valid C# namespace.");
        }
    }

    private static bool IsNamespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var part in value.Trim().Split('.'))
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

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string PackageVersion()
    {
        var informationalVersion = typeof(AddCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "2.0.0";
        }

        return NormalizeInformationalVersion(informationalVersion);
    }

    internal static string NormalizeInformationalVersion(string informationalVersion)
    {
        var metadataStart = informationalVersion.IndexOf('+');
        return (metadataStart < 0 ? informationalVersion : informationalVersion[..metadataStart]).Trim();
    }
}

internal sealed class AddOptions
{
    public string? Project { get; set; }

    public string? MigrationProject { get; set; }

    public string? GeneratedNamespace { get; set; }

    public string? Provider { get; set; }

    public string? Framework { get; set; }

    public bool CreateMigrationProject { get; set; }

    public static AddOptions Parse(string[] args)
    {
        var options = new AddOptions();
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--project":
                case "-p":
                    options.Project = ReadValue(args, ref index, args[index]);
                    break;

                case "--migration-project":
                case "-m":
                    options.MigrationProject = ReadValue(args, ref index, args[index]);
                    break;

                case "--generated-namespace":
                    options.GeneratedNamespace = ReadValue(args, ref index, args[index]).Trim();
                    break;

                case "--provider":
                    options.Provider = MigrationProviders.Normalize(ReadValue(args, ref index, args[index]));
                    break;

                case "--framework":
                case "-f":
                    options.Framework = ReadValue(args, ref index, args[index]);
                    break;

                case "--create-migration-project":
                    options.CreateMigrationProject = true;
                    break;

                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ToolUsageException($"Unknown option '{args[index]}'.");
                    }

                    throw new ToolUsageException(
                        $"The add command does not accept positional arguments, but got '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Project))
        {
            throw new ToolUsageException("The add command requires --project <path>.");
        }

        if (string.IsNullOrWhiteSpace(options.MigrationProject))
        {
            throw new ToolUsageException("The add command requires --migration-project <path>.");
        }

        if (options.Framework is not null && !options.CreateMigrationProject)
        {
            throw new ToolUsageException(
                "--framework can only be used with --create-migration-project.");
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index == args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ToolUsageException($"{option} requires a value.");
        }

        return args[index];
    }
}

internal sealed class MigrationProjectInput
{
    private MigrationProjectInput(
        string rootNamespace,
        string? generatedNamespace,
        string? provider,
        ProjectFile? project,
        CentralPackageFile? packages,
        bool usesCentralPackageManagement,
        string? projectName,
        string? outputDirectory)
    {
        RootNamespace = rootNamespace;
        GeneratedNamespace = generatedNamespace;
        Provider = provider;
        Project = project;
        Packages = packages;
        UsesCentralPackageManagement = usesCentralPackageManagement;
        ProjectName = projectName;
        OutputDirectory = outputDirectory;
    }

    public string RootNamespace { get; }

    public string? GeneratedNamespace { get; }

    public string? Provider { get; }

    public ProjectFile? Project { get; }

    public CentralPackageFile? Packages { get; }

    public bool UsesCentralPackageManagement { get; }

    public string? ProjectName { get; }

    public string? OutputDirectory { get; }

    public bool IsCreation => ProjectName is not null;

    public IReadOnlyList<PackageRequirement> GetDriverPackages(string provider)
    {
        if (Project is null)
        {
            return MigrationProviders.RuntimePackages(provider)
                .Select(package =>
                {
                    var centralVersion = UsesCentralPackageManagement
                        ? Packages!.ReadPackageVersion(package.Id)
                        : null;
                    return new PackageRequirement(
                        package.Id,
                        centralVersion ?? package.Version,
                        centralVersion is not null);
                })
                .ToArray();
        }

        return AddCommand.ReadDriverPackages(
            Project,
            provider,
            Packages,
            UsesCentralPackageManagement);
    }

    public static MigrationProjectInput ForExisting(
        string rootNamespace,
        string? generatedNamespace,
        string? provider,
        ProjectFile project,
        CentralPackageFile? packages,
        bool usesCentralPackageManagement) =>
        new(rootNamespace, generatedNamespace, provider, project, packages, usesCentralPackageManagement, null, null);

    public static MigrationProjectInput ForCreation(
        string projectName,
        string outputDirectory,
        CentralPackageFile? packages,
        bool usesCentralPackageManagement)
    {
        ValidateProjectName(projectName);
        return new MigrationProjectInput(
            projectName,
            projectName + ".Generated",
            null,
            null,
            packages,
            usesCentralPackageManagement,
            projectName,
            outputDirectory);
    }

    private static void ValidateProjectName(string projectName)
    {
        if (projectName.Split('.').Any(segment => segment.Length == 0 ||
            (!char.IsLetter(segment[0]) && segment[0] != '_') ||
            segment.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
        {
            throw new ToolUsageException(
                "The migration project file name must be a dot-separated C# namespace, such as MyApp.Database.csproj.");
        }
    }
}

internal sealed record PackageRequirement(string Id, string? Version, bool IsExplicit = true);

internal sealed class ProjectFile
{
    private static readonly Regex ProjectClosingTag = new(
        @"</(?:[A-Za-z_][A-Za-z0-9_.-]*:)?Project\s*>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _path;
    private readonly string _content;
    private readonly Encoding _encoding;
    private readonly XDocument _document;

    private ProjectFile(string path, string content, Encoding encoding, XDocument document)
    {
        _path = path;
        _content = content;
        _encoding = encoding;
        _document = document;
    }

    public string FilePath => _path;

    public static ProjectFile Load(string path)
    {
        string content;
        Encoding encoding;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            content = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new ToolUsageException($"Could not read project '{path}': {exception.Message}");
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stringReader = new StringReader(content);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
            if (document.Root is null || document.Root.Name.LocalName != "Project")
            {
                throw new ToolUsageException($"Project '{path}' does not have a Project root element.");
            }

            return new ProjectFile(path, content, encoding, document);
        }
        catch (ToolUsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException || exception is InvalidOperationException)
        {
            throw new ToolUsageException($"Could not read project '{path}': {exception.Message}");
        }
    }

    public bool HasProperty(string name, string expectedValue)
    {
        return ReadProperties(name).Any(value =>
            string.Equals(value.Value.Trim(), expectedValue, StringComparison.OrdinalIgnoreCase));
    }

    public string? ReadOptionalProperty(string name)
    {
        var properties = ReadProperties(name).ToArray();
        if (properties.Length == 0)
        {
            return null;
        }

        var values = properties
            .Select(property => property.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length != 1)
        {
            throw new ToolUsageException(
                $"Project '{_path}' defines conflicting values for {name}.");
        }

        return values[0];
    }

    public string? ReadOptionalProvider()
    {
        var value = ReadOptionalProperty("CobaltumOrmDatabaseProvider");
        return value is null ? null : MigrationProviders.Normalize(value);
    }

    public string? ReadPackageVersion(string packageId)
    {
        var references = PackageReferences(packageId).ToArray();
        if (references.Length == 0)
        {
            return null;
        }

        var versions = references
            .Select(ReadVersion)
            .Where(version => version is not null)
            .Select(version => version!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (versions.Length > 1)
        {
            throw new ToolUsageException(
                $"Project '{_path}' defines conflicting versions for PackageReference '{packageId}'.");
        }

        return versions.SingleOrDefault();
    }

    public IReadOnlyList<string?> ReadPackageReferenceVersions(string packageId) =>
        PackageReferences(packageId).Select(ReadVersion).ToArray();

    public ProjectChangePlan PlanChanges(
        string migrationProjectPath,
        string provider,
        string generatedNamespace,
        IReadOnlyList<PackageRequirement> driverPackages,
        string packageVersion,
        bool usesCentralPackageManagement)
    {
        var changes = new List<string>();
        var missingProperties = new List<(string Name, string Value)>();
        var existingProvider = ReadOptionalProperty("CobaltumOrmDatabaseProvider");
        if (existingProvider is not null)
        {
            var normalized = MigrationProviders.Normalize(existingProvider);
            if (!string.Equals(normalized, provider, StringComparison.Ordinal))
            {
                throw new ToolUsageException(
                    $"The target project's CobaltumOrmDatabaseProvider '{existingProvider}' conflicts with '{provider}'.");
            }
        }
        else if (!string.Equals(provider, MigrationProviders.Default, StringComparison.Ordinal))
        {
            missingProperties.Add(("CobaltumOrmDatabaseProvider", provider));
            changes.Add($"added CobaltumOrmDatabaseProvider={provider}");
        }

        var existingNamespace = ReadOptionalProperty("CobaltumOrmGeneratedNamespace");
        if (existingNamespace is not null)
        {
            if (!string.Equals(existingNamespace, generatedNamespace, StringComparison.Ordinal))
            {
                throw new ToolUsageException(
                    $"The target project's CobaltumOrmGeneratedNamespace '{existingNamespace}' conflicts with '{generatedNamespace}'.");
            }
        }
        else
        {
            missingProperties.Add(("CobaltumOrmGeneratedNamespace", generatedNamespace));
            changes.Add($"added CobaltumOrmGeneratedNamespace={generatedNamespace}");
        }

        var packageRequirements = new List<PackageRequirement>
        {
            new("CobaltumOrm", packageVersion),
            new("CobaltumOrm.Migrations", packageVersion),
            new("CobaltumOrm.SourceGenerator", packageVersion),
        };
        packageRequirements.AddRange(driverPackages);
        foreach (var requirement in packageRequirements
            .GroupBy(requirement => requirement.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var references = PackageReferences(requirement.Id).ToArray();
            if (references.Length == 0)
            {
                var version = usesCentralPackageManagement || requirement.Version is null
                    ? string.Empty
                    : $" Version={requirement.Version}";
                var privateAssets = string.Equals(
                    requirement.Id,
                    "CobaltumOrm.SourceGenerator",
                    StringComparison.OrdinalIgnoreCase)
                    ? " PrivateAssets=all"
                    : string.Empty;
                changes.Add($"added PackageReference {requirement.Id}{version}{privateAssets}");
            }
            else
            {
                ValidatePackageReferences(requirement, references);
            }
        }

        var compilerVisibleProperties = new List<string>();
        var visibleNamespace = HasItem("CompilerVisibleProperty", "CobaltumOrmGeneratedNamespace");
        if (!visibleNamespace)
        {
            compilerVisibleProperties.Add("CobaltumOrmGeneratedNamespace");
            changes.Add("added CompilerVisibleProperty CobaltumOrmGeneratedNamespace");
        }

        if (!string.Equals(provider, MigrationProviders.Default, StringComparison.Ordinal) &&
            !HasItem("CompilerVisibleProperty", "CobaltumOrmDatabaseProvider"))
        {
            compilerVisibleProperties.Add("CobaltumOrmDatabaseProvider");
            changes.Add("added CompilerVisibleProperty CobaltumOrmDatabaseProvider");
        }

        var include = RelativeProjectPath(Path.GetDirectoryName(_path)!, migrationProjectPath);
        var migrationReferences = Items("CobaltumOrmMigrationProjectReference")
            .Select(item => item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        if (migrationReferences.Length == 0)
        {
            changes.Add($"added CobaltumOrmMigrationProjectReference {include}");
        }
        else if (migrationReferences.Any(reference => !PathsEqual(
            Path.GetFullPath(reference, Path.GetDirectoryName(_path)!),
            migrationProjectPath)))
        {
            throw new ToolUsageException(
                $"The target project already references a different CobaltumOrm migration project.");
        }

        var fragment = BuildFragment(
            missingProperties,
            packageRequirements
                .GroupBy(requirement => requirement.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(requirement => PackageReferences(requirement.Id).Any() == false)
                .ToArray(),
            compilerVisibleProperties,
            migrationReferences.Length == 0 ? include : null,
            usesCentralPackageManagement);
        var content = fragment.Length == 0 ? null : InsertFragment(fragment);
        return new ProjectChangePlan(content, changes);
    }

    public void Write(string content)
    {
        try
        {
            File.WriteAllText(_path, content, _encoding);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new ToolUsageException($"Could not update project '{_path}': {exception.Message}");
        }
    }

    private void ValidatePackageReferences(
        PackageRequirement requirement,
        IReadOnlyList<XElement> references)
    {
        var versions = references
            .Select(ReadVersion)
            .Where(version => version is not null)
            .Select(version => version!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (versions.Any(version => requirement.Version is not null &&
            !string.Equals(version, requirement.Version, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolUsageException(
                $"The existing PackageReference '{requirement.Id}' has a version that conflicts with '{requirement.Version}'.");
        }

        if (string.Equals(requirement.Id, "CobaltumOrm.SourceGenerator", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var reference in references)
            {
                var privateAssets = ReadMetadata(reference, "PrivateAssets");
                if (!string.Equals(privateAssets?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolUsageException(
                        "The existing CobaltumOrm.SourceGenerator PackageReference must set PrivateAssets to all.");
                }
            }
        }
    }

    private string BuildFragment(
        IReadOnlyList<(string Name, string Value)> properties,
        IReadOnlyList<PackageRequirement> packages,
        IReadOnlyList<string> compilerVisibleProperties,
        string? migrationProjectReference,
        bool usesCentralPackageManagement)
    {
        var groups = new List<string>();
        if (properties.Count != 0)
        {
            var lines = new List<string>();
            foreach (var property in properties)
            {
                lines.Add($"<{property.Name}>{XmlEscape(property.Value)}</{property.Name}>");
            }

            groups.Add(BuildGroup("PropertyGroup", lines));
        }

        var itemLines = new List<string>();
        foreach (var package in packages)
        {
            var version = usesCentralPackageManagement || package.Version is null
                ? string.Empty
                : $" Version=\"{XmlEscape(package.Version)}\"";
            var privateAssets = string.Equals(
                package.Id,
                "CobaltumOrm.SourceGenerator",
                StringComparison.OrdinalIgnoreCase)
                ? " PrivateAssets=\"all\""
                : string.Empty;
            itemLines.Add($"<PackageReference Include=\"{XmlEscape(package.Id)}\"{version}{privateAssets} />");
        }

        foreach (var property in compilerVisibleProperties)
        {
            itemLines.Add($"<CompilerVisibleProperty Include=\"{XmlEscape(property)}\" />");
        }

        if (migrationProjectReference is not null)
        {
            itemLines.Add(
                $"<CobaltumOrmMigrationProjectReference Include=\"{XmlEscape(migrationProjectReference)}\" />");
        }

        if (itemLines.Count != 0)
        {
            groups.Add(BuildGroup("ItemGroup", itemLines));
        }

        return string.Join("\n", groups);
    }

    private static string BuildGroup(string name, IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.Append('<').Append(name).Append(">\n");
        foreach (var line in lines)
        {
            builder.Append("  ").Append(line).Append('\n');
        }

        builder.Append("</").Append(name).Append('>');
        return builder.ToString();
    }

    private string InsertFragment(string fragment)
    {
        var match = ProjectClosingTag.Matches(_content).LastOrDefault();
        if (match is null)
        {
            throw new ToolUsageException($"Could not locate the Project closing element in '{_path}'.");
        }

        var newline = _content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lineStart = _content.LastIndexOf('\n', match.Index) + 1;
        var prefix = _content[lineStart..match.Index];
        var rootIndent = prefix.All(char.IsWhiteSpace) ? prefix : string.Empty;
        var childIndent = DetectChildIndent(match.Index, rootIndent);
        var adjustedFragment = fragment.Replace("\n", newline, StringComparison.Ordinal);

        var closingStartsOnNewLine = match.Index > 0 && _content[match.Index - 1] == '\n';
        if (prefix.Length == 0 && closingStartsOnNewLine)
        {
            var lineInsertion = IndentBlock(adjustedFragment, childIndent) + newline;
            return _content.Insert(match.Index, lineInsertion);
        }

        if (rootIndent.Length == 0 || prefix.Length == 0)
        {
            adjustedFragment = newline + IndentBlock(adjustedFragment, childIndent) + newline;
            return _content.Insert(match.Index, adjustedFragment);
        }

        var insertion = IndentBlock(adjustedFragment, childIndent) + newline;
        return _content.Insert(lineStart, insertion);
    }

    private string DetectChildIndent(int closingIndex, string rootIndent)
    {
        var rootOpeningStart = FindNextElementStart(0, closingIndex);
        var searchStart = rootOpeningStart < 0
            ? 0
            : _content.IndexOf('>', rootOpeningStart) + 1;
        while (searchStart >= 0 && searchStart < closingIndex)
        {
            var elementStart = FindNextElementStart(searchStart, closingIndex);
            if (elementStart < 0)
            {
                break;
            }

            var lineStart = _content.LastIndexOf('\n', elementStart) + 1;
            var indentation = _content[lineStart..elementStart];
            if (indentation.All(character => character is ' ' or '\t'))
            {
                return indentation + "  ";
            }

            searchStart = elementStart + 1;
        }

        return rootIndent + "  ";
    }

    private int FindNextElementStart(int start, int closingIndex)
    {
        var position = start;
        while (position >= 0 && position < closingIndex)
        {
            position = _content.IndexOf('<', position);
            if (position < 0 || position >= closingIndex)
            {
                return -1;
            }

            if (position + 1 < _content.Length &&
                _content[position + 1] is not '?' and not '!' and not '/')
            {
                return position;
            }

            position++;
        }

        return -1;
    }

    private static string IndentBlock(string value, string indent)
    {
        var lines = value.Split('\n');
        return string.Join('\n', lines.Select(line => line.Length == 0 ? line : indent + line));
    }

    private static string RelativeProjectPath(string projectDirectory, string migrationProjectPath) =>
        Path.GetRelativePath(projectDirectory, migrationProjectPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private IEnumerable<XElement> ReadProperties(string name) =>
        _document.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "PropertyGroup")
            .SelectMany(group => group.Elements())
            .Where(element => element.Name.LocalName == name);

    private IEnumerable<XElement> PackageReferences(string packageId) =>
        Items("PackageReference")
            .Where(item => string.Equals(
                item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value,
                packageId,
                StringComparison.OrdinalIgnoreCase));

    private IEnumerable<XElement> Items(string name) =>
        _document.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "ItemGroup")
            .SelectMany(group => group.Elements())
            .Where(element => element.Name.LocalName == name);

    private bool HasItem(string name, string include) =>
        Items(name).Any(item => string.Equals(
            item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value,
            include,
            StringComparison.OrdinalIgnoreCase));

    private static string? ReadVersion(XElement element) =>
        ReadMetadata(element, "Version") ?? ReadMetadata(element, "VersionOverride");

    private static string? ReadMetadata(XElement element, string name) =>
        element.Attribute(name)?.Value ??
        element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

    private static string XmlEscape(string value) =>
        new XElement("value", value).ToString(SaveOptions.DisableFormatting)[7..^8];
}

internal sealed class ProjectChangePlan
{
    public ProjectChangePlan(string? content, IReadOnlyList<string> changes)
    {
        Content = content;
        Changes = changes;
    }

    public string? Content { get; }

    public IReadOnlyList<string> Changes { get; }
}

internal sealed class CentralPackageChangePlan
{
    public CentralPackageChangePlan(string? content, IReadOnlyList<string> changes)
    {
        Content = content;
        Changes = changes;
    }

    public static CentralPackageChangePlan Empty { get; } =
        new(null, Array.Empty<string>());

    public string? Content { get; }

    public IReadOnlyList<string> Changes { get; }
}

internal sealed class CentralPackageFile
{
    private static readonly Regex ProjectClosingTag = new(
        @"</(?:[A-Za-z_][A-Za-z0-9_.-]*:)?Project\s*>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _content;
    private readonly Encoding _encoding;
    private readonly XDocument _document;

    private CentralPackageFile(
        string path,
        string content,
        Encoding encoding,
        XDocument document)
    {
        Path = path;
        _content = content;
        _encoding = encoding;
        _document = document;
    }

    public string Path { get; }

    public static string? FindNearest(string projectPath)
    {
        var directory = new DirectoryInfo(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath))!);
        while (true)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (directory.Parent is null)
            {
                return null;
            }

            directory = directory.Parent;
        }
    }

    public static CentralPackageFile Load(string path)
    {
        string content;
        Encoding encoding;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            content = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new ToolUsageException(
                $"Could not read central package file '{path}': {exception.Message}");
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stringReader = new StringReader(content);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
            if (document.Root is null || document.Root.Name.LocalName != "Project")
            {
                throw new ToolUsageException(
                    $"Central package file '{path}' does not have a Project root element.");
            }

            return new CentralPackageFile(path, content, encoding, document);
        }
        catch (ToolUsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException || exception is InvalidOperationException)
        {
            throw new ToolUsageException(
                $"Could not read central package file '{path}': {exception.Message}");
        }
    }

    public bool ReadManagePackageVersionsCentrally()
    {
        var value = ReadOptionalProperty("ManagePackageVersionsCentrally");
        if (value is null)
        {
            return false;
        }

        if (!bool.TryParse(value, out var enabled))
        {
            throw new ToolUsageException(
                $"Central package file '{Path}' has an invalid ManagePackageVersionsCentrally value '{value}'.");
        }

        return enabled;
    }

    public string? ReadPackageVersion(string packageId)
    {
        var packageVersions = PackageVersions(packageId).ToArray();
        if (packageVersions.Length == 0)
        {
            return null;
        }

        var versions = packageVersions
            .Select(ReadVersion)
            .ToArray();
        if (versions.Any(version => string.IsNullOrWhiteSpace(version)))
        {
            throw new ToolUsageException(
                $"Central package file '{Path}' has a PackageVersion '{packageId}' without a version.");
        }

        var distinctVersions = versions
            .Select(version => version!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctVersions.Length > 1)
        {
            throw new ToolUsageException(
                $"Central package file '{Path}' defines conflicting PackageVersion values for '{packageId}'.");
        }

        return distinctVersions[0];
    }

    public CentralPackageChangePlan PlanPackageVersions(
        IReadOnlyList<PackageRequirement> requirements)
    {
        var missing = new List<PackageRequirement>();
        var changes = new List<string>();
        foreach (var requirement in requirements
            .GroupBy(requirement => requirement.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var existingVersion = ReadPackageVersion(requirement.Id);
            if (existingVersion is null)
            {
                missing.Add(requirement);
                changes.Add(
                    $"added PackageVersion {requirement.Id}={requirement.Version}");
            }
            else if (requirement.Version is not null &&
                !string.Equals(existingVersion, requirement.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolUsageException(
                    $"The central PackageVersion '{requirement.Id}' has version '{existingVersion}', which conflicts with '{requirement.Version}'.");
            }
        }

        var fragment = BuildFragment(missing);
        var content = fragment.Length == 0 ? null : InsertFragment(fragment);
        return new CentralPackageChangePlan(content, changes);
    }

    public void Write(string content)
    {
        try
        {
            File.WriteAllText(Path, content, _encoding);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new ToolUsageException(
                $"Could not update central package file '{Path}': {exception.Message}");
        }
    }

    private string? ReadOptionalProperty(string name)
    {
        var values = _document.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "PropertyGroup")
            .SelectMany(group => group.Elements())
            .Where(element => element.Name.LocalName == name)
            .Select(element => element.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length > 1)
        {
            throw new ToolUsageException(
                $"Central package file '{Path}' defines conflicting values for {name}.");
        }

        return values.SingleOrDefault();
    }

    private IEnumerable<XElement> PackageVersions(string packageId) =>
        _document.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "ItemGroup")
            .SelectMany(group => group.Elements())
            .Where(element => element.Name.LocalName == "PackageVersion")
            .Where(element => string.Equals(
                element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
                packageId,
                StringComparison.OrdinalIgnoreCase));

    private static string? ReadVersion(XElement element) =>
        element.Attribute("Version")?.Value ??
        element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value;

    private static string BuildFragment(IReadOnlyList<PackageRequirement> packages)
    {
        if (packages.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("<ItemGroup>\n");
        foreach (var package in packages)
        {
            builder.Append("  <PackageVersion Include=\"")
                .Append(XmlEscape(package.Id))
                .Append("\" Version=\"")
                .Append(XmlEscape(package.Version!))
                .Append("\" />\n");
        }

        builder.Append("</ItemGroup>");
        return builder.ToString();
    }

    private string InsertFragment(string fragment)
    {
        var match = ProjectClosingTag.Matches(_content).LastOrDefault();
        if (match is null)
        {
            throw new ToolUsageException(
                $"Could not locate the Project closing element in '{Path}'.");
        }

        var newline = _content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lineStart = _content.LastIndexOf('\n', match.Index) + 1;
        var prefix = _content[lineStart..match.Index];
        var rootIndent = prefix.All(char.IsWhiteSpace) ? prefix : string.Empty;
        var childIndent = DetectChildIndent(match.Index, rootIndent);
        var adjustedFragment = fragment.Replace("\n", newline, StringComparison.Ordinal);

        var closingStartsOnNewLine = match.Index > 0 && _content[match.Index - 1] == '\n';
        if (prefix.Length == 0 && closingStartsOnNewLine)
        {
            var lineInsertion = IndentBlock(adjustedFragment, childIndent) + newline;
            return _content.Insert(match.Index, lineInsertion);
        }

        if (rootIndent.Length == 0 || prefix.Length == 0)
        {
            adjustedFragment = newline + IndentBlock(adjustedFragment, childIndent) + newline;
            return _content.Insert(match.Index, adjustedFragment);
        }

        var insertion = IndentBlock(adjustedFragment, childIndent) + newline;
        return _content.Insert(lineStart, insertion);
    }

    private string DetectChildIndent(int closingIndex, string rootIndent)
    {
        var rootOpeningStart = FindNextElementStart(0, closingIndex);
        var searchStart = rootOpeningStart < 0
            ? 0
            : _content.IndexOf('>', rootOpeningStart) + 1;
        while (searchStart >= 0 && searchStart < closingIndex)
        {
            var elementStart = FindNextElementStart(searchStart, closingIndex);
            if (elementStart < 0)
            {
                break;
            }

            var lineStart = _content.LastIndexOf('\n', elementStart) + 1;
            var indentation = _content[lineStart..elementStart];
            if (indentation.All(character => character is ' ' or '\t'))
            {
                return indentation + "  ";
            }

            searchStart = elementStart + 1;
        }

        return rootIndent + "  ";
    }

    private int FindNextElementStart(int start, int closingIndex)
    {
        var position = start;
        while (position >= 0 && position < closingIndex)
        {
            position = _content.IndexOf('<', position);
            if (position < 0 || position >= closingIndex)
            {
                return -1;
            }

            if (position + 1 < _content.Length &&
                _content[position + 1] is not '?' and not '!' and not '/')
            {
                return position;
            }

            position++;
        }

        return -1;
    }

    private static string IndentBlock(string value, string indent)
    {
        var lines = value.Split('\n');
        return string.Join('\n', lines.Select(line => line.Length == 0 ? line : indent + line));
    }

    private static string XmlEscape(string value) =>
        new XElement("value", value).ToString(SaveOptions.DisableFormatting)[7..^8];
}
