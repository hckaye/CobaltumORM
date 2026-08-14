using CobaltumOrm.Compiler;

namespace CobaltumOrm.Tool;

internal enum DoctorStatus
{
    Ok,
    Warning,
    Error,
}

internal sealed record DoctorCheck(
    string Id,
    DoctorStatus Status,
    string Message,
    string? NextAction,
    string? HelpUri);

internal sealed class ProjectDoctorReport
{
    public ProjectDoctorReport(
        ProjectAnalysis analysis,
        IReadOnlyList<DoctorCheck> checks,
        DoctorStatus status)
    {
        Analysis = analysis;
        Checks = checks;
        Status = status;
    }

    public ProjectAnalysis Analysis { get; }

    public IReadOnlyList<DoctorCheck> Checks { get; }

    public DoctorStatus Status { get; }
}

/// <summary>Builds stable, actionable checks from one evaluated project analysis.</summary>
internal static class ProjectDoctor
{
    private const string DiagnosticsHelpUri =
        "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/diagnostics.md#cob008";

    private const string SetupHelpUri =
        "https://github.com/hckaye/CobaltumORM/blob/main/docs/ai/quick-reference.md";

    public static ProjectDoctorReport Diagnose(ProjectAnalysis analysis)
    {
        var checks = new List<DoctorCheck>
        {
            TargetFrameworkCheck(analysis),
            WiringCheck(analysis),
            DatabaseProviderCheck(analysis),
            GeneratedNamespaceCheck(analysis),
            MigrationInputsCheck(analysis),
            GenerationDiagnosticsCheck(analysis),
        };
        var status = checks.Select(check => check.Status).Aggregate(DoctorStatus.Ok, Max);
        return new ProjectDoctorReport(analysis, checks, status);
    }

    private static DoctorCheck TargetFrameworkCheck(ProjectAnalysis analysis) =>
        new(
            "target-framework",
            DoctorStatus.Ok,
            $"Target framework '{analysis.Evaluation.TargetFramework}' was resolved.",
            null,
            null);

    private static DoctorCheck WiringCheck(ProjectAnalysis analysis)
    {
        var evaluation = analysis.Evaluation;
        var failures = new List<string>();
        var actions = new List<string>();
        if (!evaluation.References.Any(IsCobaltumOrmRuntimeReference))
        {
            failures.Add("the CobaltumORM runtime reference was not resolved");
            actions.Add("Add a CobaltumOrm package or project reference.");
        }

        if (evaluation.CobaltumOrmSourceGeneratorPaths.Count == 0)
        {
            failures.Add("the CobaltumORM source generator was not resolved");
            actions.Add("Add CobaltumOrm.SourceGenerator as an analyzer package or project reference.");
        }
        else if (evaluation.CobaltumOrmSourceGeneratorPaths.Any(path => !File.Exists(path)))
        {
            failures.Add("a resolved CobaltumORM source generator assembly is missing");
            actions.Add("Restore packages or rebuild the CobaltumOrm.SourceGenerator project.");
        }

        if (string.IsNullOrWhiteSpace(evaluation.CompilerTaskAssembly))
        {
            failures.Add("the CobaltumORM MSBuild targets were not evaluated");
            actions.Add("Import the CobaltumORM source-generator targets or reference its package.");
        }
        else if (!File.Exists(evaluation.CompilerTaskAssembly))
        {
            failures.Add("the configured CobaltumORM transform task assembly does not exist");
            actions.Add("Restore packages or rebuild the configured CobaltumOrm.Compiler task assembly.");
        }

        if (failures.Count != 0)
        {
            return new DoctorCheck(
                "cobaltumorm-wiring",
                DoctorStatus.Error,
                "CobaltumORM wiring is incomplete: " + Join(failures) + ".",
                Join(actions),
                SetupHelpUri);
        }

        if (!evaluation.CompileTimeQueriesEnabled)
        {
            return new DoctorCheck(
                "cobaltumorm-wiring",
                DoctorStatus.Warning,
                "CobaltumORM compile-time query generation is disabled for this target.",
                "Remove <CobaltumOrmCompileTimeQueries>false</CobaltumOrmCompileTimeQueries> to enable build-time generation.",
                SetupHelpUri);
        }

        return new DoctorCheck(
            "cobaltumorm-wiring",
            DoctorStatus.Ok,
            $"CobaltumORM runtime and source-generator wiring were resolved ({evaluation.CobaltumOrmSourceGeneratorPaths.Count} source generator assembly).",
            null,
            SetupHelpUri);
    }

