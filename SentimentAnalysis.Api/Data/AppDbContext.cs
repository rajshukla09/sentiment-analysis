using Microsoft.EntityFrameworkCore;
using SentimentAnalysis.Api.Models;

namespace SentimentAnalysis.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobResult> JobResults => Set<JobResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.StoredFilePath).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ErrorMessage).HasMaxLength(2048);
            entity.HasIndex(x => new { x.Status, x.CreatedAtUtc });
            entity.HasOne(x => x.Result).WithOne(x => x.Job).HasForeignKey<JobResult>(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobResult>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OverallSummary).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.OverallSentiment).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ThemesJson).IsRequired();
            entity.Property(x => x.RecommendedActionsJson).IsRequired();
            entity.Property(x => x.RawStructuredJson).IsRequired();
            entity.HasIndex(x => x.JobId).IsUnique();
        });
    }
}
