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
        var tableRows = await connection
            .Query(Tables.E2eValues.Where(Tables.E2eValues.Document.Equal("{\"active\": true}")))
            .ReadAsync();

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
    public async Task ArrayParametersAndResultsUseNpgsqlArrayTypes()
    {
        await using var connection = await OpenConnectionAsync();

        var rows = await PostgreSqlE2EQueries.FindByArrayAsync(
            connection,
            new[] { 2, 3 },
            2,
            3,
            new[] { "two" },
            new[] { Guid.Parse("11111111-1111-1111-1111-111111111111") });
        var tableRows = await connection
            .Query(Tables.E2eValues.Where(Tables.E2eValues.Numbers.Equal(new[] { 1, 2, 3 })))
            .ReadAsync();

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Id);
        Assert.Equal(new[] { 1, 2, 3 }, row.Numbers);
        Assert.Equal(new[] { "one", "two" }, row.Labels);
        Assert.Equal(
            new[] { Guid.Parse("11111111-1111-1111-1111-111111111111") },
            row.Identifiers);
        Assert.Equal(1, Assert.Single(tableRows).Id);
    }

    [Fact]
    public async Task ArrayConstructorsSubscriptsAndUnnestRunAgainstPostgreSql()
    {
        await using var connection = await OpenConnectionAsync();

        var expressions = Assert.Single(await PostgreSqlE2EQueries.ReadArrayExpressionsAsync(connection, 1));
        var nullArray = Assert.Single(await PostgreSqlE2EQueries.ReadArrayExpressionsAsync(connection, 2));
        var expanded = await PostgreSqlE2EQueries.ExpandNumbersAsync(connection, 1);
        var subscripts = await PostgreSqlE2EQueries.ReadArraySubscriptsAsync(connection, 1);

        Assert.Equal(new[] { 7, 8, 9 }, expressions.Constructed);
        Assert.Equal(2, expressions.SecondItem);
        Assert.Equal(new[] { "one", "two" }, expressions.Labels);
        Assert.Null(nullArray.Labels);
        Assert.Equal(new int?[] { 1, 2, 3 }, expanded.Select(row => row.Item).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, subscripts.Select(row => row.Position).ToArray());
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
        var table = Assert.Single(dryRun.FinalSchema.Tables, item => item.Name == "e2e_values");
        Assert.Equal(7, table.Columns.Count);

        var missingHistoryDryRun = await new MigrationRunner(
                new PostgreSqlMigrationAdapter(),
                new MigrationRunnerOptions("__cobaltum_dry_run_missing"))
            .DryRunUpAsync(connection, CobaltumMigrationCatalog.All);
        Assert.Equal(CobaltumMigrationCatalog.All.Count, missingHistoryDryRun.Entries.Count);

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
