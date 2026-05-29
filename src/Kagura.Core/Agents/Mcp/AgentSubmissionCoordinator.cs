using System.Collections.Concurrent;
using System.Text.Json;

namespace Kagura.Core.Agents.Mcp;

/// <summary>
/// In-memory implementation of <see cref="IAgentSubmissionCoordinator"/>. One TCS per runId.
/// </summary>
public sealed class AgentSubmissionCoordinator : IAgentSubmissionCoordinator
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<JsonElement>> _pending = new();

    public Task<JsonElement> RegisterAsync(Guid runId, CancellationToken ct = default)
    {
        // RunContinuationsAsynchronously: completing the TCS from the MCP request thread
        // shouldn't synchronously execute caller continuations on it.
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(runId, tcs))
            throw new InvalidOperationException(
                $"Submission already registered for runId {runId}. Each Agent runId is single-shot.");

        if (ct.CanBeCanceled)
        {
            // Wire the caller's cancellation to fail the TCS — also clean up the registration.
            ct.Register(() =>
            {
                if (_pending.TryRemove(runId, out var existing))
                    existing.TrySetCanceled(ct);
            });
        }

        return tcs.Task;
    }

    public bool TrySubmit(Guid runId, JsonElement payload)
    {
        if (!_pending.TryRemove(runId, out var tcs)) return false;
        return tcs.TrySetResult(payload);
    }

    public bool Fail(Guid runId, Exception ex)
    {
        if (!_pending.TryRemove(runId, out var tcs)) return false;
        return tcs.TrySetException(ex);
    }

    public bool IsActive(Guid runId) => _pending.ContainsKey(runId);
}
