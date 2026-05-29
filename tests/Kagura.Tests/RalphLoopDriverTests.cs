using Kagura.Api.HostedServices;
using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Interactive;
using Kagura.Core.Merge;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

internal sealed class StubAgentRunner : IAgentRunner
{
    public AgentSession? Get(Guid runId) => null;
    public IReadOnlyCollection<AgentSession> Active { get; } = Array.Empty<AgentSession>();
    public Task<AgentSession> StartAsync(WorkItem wi, AgentTask task, string repoPath, CancellationToken ct = default) =>
        throw new InvalidOperationException("StartAsync was unexpectedly invoked in this test.");
    public Task StopAsync(Guid runId) => Task.CompletedTask;
    public Task DismissAsync(Guid runId) => Task.CompletedTask;
    public void MarkExitReason(Guid runId, AgentExitReason reason) { }
}

internal sealed class CapturingBroadcaster : IAgentBroadcaster
{
    public List<Guid> WorkItemUpdates { get; } = new();
    public Task DataAsync(Guid runId, byte[] data) => Task.CompletedTask;
    public Task ExitAsync(Guid runId, int? exitCode) => Task.CompletedTask;
    public Task WorkItemUpdatedAsync(Guid workItemId) { WorkItemUpdates.Add(workItemId); return Task.CompletedTask; }
    public Task PromptAsync(InteractivePrompt prompt) => Task.CompletedTask;
}

public class RalphLoopDriverTests
{
    [Fact]
    public async Task Advance_marks_task_Failed_and_halts_loop_when_retry_cap_reached()
    {
        using var test = TestDb.Create();
        var src = new Source { Name = "src-" + Guid.NewGuid().ToString("N")[..8], LocalRepoPath = "/tmp/r" };
        var wi = new WorkItem
        {
            SourceId = src.Id,
            ExternalId = "EXT-X",
            Title = "wi",
            Status = WorkItemStatus.InProgress,
            RalphLoopActive = true,
        };
        var task = new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "t1",
            Order = 0,
            Status = AgentTaskStatus.Running,
            RetryAttempts = 2, // one shy of cap; this tick's failure pushes to 3 → Failed
            LastFailureReason = "previous crash",
        };
        var run = new AgentRun
        {
            AgentTaskId = task.Id,
            WorkItemId = wi.Id,
            Kind = AgentRunKind.TaskAgent,
            Status = AgentRunStatus.Crashed,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            EndedAt = DateTime.UtcNow,
            ExitCode = 1,
        };
        wi.Tasks.Add(task);
        task.Runs.Add(run);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var broadcaster = new CapturingBroadcaster();
        var driver = new RalphLoopDriver(
            test.NewContext(),
            new StubAgentRunner(),
            new GitService("/tmp/no-worktrees", new StubMergeResolver(), NullLogger<GitService>.Instance),
            new AgentRunnerOptions { MaxRunDuration = TimeSpan.FromHours(1) },
            new RalphLoopOptions { MaxRetryAttempts = 3 },
            broadcaster,
            NullLogger<RalphLoopDriver>.Instance,
            TimeProvider.System);

        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        using var verify = test.NewContext();
        var wiAfter = await verify.WorkItems.FindAsync(wi.Id);
        var taskAfter = await verify.AgentTasks.FindAsync(task.Id);
        Assert.NotNull(wiAfter);
        Assert.NotNull(taskAfter);
        Assert.False(wiAfter!.RalphLoopActive);
        Assert.Equal(AgentTaskStatus.Failed, taskAfter!.Status);
        Assert.Equal(3, taskAfter.RetryAttempts);
        Assert.NotNull(wiAfter.RalphLoopHaltReason);
        Assert.Contains("t1", wiAfter.RalphLoopHaltReason);
        Assert.Single(broadcaster.WorkItemUpdates);
    }

    [Fact]
    public async Task Advance_increments_retry_and_resets_task_to_Approved_when_under_cap()
    {
        using var test = TestDb.Create();
        var src = new Source { Name = "src-" + Guid.NewGuid().ToString("N")[..8], LocalRepoPath = "/tmp/r" };
        var wi = new WorkItem
        {
            SourceId = src.Id,
            ExternalId = "EXT-Y",
            Title = "wi",
            Status = WorkItemStatus.InProgress,
            RalphLoopActive = true,
        };
        var task = new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "t1",
            Order = 0,
            Status = AgentTaskStatus.Running,
            RetryAttempts = 0,
            WorktreePath = "/nope/does-not-exist",
        };
        var run = new AgentRun
        {
            AgentTaskId = task.Id,
            WorkItemId = wi.Id,
            Kind = AgentRunKind.TaskAgent,
            Status = AgentRunStatus.Crashed,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            EndedAt = DateTime.UtcNow,
            ExitCode = 1,
        };
        wi.Tasks.Add(task);
        task.Runs.Add(run);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var driver = new RalphLoopDriver(
            test.NewContext(),
            new StubAgentRunner(),
            new GitService("/tmp/no-worktrees", new StubMergeResolver(), NullLogger<GitService>.Instance),
            new AgentRunnerOptions { MaxRunDuration = TimeSpan.FromHours(1) },
            new RalphLoopOptions { MaxRetryAttempts = 3, MaxConcurrentTasksPerWorkItem = 0 }, // suppress top-up
            new CapturingBroadcaster(),
            NullLogger<RalphLoopDriver>.Instance,
            TimeProvider.System);

        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        using var verify = test.NewContext();
        var taskAfter = await verify.AgentTasks.FindAsync(task.Id);
        var wiAfter = await verify.WorkItems.FindAsync(wi.Id);
        Assert.NotNull(taskAfter);
        Assert.Equal(AgentTaskStatus.Approved, taskAfter!.Status);
        Assert.Equal(1, taskAfter.RetryAttempts);
        Assert.Null(taskAfter.WorktreePath);
        Assert.True(wiAfter!.RalphLoopActive);
    }
}
