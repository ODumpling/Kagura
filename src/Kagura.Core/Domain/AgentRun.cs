namespace Kagura.Core.Domain;

public enum AgentRunStatus
{
    Starting = 0,
    Running = 1,
    Exited = 2,
    Killed = 3,
    Crashed = 4,
}

public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentTaskId { get; set; }
    public AgentTask AgentTask { get; set; } = null!;

    public AgentRunStatus Status { get; set; } = AgentRunStatus.Starting;
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }

    public string TranscriptLogPath { get; set; } = "";
}
