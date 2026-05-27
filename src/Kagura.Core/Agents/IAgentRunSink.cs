namespace Kagura.Core.Agents;

public enum AgentExitReason
{
    CompletedCleanly = 0,
    Crashed = 1,
    KilledByUser = 2,
    KilledByTimeout = 3,
}

public interface IAgentRunSink
{
    // If overrideReason is supplied (e.g. user-clicked-stop, timeout-kill), it wins.
    // Otherwise the sink infers: task is AwaitingReview → CompletedCleanly; else Crashed.
    Task RecordExitAsync(Guid runId, int? exitCode, AgentExitReason? overrideReason, CancellationToken ct = default);
}

public sealed class NullAgentRunSink : IAgentRunSink
{
    public Task RecordExitAsync(Guid runId, int? exitCode, AgentExitReason? overrideReason, CancellationToken ct = default) =>
        Task.CompletedTask;
}
