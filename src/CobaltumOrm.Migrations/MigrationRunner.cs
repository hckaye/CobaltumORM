using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace CobaltumOrm.Migrations;

/// <summary>
/// Applies and rolls back migrations through provider-neutral <see cref="DbConnection"/>
/// APIs. Each migration and its history update use one transaction; successful earlier
/// migrations remain committed if a later migration fails.
/// </summary>
public sealed class MigrationRunner
{
    private readonly IMigrationDatabaseAdapter _adapter;
    private readonly MigrationRunnerOptions _options;

    /// <summary>Initializes a runner for a database adapter and the default history table.</summary>
    public MigrationRunner(IMigrationDatabaseAdapter adapter)
        : this(adapter, new MigrationRunnerOptions())
    {
    }

    /// <summary>Initializes a runner for a database adapter and history-table options.</summary>
    public MigrationRunner(IMigrationDatabaseAdapter adapter, MigrationRunnerOptions options)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Validates a migration catalog and applies pending versions.</summary>
    public Task MigrateUpAsync(
        DbConnection connection,
        IEnumerable<MigrationInfo> migrationCatalog,
        CancellationToken cancellationToken = default) =>
        MigrateUpCoreAsync(connection, MigrationCatalogValidator.Validate(migrationCatalog), cancellationToken);

    /// <summary>Validates a migration catalog and rolls back to a version boundary.</summary>
    public Task MigrateDownAsync(
        DbConnection connection,
        IEnumerable<MigrationInfo> migrationCatalog,
        long targetVersion,
        CancellationToken cancellationToken = default) =>
        MigrateDownCoreAsync(
            connection,
            MigrationCatalogValidator.Validate(migrationCatalog),
            targetVersion,
            cancellationToken);

    /// <summary>Reads the applied state of migrations in a catalog.</summary>
    public Task<IReadOnlyList<MigrationStatus>> GetStatusAsync(
        DbConnection connection,
        IEnumerable<MigrationInfo> migrationCatalog,
        CancellationToken cancellationToken = default) =>
        GetStatusCoreAsync(connection, MigrationCatalogValidator.Validate(migrationCatalog), cancellationToken);

    /// <summary>Previews pending migrations from a catalog without changing the database.</summary>
    public Task<MigrationDryRun> DryRunUpAsync(
        DbConnection connection,
        IEnumerable<MigrationInfo> migrationCatalog,
        CancellationToken cancellationToken = default) =>
        DryRunCoreAsync(
            connection,
            MigrationCatalogValidator.Validate(migrationCatalog),
            MigrationDryRunDirection.Up,
            0,
            cancellationToken);

    /// <summary>Previews a rollback from a catalog without changing the database.</summary>
    public Task<MigrationDryRun> DryRunDownAsync(
        DbConnection connection,
        IEnumerable<MigrationInfo> migrationCatalog,
        long targetVersion,
        CancellationToken cancellationToken = default) =>
        DryRunCoreAsync(
            connection,
            MigrationCatalogValidator.Validate(migrationCatalog),
            MigrationDryRunDirection.Down,
            targetVersion,
            cancellationToken);

