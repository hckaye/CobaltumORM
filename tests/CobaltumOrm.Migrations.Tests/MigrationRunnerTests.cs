using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm.Migrations.PostgreSql;
using CobaltumOrm.Migrations.Tests.Fakes;
using Xunit;

namespace CobaltumOrm.Migrations.Tests;

internal static class TestMigrationCatalog
{
    internal static MigrationInfo CreateAllTypes { get; } =
        MigrationInfo.Create<CreateAllTypesMigration>(100, "Create all PostgreSQL types");

    internal static MigrationInfo ChangeWidgets { get; } =
        MigrationInfo.Create<ChangeWidgetsMigration>(200, "Change widgets");

    internal static MigrationInfo CreateAuditLog { get; } =
        MigrationInfo.Create<CreateAuditLogMigration>(310, "Create Audit Log");

    internal static MigrationInfo AddAuditActor { get; } =
        MigrationInfo.Create<AddAuditActorMigration>(320, "Add the audit actor");

    internal static MigrationInfo FinalizeAudit { get; } =
        MigrationInfo.Create<FinalizeAuditMigration>(330, "Finalize Audit");

    internal static MigrationInfo ForwardAudit { get; } =
        MigrationInfo.Create<ForwardAuditMigration>(340, "Forward audit import");

    internal static IReadOnlyList<MigrationInfo> All { get; } = new[]
    {
        CreateAllTypes,
        ChangeWidgets,
        CreateAuditLog,
        AddAuditActor,
        FinalizeAudit,
        ForwardAudit,
    };
}

public sealed class MigrationRunnerTests
{
    [Fact]
    public async Task AppliesUnorderedInputByVersionAndRecordsReadableHistory()
    {
        var connection = new FakeDbConnection();
        var runner = new MigrationRunner(
            new PostgreSqlMigrationAdapter(),
            new MigrationRunnerOptions("history", "meta"));

        await runner.MigrateUpAsync(
            connection,
            new[]
            {
                TestMigrationCatalog.FinalizeAudit,
                TestMigrationCatalog.CreateAuditLog,
                TestMigrationCatalog.AddAuditActor,
            });

        var rawSql = connection.Executions
            .Where(execution => execution.CommandText.StartsWith("UP-", StringComparison.Ordinal))
            .Select(execution => execution.CommandText)
            .ToArray();
        Assert.Equal(new[] { "UP-310", "UP-320", "UP-330" }, rawSql);
        Assert.Equal(new long[] { 310, 320, 330 }, connection.HistoryVersions);
        Assert.Equal(3, connection.Transactions.Count);
        Assert.All(connection.Transactions, transaction => Assert.True(transaction.WasCommitted));
        Assert.All(connection.Transactions, transaction => Assert.False(transaction.WasRolledBack));
        Assert.Equal(ConnectionState.Closed, connection.State);

        var inserts = connection.Executions
            .Where(execution => execution.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, inserts.Length);
        Assert.Contains("\"meta\".\"history\"", inserts[0].CommandText, StringComparison.Ordinal);
        Assert.Equal("Create Audit Log", inserts[0].Parameters["description"]);
        Assert.Equal("Add the audit actor", inserts[1].Parameters["description"]);
        Assert.Equal("Finalize Audit", inserts[2].Parameters["description"]);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(inserts[0].Parameters["applied_utc"]).Offset);

