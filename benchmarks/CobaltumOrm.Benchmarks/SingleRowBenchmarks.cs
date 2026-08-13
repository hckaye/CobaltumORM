using System.Data;
using BenchmarkDotNet.Attributes;
using CobaltumOrm;
using Dapper;
using LinqToDB;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RepoDb;

namespace CobaltumOrm.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Read", "Single row", "Async")]
public class SingleRowBenchmarks : OrmBenchmarkBase
{
    private const int Id = 5000;

    [Benchmark]
    public Task<IReadOnlyList<BenchmarkPost>> CobaltumORM() =>
        CobaltumBenchmarkQueries.FindByIdAsync(Connection, Id);

    [Benchmark]
    public Task<IEnumerable<BenchmarkPost>> Dapper() =>
        SqlMapper.QueryAsync<BenchmarkPost>(
            Connection,
            BenchmarkSql.FindById,
            new { id = Id });

    [Benchmark]
    public Task<List<BenchmarkPost>> EFCore() =>
        EfContext.Posts
            .Where(post => post.Id == Id)
            .Take(1)
            .ToListAsync();

    [Benchmark]
    public Task<BenchmarkPost[]> LinqToDB() =>
        LinqToDb.GetTable<BenchmarkPost>()
            .Where(post => post.Id == Id)
            .Take(1)
            .ToArrayAsync();

    [Benchmark]
    public Task<IEnumerable<BenchmarkPost>> RepoDB() =>
        Connection.ExecuteQueryAsync<BenchmarkPost>(
            BenchmarkSql.FindById,
            new { id = Id });

    [Benchmark(Baseline = true)]
    public async Task<List<BenchmarkPost>> AdoNet()
    {
        await using var command = new NpgsqlCommand(BenchmarkSql.FindById, Connection);
        command.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Integer).Value = Id;
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess)
            .ConfigureAwait(false);
        var rows = new List<BenchmarkPost>(1);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(ReadPost(reader));
        }

        return rows;
    }
}
