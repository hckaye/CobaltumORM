using LinqToDB.Mapping;

namespace CobaltumOrm.Benchmarks;

[Table(Name = "cobaltum_benchmark_posts")]
public sealed class BenchmarkPost
{
    [PrimaryKey]
    [Column("id")]
    public int Id { get; set; }

    [Column("author_id")]
    public int AuthorId { get; set; }

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("body")]
    public string Body { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("score")]
    public int Score { get; set; }
}
