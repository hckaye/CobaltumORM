using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CobaltumOrm.Migrations;

/// <summary>Identifies whether a dry-run entry applies or rolls back a migration.</summary>
public enum MigrationDryRunDirection
{
    /// <summary>The migration would be applied.</summary>
    Up,

    /// <summary>The migration would be rolled back.</summary>
    Down,
}

/// <summary>Describes one migration and the SQL that a dry run would execute.</summary>
public sealed class MigrationDryRunEntry
{
    private readonly ReadOnlyCollection<MigrationCommand> _commands;

    internal MigrationDryRunEntry(
        MigrationInfo migration,
        MigrationDryRunDirection direction,
        IEnumerable<MigrationCommand> commands)
    {
        Migration = migration ?? throw new ArgumentNullException(nameof(migration));
        Direction = direction;
        _commands = Array.AsReadOnly(commands.ToArray());
    }

    /// <summary>Gets the migration metadata.</summary>
    public MigrationInfo Migration { get; }

    /// <summary>Gets whether the migration would be applied or rolled back.</summary>
    public MigrationDryRunDirection Direction { get; }

    /// <summary>Gets the provider-specific SQL commands in execution order.</summary>
    public IReadOnlyList<MigrationCommand> Commands => _commands;
}

/// <summary>Describes the SQL and final schema produced by a migration dry run.</summary>
public sealed class MigrationDryRun
{
    private readonly ReadOnlyCollection<MigrationDryRunEntry> _entries;

    internal MigrationDryRun(
        long currentVersion,
        long targetVersion,
        IEnumerable<MigrationDryRunEntry> entries,
        MigrationSchema finalSchema)
    {
        CurrentVersion = currentVersion;
        TargetVersion = targetVersion;
        _entries = Array.AsReadOnly(entries.ToArray());
        FinalSchema = finalSchema ?? throw new ArgumentNullException(nameof(finalSchema));
    }

    /// <summary>Gets the latest applied version before the dry run, or zero for an empty history.</summary>
    public long CurrentVersion { get; }

    /// <summary>Gets the version boundary after the planned changes.</summary>
    public long TargetVersion { get; }

    /// <summary>Gets migrations that would change, in execution order.</summary>
    public IReadOnlyList<MigrationDryRunEntry> Entries => _entries;

    /// <summary>Gets the schema expected after the planned changes.</summary>
    public MigrationSchema FinalSchema { get; }
}

/// <summary>Describes a database schema reconstructed from migration commands.</summary>
public sealed class MigrationSchema
{
    private readonly ReadOnlyCollection<MigrationSchemaTable> _tables;

    /// <summary>Initializes a schema from its tables.</summary>
    public MigrationSchema(IEnumerable<MigrationSchemaTable> tables)
    {
        if (tables is null)
        {
            throw new ArgumentNullException(nameof(tables));
        }

        _tables = Array.AsReadOnly(tables.ToArray());
    }

    /// <summary>Gets tables in the schema.</summary>
    public IReadOnlyList<MigrationSchemaTable> Tables => _tables;
}

/// <summary>Describes one table in a reconstructed migration schema.</summary>
public sealed class MigrationSchemaTable
{
    private readonly ReadOnlyCollection<MigrationSchemaColumn> _columns;

    /// <summary>Initializes a table.</summary>
    public MigrationSchemaTable(
        string? schemaName,
        string name,
        IEnumerable<MigrationSchemaColumn> columns)
    {
        SchemaName = schemaName;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A table name is required.", nameof(name))
            : name;
        if (columns is null)
        {
            throw new ArgumentNullException(nameof(columns));
        }

        _columns = Array.AsReadOnly(columns.ToArray());
    }

    /// <summary>Gets the optional schema name.</summary>
    public string? SchemaName { get; }

    /// <summary>Gets the table name.</summary>
    public string Name { get; }

    /// <summary>Gets columns in declaration order.</summary>
    public IReadOnlyList<MigrationSchemaColumn> Columns => _columns;
}

/// <summary>Describes one column in a reconstructed migration schema.</summary>
public sealed class MigrationSchemaColumn
{
    /// <summary>Initializes a column.</summary>
    public MigrationSchemaColumn(
        string name,
        string sqlType,
        bool isNullable,
        bool isPrimaryKey,
        string? defaultExpression,
        bool isIdentity = false)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A column name is required.", nameof(name))
            : name;
        SqlType = string.IsNullOrWhiteSpace(sqlType)
            ? throw new ArgumentException("A SQL type is required.", nameof(sqlType))
            : sqlType;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        DefaultExpression = defaultExpression;
        IsIdentity = isIdentity;
    }

    /// <summary>Gets the column name.</summary>
    public string Name { get; }

    /// <summary>Gets the provider-specific SQL type.</summary>
    public string SqlType { get; }

    /// <summary>Gets whether the column accepts nulls.</summary>
    public bool IsNullable { get; }

    /// <summary>Gets whether the column is part of the primary key.</summary>
    public bool IsPrimaryKey { get; }

    /// <summary>Gets the optional SQL default expression.</summary>
    public string? DefaultExpression { get; }

    /// <summary>Gets whether values are generated by an identity definition.</summary>
    public bool IsIdentity { get; }
}
