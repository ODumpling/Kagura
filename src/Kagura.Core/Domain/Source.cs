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

    /// <summary>
    /// Per-Role prompt overrides for this Source (ADR 0002). Each row replaces the built-in
    /// default for that Role at Agent spawn time. Absence of a row for a given Role means the
    /// current built-in default is used (lazy lookup, not a one-time copy).
    /// </summary>
    public List<SourcePromptOverride> PromptOverrides { get; set; } = new();
}
