using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using CobaltumOrm.PostgreSql.E2E.Tests.Generated;
using Npgsql;
using Xunit;

namespace CobaltumOrm.PostgreSql.E2E.Tests;

[Collection(PostgreSqlE2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class PostgreSqlTypeE2ETests
{
    private readonly PostgreSqlContainerFixture _fixture;

    public PostgreSqlTypeE2ETests(PostgreSqlContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TimestampWithoutTimeZoneParameterUsesThePostgreSqlTimestampType()
    {
        await using var connection = await OpenConnectionAsync();
        var localTime = new DateTime(2026, 8, 10, 12, 34, 56, DateTimeKind.Unspecified);

        var rows = await PostgreSqlE2EQueries.FindByLocalTimeAsync(connection, localTime);

        Assert.Equal(1, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task JsonbParametersWorkInGeneratedQueriesAndTablePredicates()
    {
        await using var connection = await OpenConnectionAsync();

        var generatedQueryRows = await PostgreSqlE2EQueries.FindByDocumentAsync(
            connection,
            "{\"active\": true}");
        var tableRows = await connection.Query(
            Tables.E2eValues.Where(Tables.E2eValues.Document.Equal("{\"active\": true}")));

        Assert.Equal(1, Assert.Single(generatedQueryRows).Id);
        Assert.Equal(1, Assert.Single(tableRows).Id);
    }

    [Fact]
    public async Task IntegerLiteralAndBigintSumUseTheirActualPostgreSqlResultTypes()
    {
        await using var connection = await OpenConnectionAsync();

        var rows = await PostgreSqlE2EQueries.ReadNumericBoundariesAsync(connection);

        var row = Assert.Single(rows);
        Assert.Equal(2147483648L, row.IntegerLiteral);
        Assert.Equal(9223372036854775808m, row.BigintSum);
    }

    [Fact]
    public async Task DataModificationReturningUsesTheGeneratedResultType()
    {
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var rows = await PostgreSqlE2EQueries.UpdateDocumentAsync(
            connection,
            "{\"active\": false, \"updated\": true}",
            1,
            transaction);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Id);
        Assert.Contains("\"updated\": true", row.Document, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task RawParametersKeepSqlInjectionPayloadsOutOfCommandText()
    {
        await using var connection = await OpenConnectionAsync();
        const string hostileValue = "x'; DROP TABLE e2e_values; --";

        var rows = await connection.NoCheckQuery("SELECT @value::text AS value")
            .WithParameter("@value", hostileValue, DbType.String)
            .ReadAsync();

        Assert.Equal(hostileValue, Assert.Single(rows)["value"]);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('e2e_values')::text;";
        Assert.Equal("e2e_values", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task GeneratedQueriesCanRunConcurrentlyOnIndependentConnections()
    {
        var localTime = new DateTime(2026, 8, 10, 12, 34, 56, DateTimeKind.Unspecified);

        var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var connection = await OpenConnectionAsync();
            return Assert.Single(await PostgreSqlE2EQueries.FindByLocalTimeAsync(connection, localTime)).Id;
        }));

        Assert.All(ids, id => Assert.Equal(1, id));
    }

    [Fact]
    public async Task CancellationStopsALongRunningQueryAndLeavesTheConnectionUsable()
    {
        await using var connection = await OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.NoCheckQuery("SELECT pg_sleep(10)").ReadAsync(cancellation.Token));

        var row = Assert.Single(await connection.NoCheckQuery("SELECT 1 AS value").ReadAsync());
        Assert.Equal(1, row["value"]);
    }

    [Fact]
    public async Task ContainerRunsPostgreSql17()
    {
        await using var connection = await OpenConnectionAsync();

        Assert.Equal(17, connection.PostgreSqlVersion.Major);
    }

    [Fact]
    public async Task MigrationDryRunReadsPostgreSqlHistoryWithoutChangingIt()
    {
        await using var connection = await OpenConnectionAsync();
        var before = await ReadMigrationHistoryCountAsync(connection);

        var dryRun = await new MigrationRunner(new PostgreSqlMigrationAdapter()).DryRunUpAsync(
            connection,
            CobaltumMigrationCatalog.All);

        var after = await ReadMigrationHistoryCountAsync(connection);
        Assert.Equal(before, after);
        Assert.Empty(dryRun.Entries);
        var table = Assert.Single(dryRun.FinalSchema.Tables);
        Assert.Equal("e2e_values", table.Name);
        Assert.Equal(4, table.Columns.Count);

        var missingHistoryDryRun = await new MigrationRunner(
                new PostgreSqlMigrationAdapter(),
                new MigrationRunnerOptions("__cobaltum_dry_run_missing"))
            .DryRunUpAsync(connection, CobaltumMigrationCatalog.All);
        Assert.Single(missingHistoryDryRun.Entries);

        await using var missingHistoryCommand = connection.CreateCommand();
        missingHistoryCommand.CommandText = "SELECT to_regclass('__cobaltum_dry_run_missing')::text;";
        Assert.Equal(DBNull.Value, await missingHistoryCommand.ExecuteScalarAsync());
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> ReadMigrationHistoryCountAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__cobaltum_migrations\";";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
