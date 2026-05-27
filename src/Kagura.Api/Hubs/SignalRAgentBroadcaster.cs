using Kagura.Core.Agents;
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
}
