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
    public Task<IReadOnlyList<BenchmarkPost>> CobaltumORM() =>
        CobaltumBenchmarkQueries.ReadRowsAsync(Connection, RowCount);

    [Benchmark]
    public Task<IEnumerable<BenchmarkPost>> Dapper() =>
        SqlMapper.QueryAsync<BenchmarkPost>(
            Connection,
            BenchmarkSql.ReadRows,
            new { row_count = RowCount });

    [Benchmark]
    public Task<List<BenchmarkPost>> EFCore() =>
        EfContext.Posts
            .Where(post => post.Id <= RowCount)
            .OrderBy(post => post.Id)
            .ToListAsync();

    [Benchmark]
    public Task<BenchmarkPost[]> LinqToDB() =>
        LinqToDb.GetTable<BenchmarkPost>()
            .Where(post => post.Id <= RowCount)
            .OrderBy(post => post.Id)
            .ToArrayAsync();

    [Benchmark]
    public Task<IEnumerable<BenchmarkPost>> RepoDB() =>
        Connection.ExecuteQueryAsync<BenchmarkPost>(
            BenchmarkSql.ReadRows,
            new { row_count = RowCount });

    [Benchmark(Baseline = true)]
    public async Task<List<BenchmarkPost>> AdoNet()
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

        return rows;
    }
}
