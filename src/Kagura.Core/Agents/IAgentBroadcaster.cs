using Kagura.Core.Interactive;

namespace Kagura.Core.Agents;

/// <summary>
/// Snapshot of an Agent's sidebar surface — what the tree needs to render a node.
/// Per CONTEXT.md → "Source tree (navigation)": Sources are the roots, Agents are children
/// grouped by their WorkItem's Source. Status line is set by the orchestrator.
/// </summary>
public sealed record AgentSidebarEvent(
    Guid RunId,
    Guid WorkItemId,
    Guid SourceId,
    string SourceName,
    string WorkItemTitle,
    string WorkItemExternalId,
    Kagura.Core.Domain.AgentRunKind Kind,
    string StatusLine,
    DateTime StartedAt);

public interface IAgentBroadcaster
{
    Task DataAsync(Guid runId, byte[] data);
    Task ExitAsync(Guid runId, int? exitCode);
    Task WorkItemUpdatedAsync(Guid workItemId);
    Task PromptAsync(InteractivePrompt prompt);

    /// <summary>An Agent has appeared in the sidebar (PTY spawned).</summary>
    Task AgentAppearedAsync(AgentSidebarEvent evt);

    /// <summary>An Agent has been removed from the sidebar (success exit / explicit dismiss).</summary>
    Task AgentDismissedAsync(Guid runId);

    /// <summary>An Agent's status line text changed (orchestrator transitioned stages).</summary>
    Task AgentStatusChangedAsync(Guid runId, string statusLine);
}

public class NullAgentBroadcaster : IAgentBroadcaster
{
    public Task DataAsync(Guid runId, byte[] data) => Task.CompletedTask;
    public Task ExitAsync(Guid runId, int? exitCode) => Task.CompletedTask;
    public Task WorkItemUpdatedAsync(Guid workItemId) => Task.CompletedTask;
    public Task PromptAsync(InteractivePrompt prompt) => Task.CompletedTask;
    public Task AgentAppearedAsync(AgentSidebarEvent evt) => Task.CompletedTask;
    public Task AgentDismissedAsync(Guid runId) => Task.CompletedTask;
    public Task AgentStatusChangedAsync(Guid runId, string statusLine) => Task.CompletedTask;
}
