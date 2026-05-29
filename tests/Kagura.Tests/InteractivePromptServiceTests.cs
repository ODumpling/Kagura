using Kagura.Core.Agents;
using Kagura.Core.Interactive;

namespace Kagura.Tests;

public class InteractivePromptServiceTests
{
    [Fact]
    public async Task AskAsync_resumes_with_the_supplied_answer()
    {
        var broadcaster = new RecordingBroadcaster();
        var svc = new InteractivePromptService(broadcaster);
        var runId = Guid.NewGuid();

        var ask = svc.AskAsync(runId, "Merge despite the lint failure?", new[] { "yes", "no" });
        // Give AskAsync a moment to register the pending prompt and emit the broadcast.
        var emitted = await WaitForAsync(() => broadcaster.Emitted.SingleOrDefault(), TimeSpan.FromSeconds(2));
        Assert.NotNull(emitted);
        Assert.Equal(runId, emitted!.RunId);

        Assert.True(svc.TryAnswer(emitted.Id, "yes"));
        var answer = await ask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("yes", answer);
        Assert.Empty(svc.GetPending(runId));
    }

    [Fact]
    public void TryAnswer_returns_false_for_unknown_prompt()
    {
        var svc = new InteractivePromptService(new RecordingBroadcaster());
        Assert.False(svc.TryAnswer(Guid.NewGuid(), "yes"));
    }

    [Fact]
    public async Task GetPending_returns_only_unanswered_prompts_for_the_run()
    {
        var svc = new InteractivePromptService(new RecordingBroadcaster());
        var runId = Guid.NewGuid();
        var otherRun = Guid.NewGuid();

        _ = svc.AskAsync(runId, "first?");
        _ = svc.AskAsync(otherRun, "unrelated?");
        await Task.Yield();

        var pending = svc.GetPending(runId);
        Assert.Single(pending);
        Assert.Equal("first?", pending[0].Question);
    }

    [Fact]
    public async Task Cancelling_the_token_releases_the_pending_prompt()
    {
        var svc = new InteractivePromptService(new RecordingBroadcaster());
        var runId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var ask = svc.AskAsync(runId, "still there?", ct: cts.Token);
        await Task.Yield();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        Assert.Empty(svc.GetPending(runId));
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null) return value;
            await Task.Delay(10);
        }
        return probe();
    }

    private sealed class RecordingBroadcaster : IAgentBroadcaster
    {
        public List<InteractivePrompt> Emitted { get; } = new();

        public Task DataAsync(Guid runId, byte[] data) => Task.CompletedTask;
        public Task ExitAsync(Guid runId, int? exitCode) => Task.CompletedTask;
        public Task WorkItemUpdatedAsync(Guid workItemId) => Task.CompletedTask;
        public Task PromptAsync(InteractivePrompt prompt)
        {
            lock (Emitted) Emitted.Add(prompt);
            return Task.CompletedTask;
        }
    }
}
