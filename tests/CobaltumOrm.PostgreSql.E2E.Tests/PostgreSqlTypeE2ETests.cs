using System;
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
