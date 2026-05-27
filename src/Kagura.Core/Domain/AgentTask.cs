namespace Kagura.Core.Domain;

public enum AgentTaskStatus
{
    Proposed = 0,
    Approved = 1,
    Running = 2,
    AwaitingReview = 3,
    Merged = 4,
    Failed = 5,
    Cancelled = 6,
}

public class AgentTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public WorkItem WorkItem { get; set; } = null!;

    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public int Order { get; set; }

    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Proposed;
    public string? BranchName { get; set; }
    public string? WorktreePath { get; set; }

    public bool IncludeInPullRequest { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AgentRun> Runs { get; set; } = new();
}
