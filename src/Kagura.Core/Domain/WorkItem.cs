namespace Kagura.Core.Domain;

public enum WorkItemStatus
{
    New = 0,
    Triaged = 1,
    InProgress = 2,
    Merged = 3,
    PullRequested = 4,
    Done = 5,
    Cancelled = 6,
    Closed = 7,
}

public class WorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public Source Source { get; set; } = null!;

    public required string ExternalId { get; set; }
    public required string Title { get; set; }
    public string Body { get; set; } = "";
    public string? Url { get; set; }
    public string? Labels { get; set; }

    public WorkItemStatus Status { get; set; } = WorkItemStatus.New;
    public string? BranchName { get; set; }
    public string? PullRequestUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TriagedAt { get; set; }

    public List<AgentTask> Tasks { get; set; } = new();
}
