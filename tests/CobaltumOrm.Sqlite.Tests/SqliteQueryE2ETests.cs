using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CobaltumOrm.Sqlite.Tests;

public sealed class SqliteQueryE2ETests
{
    [Fact]
    public async Task ParameterizedCrudPreservesHostileUnicodeNullAndBinaryValues()
    {
        await using var connection = await CreateOpenDatabaseAsync();
        const string hostileText = "Robert'); DROP TABLE db_patterns; --\0雪🚀";
        var binary = new byte[] { 0, 1, 2, 127, 128, 255 };

        var affected = await connection.Query(
                "INSERT INTO db_patterns " +
                "(id, text_value, nullable_text, real_value, blob_value) " +
                "VALUES (@id, @text, @nullable, @real, @blob)")
            .WithParameter("@id", 1L, DbType.Int64)
            .WithParameter("@text", hostileText, DbType.String)
            .WithParameter("@nullable", null, DbType.String)
            .WithParameter("@real", 12345.5d, DbType.Double)
            .WithParameter("@blob", binary, DbType.Binary)
            .ExecuteAsync();

        var rows = await connection.Query(
                "SELECT id, text_value, nullable_text, real_value, blob_value " +
                "FROM db_patterns WHERE text_value = @text")
            .WithParameter("@text", hostileText, DbType.String)
            .ReadAsync();

        Assert.Equal(1, affected);
        var row = Assert.Single(rows);
        Assert.Equal(1L, row["id"]);
        Assert.Equal(hostileText, row["text_value"]);
        Assert.Null(row["nullable_text"]);
        Assert.Equal(12345.5d, row["real_value"]);
        Assert.Equal(binary, Assert.IsType<byte[]>(row["blob_value"]));

        var tableRows = await connection.Query(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'db_patterns'").ReadAsync();
        Assert.Single(tableRows);
    }

    [Fact]
    public async Task ExecutesJoinsSubqueriesAggregatesCaseAndPaginationAcrossMultipleRows()
    {
        await using var connection = await CreateOpenDatabaseAsync();
        await SeedRowsAsync(connection);

        var rows = await connection.Query(
                "SELECT p.id, p.text_value, " +
                "CASE WHEN EXISTS " +
                "(SELECT 1 FROM db_patterns AS newer WHERE newer.id > p.id) " +
                "THEN 'has-newer' ELSE 'last' END AS position, " +
                "COUNT(*) OVER () AS total_count " +
                "FROM db_patterns AS p " +
                "INNER JOIN (SELECT id FROM db_patterns WHERE real_value >= @minimum) AS selected " +
                "ON selected.id = p.id " +
                "ORDER BY p.id DESC LIMIT @limit OFFSET @offset")
            .WithParameter("@minimum", 2d, DbType.Double)
            .WithParameter("@limit", 2L, DbType.Int64)
            .WithParameter("@offset", 0L, DbType.Int64)
            .ReadAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new long[] { 3, 2 }, rows.Select(row => Assert.IsType<long>(row["id"])).ToArray());
        Assert.Equal("last", rows[0]["position"]);
        Assert.Equal("has-newer", rows[1]["position"]);
        Assert.All(rows, row => Assert.Equal(2L, row["total_count"]));
    }

    [Fact]
    public async Task TransactionsCommitAndRollbackWithoutLeakingChanges()
    {
        await using var connection = await CreateOpenDatabaseAsync();

        await using (var rollback = await connection.BeginTransactionAsync())
        {
            await InsertMinimalRowAsync(connection, 1, "rolled back", rollback);
            await rollback.RollbackAsync();
        }

        Assert.Equal(0L, await CountRowsAsync(connection));

        await using (var commit = await connection.BeginTransactionAsync())
        {
            await InsertMinimalRowAsync(connection, 2, "committed", commit);
            await commit.CommitAsync();
        }

        Assert.Equal(1L, await CountRowsAsync(connection));
        var row = Assert.Single(await connection.Query("SELECT id, text_value FROM db_patterns").ReadAsync());
        Assert.Equal(2L, row["id"]);
        Assert.Equal("committed", row["text_value"]);
    }

    [Fact]
    public async Task RejectsATransactionOwnedByAnotherConnectionBeforeExecutingSql()
    {
        await using var first = await CreateOpenDatabaseAsync();
        await using var second = await CreateOpenDatabaseAsync();
        await using var transaction = await second.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => first.Query("SELECT 1", transaction).ReadAsync());