    private static DoctorCheck DatabaseProviderCheck(ProjectAnalysis analysis)
    {
        var configured = analysis.Evaluation.DatabaseProvider.Trim();
        if (configured.Length == 0)
        {
            return new DoctorCheck(
                "database-provider",
                DoctorStatus.Warning,
                $"No explicit database provider was evaluated; CobaltumORM uses '{analysis.DatabaseProvider}'.",
                "Set <CobaltumOrmDatabaseProvider> to the database used by this project.",
                DiagnosticsHelpUri);
        }

        var provider = MigrationProviders.Names.FirstOrDefault(candidate =>
            string.Equals(candidate, configured, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return new DoctorCheck(
                "database-provider",
                DoctorStatus.Error,
                $"Database provider '{configured}' is not supported.",
                "Set CobaltumOrmDatabaseProvider to PostgreSql, MySql, Sqlite, SqlServer, or Oracle.",
                DiagnosticsHelpUri);
        }

        if (!analysis.Evaluation.CompilerVisibleProperties.Any(property =>
                string.Equals(property, "CobaltumOrmDatabaseProvider", StringComparison.Ordinal)))
        {
            return new DoctorCheck(
                "database-provider",
                DoctorStatus.Error,
                $"Database provider '{provider}' is not visible to the source generator.",
                "Add <CompilerVisibleProperty Include=\"CobaltumOrmDatabaseProvider\" /> to the project.",
                DiagnosticsHelpUri);
        }

        var migrationProviderPackages = analysis.Evaluation.CobaltumOrmPackageReferences
            .Where(reference => MigrationProviders.Names.Any(candidate =>
                string.Equals(reference.Id, MigrationProviderPackageId(candidate), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(reference => reference.Id, StringComparer.Ordinal)
            .ToArray();
        if (migrationProviderPackages.Length != 0 && !migrationProviderPackages.Any(reference =>
                string.Equals(reference.Id, MigrationProviderPackageId(provider), StringComparison.OrdinalIgnoreCase)))
        {
            return new DoctorCheck(
                "database-provider",
                DoctorStatus.Error,
                $"Database provider '{provider}' does not match the evaluated migration provider package(s): " +
                string.Join(", ", migrationProviderPackages.Select(reference => reference.Id)) + ".",
                $"Set CobaltumOrmDatabaseProvider to match the migration provider package, or reference {MigrationProviderPackageId(provider)}.",
                DiagnosticsHelpUri);
        }

        return new DoctorCheck(
            "database-provider",
            DoctorStatus.Ok,
            $"Database provider '{analysis.DatabaseProvider}' was evaluated.",
            null,
            DiagnosticsHelpUri);
    }

    private static DoctorCheck GeneratedNamespaceCheck(ProjectAnalysis analysis)
    {
        var evaluation = analysis.Evaluation;
        var configured = evaluation.GeneratedNamespace.Trim();
        if (configured.Length == 0)
        {
            return new DoctorCheck(
                "generated-namespace",
                DoctorStatus.Warning,
                $"No generated namespace was configured; CobaltumORM uses '{analysis.GeneratedNamespace}'.",
                "Set <CobaltumOrmGeneratedNamespace> when generated code must use a project-specific namespace.",
                DiagnosticsHelpUri);
        }

        if (!CSharpNameValidator.IsValidNamespace(configured))
        {
            return new DoctorCheck(
                "generated-namespace",
                DoctorStatus.Error,
                $"Generated namespace '{configured}' is not a valid C# namespace.",
                "Set CobaltumOrmGeneratedNamespace to dot-separated C# identifiers.",
                DiagnosticsHelpUri);
        }

        if (!evaluation.CompilerVisibleProperties.Any(property =>
                string.Equals(property, "CobaltumOrmGeneratedNamespace", StringComparison.Ordinal)))
        {
            return new DoctorCheck(
                "generated-namespace",
                DoctorStatus.Error,
                $"Generated namespace '{configured}' is not visible to the source generator.",
                "Add <CompilerVisibleProperty Include=\"CobaltumOrmGeneratedNamespace\" /> to the project.",
                DiagnosticsHelpUri);
        }

        return new DoctorCheck(
            "generated-namespace",
            DoctorStatus.Ok,
            $"Generated namespace '{analysis.GeneratedNamespace}' was evaluated.",
            null,
            DiagnosticsHelpUri);
    }

    private static DoctorCheck MigrationInputsCheck(ProjectAnalysis analysis)
    {
        var evaluation = analysis.Evaluation;
        var referenceCount = evaluation.MigrationProjectReferencePaths.Count;
        var externalInputCount = evaluation.MigrationInputPaths.Count;
        var localSqlInputCount = evaluation.AdditionalFiles.Count(IsFlywayMigrationFile);
        var inputCount = externalInputCount + localSqlInputCount;
        if (referenceCount == 0)
        {
            if (localSqlInputCount != 0)
            {
                return new DoctorCheck(
                    "migration-inputs",
                    DoctorStatus.Ok,
                    $"{localSqlInputCount} migration input(s) were evaluated from the target project.",
                    null,
                    SetupHelpUri);
            }

            return new DoctorCheck(
                "migration-inputs",
                DoctorStatus.Warning,
                "No CobaltumOrmMigrationProjectReference was evaluated.",
                "Configure CobaltumOrmMigrationProjectReference when migrations are stored in another project.",
                SetupHelpUri);
        }

        if (inputCount == 0)
        {
            return new DoctorCheck(
                "migration-inputs",
                DoctorStatus.Warning,
                $"{referenceCount} migration project reference was evaluated, but it reported no migration inputs.",
                "Add C# migrations or V*__*.sql files under the referenced project's Migrations directory.",
                SetupHelpUri);
        }

        return new DoctorCheck(
            "migration-inputs",
            DoctorStatus.Ok,
            $"{referenceCount} migration project reference and {inputCount} migration input(s) were evaluated.",
            null,
            SetupHelpUri);
    }

    private static DoctorCheck GenerationDiagnosticsCheck(ProjectAnalysis analysis)
    {
        var diagnostics = analysis.Generation.Diagnostics;
        var errors = diagnostics.Where(diagnostic => diagnostic.IsError).ToArray();
        if (errors.Length != 0)
        {
            return new DoctorCheck(
                "generation-diagnostics",
                DoctorStatus.Error,
                $"Generation reported {errors.Length} error diagnostic(s): {string.Join(", ", errors.Select(diagnostic => diagnostic.Code).Distinct(StringComparer.Ordinal))}.",
                "Fix the reported generation diagnostics and run cobaltum doctor again.",
                errors.Select(diagnostic => diagnostic.HelpUri).FirstOrDefault(uri => uri is not null));
        }

        if (diagnostics.Count != 0)
        {
            return new DoctorCheck(
                "generation-diagnostics",
                DoctorStatus.Warning,
                $"Generation reported {diagnostics.Count} warning diagnostic(s).",
                "Review the reported generation diagnostics.",
                diagnostics.Select(diagnostic => diagnostic.HelpUri).FirstOrDefault(uri => uri is not null));
        }

        return new DoctorCheck(
            "generation-diagnostics",
            DoctorStatus.Ok,
            "Generation completed without diagnostics.",
            null,
            null);
    }

    private static bool IsCobaltumOrmRuntimeReference(string path) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(path),
            "CobaltumOrm",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsFlywayMigrationFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("V", StringComparison.OrdinalIgnoreCase) &&
            fileName.IndexOf("__", StringComparison.Ordinal) > 1 &&
            string.Equals(Path.GetExtension(fileName), ".sql", StringComparison.OrdinalIgnoreCase);
    }

    private static string MigrationProviderPackageId(string provider) =>
        "CobaltumOrm.Migrations." + provider;

    private static DoctorStatus Max(DoctorStatus left, DoctorStatus right) =>
        left >= right ? left : right;

    private static string Join(IEnumerable<string> values) => string.Join(" ", values);
}
