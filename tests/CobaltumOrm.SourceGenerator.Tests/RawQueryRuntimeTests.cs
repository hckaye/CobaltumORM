using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class RawQueryRuntimeTests
{
    [Fact]
    public async Task ReadsImmutableRowsWithParametersNullsAndDuplicateColumnNames()
    {
        using var cancellationSource = new CancellationTokenSource();
        var token = cancellationSource.Token;
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id", "name", "name" },
            new object?[] { 7, DBNull.Value, "second" });

        var query = connection
            .Query("SELECT id, first_name AS name, last_name AS name FROM users WHERE name = @name")
            .WithParameter("@name", null, DbType.String);
        var rows = await query.ReadAsync(token);

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "id", "name", "name" }, row.ColumnNames);
        Assert.Equal(3, row.FieldCount);
        Assert.Equal(7, row[0]);
        Assert.Equal(7, row["id"]);
        Assert.Equal(new object?[] { null, "second" }, row.GetValues("name"));
        Assert.False(row.TryGetValue("name", out _));
        Assert.False(row.TryGetValue("missing", out _));
        Assert.Throws<InvalidOperationException>(() => row["name"]);
        Assert.Equal(
            new[] { "id", "name", "name" },
            row.Select(item => item.Key));

        var command = Assert.Single(connection.Commands);
        Assert.Equal(query.Sql, command.CommandText);
        Assert.Equal(DBNull.Value, command.ParameterValues["@name"].Value);
        Assert.Equal(DbType.String, command.ParameterValues["@name"].DbType);
        Assert.Equal(token, command.CancellationTokenSeen);
        Assert.True(command.WasDisposed);
        Assert.True(Assert.Single(connection.Readers).IsClosed);
        Assert.Equal(token, Assert.Single(connection.OpenTokens));
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ExecutesNonQueryWithoutInterpolationAndLeavesOpenConnectionOpen()
    {
        using var cancellationSource = new CancellationTokenSource();
        var connection = new QueryFakeDbConnection();
        connection.Open();
        const string sql = "UPDATE users SET name = @name WHERE id = @id";
        var query = connection.Query(sql)
            .WithParameter("@name", "literal'; DROP TABLE users; --")
            .WithParameter("@id", 42, DbType.Int32);

        var affected = await query.ExecuteAsync(cancellationSource.Token);

        Assert.Equal(1, affected);
        var command = Assert.Single(connection.Commands);
        Assert.Equal(sql, command.CommandText);
        Assert.Equal("literal'; DROP TABLE users; --", command.ParameterValues["@name"].Value);
        Assert.Equal(42, command.ParameterValues["@id"].Value);
        Assert.Equal(DbType.Int32, command.ParameterValues["@id"].DbType);
        Assert.Equal(cancellationSource.Token, command.CancellationTokenSeen);
        Assert.True(command.WasDisposed);
        Assert.Empty(connection.OpenTokens);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task CommandDefinitionBindsParametersAndReturnsAffectedCount()
    {
        using var cancellationSource = new CancellationTokenSource();
        var connection = new QueryFakeDbConnection();
        const string sql = "UPDATE users SET name = @name WHERE id = @id";
        var definition = new CobaltumCommandDefinition<(int Id, string? Name)>(
            sql,
            (command, parameters) =>
            {
                CobaltumParameter.Add(command, "@name", parameters.Name, DbType.String);
                CobaltumParameter.Add(command, "@id", parameters.Id, DbType.Int32);
            });

        var affected = await connection.ExecuteAsync(definition, (42, null), null, cancellationSource.Token);

        Assert.Equal(1, affected);
        Assert.Equal(sql, definition.Sql);
        var command = Assert.Single(connection.Commands);
        Assert.Equal(sql, command.CommandText);
        Assert.Equal(DBNull.Value, command.ParameterValues["@name"].Value);
        Assert.Equal(DbType.String, command.ParameterValues["@name"].DbType);
        Assert.Equal(42, command.ParameterValues["@id"].Value);
        Assert.Equal(cancellationSource.Token, command.CancellationTokenSeen);
        Assert.True(command.WasDisposed);
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ClosesAConnectionWhenOpeningChangesStateAndThenFails()
    {
        var connection = new QueryFakeDbConnection
        {
            OpenExceptionAfterStateChange = new InvalidOperationException("open failed"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.Query("SELECT 1").ReadAsync());

        Assert.Equal("open failed", exception.Message);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(1, connection.CloseCount);
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task NoCheckQueryCreatesAnUntypedExecutableCommand()
    {
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "value" },
            new object?[] { 1 });
        const string sql = "SQL outside the compile-time parser subset";

        var rows = await connection.NoCheckQuery(sql).ReadAsync();

        Assert.Equal(1, Assert.Single(rows)[0]);
        Assert.Equal(sql, Assert.Single(connection.Commands).CommandText);
    }

    [Fact]
    public async Task RawQueryCanApplyAJsonbParameterType()
    {
        var connection = new QueryFakeDbConnection();
        var query = connection
            .Query("INSERT INTO documents (payload) VALUES (@payload)")
            .WithConfiguredParameter(
                "@payload",
                "{\"active\":true}",
                DbType.String,
                static parameter => ((QueryFakeDbParameter)parameter).DataTypeName = "jsonb");

        await query.ExecuteAsync();

        var parameter = Assert.IsType<QueryFakeDbParameter>(
            Assert.Single(connection.Commands).ParameterValues["@payload"]);
        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal("jsonb", parameter.DataTypeName);
    }

    [Fact]
    public void RejectsDuplicateParameterNamesWithoutChangingTheOriginalQuery()
    {
        var connection = new QueryFakeDbConnection();
        var original = connection.Query("SELECT @value");
        var parameterized = original.WithParameter("@value", 1);

        Assert.Throws<ArgumentException>(() => parameterized.WithParameter("@VALUE", 2));
        Assert.Equal(original.Sql, parameterized.Sql);
    }

    [Fact]
    public void RejectsEquivalentOracleParameterNamesBeforeExecution()
    {
        var connection = new QueryFakeDbConnection();
        var query = connection.Query("SELECT :value FROM dual").WithParameter(":value", 1);

        Assert.Throws<ArgumentException>(() => query.WithParameter("value", 2));
        Assert.Throws<ArgumentException>(() => connection.Query("SELECT :").WithParameter(":", 1));
    }

    [Fact]
    public void ParameterHelperValidatesNamesAndNormalizesOraclePrefixes()
    {
        var command = new QueryFakeDbCommand(new QueryFakeDbConnection());

        Assert.Throws<ArgumentException>(() => CobaltumParameter.Add(command, "", 1));
        Assert.Throws<ArgumentException>(() => CobaltumParameter.Add(command, ":", 1));
        CobaltumParameter.Add(command, ":id", 7, DbType.Int32);

        Assert.Equal(7, command.ParameterValues["id"].Value);
    }

    [Fact]
    public async Task TypedQueryUsesTheSameClosedConnectionLifecycle()
    {
        using var cancellationSource = new CancellationTokenSource();
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id" },
            new object?[] { 9 });
        var definition = new CobaltumQueryDefinition<int>(
            "SELECT id FROM users",
            _ => { },
            reader => reader.GetInt32(0));

        var rows = await connection.Query(
            definition,
            cancellationToken: cancellationSource.Token);

        Assert.Equal(9, Assert.Single(rows));
        Assert.Equal(cancellationSource.Token, Assert.Single(connection.OpenTokens));
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.True(Assert.Single(connection.Commands).WasDisposed);
        Assert.True(Assert.Single(connection.Readers).IsClosed);
    }

    [Fact]
    public async Task CheckedQueryKeepsInterpolatedValueOutOfCommandText()
    {
        const string sql = "SELECT id FROM users WHERE name = @__cobaltum_value_0";
        const string hostileValue = "x'; DROP TABLE users; --";
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id" },
            new object?[] { 7 });
        var definition = new CobaltumQueryDefinition<int>(
            sql,
            command => CobaltumParameter.Add(
                command,
                "@__cobaltum_value_0",
                hostileValue,
                DbType.String),
            reader => reader.GetInt32(0));
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var cancellationSource = new CancellationTokenSource();

        var rows = await CobaltumQueryExtensions
            .QueryChecked(connection, definition, transaction)
            .ReadAsync(cancellationSource.Token);

        Assert.Equal(7, Assert.Single(rows));
        var command = Assert.Single(connection.Commands);
        Assert.Equal(sql, command.CommandText);
        Assert.DoesNotContain(hostileValue, command.CommandText, StringComparison.Ordinal);
        Assert.Equal(hostileValue, command.ParameterValues["@__cobaltum_value_0"].Value);
        Assert.Equal(DbType.String, command.ParameterValues["@__cobaltum_value_0"].DbType);
        Assert.Same(transaction, command.TransactionSeen);
        Assert.Equal(cancellationSource.Token, command.CancellationTokenSeen);
        Assert.Equal(0, connection.CloseCount);
    }

    [Fact]
    public async Task CheckedNamedParametersEnforceAnalyzedNameAndType()
    {
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id" },
            new object?[] { 9 });
        var definition = new CobaltumQueryDefinition<int>(
            "SELECT id FROM users WHERE id = @id",
            _ => { },
            reader => reader.GetInt32(0));
        var query = CobaltumQueryExtensions.QueryChecked(
            connection,
            definition,
            null,
            new CobaltumExpectedParameter("@id", DbType.Int32));

        Assert.Throws<ArgumentException>(() => query.WithParameter("@missing", 1));
        Assert.Throws<ArgumentException>(() => query.WithParameter("@id", "1"));
        Assert.Throws<ArgumentException>(() => query.WithParameter("@id", 1, DbType.String));
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.ReadAsync());

        var rows = await query.WithParameter("@id", 9).ReadAsync();

        Assert.Equal(9, Assert.Single(rows));
        Assert.Equal(9, Assert.Single(connection.Commands).ParameterValues["@id"].Value);
    }

    [Fact]
    public async Task CheckedJsonParameterAppliesTheDatabaseTypeName()
    {
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "payload" },
            new object?[] { "{\"active\":true}" });
        var definition = new CobaltumQueryDefinition<string>(
            "SELECT payload FROM documents WHERE payload = @payload",
            _ => { },
            reader => reader.GetString(0));
        var query = CobaltumQueryExtensions.QueryChecked(
            connection,
            definition,
            null,
            new CobaltumExpectedParameter(
                "@payload",
                DbType.String,
                "jsonb",
                static parameter => ((QueryFakeDbParameter)parameter).DataTypeName = "jsonb"));

        var rows = await query.WithParameter("@payload", "{\"active\":true}").ReadAsync();

        Assert.Equal("{\"active\":true}", Assert.Single(rows));
        var parameter = Assert.IsType<QueryFakeDbParameter>(
            Assert.Single(connection.Commands).ParameterValues["@payload"]);
        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal("jsonb", parameter.DataTypeName);
    }

    [Fact]
    public void DateTime2AcceptsAndBindsDateTimeValues()
    {
        var connection = new QueryFakeDbConnection();
        var definition = new CobaltumQueryDefinition<int>(
            "SELECT 1 WHERE @created_at IS NOT NULL",
            _ => { },
            reader => reader.GetInt32(0));
        var query = CobaltumQueryExtensions.QueryChecked(
            connection,
            definition,
            null,
            new CobaltumExpectedParameter("@created_at", DbType.DateTime2));
        var value = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Unspecified);

        _ = query.WithParameter("@created_at", value);

        var command = new QueryFakeDbCommand(new QueryFakeDbConnection());
        CobaltumParameter.Add(command, "@created_at", value, DbType.DateTime2);

        Assert.Equal(DbType.DateTime2, command.ParameterValues["@created_at"].DbType);
    }

    [Fact]
    public async Task QueryChainAddsConditionalPredicatesWithUniqueParameters()
    {
        var table = new FilterTable();
        var original = table.Query();
        var trueFactoryCalls = 0;
        var falseFactoryCalls = 0;
        var query = original
            .Where(table.Id.Equal(7))
            .WhereIf(true, () =>
            {
                trueFactoryCalls++;
                return table.Name.Equal("alice");
            })
            .WhereIf(false, () =>
            {
                falseFactoryCalls++;
                return table.Name.Equal("ignored");
            });

        Assert.Equal(1, trueFactoryCalls);
        Assert.Equal(0, falseFactoryCalls);
        Assert.Equal(
            "SELECT \"id\", \"name\" FROM \"users\" WHERE \"id\" = @__cobaltum_where_0 AND \"name\" = @__cobaltum_where_1",
            query.Sql);
        Assert.Equal("SELECT \"id\", \"name\" FROM \"users\"", original.Sql);
        Assert.NotSame(original, query);

        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id", "name" },
            new object?[] { 7, "alice" });
        var rows = await connection.Query(query);

        Assert.Equal(7, Assert.Single(rows).Id);
        var command = Assert.Single(connection.Commands);
        Assert.Equal(2, command.ParameterValues.Count);
        Assert.Equal(7, command.ParameterValues["@__cobaltum_where_0"].Value);
        Assert.Equal("alice", command.ParameterValues["@__cobaltum_where_1"].Value);
    }

    [Fact]
    public async Task OracleQueryChainsUseColonSqlAndUnprefixedProviderParameterNames()
    {
        var id = new CobaltumColumn<FilterRecord, int>("\"ID\"", DbType.Int32, ':');
        var name = new CobaltumColumn<FilterRecord, string?>("\"NAME\"", DbType.String, ':');
        var definition = new CobaltumQueryDefinition<FilterRecord>(
            "SELECT \"ID\", \"NAME\" FROM \"USERS\"",
            static _ => { },
            static reader => new FilterRecord(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        var query = definition.Where(id.Equal(7)).Where(name.Equal("alice"));
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "ID", "NAME" },
            new object?[] { 7, "alice" });

        var rows = await connection.Query(query);

        Assert.Equal(7, Assert.Single(rows).Id);
        Assert.Equal(
            "SELECT \"ID\", \"NAME\" FROM \"USERS\" " +
            "WHERE \"ID\" = :__cobaltum_where_0 AND \"NAME\" = :__cobaltum_where_1",
            Assert.Single(connection.Commands).CommandText);
        var parameters = Assert.Single(connection.Commands).ParameterValues;
        Assert.Equal(7, parameters["__cobaltum_where_0"].Value);
        Assert.Equal("alice", parameters["__cobaltum_where_1"].Value);
    }

    [Fact]
    public async Task NullEqualityUsesIsNullWithoutAddingAParameter()
    {
        var table = new FilterTable();
        var query = table.Query().Where(table.Name.Equal(null));

        Assert.Equal(
            "SELECT \"id\", \"name\" FROM \"users\" WHERE \"name\" IS NULL",
            query.Sql);

        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id", "name" },
            new object?[] { 7, DBNull.Value });
        var rows = await connection.Query(query);

        Assert.Equal(7, Assert.Single(rows).Id);
        Assert.Empty(Assert.Single(connection.Commands).ParameterValues);
    }

    [Fact]
    public void QueryDefinitionsAreImmutableAndCanBeReused()
    {
        var table = new FilterTable();
        var original = table.Query();
        var first = original.Where(table.Id.Equal(7));
        var second = original.Where(table.Id.Equal(8));

        Assert.Equal("SELECT \"id\", \"name\" FROM \"users\"", original.Sql);
        Assert.Equal(
            "SELECT \"id\", \"name\" FROM \"users\" WHERE \"id\" = @__cobaltum_where_0",
            first.Sql);
        Assert.Equal(first.Sql, second.Sql);
        Assert.NotSame(first, second);
    }

    private sealed record FilterRecord(int Id, string? Name);

    private sealed class FilterTable : CobaltumTable<FilterRecord>
    {
        internal FilterTable()
            : base("SELECT \"id\", \"name\" FROM \"users\"", Materialize)
        {
        }

        internal CobaltumColumn<FilterRecord, int> Id { get; } =
            new CobaltumColumn<FilterRecord, int>("\"id\"");

        internal CobaltumColumn<FilterRecord, string?> Name { get; } =
            new CobaltumColumn<FilterRecord, string?>("\"name\"");

        private static FilterRecord Materialize(DbDataReader reader) =>
            new FilterRecord(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
