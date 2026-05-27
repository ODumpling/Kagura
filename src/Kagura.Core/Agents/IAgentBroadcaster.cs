namespace Kagura.Core.Agents;

public interface IAgentBroadcaster
{
    Task DataAsync(Guid runId, byte[] data);
    Task ExitAsync(Guid runId, int? exitCode);
}

public class NullAgentBroadcaster : IAgentBroadcaster
{
    public Task DataAsync(Guid runId, byte[] data) => Task.CompletedTask;
    public Task ExitAsync(Guid runId, int? exitCode) => Task.CompletedTask;
}
