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
    public async Task<int> CobaltumORM() =>
        (await CobaltumBenchmarkQueries.FindByIdAsync(Connection, Id).ConfigureAwait(false)).Count;

    [Benchmark]
    public async Task<int> Dapper() =>
        (await SqlMapper.QueryAsync<BenchmarkPost>(
            Connection,
            BenchmarkSql.FindById,
            new { id = Id }).ConfigureAwait(false)).AsList().Count;

    [Benchmark]
    public async Task<int> EFCore() =>
        (await EfContext.Posts
            .Where(post => post.Id == Id)
            .Take(1)
            .ToListAsync()
            .ConfigureAwait(false)).Count;

    [Benchmark]
    public async Task<int> LinqToDB() =>
        (await LinqToDb.GetTable<BenchmarkPost>()
            .Where(post => post.Id == Id)
            .Take(1)
            .ToArrayAsync()
            .ConfigureAwait(false)).Length;

    [Benchmark]
    public async Task<int> RepoDB()
    {
        var rows = await Connection.ExecuteQueryAsync<BenchmarkPost>(
            BenchmarkSql.FindById,
            new { id = Id }).ConfigureAwait(false);
        return rows.TryGetNonEnumeratedCount(out var count) ? count : rows.Count();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> AdoNet()
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

        return rows.Count;
    }
}
