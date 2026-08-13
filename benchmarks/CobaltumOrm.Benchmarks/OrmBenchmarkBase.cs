using BenchmarkDotNet.Attributes;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RepoDb;

namespace CobaltumOrm.Benchmarks;

public abstract class OrmBenchmarkBase
{
    private BenchmarkDatabase? _database;

    protected NpgsqlConnection Connection { get; private set; } = null!;

    protected BenchmarkDbContext EfContext { get; private set; } = null!;

    protected DataConnection LinqToDb { get; private set; } = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _database = new BenchmarkDatabase();
        await _database.StartAsync().ConfigureAwait(false);

        GlobalConfiguration.Setup().UsePostgreSql();

        Connection = new NpgsqlConnection(_database.ConnectionString);
        await Connection.OpenAsync().ConfigureAwait(false);

        var efOptions = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseNpgsql(Connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        EfContext = new BenchmarkDbContext(efOptions);

        var linqToDbOptions = new DataOptions()
            .UsePostgreSQL(
                _database.ConnectionString,
                PostgreSQLVersion.v18);
        LinqToDb = new DataConnection(linqToDbOptions);
        await LinqToDb.GetTable<BenchmarkPost>()
            .Where(post => post.Id < 0)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (LinqToDb is not null)
        {
            await LinqToDb.DisposeAsync().ConfigureAwait(false);
        }

        if (EfContext is not null)
        {
            await EfContext.DisposeAsync().ConfigureAwait(false);
        }

        if (Connection is not null)
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_database is not null)
        {
            await _database.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected static BenchmarkPost ReadPost(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        AuthorId = reader.GetInt32(1),
        Title = reader.GetString(2),
        Body = reader.GetString(3),
        CreatedAt = reader.GetDateTime(4),
        Score = reader.GetInt32(5),
    };
}
