using Kagura.Core.Agents;
using Kagura.Core.Interactive;
using Microsoft.AspNetCore.SignalR;

namespace Kagura.Api.Hubs;

public class SignalRAgentBroadcaster : IAgentBroadcaster
{
    private readonly IHubContext<AgentHub> _hub;

    public SignalRAgentBroadcaster(IHubContext<AgentHub> hub)
    {
        _hub = hub;
    }

    public Task DataAsync(Guid runId, byte[] data) =>
        _hub.Clients.Group(runId.ToString()).SendAsync("data", runId.ToString(), Convert.ToBase64String(data));

    public Task ExitAsync(Guid runId, int? exitCode) =>
        _hub.Clients.Group(runId.ToString()).SendAsync("exit", runId.ToString(), exitCode);

    public Task WorkItemUpdatedAsync(Guid workItemId) =>
        _hub.Clients.Group($"wi-{workItemId}").SendAsync("workItemUpdated", workItemId.ToString());

    public Task PromptAsync(InteractivePrompt prompt) =>
        _hub.Clients.Group(prompt.RunId.ToString()).SendAsync(
            "prompt",
            prompt.RunId.ToString(),
            new
            {
                id = prompt.Id,
                question = prompt.Question,
                choices = prompt.Choices,
                createdAt = prompt.CreatedAt,
            });

    // Sidebar tree events — fanned out to all connected clients so every browser tab
    // sees Agents appear/disappear live. The frontend filters by Source/WorkItem.
    public Task AgentAppearedAsync(AgentSidebarEvent evt) =>
        _hub.Clients.All.SendAsync("agentAppeared", new
        {
            runId = evt.RunId.ToString(),
            workItemId = evt.WorkItemId.ToString(),
            sourceId = evt.SourceId.ToString(),
            sourceName = evt.SourceName,
            workItemTitle = evt.WorkItemTitle,
            workItemExternalId = evt.WorkItemExternalId,
            kind = (int)evt.Kind,
            statusLine = evt.StatusLine,
            startedAt = evt.StartedAt,
            taskId = evt.TaskId?.ToString(),
            taskTitle = evt.TaskTitle,
        });

    public Task AgentDismissedAsync(Guid runId) =>
        _hub.Clients.All.SendAsync("agentDismissed", runId.ToString());

    public Task AgentStatusChangedAsync(Guid runId, string statusLine) =>
        _hub.Clients.All.SendAsync("agentStatusChanged", runId.ToString(), statusLine);
}
