using Microsoft.EntityFrameworkCore;

namespace CobaltumOrm.Benchmarks;

public sealed class BenchmarkDbContext(DbContextOptions<BenchmarkDbContext> options)
    : DbContext(options)
{
    internal DbSet<BenchmarkPost> Posts => Set<BenchmarkPost>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchmarkPost>(entity =>
        {
            entity.ToTable("cobaltum_benchmark_posts");
            entity.HasKey(post => post.Id);
            entity.Property(post => post.Id).HasColumnName("id");
            entity.Property(post => post.AuthorId).HasColumnName("author_id");
            entity.Property(post => post.Title).HasColumnName("title");
            entity.Property(post => post.Body).HasColumnName("body");
            entity.Property(post => post.CreatedAt).HasColumnName("created_at");
            entity.Property(post => post.Score).HasColumnName("score");
        });
    }
}
