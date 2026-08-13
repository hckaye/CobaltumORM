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
[BenchmarkCategory("Read", "Multiple rows", "Async")]
public class MultipleRowsBenchmarks : OrmBenchmarkBase
{
    [Params(10, 1000)]
    public int RowCount { get; set; }

    [Benchmark]
    public async Task<int> CobaltumORM() =>
        (await CobaltumBenchmarkQueries.ReadRowsAsync(Connection, RowCount).ConfigureAwait(false)).Count;

    [Benchmark]
    public async Task<int> Dapper() =>
        (await SqlMapper.QueryAsync<BenchmarkPost>(
            Connection,
            BenchmarkSql.ReadRows,
            new { row_count = RowCount }).ConfigureAwait(false)).AsList().Count;

    [Benchmark]
    public async Task<int> EFCore() =>
        (await EfContext.Posts
            .Where(post => post.Id <= RowCount)
            .OrderBy(post => post.Id)
            .ToListAsync()
            .ConfigureAwait(false)).Count;

    [Benchmark]
    public async Task<int> LinqToDB() =>
        (await LinqToDb.GetTable<BenchmarkPost>()
            .Where(post => post.Id <= RowCount)
            .OrderBy(post => post.Id)
            .ToArrayAsync()
            .ConfigureAwait(false)).Length;

    [Benchmark]
    public async Task<int> RepoDB()
    {
        var rows = await Connection.ExecuteQueryAsync<BenchmarkPost>(
            BenchmarkSql.ReadRows,
            new { row_count = RowCount }).ConfigureAwait(false);
        return rows.TryGetNonEnumeratedCount(out var count) ? count : rows.Count();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> AdoNet()
    {
        await using var command = new NpgsqlCommand(BenchmarkSql.ReadRows, Connection);
        command.Parameters.Add("row_count", NpgsqlTypes.NpgsqlDbType.Integer).Value = RowCount;
        await using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess)
            .ConfigureAwait(false);
        var rows = new List<BenchmarkPost>(RowCount);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(ReadPost(reader));
        }

        return rows.Count;
    }
}
