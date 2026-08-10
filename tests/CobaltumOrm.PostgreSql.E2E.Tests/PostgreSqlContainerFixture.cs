using System.Threading.Tasks;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using CobaltumOrm.PostgreSql.E2E.Tests.Generated;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CobaltumOrm.PostgreSql.E2E.Tests;

public sealed class PostgreSqlContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cobaltum_e2e")
        .WithUsername("postgres")
        .WithPassword("cobaltum_e2e_password")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await new MigrationRunner(new PostgreSqlMigrationAdapter()).MigrateUpAsync(
            connection,
            CobaltumMigrationCatalog.All);

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO e2e_values (id, local_time, document, big_id) VALUES
                (1, TIMESTAMP '2026-08-10 12:34:56', '{"active":true}'::jsonb, 9223372036854775807),
                (2, TIMESTAMP '2026-08-11 01:02:03', '{"active":false}'::jsonb, 1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlE2ECollection : ICollectionFixture<PostgreSqlContainerFixture>
{
    public const string Name = "PostgreSQL E2E";
}