    private async Task MigrateUpCoreAsync(
        DbConnection connection,
        IReadOnlyList<MigrationInfo> migrations,
        CancellationToken cancellationToken)
    {
        ValidateConnection(connection);
        var closeWhenFinished = connection.State == ConnectionState.Closed;
        try
        {
            await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
            await ExecuteCommandAsync(
                    connection,
                    null,
                    _adapter.CreateEnsureHistoryTableCommand(
                        _options.HistoryTableSchema,
                        _options.HistoryTableName),
                    cancellationToken)
                .ConfigureAwait(false);
            var appliedVersions = await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
            ValidateHistoryIsOrderedPrefix(migrations, appliedVersions);

            for (var index = appliedVersions.Count; index < migrations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var migration = migrations[index];
                var instance = CreateMigration(migration);
                var operations = instance.CollectOperations(true);
                await ExecuteInTransactionAsync(
                        connection,
                        operations,
                        _adapter.CreateInsertHistoryCommand(
                            _options.HistoryTableSchema,
                            _options.HistoryTableName,
                            migration.Version,
                            migration.Description,
                            DateTimeOffset.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (closeWhenFinished && connection.State != ConnectionState.Closed)
            {
                connection.Close();
            }
        }
    }

    private async Task MigrateDownCoreAsync(
        DbConnection connection,
        IReadOnlyList<MigrationInfo> migrations,
        long targetVersion,
        CancellationToken cancellationToken)
    {
        if (targetVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion), "The target version cannot be negative.");
        }

        ValidateConnection(connection);
        var closeWhenFinished = connection.State == ConnectionState.Closed;
        try
        {
            await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
            await ExecuteCommandAsync(
                    connection,
                    null,
                    _adapter.CreateEnsureHistoryTableCommand(
                        _options.HistoryTableSchema,
                        _options.HistoryTableName),
                    cancellationToken)
                .ConfigureAwait(false);
            var appliedVersions = await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
            ValidateHistoryIsOrderedPrefix(migrations, appliedVersions);
            ValidateRollbackIsReversible(migrations, appliedVersions.Count, targetVersion);

            for (var index = appliedVersions.Count - 1; index >= 0; index--)
            {
                var migration = migrations[index];
                if (migration.Version <= targetVersion)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var instance = CreateMigration(migration);
                var operations = instance.CollectOperations(false);
                await ExecuteInTransactionAsync(
                        connection,
                        operations,
                        _adapter.CreateDeleteHistoryCommand(
                            _options.HistoryTableSchema,
                            _options.HistoryTableName,
                            migration.Version),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (closeWhenFinished && connection.State != ConnectionState.Closed)
            {
                connection.Close();
            }
        }
    }

    private async Task<IReadOnlyList<MigrationStatus>> GetStatusCoreAsync(
        DbConnection connection,
        IReadOnlyList<MigrationInfo> migrations,
        CancellationToken cancellationToken)
    {
        ValidateConnection(connection);
        var closeWhenFinished = connection.State == ConnectionState.Closed;
        try
        {
            await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
            await ExecuteCommandAsync(
                    connection,
                    null,
                    _adapter.CreateEnsureHistoryTableCommand(
                        _options.HistoryTableSchema,
                        _options.HistoryTableName),
                    cancellationToken)
                .ConfigureAwait(false);
            var appliedVersions = await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false);
            ValidateHistoryIsOrderedPrefix(migrations, appliedVersions);

            var statuses = new List<MigrationStatus>(migrations.Count);
            for (var index = 0; index < migrations.Count; index++)
            {
                statuses.Add(new MigrationStatus(migrations[index], index < appliedVersions.Count));
            }

            return statuses.AsReadOnly();
        }
        finally
        {
            if (closeWhenFinished && connection.State != ConnectionState.Closed)
            {
                connection.Close();
            }
        }
    }

    private async Task<MigrationDryRun> DryRunCoreAsync(
        DbConnection connection,
        IReadOnlyList<MigrationInfo> migrations,
        MigrationDryRunDirection direction,
        long requestedTargetVersion,
        CancellationToken cancellationToken)
    {
        if (requestedTargetVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedTargetVersion),
                "The target version cannot be negative.");
        }

        if (!(_adapter is IMigrationDryRunDatabaseAdapter dryRunAdapter))
        {
            throw new MigrationValidationException(
                $"The migration adapter '{_adapter.GetType().FullName}' does not support dry runs.");
        }

        ValidateConnection(connection);
        var closeWhenFinished = connection.State == ConnectionState.Closed;
        try
        {
            await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
            var historyExists = await ReadHistoryTableExistsAsync(
                    connection,
                    dryRunAdapter,
                    cancellationToken)
                .ConfigureAwait(false);
            var appliedVersions = historyExists
                ? await ReadAppliedVersionsAsync(connection, cancellationToken).ConfigureAwait(false)
                : new List<long>();
            ValidateHistoryIsOrderedPrefix(migrations, appliedVersions);

            var entries = new List<MigrationDryRunEntry>();
            var finalMigrationCount = appliedVersions.Count;
            if (direction == MigrationDryRunDirection.Up)
            {
                finalMigrationCount = migrations.Count;
                for (var index = appliedVersions.Count; index < migrations.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(CreateDryRunEntry(migrations[index], direction));
                }
            }
            else
            {
                ValidateRollbackIsReversible(migrations, appliedVersions.Count, requestedTargetVersion);
                while (finalMigrationCount > 0 &&
                       migrations[finalMigrationCount - 1].Version > requestedTargetVersion)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    finalMigrationCount--;
                    entries.Add(CreateDryRunEntry(migrations[finalMigrationCount], direction));
                }
            }

            var schemaCommands = new List<MigrationCommand>();
            for (var index = 0; index < appliedVersions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                schemaCommands.AddRange(CollectCommands(migrations[index], up: true));
            }

            foreach (var entry in entries)
            {
                schemaCommands.AddRange(entry.Commands);
            }

            var finalSchema = dryRunAdapter.BuildSchema(schemaCommands);
            if (finalSchema is null)
            {
                throw new MigrationValidationException("The migration adapter returned a null final schema.");
            }

            var currentVersion = appliedVersions.Count == 0 ? 0 : appliedVersions[appliedVersions.Count - 1];
            var targetVersion = finalMigrationCount == 0 ? 0 : migrations[finalMigrationCount - 1].Version;
            return new MigrationDryRun(currentVersion, targetVersion, entries, finalSchema);
        }
        finally
        {
            if (closeWhenFinished && connection.State != ConnectionState.Closed)
            {
                connection.Close();
            }
        }
    }

    private async Task ExecuteInTransactionAsync(
        DbConnection connection,
        IReadOnlyList<MigrationOperation> operations,
        MigrationCommand historyCommand,
        CancellationToken cancellationToken)
    {
        using (var transaction = await BeginTransactionAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                foreach (var operation in operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (operation is ExecuteWithConnectionOperation withConnection)
                    {
                        withConnection.Callback(connection, transaction);
                        continue;
                    }
                    if (operation is ConditionalMigrationOperation conditional &&
                        conditional.Operation is ExecuteWithConnectionOperation conditionalConnection)
                    {
                        if (GenerateCommands(operation).Count != 0)
                            conditionalConnection.Callback(connection, transaction);
                        continue;
                    }

                    var commands = GenerateCommands(operation);
                    foreach (var command in commands)
                    {
                        await ExecuteCommandAsync(connection, transaction, command, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                await ExecuteCommandAsync(connection, transaction, historyCommand, cancellationToken)
                    .ConfigureAwait(false);
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                try
                {
                    await RollbackAsync(transaction).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The migration failed and its transaction could not be rolled back.",
                        exception,
                        rollbackException);
                }

                throw;
            }
        }
    }

    private async Task<List<long>> ReadAppliedVersionsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        using (var command = BuildCommand(
                   connection,
                   null,
                   _adapter.CreateReadHistoryCommand(
                       _options.HistoryTableSchema,
                       _options.HistoryTableName)))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var versions = new List<long>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    versions.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
                }
                catch (Exception exception) when (exception is InvalidCastException || exception is FormatException || exception is OverflowException)
                {
                    throw new MigrationValidationException(
                        "The migration history query returned a version that is not a signed 64-bit integer.",
                        exception);
                }
            }

            return versions;
        }
    }

    private async Task<bool> ReadHistoryTableExistsAsync(
        DbConnection connection,
        IMigrationDryRunDatabaseAdapter dryRunAdapter,
        CancellationToken cancellationToken)
    {
        using (var command = BuildCommand(
                   connection,
                   null,
                   dryRunAdapter.CreateHistoryTableExistsCommand(
                       _options.HistoryTableSchema,
                       _options.HistoryTableName)))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return value is not null && value != DBNull.Value &&
                    Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is InvalidCastException || exception is FormatException)
            {
                throw new MigrationValidationException(
                    "The history-table existence query did not return a Boolean value.",
                    exception);
            }
        }
    }

    private MigrationDryRunEntry CreateDryRunEntry(
        MigrationInfo migration,
        MigrationDryRunDirection direction) =>
        new MigrationDryRunEntry(
            migration,
            direction,
            CollectCommands(migration, direction == MigrationDryRunDirection.Up));

    private IReadOnlyList<MigrationCommand> CollectCommands(MigrationInfo migration, bool up)
    {
        var instance = CreateMigration(migration);
        var operations = instance.CollectOperations(up);
        var commands = new List<MigrationCommand>();
        foreach (var operation in operations)
        {
            commands.AddRange(GenerateCommands(operation));
        }

        return commands.AsReadOnly();
    }

    private IReadOnlyList<MigrationCommand> GenerateCommands(MigrationOperation operation)
    {
        var commands = _adapter.GenerateCommands(operation);
        if (commands is null ||
            (commands.Count == 0 && !(operation is ConditionalMigrationOperation)))
        {
            throw new MigrationValidationException(
                $"The adapter generated no commands for '{operation.GetType().Name}'.");
        }

        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index] is null)
            {
                throw new MigrationValidationException("The adapter returned a null migration command.");
            }
        }

        return commands;
    }

    private static void ValidateHistoryIsOrderedPrefix(
        IReadOnlyList<MigrationInfo> migrations,
        IReadOnlyList<long> appliedVersions)
    {
        if (appliedVersions.Count > migrations.Count)
        {
            throw new MigrationValidationException(
                "Migration history contains versions that were not present in the discovered migration set.");
        }

        long? previous = null;
        for (var index = 0; index < appliedVersions.Count; index++)
        {
            var version = appliedVersions[index];
            if (previous.HasValue && version <= previous.Value)
            {
                throw new MigrationValidationException("Migration history versions must be unique and strictly ordered.");
            }

            if (migrations[index].Version != version)
            {
                throw new MigrationValidationException(
                    $"Migration history is not an ordered prefix of the discovered migrations at version {version}.");
            }

            previous = version;
        }
    }

    private static void ValidateRollbackIsReversible(
        IReadOnlyList<MigrationInfo> migrations,
        int appliedCount,
        long targetVersion)
    {
        for (var index = appliedCount - 1; index >= 0; index--)
        {
            var migration = migrations[index];
            if (migration.Version <= targetVersion)
            {
                break;
            }

            if (migration.IsForwardOnly)
            {
                throw new MigrationValidationException(
                    $"Migration {migration.Version} ('{migration.Description}') is forward-only; " +
                    $"rollback to version {targetVersion} cannot be performed.");
            }
        }
    }

    private static Migration CreateMigration(MigrationInfo migration)
    {
        try
        {
            return migration.CreateMigration();
        }
        catch (Exception exception)
        {
            throw new MigrationValidationException(
                $"Migration '{migration.MigrationType.FullName}' could not be created.",
                exception);
        }
    }

    private static async Task ExecuteCommandAsync(
        DbConnection connection,
        DbTransaction? transaction,
        MigrationCommand migrationCommand,
        CancellationToken cancellationToken)
    {
        using (var command = BuildCommand(connection, transaction, migrationCommand))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static DbCommand BuildCommand(
        DbConnection connection,
        DbTransaction? transaction,
        MigrationCommand migrationCommand)
    {
        if (migrationCommand is null)
        {
            throw new MigrationValidationException("The adapter returned a null command.");
        }

        var command = connection.CreateCommand();
        try
        {
            command.CommandText = migrationCommand.CommandText;
            command.Transaction = transaction;
            foreach (var migrationParameter in migrationCommand.Parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = migrationParameter.Name;
                parameter.Value = migrationParameter.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private static void ValidateConnection(DbConnection connection)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (connection.State != ConnectionState.Closed && connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                $"The connection must be closed or open before running migrations; current state is {connection.State}.");
        }
    }

    private static async Task OpenIfNeededAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<DbTransaction> BeginTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        return await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
#else
        cancellationToken.ThrowIfCancellationRequested();
        return connection.BeginTransaction();
#endif
    }

    private static async Task CommitAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
#else
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        await Task.CompletedTask.ConfigureAwait(false);
#endif
    }

    private static async Task RollbackAsync(DbTransaction transaction)
    {
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
#else
        transaction.Rollback();
        await Task.CompletedTask.ConfigureAwait(false);
#endif
    }
}
