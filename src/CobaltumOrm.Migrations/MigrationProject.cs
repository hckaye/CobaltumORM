using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CobaltumOrm.Migrations;

/// <summary>
/// Defines the database used by the CobaltumORM command-line tool.
/// </summary>
public abstract class MigrationProject
{
    /// <summary>Creates the database connection used for status and migration commands.</summary>
    public abstract DbConnection CreateConnection(MigrationProjectContext context);

    /// <summary>Creates the database-specific migration adapter.</summary>
    public abstract IMigrationDatabaseAdapter CreateAdapter();

    /// <summary>Gets the migration history table settings.</summary>
    public virtual MigrationRunnerOptions RunnerOptions => new MigrationRunnerOptions();
}

/// <summary>Runs migration commands inside the target application's runtime.</summary>
public static class MigrationProjectHost
{
    /// <summary>Runs a command with a migration catalog.</summary>
    public static Task<int> RunAsync<TProject>(
        string[] args,
        IEnumerable<MigrationInfo> migrationCatalog,
        CancellationToken cancellationToken = default)
        where TProject : MigrationProject, new() =>
        RunAsync(
            new TProject(),
            migrationCatalog,
            args,
            Console.Out,
            Console.Error,
            cancellationToken);

    /// <summary>Runs a command with a migration catalog and explicit output streams.</summary>
    public static async Task<int> RunAsync(
        MigrationProject project,
        IEnumerable<MigrationInfo> migrationCatalog,
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(project, args, output, error);

        if (!TryParseCommand(args, out var hostCommand, out var parseError))
        {
            await error.WriteLineAsync(parseError).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var migrations = MigrationCatalogValidator.Validate(migrationCatalog);
            return await RunCommandAsync(project, migrations, hostCommand, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Migration command was canceled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Migration command failed: {InnermostMessage(exception)}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(
        MigrationProject project,
        IReadOnlyList<MigrationInfo> migrations,
        MigrationHostCommand hostCommand,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (hostCommand.Name == "list")
        {
            WriteAvailableMigrations(output, migrations);
            return 0;
        }

        var adapter = project.CreateAdapter()
            ?? throw new MigrationValidationException("The migration project returned a null database adapter.");
        if (hostCommand.Name == "schema")
        {
            var schema = new MigrationRunner(adapter).BuildFinalSchema(migrations, cancellationToken);
            var outputPath = Path.GetFullPath(hostCommand.OutputPath!);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using (var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                WriteSchema(writer, schema);
            }

            await output.WriteLineAsync($"Final schema was written to '{outputPath}'.").ConfigureAwait(false);
            return 0;
        }

        var options = project.RunnerOptions
            ?? throw new MigrationValidationException("The migration project returned null runner options.");
        using (var context = MigrationProjectConfiguration.Load(
                   project.GetType().Assembly,
                   hostCommand.EnvironmentName,
                   hostCommand.SettingsPath))
        {
            await output.WriteLineAsync($"Environment: {context.EnvironmentName}").ConfigureAwait(false);
            using (var connection = project.CreateConnection(context)
                ?? throw new MigrationValidationException("The migration project returned a null database connection."))
            {
                var runner = new MigrationRunner(adapter, options);
                switch (hostCommand.Name)
                {
                    case "status":
                        var statuses = await runner.GetStatusAsync(connection, migrations, cancellationToken)
                            .ConfigureAwait(false);
                        WriteStatuses(output, statuses);
                        break;

                    case "up":
                        if (hostCommand.DryRun)
                        {
                            var dryRun = await runner.DryRunUpAsync(connection, migrations, cancellationToken)
                                .ConfigureAwait(false);
                            WriteDryRun(output, dryRun, context.ContentRootPath);
                        }
                        else
                        {
                            await runner.MigrateUpAsync(connection, migrations, cancellationToken).ConfigureAwait(false);
                            await output.WriteLineAsync("Migrations are up to date.").ConfigureAwait(false);
                        }
                        break;

                    case "down":
                        if (hostCommand.DryRun)
                        {
                            var dryRun = await runner.DryRunDownAsync(
                                    connection,
                                    migrations,
                                    hostCommand.TargetVersion!.Value,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            WriteDryRun(output, dryRun, context.ContentRootPath);
                        }
                        else
                        {
                            await runner.MigrateDownAsync(
                                    connection,
                                    migrations,
                                    hostCommand.TargetVersion!.Value,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await output.WriteLineAsync(
                                    $"Database is at migration version {hostCommand.TargetVersion.Value}.")
                                .ConfigureAwait(false);
                        }
                        break;
                }
            }
        }

        return 0;
    }

    private static void ValidateArguments(
        MigrationProject project,
        string[] args,
        TextWriter output,
        TextWriter error)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (args is null) throw new ArgumentNullException(nameof(args));
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (error is null) throw new ArgumentNullException(nameof(error));
    }

    private static bool TryParseCommand(
        string[] args,
        out MigrationHostCommand command,
        out string error)
    {
        command = new MigrationHostCommand();
        error = string.Empty;

        if (args.Length == 0)
        {
            error = "A migration command is required. Expected list, status, schema, up, or down <target-version>.";
            return false;
        }

        command.Name = args[0].ToLowerInvariant();
        var positionals = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--environment" || args[index] == "--settings" || args[index] == "--output")
            {
                if (++index == args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    error = $"{args[index - 1]} requires a value.";
                    return false;
                }

                if (args[index - 1] == "--environment")
                {
                    command.EnvironmentName = args[index];
                }
                else if (args[index - 1] == "--settings")
                {
                    command.SettingsPath = args[index];
                }
                else
                {
                    command.OutputPath = args[index];
                }
            }
            else if (args[index] == "--dry-run")
            {
                command.DryRun = true;
            }
            else
            {
                positionals.Add(args[index]);
            }
        }

        switch (command.Name)
        {
            case "list":
            case "status":
            case "up":
                if (positionals.Count != 0)
                {
                    error = $"The {command.Name} command does not accept positional arguments.";
                    return false;
                }

                if (command.OutputPath is not null)
                {
                    error = "--output can only be used with the schema command.";
                    return false;
                }

                if (command.DryRun && command.Name != "up")
                {
                    error = "--dry-run can only be used with the up and down commands.";
                    return false;
                }

                return true;

            case "schema":
                if (positionals.Count != 0)
                {
                    error = "The schema command does not accept positional arguments.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(command.OutputPath))
                {
                    error = "The schema command requires --output <path>.";
                    return false;
                }

                if (command.DryRun || command.EnvironmentName is not null || command.SettingsPath is not null)
                {
                    error = "The schema command only accepts --output.";
                    return false;
                }

                return true;

            case "down":
                if (command.OutputPath is not null)
                {
                    error = "--output can only be used with the schema command.";
                    return false;
                }

                if (positionals.Count != 1 ||
                    !long.TryParse(positionals[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion) ||
                    parsedVersion < 0)
                {
                    error = "The down command requires one non-negative target version.";
                    return false;
                }

                command.TargetVersion = parsedVersion;
                return true;

            default:
                error = $"Unknown migration command '{args[0]}'. Expected list, status, schema, up, or down.";
                return false;
        }
    }

    private sealed class MigrationHostCommand
    {
        internal string Name { get; set; } = string.Empty;

        internal long? TargetVersion { get; set; }

        internal string? EnvironmentName { get; set; }

        internal string? SettingsPath { get; set; }

        internal string? OutputPath { get; set; }

        internal bool DryRun { get; set; }
    }

    private static void WriteAvailableMigrations(TextWriter output, IReadOnlyList<MigrationInfo> migrations)
    {
        if (migrations.Count == 0)
        {
            output.WriteLine("No migrations were found.");
            return;
        }

        foreach (var migration in migrations)
        {
            output.WriteLine(
                $"{migration.Version}\t{(migration.IsForwardOnly ? "forward-only" : "reversible")}\t{migration.Description}");
        }
    }

    private static void WriteStatuses(TextWriter output, IReadOnlyList<MigrationStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            output.WriteLine("No migrations were found.");
            return;
        }

        foreach (var status in statuses)
        {
            output.WriteLine(
                $"{(status.IsApplied ? "applied" : "pending")}\t{status.Migration.Version}\t{status.Migration.Description}");
        }
    }

    private static void WriteDryRun(TextWriter output, MigrationDryRun dryRun, string contentRootPath)
    {
        output.WriteLine("Dry run: no database changes were made.");
        output.WriteLine($"Current version: {dryRun.CurrentVersion}");
        output.WriteLine($"Final version: {dryRun.TargetVersion}");
        output.WriteLine();
        output.WriteLine("Migrations:");

        var sourceFiles = FindMigrationSourceFiles(contentRootPath);
        if (dryRun.Entries.Count == 0)
        {
            output.WriteLine("  No migrations would be applied or rolled back.");
        }

        foreach (var entry in dryRun.Entries)
        {
            var direction = entry.Direction == MigrationDryRunDirection.Up ? "up" : "down";
            output.WriteLine($"[{direction}] {entry.Migration.Version} {entry.Migration.Description}");
            if (sourceFiles.TryGetValue(entry.Migration.Version, out var sourceFile))
            {
                output.WriteLine($"File: {sourceFile}");
            }
            else
            {
                output.WriteLine($"Type: {entry.Migration.MigrationType.FullName}");
            }

            output.WriteLine("SQL:");
            if (entry.Commands.Count == 0)
            {
                output.WriteLine("  No migration SQL.");
            }

            foreach (var command in entry.Commands)
            {
                WriteIndented(output, command.CommandText, "  ");
            }

            output.WriteLine();
        }

        output.WriteLine("Final schema:");
        WriteSchema(output, dryRun.FinalSchema);
    }

    private static void WriteSchema(TextWriter output, MigrationSchema schema)
    {
        if (schema.Tables.Count == 0)
        {
            output.WriteLine("  No tables.");
            return;
        }

        foreach (var table in schema.Tables
                     .OrderBy(table => table.SchemaName ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(table => table.Name, StringComparer.Ordinal))
        {
            var tableName = string.IsNullOrEmpty(table.SchemaName)
                ? table.Name
                : table.SchemaName + "." + table.Name;
            output.WriteLine($"Table: {tableName}");
            foreach (var column in table.Columns)
            {
                var definition = "  " + column.Name + " " + column.SqlType +
                    (column.IsIdentity ? " IDENTITY" : string.Empty) +
                    (column.IsNullable ? " NULL" : " NOT NULL") +
                    (column.IsPrimaryKey ? " PRIMARY KEY" : string.Empty) +
                    (column.DefaultExpression is null ? string.Empty : " DEFAULT " + column.DefaultExpression);
                output.WriteLine(definition);
            }
        }
    }

    internal static IReadOnlyDictionary<long, string> FindMigrationSourceFiles(string contentRootPath)
    {
        var migrationsDirectory = Path.Combine(contentRootPath, "Migrations");
        var candidates = new Dictionary<long, List<string>>();
        if (!Directory.Exists(migrationsDirectory))
        {
            return new Dictionary<long, string>();
        }

        var csharpPattern = new Regex(
            @"\bMigration(?:Attribute)?\s*\(\s*([0-9]+)",
            RegexOptions.CultureInvariant);
        var sqlPattern = new Regex(
            @"^V([0-9]+)__.+\.sql$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        foreach (var file in Directory.EnumerateFiles(migrationsDirectory, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in csharpPattern.Matches(File.ReadAllText(file)))
                {
                    AddMigrationSource(candidates, match.Groups[1].Value, file, contentRootPath);
                }
            }
            else
            {
                var match = sqlPattern.Match(Path.GetFileName(file));
                if (match.Success)
                {
                    AddMigrationSource(candidates, match.Groups[1].Value, file, contentRootPath);
                }
            }
        }

        return candidates
            .Where(candidate => candidate.Value.Count == 1)
            .ToDictionary(candidate => candidate.Key, candidate => candidate.Value[0]);
    }

    private static void AddMigrationSource(
        IDictionary<long, List<string>> candidates,
        string versionText,
        string file,
        string contentRootPath)
    {
        if (!long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return;
        }

        if (!candidates.TryGetValue(version, out var paths))
        {
            paths = new List<string>();
            candidates.Add(version, paths);
        }

        var rootPath = Path.GetFullPath(contentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(file);
        var relativePath = fullPath.Length > rootPath.Length
            ? fullPath.Substring(rootPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(fullPath);
        if (!paths.Contains(relativePath, StringComparer.Ordinal))
        {
            paths.Add(relativePath);
        }
    }

    private static void WriteIndented(TextWriter output, string value, string indentation)
    {
        using (var reader = new StringReader(value))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                output.WriteLine(indentation + line);
            }
        }
    }

    private static string InnermostMessage(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception.Message;
    }
}
