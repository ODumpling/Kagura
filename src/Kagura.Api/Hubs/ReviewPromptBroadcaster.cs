using Kagura.Core.Review;
using Microsoft.AspNetCore.SignalR;

namespace Kagura.Api.Hubs;

/// <summary>
/// Bridges <see cref="IReviewPromptCoordinator"/> events to SignalR clients in the work-item group.
/// Activated at startup so the coordinator (a singleton) can broadcast without referencing SignalR directly.
/// </summary>
public sealed class ReviewPromptBroadcaster : IHostedService
{
    private readonly IReviewPromptCoordinator _coordinator;
    private readonly IHubContext<AgentHub> _hub;

    public ReviewPromptBroadcaster(IReviewPromptCoordinator coordinator, IHubContext<AgentHub> hub)
    {
        _coordinator = coordinator;
        _hub = hub;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _coordinator.PromptRaised += OnRaised;
        _coordinator.PromptResolved += OnResolved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _coordinator.PromptRaised -= OnRaised;
        _coordinator.PromptResolved -= OnResolved;
        return Task.CompletedTask;
    }

    private void OnRaised(ReviewPrompt prompt) =>
        _hub.Clients.Group($"wi-{prompt.WorkItemId}").SendAsync("reviewPromptRaised", prompt);

    private void OnResolved(ReviewPromptResponse response) =>
        _hub.Clients.Group($"wi-{response.WorkItemId}").SendAsync("reviewPromptResolved", response);
}
