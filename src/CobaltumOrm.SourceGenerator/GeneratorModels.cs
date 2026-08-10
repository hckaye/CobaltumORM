using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace CobaltumOrm.SourceGenerator;

internal sealed class AdditionalSqlFile
{
    internal AdditionalSqlFile(string path, string text)
    {
        Path = path;
        Text = text;
    }

    internal string Path { get; }
    internal string Text { get; }
}

internal sealed class AdditionalCSharpFile
{
    internal AdditionalCSharpFile(string path, string text)
    {
        Path = path;
        Text = text;
    }

    internal string Path { get; }
    internal string Text { get; }
}

internal sealed class MigrationSource
{
    internal MigrationSource(
        long version,
        string description,
        Location location,
        IReadOnlyList<MigrationStep> steps,
        AdditionalSqlFile? flywayFile,
        INamedTypeSymbol? migrationType)
    {
        Version = version;
        Description = description;
        Location = location;
        Steps = steps;
        FlywayFile = flywayFile;
        MigrationType = migrationType;
    }

    internal long Version { get; }
    internal string Description { get; }
    internal Location Location { get; }
    internal IReadOnlyList<MigrationStep> Steps { get; }
    internal AdditionalSqlFile? FlywayFile { get; }
    internal INamedTypeSymbol? MigrationType { get; }
}

internal sealed class MigrationStep
{
    internal MigrationStep(string sql, Location location)
    {
        Sql = sql;
        Location = location;
    }

    internal string Sql { get; }
    internal Location Location { get; }
}

internal sealed class QuerySource
{
    internal QuerySource(INamedTypeSymbol owner, string name, string sql, Location location)
    {
        Owner = owner;
        Name = name;
        Sql = sql;
        Location = location;
    }

    internal INamedTypeSymbol Owner { get; }
    internal string Name { get; }
    internal string Sql { get; }
    internal Location Location { get; }
}

internal sealed class RawQuerySource
{
    internal RawQuerySource(string? sql, Location location)
    {
        Sql = sql;
        Location = location;
    }

    internal string? Sql { get; }
    internal Location Location { get; }
}

internal sealed class CompilationSchemaResult
{
    internal CompilationSchemaResult(
        CobaltumOrm.Analysis.DatabaseSchema schema,
        IReadOnlyList<MigrationSource> migrations,
        bool hasErrors)
    {
        Schema = schema;
        Migrations = migrations;
        HasErrors = hasErrors;
    }

    internal CobaltumOrm.Analysis.DatabaseSchema Schema { get; }
    internal IReadOnlyList<MigrationSource> Migrations { get; }
    internal bool HasErrors { get; }
}
