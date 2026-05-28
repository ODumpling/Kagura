using Kagura.Core.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Data;

public class KaguraDbContext : DbContext
{
    public const string ProtectorPurpose = "Kagura.Data.Source.ConfigJson.v1";

    private readonly IDataProtector _protector;

    public KaguraDbContext(DbContextOptions<KaguraDbContext> options, IDataProtectionProvider protectionProvider)
        : base(options)
    {
        _protector = protectionProvider.CreateProtector(ProtectorPurpose);
    }

    public DbSet<Source> Sources => Set<Source>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var encrypted = new EncryptedStringConverter(_protector);

        mb.Entity<Source>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.LocalRepoPath).IsRequired();
            e.Property(x => x.ConfigJson).HasConversion(encrypted);
            e.HasIndex(x => x.Name).IsUnique();
        });

        mb.Entity<WorkItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).IsRequired().HasMaxLength(200);
            e.Property(x => x.Title).IsRequired().HasMaxLength(500);
            e.HasOne(x => x.Source).WithMany(x => x.WorkItems).HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SourceId, x.ExternalId }).IsUnique();
        });

        mb.Entity<AgentTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).IsRequired().HasMaxLength(500);
            e.Property(x => x.IncludeInPullRequest).HasDefaultValue(true);
            e.HasOne(x => x.WorkItem).WithMany(x => x.Tasks).HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<AgentRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.AgentTask).WithMany(x => x.Runs).HasForeignKey(x => x.AgentTaskId).OnDelete(DeleteBehavior.Cascade).IsRequired(false);
            e.HasOne(x => x.WorkItem).WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade).IsRequired(false);
        });
    }
}