        Assert.Equal("transaction", exception.ParamName);
    }

    [Fact]
    public async Task ClosedConnectionsAreClosedAgainAfterSuccessFailureAndCancellation()
    {
        await using (var successful = new SqliteConnection("Data Source=:memory:"))
        {
            var rows = await successful.Query("SELECT 1 AS value").ReadAsync();
            Assert.Equal(1L, Assert.Single(rows)["value"]);
            Assert.Equal(ConnectionState.Closed, successful.State);
        }

        await using (var failing = new SqliteConnection("Data Source=:memory:"))
        {
            await Assert.ThrowsAsync<SqliteException>(
                () => failing.Query("SELECT * FROM missing_table").ReadAsync());
            Assert.Equal(ConnectionState.Closed, failing.State);
        }

        await using (var cancelled = new SqliteConnection("Data Source=:memory:"))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelled.Query("SELECT 1").ReadAsync(cancellation.Token));
            Assert.Equal(ConnectionState.Closed, cancelled.State);
        }
    }

    [Fact]
    public async Task ClosedConnectionsAreClosedWhenBindingOrMaterializationFails()
    {
        await using (var bindingFailure = new SqliteConnection("Data Source=:memory:"))
        {
            var query = bindingFailure.Query("SELECT @value")
                .WithConfiguredParameter(
                    "@value",
                    1L,
                    DbType.Int64,
                    static _ => throw new InvalidOperationException("binding failed"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ReadAsync());
            Assert.Equal("binding failed", exception.Message);
            Assert.Equal(ConnectionState.Closed, bindingFailure.State);
        }

        await using (var materializationFailure = new SqliteConnection("Data Source=:memory:"))
        {
            var definition = new CobaltumQueryDefinition<long>(
                "SELECT 1",
                static _ => { },
                static _ => throw new InvalidOperationException("mapping failed"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => materializationFailure.Query(definition).ReadAsync());
            Assert.Equal("mapping failed", exception.Message);
            Assert.Equal(ConnectionState.Closed, materializationFailure.State);
        }
    }

    [Fact]
    public async Task RawRowsPreserveDuplicateNamesOrdinalsNullsAndCase()
    {
        await using var connection = await CreateOpenDatabaseAsync();

        var row = Assert.Single(await connection.Query(
            "SELECT 1 AS value, 2 AS value, NULL AS Value, '' AS empty_text").ReadAsync());

        Assert.Equal(new[] { "value", "value", "Value", "empty_text" }, row.ColumnNames);
        Assert.Equal(new object?[] { 1L, 2L }, row.GetValues("value"));
        Assert.Null(row["Value"]);
        Assert.Equal(string.Empty, row["empty_text"]);
        Assert.False(row.TryGetValue("value", out _));
        Assert.Throws<InvalidOperationException>(() => row["value"]);
        Assert.Equal(2L, row[1]);
    }

    [Fact]
    public async Task ColonPrefixedSqlBindsAnUnprefixedProviderParameterName()
    {
        await using var connection = await CreateOpenDatabaseAsync();

        var row = Assert.Single(await connection.Query("SELECT :value AS value")
            .WithParameter(":value", 42L, DbType.Int64)
            .ReadAsync());

        Assert.Equal(42L, row["value"]);
    }

    [Fact]
    public async Task MappedAndTypedQueriesMaterializeZeroOneAndManyRows()
    {
        await using var connection = await CreateOpenDatabaseAsync();
        await SeedRowsAsync(connection);

        var mapped = await connection.NoCheckQueryMapped(
                "SELECT id, text_value FROM db_patterns WHERE id >= @minimum ORDER BY id",
                static reader => new PatternRecord(reader.GetInt64(0), reader.GetString(1)))
            .WithParameter("@minimum", 2L, DbType.Int64)
            .ReadAsync();

        var definition = new CobaltumQueryDefinition<PatternRecord>(
            "SELECT id, text_value FROM db_patterns ORDER BY id",
            static _ => { },
            static reader => new PatternRecord(reader.GetInt64(0), reader.GetString(1)));
        var all = await connection.Query(definition).ReadAsync();
        var empty = await connection.Query(
                new CobaltumQueryDefinition<PatternRecord>(
                    "SELECT id, text_value FROM db_patterns WHERE 0",
                    static _ => { },
                    static reader => new PatternRecord(reader.GetInt64(0), reader.GetString(1))))
            .ReadAsync();

        Assert.Equal(new long[] { 2, 3 }, mapped.Select(row => row.Id).ToArray());
        Assert.Equal(new long[] { 1, 2, 3 }, all.Select(row => row.Id).ToArray());
        Assert.Empty(empty);
    }

    [Fact]
    public async Task ResultMappingMatchesReorderedSnakeCaseColumnsByMemberName()
    {
        await using var connection = await CreateOpenDatabaseAsync();

        var rows = await connection.NoCheckQueryMapped(
            "SELECT 'Ada' AS display_name, 7 AS user_id",
            static reader => new NamedPatternRecord(
                CobaltumResultReader.Read<long>(reader, "UserId", "NamedPatternRecord.UserId", false),
                CobaltumResultReader.Read<string>(reader, "DisplayName", "NamedPatternRecord.DisplayName", false)))
            .ReadAsync();

        Assert.Equal(new NamedPatternRecord(7, "Ada"), Assert.Single(rows));
    }

    [Fact]
    public async Task ResultMappingRejectsMissingAndAmbiguousNormalizedColumnNames()
    {
        await using var connection = await CreateOpenDatabaseAsync();

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.NoCheckQueryMapped(
                "SELECT 1 AS other_value",
                static reader => CobaltumResultReader.Read<long>(reader, "UserId", "Result.UserId", false))
                .ReadAsync());
        var ambiguous = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.NoCheckQueryMapped(
                "SELECT 1 AS user_id, 2 AS 'user-id'",
                static reader => CobaltumResultReader.Read<long>(reader, "UserId", "Result.UserId", false))
                .ReadAsync());

        Assert.Contains("No returned column", missing.Message, StringComparison.Ordinal);
        Assert.Contains("More than one returned column", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultMappingRejectsDatabaseNullAndWrongTypesForRequiredMembers()
    {
        await using var connection = await CreateOpenDatabaseAsync();

        var databaseNull = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.NoCheckQueryMapped(
                "SELECT NULL AS user_id",
                static reader => CobaltumResultReader.Read<long>(reader, "UserId", "Result.UserId", false))
                .ReadAsync());
        var wrongType = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.NoCheckQueryMapped(
                "SELECT 'not-a-guid' AS external_id",
                static reader => CobaltumResultReader.Read<Guid>(reader, "ExternalId", "Result.ExternalId", false))
                .ReadAsync());

        Assert.Contains("database null", databaseNull.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read", wrongType.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedPredicatesRemainParameterizedForHostileValuesAndNulls()
    {
        await using var connection = await CreateOpenDatabaseAsync();
        const string hostileText = "x' OR 1=1; DROP TABLE db_patterns; --";
        await InsertMinimalRowAsync(connection, 1, hostileText);
        await InsertMinimalRowAsync(connection, 2, "safe");
        await connection.Query(
                "UPDATE db_patterns SET nullable_text = @value WHERE id = 1")
            .WithParameter("@value", "present", DbType.String)
            .ExecuteAsync();
        var table = new PatternTable();

        var hostileQuery = table.Query()
            .Where(table.Text.Equal(hostileText))
            .Where(table.Id.Equal(1L));
        var nullQuery = table.Query().Where(table.NullableText.Equal(null));

        Assert.DoesNotContain(hostileText, hostileQuery.Sql, StringComparison.Ordinal);
        Assert.Equal(1L, Assert.Single(await connection.Query(hostileQuery).ReadAsync()).Id);
        Assert.Equal(2L, Assert.Single(await connection.Query(nullQuery).ReadAsync()).Id);
        Assert.Single(await connection.Query(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'db_patterns'").ReadAsync());
    }

    private static async Task<SqliteConnection> CreateOpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await connection.Query(
            "CREATE TABLE db_patterns (" +
            "id INTEGER PRIMARY KEY, " +
            "text_value TEXT NOT NULL, " +
            "nullable_text TEXT NULL, " +
            "real_value REAL NOT NULL, " +
            "blob_value BLOB NOT NULL)").ExecuteAsync();
        return connection;
    }

    private static async Task SeedRowsAsync(DbConnection connection)
    {
        await InsertMinimalRowAsync(connection, 1, "one", realValue: 1d);
        await InsertMinimalRowAsync(connection, 2, "two", realValue: 2d);
        await InsertMinimalRowAsync(connection, 3, "three", realValue: 3d);
    }

    private static Task<int> InsertMinimalRowAsync(
        DbConnection connection,
        long id,
        string text,
        DbTransaction? transaction = null,
        double realValue = 0d) =>
        connection.Query(
                "INSERT INTO db_patterns " +
                "(id, text_value, nullable_text, real_value, blob_value) " +
                "VALUES (@id, @text, NULL, @real, @blob)",
                transaction)
            .WithParameter("@id", id, DbType.Int64)
            .WithParameter("@text", text, DbType.String)
            .WithParameter("@real", realValue, DbType.Double)
            .WithParameter("@blob", Array.Empty<byte>(), DbType.Binary)
            .ExecuteAsync();

    private static async Task<long> CountRowsAsync(DbConnection connection)
    {
        var row = Assert.Single(await connection.Query("SELECT COUNT(*) AS count FROM db_patterns").ReadAsync());
        return Assert.IsType<long>(row["count"]);
    }

    private sealed record PatternRecord(long Id, string Text);

    private sealed record NamedPatternRecord(long UserId, string DisplayName);

    private sealed class PatternTable : CobaltumTable<PatternRecord>
    {
        internal PatternTable()
            : base(
                "SELECT id, text_value FROM db_patterns",
                static reader => new PatternRecord(reader.GetInt64(0), reader.GetString(1)))
        {
        }

        internal CobaltumColumn<PatternRecord, long> Id { get; } =
            new CobaltumColumn<PatternRecord, long>("id", DbType.Int64);

        internal CobaltumColumn<PatternRecord, string> Text { get; } =
            new CobaltumColumn<PatternRecord, string>("text_value", DbType.String);

        internal CobaltumColumn<PatternRecord, string?> NullableText { get; } =
            new CobaltumColumn<PatternRecord, string?>("nullable_text", DbType.String);
    }
}
