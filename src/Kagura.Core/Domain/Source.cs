namespace Kagura.Core.Domain;

public class Source
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public SourceType Type { get; set; }
    public required string LocalRepoPath { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<WorkItem> WorkItems { get; set; } = new();
}
