using System.Linq;
using System.Threading.Tasks;
using CobaltumOrm;
using CobaltumOrm.PostgreSql.E2E.Tests.Generated;
using Npgsql;
using Xunit;

namespace CobaltumOrm.PostgreSql.E2E.Tests;

[Collection(PostgreSqlE2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class PostgreSqlRecordCommandE2ETests
{
    private readonly PostgreSqlContainerFixture _fixture;

    public PostgreSqlRecordCommandE2ETests(PostgreSqlContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InsertLeavesTheIdentityKeyToTheDatabase()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            var affected = await connection
                .Query(Tables.E2eRecords.Insert(new E2eRecordsRow(0, "assigned", null)))
                .ExecuteAsync();

            Assert.Equal(1, affected);
            var stored = Assert.Single(await connection
                .Query(Tables.E2eRecords.Where(Tables.E2eRecords.Label.Equal("assigned")))
                .ReadAsync());
            Assert.True(stored.Id > 0);
            Assert.Null(stored.Note);
        }
        finally
        {
            await ClearAsync(connection);
        }
    }

    [Fact]
    public async Task InsertReturningReportsTheStoredRecord()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            var stored = Assert.Single(await connection
                .Query(Tables.E2eRecords.InsertReturning(new E2eRecordsRow(0, "returning", "note")))
                .ReadAsync());

            Assert.True(stored.Id > 0);
            Assert.Equal("returning", stored.Label);
            Assert.Equal("note", stored.Note);

            var reread = await connection
                .Query(Tables.E2eRecords.Where(Tables.E2eRecords.Id.Equal(stored.Id)))
                .ReadAsync();
            Assert.Equal(stored, Assert.Single(reread));
        }
        finally
        {
            await ClearAsync(connection);
        }
    }

    [Fact]
    public async Task UpdateAndDeleteMatchOneRowByItsPrimaryKey()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            var target = await InsertReturningAsync(connection, "before", null);
            var other = await InsertReturningAsync(connection, "untouched", null);

            var updated = await connection
                .Query(Tables.E2eRecords.Update(target with { Label = "after", Note = "edited" }))
                .ExecuteAsync();
            Assert.Equal(1, updated);

            var reread = Assert.Single(await connection
                .Query(Tables.E2eRecords.Where(Tables.E2eRecords.Id.Equal(target.Id)))
                .ReadAsync());
            Assert.Equal("after", reread.Label);
            Assert.Equal("edited", reread.Note);

            Assert.Equal(1, await connection.Query(Tables.E2eRecords.Delete(target)).ExecuteAsync());
            Assert.Empty(await connection
                .Query(Tables.E2eRecords.Where(Tables.E2eRecords.Id.Equal(target.Id)))
                .ReadAsync());
            Assert.Single(await connection
                .Query(Tables.E2eRecords.Where(Tables.E2eRecords.Id.Equal(other.Id)))
                .ReadAsync());
        }
        finally
        {
            await ClearAsync(connection);
        }
    }

    [Fact]
    public async Task DeleteWhereRemovesEveryMatchingRow()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            await InsertReturningAsync(connection, "batch", "a");
            await InsertReturningAsync(connection, "batch", "b");
            await InsertReturningAsync(connection, "kept", null);

            var deleted = await connection
                .Query(Tables.E2eRecords.DeleteWhere(Tables.E2eRecords.Label.Equal("batch")))
                .ExecuteAsync();

            Assert.Equal(2, deleted);
            var remaining = await connection.Query(Tables.E2eRecords.All()).ReadAsync();
            Assert.Equal("kept", Assert.Single(remaining).Label);
        }
        finally
        {
            await ClearAsync(connection);
        }
    }

    [Fact]
    public async Task RecordCommandsJoinAnExplicitTransaction()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await connection
                    .Query(Tables.E2eRecords.Insert(new E2eRecordsRow(0, "rolled-back", null)), transaction)
                    .ExecuteAsync();
                Assert.Single(await connection.Query(Tables.E2eRecords.All(), transaction).ReadAsync());
                await transaction.RollbackAsync();
            }

            Assert.Empty(await connection.Query(Tables.E2eRecords.All()).ReadAsync());
        }
        finally
        {
            await ClearAsync(connection);
        }
    }

    [Fact]
    public async Task TheTableRecordCanBeTheResultTypeOfACallerWrittenQuery()
    {
        await using var connection = await OpenConnectionAsync();
        try
        {
            var stored = await InsertReturningAsync(connection, "mapped", "note");

            var named = await PostgreSqlE2EQueries.FindRecordsByLabelAsync(connection, "mapped");
            var inline = await connection
                .Query<E2eRecordsRow>("SELECT id, label, note FROM e2e_records WHERE label = 'mapped'")
                .ReadAsync();

            Assert.Equal(stored, Assert.Single(named));
            Assert.Equal(stored, Assert.Single(inline));
        }
        finally
        {
            await ClearAsync(connection);
        }
    }

    private static async Task<E2eRecordsRow> InsertReturningAsync(
        NpgsqlConnection connection,
        string label,
        string? note) =>
        (await connection
            .Query(Tables.E2eRecords.InsertReturning(new E2eRecordsRow(0, label, note)))
            .ReadAsync())
        .Single();

    private static async Task ClearAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM e2e_records;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