        for (var index = 0; index < 3; index++)
        {
            var operation = connection.Executions.Single(execution => execution.CommandText == $"UP-{310 + (index * 10)}");
            Assert.Equal(operation.TransactionId, inserts[index].TransactionId);
        }
    }

    [Fact]
    public async Task ExistingOrderedPrefixIsSkipped()
    {
        var connection = new FakeDbConnection();
        connection.HistoryVersions.Add(310);
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await runner.MigrateUpAsync(
            connection,
            new[] { TestMigrationCatalog.AddAuditActor, TestMigrationCatalog.CreateAuditLog });

        Assert.DoesNotContain(connection.Executions, execution => execution.CommandText == "UP-310");
        Assert.Contains(connection.Executions, execution => execution.CommandText == "UP-320");
        Assert.Equal(new long[] { 310, 320 }, connection.HistoryVersions);
        Assert.Single(connection.Transactions);
    }

    [Fact]
    public async Task RollsBackToTargetInReverseVersionOrder()
    {
        var connection = new FakeDbConnection();
        connection.HistoryVersions.AddRange(new long[] { 310, 320, 330 });
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await runner.MigrateDownAsync(
            connection,
            new[]
            {
                TestMigrationCatalog.AddAuditActor,
                TestMigrationCatalog.FinalizeAudit,
                TestMigrationCatalog.CreateAuditLog,
            },
            310);

        var downSql = connection.Executions
            .Where(execution => execution.CommandText.StartsWith("DOWN-", StringComparison.Ordinal))
            .Select(execution => execution.CommandText)
            .ToArray();
        Assert.Equal(new[] { "DOWN-330", "DOWN-320" }, downSql);
        Assert.Equal(new long[] { 310 }, connection.HistoryVersions);
        Assert.Equal(2, connection.Transactions.Count);
        Assert.All(connection.Transactions, transaction => Assert.True(transaction.WasCommitted));

        var deletes = connection.Executions
            .Where(execution => execution.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(330L, deletes[0].Parameters["version"]);
        Assert.Equal(320L, deletes[1].Parameters["version"]);
        Assert.Equal(
            connection.Executions.Single(execution => execution.CommandText == "DOWN-330").TransactionId,
            deletes[0].TransactionId);
    }

    [Fact]
    public async Task LaterUpFailureRollsBackCurrentMigrationAndKeepsEarlierCommit()
    {
        var connection = new FakeDbConnection { FailWhenCommandContains = "UP-320" };
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MigrateUpAsync(
                connection,
                new[] { TestMigrationCatalog.AddAuditActor, TestMigrationCatalog.CreateAuditLog }));

        Assert.Equal("Configured fake command failure.", exception.Message);
        Assert.Equal(new long[] { 310 }, connection.HistoryVersions);
        Assert.Equal(2, connection.Transactions.Count);
        Assert.True(connection.Transactions[0].WasCommitted);
        Assert.False(connection.Transactions[0].WasRolledBack);
        Assert.False(connection.Transactions[1].WasCommitted);
        Assert.True(connection.Transactions[1].WasRolledBack);
        Assert.DoesNotContain(
            connection.Executions,
            execution => execution.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal) &&
                         Equals(execution.Parameters["version"], 320L));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task DownFailureKeepsVersionInHistory()
    {
        var connection = new FakeDbConnection { FailWhenCommandContains = "DOWN-320" };
        connection.HistoryVersions.AddRange(new long[] { 310, 320 });
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.MigrateDownAsync(
                connection,
                new[] { TestMigrationCatalog.CreateAuditLog, TestMigrationCatalog.AddAuditActor },
                0));

        Assert.Equal(new long[] { 310, 320 }, connection.HistoryVersions);
        var transaction = Assert.Single(connection.Transactions);
        Assert.True(transaction.WasRolledBack);
        Assert.DoesNotContain(
            connection.Executions,
            execution => execution.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsAForwardOnlyRollbackBeforeStartingAnyRollbackTransaction()
    {
        var connection = new FakeDbConnection();
        connection.HistoryVersions.AddRange(new long[] { 310, 320, 340 });
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        var exception = await Assert.ThrowsAsync<MigrationValidationException>(
            () => runner.MigrateDownAsync(
                connection,
                new[]
                {
                    TestMigrationCatalog.CreateAuditLog,
                    TestMigrationCatalog.AddAuditActor,
                    TestMigrationCatalog.ForwardAudit,
                },
                0));

        Assert.Contains("forward-only", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("340", exception.Message, StringComparison.Ordinal);
        Assert.Empty(connection.Transactions);
        Assert.DoesNotContain(
            connection.Executions,
            execution => execution.CommandText.StartsWith("DOWN-", StringComparison.Ordinal));
        Assert.Equal(new long[] { 310, 320, 340 }, connection.HistoryVersions);
    }

    [Fact]
    public async Task PassesCancellationTokenToProviderAsyncCalls()
    {
        using var cancellationSource = new CancellationTokenSource();
        var token = cancellationSource.Token;
        var connection = new FakeDbConnection();
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await runner.MigrateUpAsync(connection, new[] { TestMigrationCatalog.CreateAuditLog }, token);

        Assert.Equal(token, Assert.Single(connection.OpenTokens));
        Assert.Equal(token, Assert.Single(connection.BeginTransactionTokens));
        Assert.All(connection.Executions, execution => Assert.Equal(token, execution.CancellationToken));
        Assert.Equal(token, Assert.Single(connection.Transactions).CommitToken);
    }

    [Fact]
    public async Task LeavesAnAlreadyOpenConnectionOpen()
    {
        var connection = new FakeDbConnection();
        connection.Open();
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await runner.MigrateUpAsync(connection, Array.Empty<MigrationInfo>());

        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Empty(connection.OpenTokens);
    }

    [Fact]
    public async Task ReportsAppliedAndPendingMigrations()
    {
        var connection = new FakeDbConnection();
        connection.HistoryVersions.Add(310);
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        var statuses = await runner.GetStatusAsync(
            connection,
            new[] { TestMigrationCatalog.AddAuditActor, TestMigrationCatalog.CreateAuditLog });

        Assert.Collection(
            statuses,
            status =>
            {
                Assert.Equal(310, status.Migration.Version);
                Assert.True(status.IsApplied);
            },
            status =>
            {
                Assert.Equal(320, status.Migration.Version);
                Assert.False(status.IsApplied);
            });
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(connection.Transactions);
    }

    [Fact]
    public async Task StatusRejectsHistoryThatSkipsADiscoveredVersion()
    {
        var connection = new FakeDbConnection();
        connection.HistoryVersions.Add(320);
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await Assert.ThrowsAsync<MigrationValidationException>(
            () => runner.GetStatusAsync(
                connection,
                new[] { TestMigrationCatalog.CreateAuditLog, TestMigrationCatalog.AddAuditActor }));

        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task DryRunUpReturnsSqlAndFinalSchemaWithoutDatabaseChanges()
    {
        var connection = new FakeDbConnection { HistoryTableExists = false };
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        var dryRun = await runner.DryRunUpAsync(
            connection,
            new[] { TestMigrationCatalog.CreateAllTypes });

        var entry = Assert.Single(dryRun.Entries);
        Assert.Equal(MigrationDryRunDirection.Up, entry.Direction);
        Assert.Equal(100, entry.Migration.Version);
        Assert.StartsWith("CREATE TABLE \"app\"\"data\".", Assert.Single(entry.Commands).CommandText);
        var table = Assert.Single(dryRun.FinalSchema.Tables);
        Assert.Equal("app\"data", table.SchemaName);
        Assert.Equal("widget\"items", table.Name);
        Assert.Contains(
            table.Columns,
            column => column.Name == "id" && column.IsPrimaryKey && column.IsIdentity);
        Assert.Equal(0, dryRun.CurrentVersion);
        Assert.Equal(100, dryRun.TargetVersion);
        Assert.Empty(connection.HistoryVersions);
        Assert.Empty(connection.Transactions);
        Assert.DoesNotContain(
            connection.Executions,
            execution => execution.CommandText.StartsWith("CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal));
        Assert.DoesNotContain(connection.Executions, execution => execution.IsReader);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task DryRunDownReturnsRollbackSqlAndAnEmptyFinalSchemaWithoutDatabaseChanges()
    {
        var connection = new FakeDbConnection();
        connection.HistoryVersions.Add(100);
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        var dryRun = await runner.DryRunDownAsync(
            connection,
            new[] { TestMigrationCatalog.CreateAllTypes },
            0);

        var entry = Assert.Single(dryRun.Entries);
        Assert.Equal(MigrationDryRunDirection.Down, entry.Direction);
        Assert.Equal(
            "DROP TABLE \"app\"\"data\".\"widget\"\"items\";",
            Assert.Single(entry.Commands).CommandText);
        Assert.Empty(dryRun.FinalSchema.Tables);
        Assert.Equal(100, dryRun.CurrentVersion);
        Assert.Equal(0, dryRun.TargetVersion);
        Assert.Equal(new long[] { 100 }, connection.HistoryVersions);
        Assert.Empty(connection.Transactions);
        Assert.DoesNotContain(
            connection.Executions,
            execution => execution.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task RejectsDuplicateDiscoveredVersionsBeforeOpeningConnection()
    {
        var connection = new FakeDbConnection();
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        var exception = await Assert.ThrowsAsync<MigrationValidationException>(
            () => runner.MigrateUpAsync(
                connection,
                new[] { TestMigrationCatalog.CreateAuditLog, TestMigrationCatalog.CreateAuditLog }));

        Assert.Contains("version 310", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(connection.OpenTokens);
        Assert.Empty(connection.Executions);
    }

    [Fact]
    public Task RejectsHistoryThatIsNotStrictlyOrdered() =>
        AssertInvalidHistoryAsync(new long[] { 320, 310 });

    [Fact]
    public Task RejectsHistoryThatSkipsADiscoveredVersion() =>
        AssertInvalidHistoryAsync(new long[] { 320 });

    private static async Task AssertInvalidHistoryAsync(long[] history)
    {
        var connection = new FakeDbConnection();
        connection.HistoryVersions.AddRange(history);
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await Assert.ThrowsAsync<MigrationValidationException>(
            () => runner.MigrateUpAsync(
                connection,
                new[] { TestMigrationCatalog.CreateAuditLog, TestMigrationCatalog.AddAuditActor }));

        Assert.Empty(connection.Transactions);
    }

    [Fact]
    public async Task OrdersCatalogMigrationsByVersion()
    {
        var connection = new FakeDbConnection();
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await runner.MigrateUpAsync(
            connection,
            new[]
            {
                TestMigrationCatalog.FinalizeAudit,
                TestMigrationCatalog.CreateAuditLog,
                TestMigrationCatalog.AddAuditActor,
            });

        Assert.Equal(new long[] { 310, 320, 330 }, connection.HistoryVersions);
    }

    [Fact]
    public async Task RunnerAppliesEveryMigrationInCatalog()
    {
        var connection = new FakeDbConnection();
        var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());

        await runner.MigrateUpAsync(connection, TestMigrationCatalog.All);

        Assert.Equal(new long[] { 100, 200, 310, 320, 330, 340 }, connection.HistoryVersions);
        Assert.Equal(6, connection.Transactions.Count);
        Assert.All(connection.Transactions, transaction => Assert.True(transaction.WasCommitted));
    }
}

[Migration(310)]
public sealed class CreateAuditLogMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("UP-310");
    }

    public override void Down()
    {
        Execute.Sql("DOWN-310");
    }
}

[Migration(320, "Add the audit actor")]
public sealed class AddAuditActorMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("UP-320");
    }

    public override void Down()
    {
        Execute.Sql("DOWN-320");
    }
}

[Migration(330)]
public sealed class FinalizeAuditMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("UP-330");
    }

    public override void Down()
    {
        Execute.Sql("DOWN-330");
    }
}

[Migration(340, "Forward audit import")]
public sealed class ForwardAuditMigration : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.Sql("UP-340");
    }
}
