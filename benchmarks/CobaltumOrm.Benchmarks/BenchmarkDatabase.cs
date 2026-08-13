using Npgsql;
using Testcontainers.PostgreSql;

namespace CobaltumOrm.Benchmarks;

internal sealed class BenchmarkDatabase : IAsyncDisposable
{
    internal const string ConnectionStringEnvironmentVariable =
        "COBALTUM_BENCHMARK_CONNECTION_STRING";
    internal const string ImageEnvironmentVariable =
        "COBALTUM_BENCHMARK_POSTGRES_IMAGE";
    internal const string PreparedEnvironmentVariable =
        "COBALTUM_BENCHMARK_DATABASE_PREPARED";

    private PostgreSqlContainer? _container;

    internal string ConnectionString { get; private set; } = string.Empty;

    internal async Task StartAsync()
    {
        var suppliedConnectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(suppliedConnectionString))
        {
            ConnectionString = suppliedConnectionString;
        }
        else
        {
            var image = Environment.GetEnvironmentVariable(ImageEnvironmentVariable);
            _container = new PostgreSqlBuilder(
                    string.IsNullOrWhiteSpace(image) ? "postgres:18-alpine" : image)
                .WithDatabase("cobaltum_benchmarks")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _container.StartAsync().ConfigureAwait(false);
            ConnectionString = _container.GetConnectionString();
        }

        if (!string.Equals(
                Environment.GetEnvironmentVariable(PreparedEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            await SeedAsync().ConfigureAwait(false);
        }
    }

    private async Task SeedAsync()
    {
        const string sql = """
            DROP TABLE IF EXISTS cobaltum_benchmark_posts;
            CREATE TABLE cobaltum_benchmark_posts (
                id integer PRIMARY KEY,
                author_id integer NOT NULL,
                title text NOT NULL,
                body text NOT NULL,
                created_at timestamp without time zone NOT NULL,
                score integer NOT NULL
            );
            INSERT INTO cobaltum_benchmark_posts
                (id, author_id, title, body, created_at, score)
            SELECT
                value,
                (value % 100) + 1,
                'Post ' || value,
                repeat('benchmark body ' || value || ' ', 12),
                timestamp '2024-01-01 00:00:00' + (value * interval '1 minute'),
                value % 1000
            FROM generate_series(1, 10000) AS value;
            ANALYZE cobaltum_benchmark_posts;
            """;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
