using System;

namespace CobaltumOrm.Migrations;

/// <summary>Configures the migration history table used by <see cref="MigrationRunner"/>.</summary>
public sealed class MigrationRunnerOptions
{
    /// <summary>Initializes options using <c>__cobaltum_migrations</c> in the provider's default schema.</summary>
    public MigrationRunnerOptions()
        : this("__cobaltum_migrations", null)
    {
    }

    /// <summary>Initializes options with an explicit unquoted history table name and schema.</summary>
    public MigrationRunnerOptions(string historyTableName, string? historyTableSchema = null)
    {
        HistoryTableName = ValidateName(historyTableName, nameof(historyTableName));
        HistoryTableSchema = historyTableSchema is null
            ? null
            : ValidateName(historyTableSchema, nameof(historyTableSchema));
    }

    /// <summary>Gets the unquoted history table name.</summary>
    public string HistoryTableName { get; }

    /// <summary>Gets the optional unquoted history table schema.</summary>
    public string? HistoryTableSchema { get; }

    private static string ValidateName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A non-empty database object name without null characters is required.", parameterName);
        }

        return value;
    }
}
