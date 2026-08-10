using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CobaltumOrm.Migrations;

/// <summary>Describes one migration in a generated or explicitly constructed catalog.</summary>
public sealed class MigrationInfo
{
    private readonly Func<Migration> _factory;

    private MigrationInfo(
        Type migrationType,
        long version,
        string description,
        bool isForwardOnly,
        Func<Migration> factory)
    {
        MigrationType = migrationType;
        Version = version;
        Description = description;
        IsForwardOnly = isForwardOnly;
        _factory = factory;
    }

    /// <summary>Creates migration metadata without runtime type discovery or reflective construction.</summary>
    public static MigrationInfo Create<TMigration>(long version, string description)
        where TMigration : Migration, new()
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A migration version must be positive.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A migration description is required.", nameof(description));
        }

        return new MigrationInfo(
            typeof(TMigration),
            version,
            description,
            typeof(ForwardOnlyMigration).IsAssignableFrom(typeof(TMigration)),
            static () => new TMigration());
    }

    /// <summary>Gets the concrete migration type.</summary>
    public Type MigrationType { get; }

    /// <summary>Gets the positive migration version.</summary>
    public long Version { get; }

    /// <summary>Gets the readable description stored in history.</summary>
    public string Description { get; }

    /// <summary>Gets whether this migration intentionally has no rollback operation.</summary>
    public bool IsForwardOnly { get; }

    internal Migration CreateMigration() => _factory();
}

internal static class MigrationCatalogValidator
{
    internal static IReadOnlyList<MigrationInfo> Validate(IEnumerable<MigrationInfo> migrationCatalog)
    {
        if (migrationCatalog is null)
        {
            throw new ArgumentNullException(nameof(migrationCatalog));
        }

        var migrations = new List<MigrationInfo>();
        foreach (var migration in migrationCatalog)
        {
            if (migration is null)
            {
                throw new ArgumentException(
                    "The migration catalog cannot contain null values.",
                    nameof(migrationCatalog));
            }

            migrations.Add(migration);
        }

        var ordered = migrations.OrderBy(migration => migration.Version).ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index - 1].Version == ordered[index].Version)
            {
                throw new MigrationValidationException(
                    $"Migration version {ordered[index].Version} is used by both " +
                    $"'{ordered[index - 1].MigrationType.FullName}' and '{ordered[index].MigrationType.FullName}'.");
            }
        }

        return new ReadOnlyCollection<MigrationInfo>(ordered);
    }
}
