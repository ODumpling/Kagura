namespace Kagura.Core.Domain;

public enum AgentRunStatus
{
    Starting = 0,
    Running = 1,
    Exited = 2,
    Killed = 3,
    Crashed = 4,
}

public enum AgentRunKind
{
    TaskAgent = 0,
    Triage = 1,
    AutoReview = 2,
    Grill = 3,
    MergeResolver = 4,
}

public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? AgentTaskId { get; set; }
    public AgentTask? AgentTask { get; set; }

    public Guid WorkItemId { get; set; }
    public WorkItem WorkItem { get; set; } = null!;

    public AgentRunKind Kind { get; set; } = AgentRunKind.TaskAgent;

    public AgentRunStatus Status { get; set; } = AgentRunStatus.Starting;
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    public string TranscriptLogPath { get; set; } = "";

    /// <summary>
    /// Snapshot of the resolved prompt (post-interpolation) that the Agent was launched with.
    /// Per ADR 0002: every AgentRun snapshots the resolved prompt so the audit trail of past
    /// runs is unaffected by later prompt edits. Stored as TEXT (no length cap) in SQLite.
    /// </summary>
    public string? PromptText { get; set; }

    public List<AutoReviewInteraction> Interactions { get; set; } = new();
}
