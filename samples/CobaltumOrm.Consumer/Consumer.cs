using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using CobaltumOrm.Sample.Generated;

namespace CobaltumOrm.Sample;

[Query(
    "ById",
    $"SELECT {SqlSchema.Tables.AppUsers.Columns.Id}, {SqlSchema.Tables.AppUsers.Columns.Email}, " +
    $"{SqlSchema.Tables.AppUsers.Columns.DisplayName}, {SqlSchema.Tables.AppUsers.Columns.CreatedAt} " +
    $"FROM {SqlSchema.Tables.AppUsers.Name} WHERE {SqlSchema.Tables.AppUsers.Columns.Id} = @id")]
public static partial class UserQueries
{
}

public static class ConsumerProof
{
    public static IReadOnlyList<long> MergedMigrationVersions()
    {
        return CobaltumMigrationCatalog.All
            .Select(migration => migration.Version)
            .ToArray();
    }

    public static Type ImportedFlywayMigrationType => typeof(FlywayV20_AddDisplayName);

    public static Task<IReadOnlyList<UserQueries.ByIdResult>> NamedQueryAsync(
        DbConnection connection,
        int id,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        UserQueries.ByIdAsync(connection, id, transaction, cancellationToken);

    public static Task<IReadOnlyList<AppUsersRow>> TableMethodChainAsync(
        DbConnection connection,
        int id,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        connection.Query(
            Tables.Users.Where(Tables.Users.Id.Equal(id)),
            transaction,
            cancellationToken);

    public static async Task<string?> RawQueryAsync(
        DbConnection connection,
        int id,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await connection
            .Query(
                "SELECT id, display_name FROM app.users WHERE id = @id",
                transaction)
            .WithParameter("@id", id, DbType.Int32)
            .ReadAsync(cancellationToken);
        return rows[0].DisplayName;
    }

    public static async Task<string?> InterpolatedQueryAsync(
        DbConnection connection,
        int id,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await connection
            .Query($"SELECT id, display_name FROM app.users WHERE id = {id}", transaction)
            .ReadAsync(cancellationToken);
        return rows[0].DisplayName;
    }

    public static Task<int> RawCommandAsync(
        DbConnection connection,
        int id,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        connection
            .Query("UPDATE app.users SET display_name = @name WHERE id = @id", transaction)
            .WithParameter("@name", "updated", DbType.String)
            .WithParameter("@id", id, DbType.Int32)
            .ExecuteAsync(cancellationToken);

    public static Task ApplyMigrationsAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default) =>
        new MigrationRunner(new PostgreSqlMigrationAdapter())
            .MigrateUpAsync(connection, CobaltumMigrationCatalog.All, cancellationToken);
}
