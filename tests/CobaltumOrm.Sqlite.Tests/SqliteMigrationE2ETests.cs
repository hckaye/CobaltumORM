using System;
using System.Linq;
using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CobaltumOrm.Sqlite.Tests;

public sealed class SqliteMigrationE2ETests
{
    [Fact]
    public async Task MigrationsRunAgainstInMemorySqliteAndCanBeRolledBack()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var runner = new MigrationRunner(new SqliteMigrationAdapter());
        var migrations = new[]
        {
            MigrationInfo.Create<E2eCreateWidgetsMigration>(100, "Create SQLite widgets"),
            MigrationInfo.Create<E2eAddDescriptionMigration>(200, "Add SQLite widget description"),
        };

        await runner.MigrateUpAsync(connection, migrations);

        var createSql = await ScalarStringAsync(
            connection,
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'widgets';");
        Assert.Contains("INTEGER PRIMARY KEY AUTOINCREMENT", createSql, StringComparison.Ordinal);
        Assert.Contains("\"description\" TEXT", createSql, StringComparison.Ordinal);

        await ExecuteAsync(
            connection,
            "INSERT INTO \"widgets\" (\"label\", \"created_utc\", \"description\") " +
            "VALUES (@label, @created_utc, @description);",
            ("label", "first"),
            ("created_utc", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)),
            ("description", "created by E2E"));
        Assert.Equal(
            "first",
            await ScalarStringAsync(connection, "SELECT \"label\" FROM \"widgets\" WHERE \"id\" = 1;"));
        Assert.Equal(
            "created by E2E",
            await ScalarStringAsync(connection, "SELECT \"description\" FROM \"widgets\" WHERE \"id\" = 1;"));

        var history = await ReadVersionsAsync(connection);
        Assert.Equal(new long[] { 100, 200 }, history);
        var status = await runner.GetStatusAsync(connection, migrations);
        Assert.All(status, item => Assert.True(item.IsApplied));

        var dryDown = await runner.DryRunDownAsync(connection, migrations, 100);
        Assert.Equal(100, dryDown.TargetVersion);
        Assert.Equal(new[] { 200L }, dryDown.Entries.Select(entry => entry.Migration.Version));
        var dryDownTable = Assert.Single(dryDown.FinalSchema.Tables, table => table.Name == "widgets");
        Assert.Equal(new[] { "id", "name", "created_utc" }, dryDownTable.Columns.Select(column => column.Name));
        Assert.Equal(new long[] { 100, 200 }, await ReadVersionsAsync(connection));

        await runner.MigrateDownAsync(connection, migrations, 100);
        Assert.Equal("first", await ScalarStringAsync(connection, "SELECT \"name\" FROM \"widgets\" WHERE \"id\" = 1;"));
        Assert.True(await TableExistsAsync(connection, "__cobaltum_migrations"));
        Assert.Equal(new long[] { 100 }, await ReadVersionsAsync(connection));

        await runner.MigrateDownAsync(connection, migrations, 0);
        Assert.False(await TableExistsAsync(connection, "widgets"));
        Assert.Empty(await ReadVersionsAsync(connection));
    }

    [Fact]
    public async Task DryRunUpReadsSqliteMasterWithoutChangingDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var runner = new MigrationRunner(new SqliteMigrationAdapter());
        var migrations = new[]
        {
            MigrationInfo.Create<E2eCreateWidgetsMigration>(100, "Create SQLite widgets"),
            MigrationInfo.Create<E2eAddDescriptionMigration>(200, "Add SQLite widget description"),
        };

        var dryRun = await runner.DryRunUpAsync(connection, migrations);

        Assert.Equal(0, dryRun.CurrentVersion);
        Assert.Equal(200, dryRun.TargetVersion);
        Assert.Equal(2, dryRun.Entries.Count);
        var table = Assert.Single(dryRun.FinalSchema.Tables);
        Assert.Equal("widgets", table.Name);
        Assert.Equal(new[] { "id", "label", "created_utc", "description" }, table.Columns.Select(column => column.Name));
        Assert.False(await TableExistsAsync(connection, "__cobaltum_migrations"));
        Assert.False(await TableExistsAsync(connection, "widgets"));
    }

    [Fact]
    public async Task AlterColumnFailsBeforeSQLiteReceivesInvalidSql()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var runner = new MigrationRunner(new SqliteMigrationAdapter());

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            runner.MigrateUpAsync(
                connection,
                new[]
                {
                    MigrationInfo.Create<E2eAlterColumnMigration>(300, "Unsupported SQLite alteration"),
                }));
        Assert.Contains("table rebuild", exception.Message, StringComparison.Ordinal);
        Assert.False(await TableExistsAsync(connection, "widgets"));
        Assert.Empty(await ReadVersionsAsync(connection));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameterValue in parameters)
        {
            command.Parameters.AddWithValue("@" + parameterValue.Name, parameterValue.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Assert.IsType<string>(value);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<long[]> ReadVersionsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"version\" FROM \"__cobaltum_migrations\" ORDER BY \"version\";";
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new System.Collections.Generic.List<long>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions.ToArray();
    }
}

[Migration(100, "Create SQLite widgets")]
public sealed class E2eCreateWidgetsMigration : Migration
{
    public override void Up()
    {
        Create.Table("widgets")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("name").AsString(100).NotNullable()
            .WithColumn("created_utc").AsDateTimeOffset().NotNullable();
    }

    public override void Down() => Delete.Table("widgets");
}

[Migration(200, "Add SQLite widget description")]
public sealed class E2eAddDescriptionMigration : Migration
{
    public override void Up()
    {
        Alter.Table("widgets").AddColumn("description").AsText().Nullable();
        Rename.Column("name").OnTable("widgets").To("label");
    }

    public override void Down()
    {
        Rename.Column("label").OnTable("widgets").To("name");
        Delete.Column("description").FromTable("widgets");
    }
}

[Migration(300, "Unsupported SQLite alteration")]
public sealed class E2eAlterColumnMigration : Migration
{
    public override void Up()
    {
        Alter.Table("widgets").AlterColumn("name").AsText().NotNullable();
    }

    public override void Down() => Execute.Sql("SELECT 1;");
}
