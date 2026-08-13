using CobaltumOrm;

namespace CobaltumOrm.Benchmarks;

[Query<BenchmarkPost>("FindById", BenchmarkSql.FindById)]
[Query<BenchmarkPost>("ReadRows", BenchmarkSql.ReadRows)]
public static partial class CobaltumBenchmarkQueries
{
}

internal static class BenchmarkSql
{
    internal const string Projection =
        "id AS \"Id\", author_id AS \"AuthorId\", title AS \"Title\", " +
        "body AS \"Body\", created_at AS \"CreatedAt\", score AS \"Score\"";

    internal const string FindById =
        "SELECT " + Projection + " FROM cobaltum_benchmark_posts WHERE id = @id";

    internal const string ReadRows =
        "SELECT " + Projection +
        " FROM cobaltum_benchmark_posts WHERE id <= @row_count ORDER BY id";
}
