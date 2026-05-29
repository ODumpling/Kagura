using Kagura.Core.Interactive;

namespace Kagura.Core.Agents;

public interface IAgentBroadcaster
{
    Task DataAsync(Guid runId, byte[] data);
    Task ExitAsync(Guid runId, int? exitCode);
    Task WorkItemUpdatedAsync(Guid workItemId);
    Task PromptAsync(InteractivePrompt prompt);
}

public class NullAgentBroadcaster : IAgentBroadcaster
{
    public Task DataAsync(Guid runId, byte[] data) => Task.CompletedTask;
    public Task ExitAsync(Guid runId, int? exitCode) => Task.CompletedTask;
    public Task WorkItemUpdatedAsync(Guid workItemId) => Task.CompletedTask;
    public Task PromptAsync(InteractivePrompt prompt) => Task.CompletedTask;
}
