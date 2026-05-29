using System.Collections.Concurrent;
using Kagura.Core.Agents;

namespace Kagura.Core.Interactive;

// In-memory store of prompts the auto-review pipeline (or any agent run) is blocked on.
// AskAsync registers a pending prompt, broadcasts it to listeners via IAgentBroadcaster,
// and returns a Task that the caller awaits. The Task completes when a corresponding
// TryAnswer call arrives — typically from POST /api/agents/{runId}/prompts/{id}/respond.
//
// State is intentionally process-local: a Kagura restart drops outstanding prompts.
// Auto-review is fire-and-forget so the same restart would lose the run anyway.
public sealed class InteractivePromptService : IInteractivePromptService
{
    private readonly IAgentBroadcaster _broadcaster;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    public InteractivePromptService(IAgentBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    public async Task<string> AskAsync(
        Guid runId,
        string question,
        IReadOnlyList<string>? choices = null,
        CancellationToken ct = default)
    {
        var prompt = new InteractivePrompt(
            Guid.NewGuid(), runId, question, choices, DateTime.UtcNow);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[prompt.Id] = new Pending(prompt, tcs);

        await using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(prompt.Id, out var p))
                p.Tcs.TrySetCanceled(ct);
        });

        await _broadcaster.PromptAsync(prompt);
        return await tcs.Task;
    }

    public bool TryAnswer(Guid promptId, string answer)
    {
        if (!_pending.TryRemove(promptId, out var p)) return false;
        return p.Tcs.TrySetResult(answer);
    }

    public IReadOnlyList<InteractivePrompt> GetPending(Guid runId) =>
        _pending.Values
            .Where(p => p.Prompt.RunId == runId)
            .Select(p => p.Prompt)
            .ToList();

    private sealed record Pending(InteractivePrompt Prompt, TaskCompletionSource<string> Tcs);
}
