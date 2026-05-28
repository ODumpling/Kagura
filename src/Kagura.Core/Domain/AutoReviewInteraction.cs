namespace Kagura.Core.Domain;

public class AutoReviewInteraction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }
    public AgentRun AgentRun { get; set; } = null!;

    public Guid WorkItemId { get; set; }
    public WorkItem WorkItem { get; set; } = null!;

    public Guid? AgentTaskId { get; set; }
    public AgentTask? AgentTask { get; set; }

    public int Sequence { get; set; }

    public required string Prompt { get; set; }
    public string? Response { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAt { get; set; }

    public bool IsPending => Response is null;
}
