using Kagura.Core.Agents;
using Microsoft.AspNetCore.SignalR;

namespace Kagura.Api.Hubs;

public class AgentHub : Hub
{
    private readonly IAgentRunner _runner;
    private readonly ILogger<AgentHub> _log;

    public AgentHub(IAgentRunner runner, ILogger<AgentHub> log)
    {
        _runner = runner;
        _log = log;
    }

    public async Task Join(string runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, runId);

        var session = _runner.Get(Guid.Parse(runId));
        if (session is not null)
        {
            var transcript = session.ReadTranscript();
            if (transcript.Length > 0)
                await Clients.Caller.SendAsync("data", runId, Convert.ToBase64String(transcript));
            if (!session.Alive)
                await Clients.Caller.SendAsync("exit", runId, session.ExitCode);
        }
        else
        {
            await Clients.Caller.SendAsync("exit", runId, (int?)null);
        }
    }

    public Task Leave(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);

    public async Task Input(string runId, string base64Bytes)
    {
        var session = _runner.Get(Guid.Parse(runId));
        if (session is null || !session.Alive) return;
        var data = Convert.FromBase64String(base64Bytes);
        await session.WriteAsync(data);
    }

    public Task Resize(string runId, int cols, int rows)
    {
        var session = _runner.Get(Guid.Parse(runId));
        session?.Resize(cols, rows);
        return Task.CompletedTask;
    }
}
