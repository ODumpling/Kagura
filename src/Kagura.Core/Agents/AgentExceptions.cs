namespace Kagura.Core.Agents;

/// <summary>
/// Thrown when a user stops an Agent that was awaiting an MCP submission.
/// Per CONTEXT.md → "Service interfaces": surfaces to orchestrators as
/// the "halted by user" path (Ralph Loop maps this to RalphLoopActive=false).
/// </summary>
public sealed class AgentInterruptedException : Exception
{
    public Guid RunId { get; }

    public AgentInterruptedException(Guid runId, string? message = null)
        : base(message ?? $"Agent run {runId} was stopped by the user before submitting.")
    {
        RunId = runId;
    }
}

/// <summary>
/// Thrown when the Agent's PTY exits before calling its MCP submission tool.
/// Indicates the Agent died, crashed, or produced no structured result —
/// the orchestrator sees this as a failed run (not a user stop).
/// </summary>
public sealed class AgentSubmissionMissingException : Exception
{
    public Guid RunId { get; }
    public int? ExitCode { get; }

    public AgentSubmissionMissingException(Guid runId, int? exitCode, string? message = null)
        : base(message ?? $"Agent run {runId} exited (code {exitCode?.ToString() ?? "null"}) without calling its MCP submission tool.")
    {
        RunId = runId;
        ExitCode = exitCode;
    }
}
