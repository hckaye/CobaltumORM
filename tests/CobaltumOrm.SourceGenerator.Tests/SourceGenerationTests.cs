using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class SourceGenerationTests
{
    private const string ConsumerSource = """
        using System.Data.Common;
        using System.Threading;
        using System.Threading.Tasks;
        using CobaltumOrm;
        using CobaltumOrm.Migrations;

        namespace TestApp;

        [Migration(20, "add display name")]
        public sealed class AddDisplayName : Migration
        {
            public override void Up()
            {
                Alter.Table("users").AddColumn("display-name").AsString(80).Nullable().InSchema("accounts");
            }

            public override void Down()
            {
                Delete.Column("display-name").FromTable("users").InSchema("accounts");
            }
        }

        [Query("ById", "SELECT id, \"display-name\", created_at FROM accounts.users WHERE id = @id")]
        [Query("ByName", "SELECT id, \"display-name\", created_at FROM accounts.users WHERE \"display-name\" = @name")]
        [Query("AllUsers", "SELECT id, \"display-name\", created_at FROM accounts.users")]
        public static partial class Queries
        {
        }

        public static class Consumer
        {
            public static async Task<object?> Run(
                DbConnection connection,
                DbTransaction transaction,
                CancellationToken cancellationToken)
            {
                var raw = connection.Query("SELECT id FROM accounts.users");
                CobaltumRawQuery rawContract = raw;
                var rows = await Queries.ByIdAsync(connection, 7, transaction, cancellationToken);
                var tableRows = await connection.Query(
                    TestApp.Generated.Tables.Users.Where(TestApp.Generated.Tables.Users.Id.Equal(7)),
                    transaction,
                    cancellationToken);
                var nullableRows = await Queries.ByNameAsync(connection, null, transaction, cancellationToken);
                return rows[0].DisplayName ?? tableRows[0].DisplayName ?? nullableRows[0].DisplayName;
            }
        }
        """;

    [Fact]
    public async Task CompilesAndExecutesGeneratedConsumerWithParametersAndNullMaterialization()
    {
        var generation = GeneratorTestHost.Run(
            ConsumerSource,
            new[]
            {
                ("/migrations/V30__add_created_at.sql", "ALTER TABLE accounts.users ADD COLUMN created_at timestamptz NOT NULL;"),
                ("/migrations/V10__create_users.sql", "CREATE TABLE accounts.users (id integer PRIMARY KEY);")
            });

        AssertNoErrors(generation);
        Assert.Contains("record AccountsUsersRow", generation.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record ByIdResult", generation.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("CobaltumColumn(\"display-name\", \"character varying(80)\", true", generation.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("ForwardOnlyMigration", generation.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("public static class CobaltumMigrationCatalog", generation.GeneratedText, StringComparison.Ordinal);

        var assembly = generation.EmitAndLoad();
        var catalogType = assembly.GetType("TestApp.Generated.CobaltumMigrationCatalog", throwOnError: true)!;
        var migrationTypes = Assert.IsAssignableFrom<IReadOnlyList<MigrationInfo>>(
            catalogType.GetProperty("All")!.GetValue(null));
        Assert.Equal(new long[] { 10, 20, 30 }, migrationTypes.Select(item => item.Version));
        Assert.True(migrationTypes.Single(item => item.Version == 10).IsForwardOnly);
        Assert.False(migrationTypes.Single(item => item.Version == 20).IsForwardOnly);

        using var cancellationSource = new CancellationTokenSource();
        var connection = new QueryFakeDbConnection(
            new object?[] { 7, DBNull.Value, new DateTimeOffset(2026, 8, 9, 1, 2, 3, TimeSpan.Zero) },
            new object?[] { 7, DBNull.Value, new DateTimeOffset(2026, 8, 9, 1, 2, 3, TimeSpan.Zero) },
            new object?[] { 7, DBNull.Value, new DateTimeOffset(2026, 8, 9, 1, 2, 3, TimeSpan.Zero) });
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var consumer = assembly.GetType("TestApp.Consumer", throwOnError: true)!;
        var task = Assert.IsAssignableFrom<Task<object?>>(consumer.GetMethod("Run")!.Invoke(
            null,
            new object[] { connection, transaction, cancellationSource.Token }));

        Assert.Null(await task);
        Assert.Equal(3, connection.Commands.Count);
        Assert.All(connection.Commands, command =>
        {
            Assert.True(command.WasDisposed);
            Assert.Same(transaction, command.TransactionSeen);
            Assert.Equal(cancellationSource.Token, command.CancellationTokenSeen);
        });
        Assert.Equal(7, connection.Commands[0].ParameterValues["@id"].Value);
        Assert.Equal(DbType.Int32, connection.Commands[0].ParameterValues["@id"].DbType);
        Assert.Equal(7, connection.Commands[1].ParameterValues["value"].Value);
        Assert.Equal(DBNull.Value, connection.Commands[2].ParameterValues["@name"].Value);
        Assert.Equal(DbType.String, connection.Commands[2].ParameterValues["@name"].DbType);
        Assert.All(connection.Readers, reader => Assert.True(reader.IsClosed));
    }

    [Fact]
    public async Task GenericQueryAttributeUsesTheDeclaredResultType()
    {
        const string source = """
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            namespace TestApp;

            public sealed record UserView(int Id, string? DisplayName);

            [Query<UserView>("All", "SELECT id, display_name FROM users")]
            public static partial class UserQueries
            {
            }

            public static class Consumer
            {
                public static async Task<UserView> Run(DbConnection connection)
                {
                    var rows = await UserQueries.AllAsync(connection);
                    return rows[0];
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL, display_name text);")
            });

        AssertNoErrors(result);
        Assert.Contains(
            "CobaltumQueryDefinition<AllParameters, global::TestApp.UserView>",
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("record AllResult", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", result.GeneratedText, StringComparison.Ordinal);

        var assembly = result.EmitAndLoad();
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id", "display_name" },
            new object?[] { 7, "alice" });
        var consumer = assembly.GetType("TestApp.Consumer", throwOnError: true)!;
        var task = Assert.IsAssignableFrom<Task>(consumer.GetMethod("Run")!.Invoke(
            null,
            new object[] { connection }));
        await task;
        var value = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal(7, value.GetType().GetProperty("Id")!.GetValue(value));
        Assert.Equal("alice", value.GetType().GetProperty("DisplayName")!.GetValue(value));
    }

    [Fact]
    public void GenericQueryAttributeReportsAnIncompatibleResultType()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            public sealed record UserView(string Id, string Name);

            [Query<UserView>("All", "SELECT id, name FROM users")]
            public static partial class UserQueries
            {
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL, name text NOT NULL);")
            });

        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB009");
        var message = diagnostic.GetMessage();
        Assert.Contains("returned column 'id' has CLR type 'int'", message, StringComparison.Ordinal);
        Assert.Contains("parameter 'Id' of type 'string'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("record AllResult", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericQueryAttributeReportsAMissingResultColumnByName()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            public sealed record UserView(int Id, [ResultColumn("display_name")] string Name);

            [Query<UserView>("All", "SELECT id, name FROM users")]
            public static partial class UserQueries
            {
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL, name text NOT NULL);")
            });

        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB009");
        Assert.Contains(
            "parameter 'Name' expects a column named 'display_name', but no such column is returned",
            diagnostic.GetMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAttributeGeneratesCommandsForStatementsWithoutRows()
    {
        const string source = """
            using System.Data.Common;
            using System.Threading;
            using System.Threading.Tasks;
            using CobaltumOrm;

            namespace TestApp;

            [Query("RenameUser", "UPDATE users SET name = @name WHERE id = @id")]
            [Query("DeleteUser", "DELETE FROM users WHERE id = @id")]
            [Query("ClearUsers", "TRUNCATE TABLE users")]
            public static partial class UserCommands
            {
            }

            public static class Consumer
            {
                public static async Task<int> Run(
                    DbConnection connection,
                    CancellationToken cancellationToken)
                {
                    var renamed = await UserCommands.RenameUserAsync(connection, "alice", 7, cancellationToken: cancellationToken);
                    var deleted = await UserCommands.DeleteUserAsync(connection, 7, cancellationToken: cancellationToken);
                    var cleared = await UserCommands.ClearUsersAsync(connection, cancellationToken: cancellationToken);
                    return renamed + deleted + cleared;
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL, name text NOT NULL);")
            });

        AssertNoErrors(result);
        Assert.Contains(
            "CobaltumCommandDefinition<RenameUserParameters>",
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.Contains("Task<int> RenameUserAsync(", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("record RenameUserResult", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("record ClearUsersResult", result.GeneratedText, StringComparison.Ordinal);

        var assembly = result.EmitAndLoad();
        using var cancellationSource = new CancellationTokenSource();
        var connection = new QueryFakeDbConnection();
        connection.Open();
        var consumer = assembly.GetType("TestApp.Consumer", throwOnError: true)!;
        var task = Assert.IsAssignableFrom<Task<int>>(consumer.GetMethod("Run")!.Invoke(
            null,
            new object[] { connection, cancellationSource.Token }));

        Assert.Equal(3, await task);
        Assert.Equal(3, connection.Commands.Count);
        Assert.Equal("UPDATE users SET name = @name WHERE id = @id", connection.Commands[0].CommandText);
        Assert.Equal("alice", connection.Commands[0].ParameterValues["@name"].Value);
        Assert.Equal(7, connection.Commands[0].ParameterValues["@id"].Value);
        Assert.Equal("DELETE FROM users WHERE id = @id", connection.Commands[1].CommandText);
        Assert.Equal("TRUNCATE TABLE users", connection.Commands[2].CommandText);
        Assert.All(connection.Commands, command => Assert.True(command.WasDisposed));
    }

    [Fact]
    public void TypedQueryAttributeRequiresARowReturningStatement()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            [Query<int>("DeleteAll", "DELETE FROM users")]
            public static partial class UserCommands
            {
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL);")
            });

        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB009");
        Assert.Contains("does not return rows", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAllAsync", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericQueryAttributeMapsASingleAggregateColumnToAScalar()
    {
        const string source = """
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            namespace TestApp;

            [Query<long>("CountUsers", "SELECT COUNT(*) AS total FROM users")]
            public static partial class UserQueries
            {
            }

            public static class Consumer
            {
                public static async Task<long> Run(DbConnection connection)
                {
                    var counts = await UserQueries.CountUsersAsync(connection);
                    return counts[0];
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL);")
            });

        AssertNoErrors(result);
        Assert.Contains(
            "CobaltumQueryDefinition<CountUsersParameters, long>",
            result.GeneratedText,
            StringComparison.Ordinal);

        var assembly = result.EmitAndLoad();
        var connection = QueryFakeDbConnection.WithColumns(new[] { "total" }, new object?[] { 41L });
        var consumer = assembly.GetType("TestApp.Consumer", throwOnError: true)!;
        var task = Assert.IsAssignableFrom<Task<long>>(consumer.GetMethod("Run")!.Invoke(
            null,
            new object[] { connection }));
        Assert.Equal(41L, await task);
    }

    [Fact]
    public async Task ExplicitResultAttributesConfigureColumnsAndGeneratedHandlers()
    {
        const string source = """
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            namespace TestApp;

            public sealed class TextIdHandler : IValueHandler<int>
            {
                public int Read(DbDataReader reader, int ordinal) =>
                    int.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);
            }

            public sealed record ExternalUser(
                [ResultColumn("external_id"), ValueHandler<TextIdHandler>] int Id,
                [ResultColumn] string Name);

            [ResultHandler<UserCountHandler>]
            public sealed record UserCount(int Value);

            public sealed class UserCountHandler : IResultHandler<UserCount>
            {
                public UserCount Read(DbDataReader reader) => new UserCount(reader.GetInt32(0));
            }

            [Query<ExternalUser>("External", "SELECT external_id, name FROM users")]
            [Query<UserCount>("Count", "SELECT COUNT(*) AS total FROM users")]
            public static partial class UserQueries
            {
            }

            public static class Consumer
            {
                public static async Task<int> Run(DbConnection connection)
                {
                    var users = await UserQueries.ExternalAsync(connection);
                    var count = await UserQueries.CountAsync(connection);
                    return users[0].Id + count[0].Value;
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (external_id text NOT NULL, name text NOT NULL);")
            });

        AssertNoErrors(result);
        Assert.Contains(
            "CobaltumHandlerCache<global::TestApp.TextIdHandler>.Instance.Read(reader, 0)",
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CobaltumHandlerCache<global::TestApp.UserCountHandler>.Instance.Read(reader)",
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", result.GeneratedText, StringComparison.Ordinal);

        var assembly = result.EmitAndLoad();
        var connection = new QueryFakeDbConnection(
            new object?[] { "7", "alice" },
            new object?[] { 3 });
        var consumer = assembly.GetType("TestApp.Consumer", throwOnError: true)!;
        var task = Assert.IsAssignableFrom<Task<int>>(consumer.GetMethod("Run")!.Invoke(
            null,
            new object[] { connection }));
        Assert.Equal(10, await task);
    }

    [Fact]
    public async Task ConversionHandlersMapArrayElementsAndWholeArrays()
    {
        const string source = """
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            namespace TestApp;

            public readonly record struct CustomInt(int Value);

            public sealed record CustomIntArray(int[] Values);

            public sealed class CustomIntHandler : IValueHandler<int, CustomInt>
            {
                public CustomInt Convert(int value) => new CustomInt(value * 10);
            }

            public sealed class CustomIntArrayHandler : IValueHandler<int[], CustomIntArray>
            {
                public CustomIntArray Convert(int[] values) => new CustomIntArray(values);
            }

            public sealed record ArrayProjection(
                [ResultColumn("single"), ValueHandler<CustomIntHandler>] CustomInt Single,
                [ResultColumn("elements"), ValueHandler<CustomIntHandler>] CustomInt[] Elements,
                [ResultColumn("nullable_elements"), ValueHandler<CustomIntHandler>] CustomInt[]? NullableElements,
                [ResultColumn("wrapped"), ValueHandler<CustomIntArrayHandler>] CustomIntArray Wrapped);

            [Query<ArrayProjection>(
                "ReadArrays",
                "SELECT id AS single, numbers AS elements, optional_numbers AS nullable_elements, " +
                "numbers AS wrapped FROM array_values")]
            public static partial class ArrayQueries
            {
            }

            public static class Consumer
            {
                public static async Task<int> Run(DbConnection connection)
                {
                    var rows = await ArrayQueries.ReadArraysAsync(connection);
                    return rows[0].Single.Value +
                        rows[0].Elements[0].Value + rows[0].Elements[1].Value +
                        rows[0].Wrapped.Values[0] + rows[0].Wrapped.Values[1] +
                        (rows[0].NullableElements is null ? 100 : 0);
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__arrays.sql", "CREATE TABLE array_values (id integer NOT NULL, numbers integer[] NOT NULL, optional_numbers integer[]);")
            });

        AssertNoErrors(result);
        Assert.Contains(
            "CobaltumHandlerCache<global::TestApp.CustomIntHandler>.Instance.Convert(",
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CobaltumArrayHandler.Convert<",
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CobaltumArrayHandler.ConvertNullable<",
            result.GeneratedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "CobaltumHandlerCache<global::TestApp.CustomIntArrayHandler>.Instance.Convert(",
            result.GeneratedText,
            StringComparison.Ordinal);

        var assembly = result.EmitAndLoad();
        var connection = new QueryFakeDbConnection(
            new object?[] { 5, new[] { 1, 2 }, System.DBNull.Value, new[] { 3, 4 } });
        var consumer = assembly.GetType("TestApp.Consumer", throwOnError: true)!;
        var task = Assert.IsAssignableFrom<Task<int>>(consumer.GetMethod("Run")!.Invoke(
            null,
            new object[] { connection }));
        Assert.Equal(187, await task);
    }

    [Fact]
    public void RejectsConversionHandlerWithAnIncompatibleSourceType()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            public readonly record struct CustomInt(int Value);

            public sealed class WrongHandler : IValueHandler<string, CustomInt>
            {
                public CustomInt Convert(string value) => new CustomInt(value.Length);
            }

            public sealed record Projection(
                [ValueHandler<WrongHandler>] CustomInt Id);

            [Query<Projection>("Read", "SELECT id FROM users")]
            public static partial class Queries
            {
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL);")
            });

        Assert.Contains(result.AllDiagnostics, diagnostic =>
            diagnostic.Id == "COB009" &&
            diagnostic.GetMessage().Contains("cannot convert", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidExplicitResultHandlerFailsAtBuildTime()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            [ResultHandler<WrongHandler>]
            public sealed record UserView(int Id);

            public sealed class WrongHandler
            {
            }

            [Query<UserView>("All", "SELECT id FROM users")]
            public static partial class UserQueries
            {
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[] { ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL);") });

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "COB009");
        Assert.Contains(
            result.AllDiagnostics,
            diagnostic => diagnostic.GetMessage().Contains("IResultHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedTablesExposeTheTypedQueryChainAndReserveQueryColumnNames()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            public static class Consumer
            {
                public static CobaltumQueryDefinition<Generated.UsersRow> Build(
                    bool includeName,
                    string? name)
                {
                    return Generated.Tables.Users
                        .Query()
                        .Where(Generated.Tables.Users.Id.Equal(7))
                        .WhereIf(includeName, () => Generated.Tables.Users.Name.Equal(name));
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (query integer, id integer, name text);")
            });

        AssertNoErrors(result);
        Assert.Contains("Query_2", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesPostgreSqlParameterAndNumericResultTypes()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            [Query("AtLocalTime", "SELECT id FROM events WHERE local_time = @local_time")]
            [Query("ByDocument", "SELECT id FROM events WHERE document = @document")]
            [Query("ByDuration", "SELECT id FROM events WHERE duration = @duration")]
            [Query("LargeLiteral", "SELECT 2147483648 AS value")]
            [Query("BigintTotal", "SELECT SUM(big_id) AS value FROM events")]
            public static partial class Queries
            {
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__events.sql", "CREATE TABLE events (id integer NOT NULL, local_time timestamp without time zone NOT NULL, document jsonb NOT NULL, duration interval NOT NULL, big_id bigint NOT NULL);")
            });

        AssertNoErrors(result);
        Assert.Contains("DbType.DateTime2", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("DbType.Time", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.TimeSpan? duration", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("DataTypeName = \"interval\"", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("DbType.String, \"jsonb\"", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record LargeLiteralResult(\n        global::System.Int64 Value)", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record BigintTotalResult(\n        global::System.Decimal? Value)", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("CobaltumParameter.AddConfigured", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("((global::Npgsql.NpgsqlParameter)parameter).DataTypeName = \"jsonb\"", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("CobaltumColumn<EventsRow, global::System.String>(\"\\\"document\\\"\", global::System.Data.DbType.String, \"jsonb\", static parameter", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesPostgreSqlArrayParametersAndResultReaders()
    {
        const string source = """
            using CobaltumOrm;

            namespace TestApp;

            [Query(
                "FindArrays",
                "SELECT numbers, labels, identifiers FROM array_values WHERE numbers @> @required AND @value = ANY(numbers)")]
            public static partial class Queries
            {
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__arrays.sql", "CREATE TABLE array_values (numbers integer[] NOT NULL, labels text[], identifiers uuid[] NOT NULL);")
            });

        AssertNoErrors(result);
        Assert.Contains("global::System.Int32[] Numbers", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.String[]? Labels", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.Guid[] Identifiers", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.Int32[]? Required", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("DbType.Object", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("DataTypeName = \"integer[]\"", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("reader.GetFieldValue<global::System.Int32[]>", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("reader.GetFieldValue<global::System.String[]>", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("CobaltumColumn<ArrayValuesRow, global::System.Int32[]>", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesTypedMethodsForPostgreSqlReturningCommands()
    {
        const string source = """"
            using CobaltumOrm;

            namespace TestApp;

            [Query("UpsertUser", """
                INSERT INTO users (id, name)
                VALUES (@id, @name)
                ON CONFLICT (id) DO UPDATE SET name = excluded.name
                RETURNING id, name
                """)]
            [Query("Labels", "VALUES (1, 'one'), (2, 'two')")]
            public static partial class Queries
            {
            }
            """";
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer PRIMARY KEY, name text NOT NULL);")
            });

        AssertNoErrors(result);
        Assert.Contains("record UpsertUserResult", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("UpsertUserAsync", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record LabelsResult", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.Int32 Column1", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.Int32? id", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.String? name", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTableQueryChainExecutesThroughTypedQueryPath()
    {
        const string source = """
            using System.Data.Common;
            using System.Threading.Tasks;
            using CobaltumOrm;

            namespace TestApp;

            public static class Consumer
            {
                public static async Task<int> Run(DbConnection connection)
                {
                    var query = Generated.Tables.Users
                        .Query()
                        .Where(Generated.Tables.Users.Id.Equal(7))
                        .WhereIf(true, () => Generated.Tables.Users.Name.Equal("alice"));
                    var rows = await connection.Query(query);
                    return rows.Count;
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[]
            {
                ("/db/V1__users.sql", "CREATE TABLE users (id integer, name text);")
            });

        AssertNoErrors(result);
        var assembly = result.EmitAndLoad();
        var connection = QueryFakeDbConnection.WithColumns(
            new[] { "id", "name" },
            new object?[] { 7, "alice" });
        var consumer = assembly.GetType("TestApp.Consumer", throwOnError: true)!;
        var task = Assert.IsAssignableFrom<Task<int>>(consumer.GetMethod("Run")!.Invoke(
            null,
            new object[] { connection }));

        Assert.Equal(1, await task);
        var command = Assert.Single(connection.Commands);
        Assert.Equal(
            "SELECT \"id\", \"name\" FROM \"users\" WHERE \"id\" = @__cobaltum_where_0 AND \"name\" = @__cobaltum_where_1",
            command.CommandText);
        Assert.Equal(7, command.ParameterValues["@__cobaltum_where_0"].Value);
        Assert.Equal("alice", command.ParameterValues["@__cobaltum_where_1"].Value);
    }

    [Fact]
    public void ReportsInvalidSqlAtTheAdditionalFileLocation()
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[] { ("/db/V1__broken.sql", "CREATE TABLE broken (id mystery);") });

        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB003");
        Assert.Equal("/db/V1__broken.sql", diagnostic.Location.GetLineSpan().Path);
        Assert.True(diagnostic.Location.SourceSpan.Length > 0);
    }

    [Fact]
    public void DiagnosesDynamicMigrationArgumentsInsteadOfGuessing()
    {
        const string source = """
            using CobaltumOrm.Migrations;
            [Migration(1)]
            public sealed class DynamicMigration : Migration
            {
                private static string Name => "users";
                public override void Up() => Create.Table(Name).WithColumn("id").AsInt32();
                public override void Down() { }
            }
            """;

        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB002");
        Assert.Equal("Consumer.cs", diagnostic.Location.GetLineSpan().Path);
        Assert.DoesNotContain(result.AllDiagnostics, item => item.Id == "COB001");
    }

    [Fact]
    public void RequiresAttributedMigrationTypesToUseTheRuntimeMigrationBase()
    {
        const string source = """
            using CobaltumOrm.Migrations;
            [Migration(1)]
            public sealed class NotAMigration
            {
                public void Up() { }
            }
            """;

        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB006");
        Assert.Contains("must derive", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosesInvalidQueryAndGeneratedNameCollisions()
    {
        const string source = """
            using CobaltumOrm;
            [Query("by-id", "SELECT missing FROM users")]
            [Query("By_Id", "SELECT id FROM users")]
            public partial class Queries { }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[] { ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL);") });

        Assert.Contains(result.AllDiagnostics, item => item.Id == "COB004");
        Assert.Contains(result.AllDiagnostics, item => item.Id == "COB005");
    }

    [Fact]
    public void ValidatesDirectSqlNamesAndSyntaxInNamedQueries()
    {
        const string source = """
            using CobaltumOrm;
            [Query("BadSyntax", "SELECT id FORM app.users")]
            [Query("BadSchema", "SELECT id FROM audit.users")]
            [Query("BadColumn", "SELECT missing FROM app.users")]
            public partial class Queries { }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[] { ("/db/V1__users.sql", "CREATE TABLE app.users (id integer NOT NULL);") });

        var messages = result.AllDiagnostics
            .Where(item => item.Id == "COB004")
            .Select(item => item.GetMessage())
            .ToArray();
        Assert.Contains(messages, message => message.Contains("SQL100", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("SQL200", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("SQL203", StringComparison.Ordinal));
    }

    [Fact]
    public void AllocatesDeterministicCollisionSafeTableAndColumnNames()
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[]
            {
                ("/db/V1__collisions.sql", """
                    CREATE TABLE "thing-a" ("first-name" integer, first_name integer);
                    CREATE TABLE thing_a (id integer);
                    CREATE TABLE sales.items (id integer DEFAULT 5);
                    CREATE TABLE support.items (id integer);
                    CREATE TABLE tables (id integer);
                    CREATE TABLE schemas.reserved_names (columns integer);
                    """)
            });

        AssertNoErrors(result);
        Assert.Contains("record ThingARow(", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record ThingARow_2(", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("FirstName_2", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record SalesItemsRow(", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("record SupportItemsRow(", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("CobaltumColumn(\"id\", \"integer\", true, false, \"5\")", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("public const global::System.String Schemas_2", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("public static class Tables_2", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("public const global::System.String Columns_2", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesQuotedSqlNamesFromTheCurrentSchemaAfterRenames()
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[]
            {
                ("/db/V1__create_values.sql", "CREATE TABLE app.e2e_values (id integer NOT NULL, document jsonb NOT NULL);"),
                ("/db/V2__rename_values.sql", "ALTER TABLE app.e2e_values RENAME COLUMN document TO payload; ALTER TABLE app.e2e_values RENAME TO stored_values;")
            });

        AssertNoErrors(result);
        Assert.Contains("public static class SqlSchema", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("public const global::System.String App = \"\\\"app\\\"\";", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("public static class AppStoredValues", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("Name = \"\\\"app\\\".\\\"stored_values\\\"\";", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("Payload = \"\\\"payload\\\"\";", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("public static class AppE2eValues", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Document = \"\\\"payload\\\"\";", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackFromDateOnlyAndTimeOnlyForNetStandard20Consumers()
    {
        var result = GeneratorTestHost.Run(
            "namespace TestApp; public sealed class Empty { }",
            new[] { ("/db/V1__calendar.sql", "CREATE TABLE calendar (day date NOT NULL, at time NOT NULL);") },
            netStandard20: true);

        AssertNoErrors(result);
        Assert.Contains("global::System.DateTime Day", result.GeneratedText, StringComparison.Ordinal);
        Assert.Contains("global::System.TimeSpan At", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("global::System.DateOnly", result.GeneratedText, StringComparison.Ordinal);
        Assert.DoesNotContain("global::System.TimeOnly", result.GeneratedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatesRawLiteralQueryButKeepsItsDeclaredStaticType()
    {
        const string source = """
            using System.Data.Common;
            using CobaltumOrm;
            public static class RawUse
            {
                public static CobaltumRawQuery Read(DbConnection connection) =>
                    connection.Query("SELECT missing FROM users");
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[] { ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL);") });

        Assert.Contains(result.AllDiagnostics, item => item.Id == "COB004");
        Assert.DoesNotContain(result.Compilation.GetDiagnostics(), item => item.Id == "CS0029");
    }

    [Fact]
    public void ValidatesExtensionAndStaticRawQuerySyntaxAtTheSqlArgument()
    {
        const string source = """
            using System.Data.Common;
            using CobaltumOrm;
            public static class RawUse
            {
                public static void Read(DbConnection connection, string dynamicSql)
                {
                    _ = connection.Query("SELECT extension_missing FROM users");
                    _ = CobaltumQueryExtensions.Query(connection, "SELECT static_missing FROM users");
                    _ = connection.Query("UPDATE users SET id = id");
                    _ = CobaltumQueryExtensions.Query(connection, "DELETE FROM users WHERE id = -1");
                    _ = connection.Query(dynamicSql);
                    _ = CobaltumQueryExtensions.Query(connection, dynamicSql);
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[] { ("/db/V1__users.sql", "CREATE TABLE users (id integer NOT NULL);") });
        var sourceText = result.Compilation.SyntaxTrees.First(tree => tree.FilePath == "Consumer.cs").GetText();

        var invalidSql = result.AllDiagnostics.Where(item => item.Id == "COB004").ToArray();
        Assert.Equal(2, invalidSql.Length);
        Assert.Equal(
            new[]
            {
                "\"SELECT extension_missing FROM users\"",
                "\"SELECT static_missing FROM users\"",
            },
            invalidSql.Select(item => sourceText.ToString(item.Location.SourceSpan)));

        var dynamicSql = result.AllDiagnostics.Where(item => item.Id == "COB007").ToArray();
        Assert.Equal(2, dynamicSql.Length);
        Assert.All(dynamicSql, item => Assert.Equal("dynamicSql", sourceText.ToString(item.Location.SourceSpan)));
    }

    [Fact]
    public void ValidatesDirectSqlNamesAndSyntaxInLiteralDataManipulation()
    {
        const string source = """
            using System.Data.Common;
            using CobaltumOrm;
            public static class RawUse
            {
                public static void Write(DbConnection connection)
                {
                    _ = connection.Query("UPDATE app.users SET missing = 1");
                    _ = connection.Query("DELETE FROM audit.users WHERE id = 1");
                    _ = connection.Query("INSERT INTO app.users (id) VALUE (1)");
                }
            }
            """;
        var result = GeneratorTestHost.Run(
            source,
            new[] { ("/db/V1__users.sql", "CREATE TABLE app.users (id integer NOT NULL);") });

        var messages = result.AllDiagnostics
            .Where(item => item.Id == "COB004")
            .Select(item => item.GetMessage())
            .ToArray();
        Assert.Contains(messages, message => message.Contains("SQL100", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("SQL200", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("SQL203", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnEmptyRawLiteralAtCompileTime()
    {
        const string source = """
            using System.Data.Common;
            using CobaltumOrm;
            public static class RawUse
            {
                public static CobaltumRawQuery Read(DbConnection connection) =>
                    connection.Query(" -- only a comment");
            }
            """;

        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.AllDiagnostics, item => item.Id == "COB004");
        Assert.Contains("must contain a statement", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private static void AssertNoErrors(GeneratorTestResult result)
    {
        var problems = result.AllDiagnostics
            .Where(item => item.Severity == DiagnosticSeverity.Error || item.Severity == DiagnosticSeverity.Warning)
            .ToArray();
        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems.Select(item => item.ToString())));
    }
}
