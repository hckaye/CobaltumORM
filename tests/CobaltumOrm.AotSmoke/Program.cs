using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.Sqlite;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Data;

namespace CobaltumOrm.AotSmoke;

[Migration(1, "create widgets")]
internal sealed class CreateWidgetsMigration : Migration
{
    public override void Up()
    {
        Create.Table("widgets")
            .WithColumn("id").AsInt64().PrimaryKey()
            .WithColumn("name").AsString(100).NotNullable();
    }

    public override void Down() => Delete.Table("widgets");
}

[ResultHandler<WidgetResultHandler>]
internal sealed record WidgetResult(long Id, string Name);

internal sealed record UncheckedWidgetResult(long Id, [ResultColumn] string Name);

internal sealed class WidgetResultHandler : IResultHandler<WidgetResult>
{
    public WidgetResultHandler()
    {
    }

    public WidgetResult Read(System.Data.Common.DbDataReader reader) =>
        new WidgetResult(reader.GetInt64(0), reader.GetString(1));
}

[Query<WidgetResult>("ById", "SELECT id, name FROM widgets WHERE id = @id")]
internal static partial class WidgetQueries
{
}

internal sealed class SmokeMigrationProject : MigrationProject
{
    public override System.Data.Common.DbConnection CreateConnection(MigrationProjectContext context) =>
        new SqliteConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() => new SqliteMigrationAdapter();
}

internal static class Program
{
    private static async Task<int> Main()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var catalog = Generated.CobaltumMigrationCatalog.All;
        var listExitCode = await MigrationProjectHost.RunAsync<SmokeMigrationProject>(
            new[] { "list" },
            catalog);
        var runner = new MigrationRunner(new SqliteMigrationAdapter());
        await runner.MigrateUpAsync(connection, catalog);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO widgets (id, name) VALUES (1, 'native');";
            await command.ExecuteNonQueryAsync();
        }

        var rows = await WidgetQueries.ByIdAsync(connection, 1);
        var uncheckedSql = "SELECT id, name FROM widgets WHERE id = 1";
        var uncheckedRows = await connection
            .NoCheckQuery<UncheckedWidgetResult>(uncheckedSql)
            .ReadAsync();
        using var postgresCommand = new NpgsqlCommand();
        CobaltumParameter.AddConfigured(
            postgresCommand,
            "payload",
            "{}",
            DbType.String,
            static parameter => ((NpgsqlParameter)parameter).DataTypeName = "jsonb");
        var postgresParameter = (NpgsqlParameter)postgresCommand.Parameters[0];

        if (listExitCode != 0 || catalog.Count != 1 || rows.Count != 1 || rows[0].Id != 1 || rows[0].Name != "native" ||
            uncheckedRows.Count != 1 || uncheckedRows[0].Id != 1 || uncheckedRows[0].Name != "native" ||
            postgresParameter.DataTypeName != "jsonb")
        {
            return 1;
        }

        Console.WriteLine("CobaltumORM publish smoke test passed.");
        return 0;
    }
}
