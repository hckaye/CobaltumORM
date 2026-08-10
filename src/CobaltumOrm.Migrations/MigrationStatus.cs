namespace CobaltumOrm.Migrations;

/// <summary>Describes whether one discovered migration has been applied.</summary>
public sealed class MigrationStatus
{
    internal MigrationStatus(MigrationInfo migration, bool isApplied)
    {
        Migration = migration;
        IsApplied = isApplied;
    }

    /// <summary>Gets the discovered migration metadata.</summary>
    public MigrationInfo Migration { get; }

    /// <summary>Gets whether the migration is present in the database history.</summary>
    public bool IsApplied { get; }
}
