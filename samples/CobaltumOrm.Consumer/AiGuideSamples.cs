using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using CobaltumOrm.Sample.Generated;

namespace CobaltumOrm.Sample;

// Every region marked with <snippet> is copied into docs/ai by the paired
// documentation tests, so this file compiles the code shown in those guides.

// <snippet named-query>
[Query(
    "ByEmail",
    "SELECT id, display_name FROM app.users WHERE email = @email")]
public static partial class UserDirectory
{
}
// </snippet>

// <snippet named-command>
[Query(
    "DeleteByEmail",
    "DELETE FROM app.users WHERE email = @email")]
public static partial class UserDirectory
{
}
// </snippet>

// <snippet result-type>
public sealed record UserSummary(
    [ResultColumn("id")] int Id,
    [ResultColumn("display_name")] string? DisplayName);
// </snippet>

public static class AiGuideSamples
{
    // <snippet checked-select>
    public static async Task<string?> ReadDisplayNameAsync(
        DbConnection connection,
        int id,
        CancellationToken cancellationToken = default)
    {
        var rows = await connection
            .Query("SELECT id, display_name FROM app.users WHERE id = @id")
            .WithParameter("@id", id, DbType.Int32)
            .ReadAsync(cancellationToken);

        return rows[0].DisplayName;
    }
    // </snippet>

    // <snippet result-type-query>
    public static Task<IReadOnlyList<UserSummary>> ReadSummariesAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default) =>
        connection
            .Query<UserSummary>("SELECT id, display_name FROM app.users ORDER BY id")
            .ReadAsync(cancellationToken);
    // </snippet>

    // <snippet named-query-call>
    public static Task<IReadOnlyList<UserDirectory.ByEmailResult>> ReadByEmailAsync(
        DbConnection connection,
        string email,
        CancellationToken cancellationToken = default) =>
        UserDirectory.ByEmailAsync(connection, email, cancellationToken: cancellationToken);
    // </snippet>

    // <snippet named-command-call>
    public static Task<int> DeleteByEmailAsync(
        DbConnection connection,
        string email,
        CancellationToken cancellationToken = default) =>
        UserDirectory.DeleteByEmailAsync(connection, email, cancellationToken: cancellationToken);
    // </snippet>

    // <snippet interpolated-query>
    public static async Task<string?> ReadDisplayNameInterpolatedAsync(
        DbConnection connection,
        int id,
        CancellationToken cancellationToken = default)
    {
        var rows = await connection
            .Query($"SELECT id, display_name FROM app.users WHERE id = {id}")
            .ReadAsync(cancellationToken);

        return rows[0].DisplayName;
    }
    // </snippet>

    // <snippet constant-dml>
    public static Task<int> RenameAsync(
        DbConnection connection,
        int id,
        string displayName,
        CancellationToken cancellationToken = default) =>
        connection
            .Query("UPDATE app.users SET display_name = @name WHERE id = @id")
            .WithParameter("@name", displayName, DbType.String)
            .WithParameter("@id", id, DbType.Int32)
            .ExecuteAsync(cancellationToken);
    // </snippet>

    // <snippet table-query>
    public static Task<IReadOnlyList<AppUsersRow>> ReadFilteredAsync(
        DbConnection connection,
        int id,
        bool filterByEmail,
        string email,
        CancellationToken cancellationToken = default)
    {
        var query = Tables.Users
            .Query()
            .Where(Tables.Users.Id.Equal(id))
            .WhereIf(filterByEmail, () => Tables.Users.Email.Equal(email));

        return connection.Query(query, transaction: null, cancellationToken);
    }
    // </snippet>

    // <snippet no-check-query>
    public static Task<IReadOnlyList<CobaltumRawRow>> ReadDynamicAsync(
        DbConnection connection,
        string sql,
        int id,
        CancellationToken cancellationToken = default) =>
        connection
            .NoCheckQuery(sql)
            .WithParameter("@id", id, DbType.Int32)
            .ReadAsync(cancellationToken);
    // </snippet>

    // <snippet no-check-query-result>
    public static Task<IReadOnlyList<UserSummary>> ReadDynamicSummariesAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken = default) =>
        connection
            .NoCheckQuery<UserSummary>(sql)
            .ReadAsync(cancellationToken);
    // </snippet>

    // <snippet migration-runner>
    public static Task MigrateUpAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default) =>
        new MigrationRunner(new PostgreSqlMigrationAdapter())
            .MigrateUpAsync(connection, CobaltumMigrationCatalog.All, cancellationToken);
    // </snippet>
}
