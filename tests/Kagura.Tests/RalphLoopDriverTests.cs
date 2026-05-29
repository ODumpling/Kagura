using Kagura.Api.HostedServices;
using Kagura.Api.Services;
using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Interactive;
using Kagura.Core.Merge;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;
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

internal sealed class FakeTriageKickoff : ITriageKickoffService
{
    public List<Guid> Calls { get; } = new();
    public string? FailWith { get; set; }
    public Func<Guid, Task>? OnKickoff { get; set; }

    public async Task<TriageKickoffResult> KickoffAsync(Guid workItemId, CancellationToken ct = default)
    {
        Calls.Add(workItemId);
        if (OnKickoff is not null) await OnKickoff(workItemId);
        if (FailWith is not null) return TriageKickoffResult.Invalid(FailWith);
        return TriageKickoffResult.Accepted(Guid.NewGuid());
    }
}

internal sealed class FakeAutoReviewKickoff : IAutoReviewKickoffService
{
    public List<Guid> Calls { get; } = new();
    public string? FailWith { get; set; }
    public Func<Guid, Task>? OnKickoff { get; set; }

    public async Task<AutoReviewKickoffResult> KickoffAsync(Guid workItemId, CancellationToken ct = default)
    {
        Calls.Add(workItemId);
        if (OnKickoff is not null) await OnKickoff(workItemId);
        if (FailWith is not null) return AutoReviewKickoffResult.Invalid(FailWith);
        return AutoReviewKickoffResult.Accepted(Guid.NewGuid());
    }
}

internal sealed class StubPromptService : IInteractivePromptService
{
    private readonly Dictionary<Guid, List<InteractivePrompt>> _pending = new();

    public void AddPending(Guid runId, string question)
    {
        var list = _pending.GetValueOrDefault(runId) ?? new List<InteractivePrompt>();
        list.Add(new InteractivePrompt(Guid.NewGuid(), runId, question, null, DateTime.UtcNow));
        _pending[runId] = list;
    }

    public Task<string> AskAsync(Guid runId, string question, IReadOnlyList<string>? choices = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public bool TryAnswer(Guid promptId, string answer) => false;

    public IReadOnlyList<InteractivePrompt> GetPending(Guid runId) =>
        _pending.TryGetValue(runId, out var list) ? list : Array.Empty<InteractivePrompt>();
}

public class RalphLoopDriverTests
{
    private static RalphLoopDriver BuildDriver(
        TestDb test,
        IAgentBroadcaster? broadcaster = null,
        ITriageKickoffService? triageKickoff = null,
        IAutoReviewKickoffService? autoReviewKickoff = null,
        IInteractivePromptService? prompts = null,
        RalphLoopOptions? options = null) =>
        new RalphLoopDriver(
            test.NewContext(),
            new StubAgentRunner(),
            new GitService("/tmp/no-worktrees", new StubMergeResolver(), NullLogger<GitService>.Instance),
            new AgentRunnerOptions { MaxRunDuration = TimeSpan.FromHours(1) },
            options ?? new RalphLoopOptions { MaxRetryAttempts = 3, MaxConcurrentTasksPerWorkItem = 0 },
            broadcaster ?? new CapturingBroadcaster(),
            triageKickoff ?? new FakeTriageKickoff(),
            autoReviewKickoff ?? new FakeAutoReviewKickoff(),
            prompts ?? new StubPromptService(),
            NullLogger<RalphLoopDriver>.Instance,
            TimeProvider.System);

    private static (Source src, WorkItem wi) SeedWorkItem(WorkItemStatus status, bool ralphActive = true)
    {
        var src = new Source { Name = "src-" + Guid.NewGuid().ToString("N")[..8], LocalRepoPath = "/tmp/r" };
        var wi = new WorkItem
        {
            SourceId = src.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..6],
            Title = "wi",
            Status = status,
            RalphLoopActive = ralphActive,
        };
        return (src, wi);
    }

    // -------------------- Crashed retry / halt --------------------

    [Fact]
    public async Task Advance_marks_task_Failed_and_halts_loop_when_retry_cap_reached()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.InProgress);
        var task = new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "t1",
            Order = 0,
            Status = AgentTaskStatus.Running,
            RetryAttempts = 2,
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
        var driver = BuildDriver(test, broadcaster: broadcaster, options: new RalphLoopOptions { MaxRetryAttempts = 3 });

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
        Assert.Null(wiAfter.RalphLoopWaitingReason);
        Assert.Single(broadcaster.WorkItemUpdates);
    }

    [Fact]
    public async Task Advance_increments_retry_and_resets_task_to_Approved_when_under_cap()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.InProgress);
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

        var driver = BuildDriver(test, options: new RalphLoopOptions { MaxRetryAttempts = 3, MaxConcurrentTasksPerWorkItem = 0 });
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

    // -------------------- Terminal-state no-op --------------------

    [Theory]
    [InlineData(WorkItemStatus.PullRequested)]
    [InlineData(WorkItemStatus.Closed)]
    [InlineData(WorkItemStatus.Cancelled)]
    [InlineData(WorkItemStatus.Done)]
    public async Task Advance_is_noop_for_terminal_statuses(WorkItemStatus terminal)
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(terminal);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var broadcaster = new CapturingBroadcaster();
        var triage = new FakeTriageKickoff();
        var driver = BuildDriver(test, broadcaster: broadcaster, triageKickoff: triage);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Empty(triage.Calls);
        Assert.Empty(broadcaster.WorkItemUpdates);
    }

    // -------------------- New / triage spawn --------------------

    [Fact]
    public async Task Advance_spawns_triage_and_standbys_when_New_with_no_runs()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.New);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var triage = new FakeTriageKickoff();
        var driver = BuildDriver(test, triageKickoff: triage);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Single(triage.Calls);
        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.True(after!.RalphLoopActive);
        Assert.Equal("Triaging…", after.RalphLoopWaitingReason);
        Assert.Null(after.RalphLoopHaltReason);
    }

    [Fact]
    public async Task Advance_standbys_without_respawning_when_triage_run_in_flight()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.New);
        var run = new AgentRun
        {
            WorkItemId = wi.Id,
            Kind = AgentRunKind.Triage,
            Status = AgentRunStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        wi.Runs.Add(run);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var triage = new FakeTriageKickoff();
        var driver = BuildDriver(test, triageKickoff: triage);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Empty(triage.Calls);
        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.Equal("Triaging…", after!.RalphLoopWaitingReason);
        Assert.True(after.RalphLoopActive);
    }

    [Fact]
    public async Task Advance_halts_when_triage_run_left_a_LastTriageError()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.New);
        wi.LastTriageError = "claude exited 1";
        var run = new AgentRun
        {
            WorkItemId = wi.Id,
            Kind = AgentRunKind.Triage,
            Status = AgentRunStatus.Crashed,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            EndedAt = DateTime.UtcNow,
        };
        wi.Runs.Add(run);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var triage = new FakeTriageKickoff();
        var driver = BuildDriver(test, triageKickoff: triage);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Empty(triage.Calls);
        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.False(after!.RalphLoopActive);
        Assert.Equal("claude exited 1", after.RalphLoopHaltReason);
        Assert.Null(after.RalphLoopWaitingReason);
    }

    // -------------------- New + Proposed tasks --------------------

    [Fact]
    public async Task Advance_auto_approves_proposed_tasks_when_AutoApproveTriage_is_true()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.New);
        wi.AutoApproveTriage = true;
        wi.Tasks.Add(new AgentTask { WorkItemId = wi.Id, Title = "t1", Order = 0, Status = AgentTaskStatus.Proposed });
        wi.Tasks.Add(new AgentTask { WorkItemId = wi.Id, Title = "t2", Order = 1, Status = AgentTaskStatus.Proposed });
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var driver = BuildDriver(test);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        var tasksAfter = await verify.AgentTasks.Where(t => t.WorkItemId == wi.Id).ToListAsync();
        Assert.Equal(WorkItemStatus.Triaged, after!.Status);
        Assert.NotNull(after.TriagedAt);
        Assert.All(tasksAfter, t => Assert.Equal(AgentTaskStatus.Approved, t.Status));
        Assert.Null(after.RalphLoopWaitingReason);
    }

    [Fact]
    public async Task Advance_standbys_for_approval_when_AutoApproveTriage_is_false()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.New);
        wi.AutoApproveTriage = false;
        wi.Tasks.Add(new AgentTask { WorkItemId = wi.Id, Title = "t1", Order = 0, Status = AgentTaskStatus.Proposed });
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var driver = BuildDriver(test);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.Equal(WorkItemStatus.New, after!.Status);
        Assert.True(after.RalphLoopActive);
        Assert.Equal("Waiting for you to approve proposed tasks.", after.RalphLoopWaitingReason);
    }

    // -------------------- AwaitingReview branches --------------------

    [Fact]
    public async Task Advance_standbys_when_AutoReviewEnabled_is_false()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.InProgress);
        wi.AutoReviewEnabled = false;
        wi.Tasks.Add(new AgentTask { WorkItemId = wi.Id, Title = "t1", Order = 0, Status = AgentTaskStatus.AwaitingReview });
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var autoReview = new FakeAutoReviewKickoff();
        var driver = BuildDriver(test, autoReviewKickoff: autoReview);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Empty(autoReview.Calls);
        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.True(after!.RalphLoopActive);
        Assert.Equal("Waiting for you to review tasks.", after.RalphLoopWaitingReason);
    }

    [Fact]
    public async Task Advance_spawns_auto_review_when_AwaitingReview_and_enabled_and_no_run()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.InProgress);
        wi.Tasks.Add(new AgentTask { WorkItemId = wi.Id, Title = "t1", Order = 0, Status = AgentTaskStatus.AwaitingReview });
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var autoReview = new FakeAutoReviewKickoff();
        var driver = BuildDriver(test, autoReviewKickoff: autoReview);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Single(autoReview.Calls);
        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.Equal("Auto-reviewing…", after!.RalphLoopWaitingReason);
        Assert.True(after.RalphLoopActive);
    }

    [Fact]
    public async Task Advance_adopts_in_flight_auto_review_without_respawn()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.InProgress);
        wi.Tasks.Add(new AgentTask { WorkItemId = wi.Id, Title = "t1", Order = 0, Status = AgentTaskStatus.AwaitingReview });
        var run = new AgentRun
        {
            WorkItemId = wi.Id,
            Kind = AgentRunKind.AutoReview,
            Status = AgentRunStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(-30),
        };
        wi.Runs.Add(run);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var autoReview = new FakeAutoReviewKickoff();
        var driver = BuildDriver(test, autoReviewKickoff: autoReview);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Empty(autoReview.Calls);
        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.Equal("Auto-reviewing…", after!.RalphLoopWaitingReason);
    }

    [Fact]
    public async Task Advance_surfaces_pending_interaction_prompt_in_waiting_reason()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.InProgress);
        wi.Tasks.Add(new AgentTask { WorkItemId = wi.Id, Title = "t1", Order = 0, Status = AgentTaskStatus.AwaitingReview });
        var run = new AgentRun
        {
            WorkItemId = wi.Id,
            Kind = AgentRunKind.AutoReview,
            Status = AgentRunStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(-10),
        };
        wi.Runs.Add(run);
        wi.AutoReviewInteractions.Add(new AutoReviewInteraction
        {
            AgentRunId = run.Id,
            WorkItemId = wi.Id,
            Sequence = 1,
            Prompt = "Reviewer flagged 't1': diff looks risky. Override and merge?",
        });
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var driver = BuildDriver(test);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.StartsWith("Auto-review needs your input: ", after!.RalphLoopWaitingReason);
        Assert.Contains("Reviewer flagged", after.RalphLoopWaitingReason);
    }

    [Fact]
    public async Task Advance_halts_when_finished_auto_review_left_tasks_flagged()
    {
        using var test = TestDb.Create();
        var (src, wi) = SeedWorkItem(WorkItemStatus.InProgress);
        wi.Tasks.Add(new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "t1",
            Order = 0,
            Status = AgentTaskStatus.AwaitingReview,
            ReviewNotes = "Looks risky to me.",
        });
        wi.Tasks.Add(new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "t2",
            Order = 1,
            Status = AgentTaskStatus.AwaitingReview,
            ReviewNotes = "Also risky.",
        });
        var run = new AgentRun
        {
            WorkItemId = wi.Id,
            Kind = AgentRunKind.AutoReview,
            Status = AgentRunStatus.Exited,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            EndedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        wi.Runs.Add(run);
        test.Context.Sources.Add(src);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var autoReview = new FakeAutoReviewKickoff();
        var driver = BuildDriver(test, autoReviewKickoff: autoReview);
        await driver.AdvanceAsync(wi.Id, CancellationToken.None);

        Assert.Empty(autoReview.Calls);
        using var verify = test.NewContext();
        var after = await verify.WorkItems.FindAsync(wi.Id);
        Assert.False(after!.RalphLoopActive);
        Assert.NotNull(after.RalphLoopHaltReason);
        Assert.Contains("flagged 2 task(s)", after.RalphLoopHaltReason);
        Assert.Null(after.RalphLoopWaitingReason);
    }

    // -------------------- MarkClosed teardown --------------------

    [Fact]
    public void MarkClosed_deactivates_ralph_and_sets_halt_reason()
    {
        var (_, wi) = SeedWorkItem(WorkItemStatus.InProgress);
        wi.RalphLoopWaitingReason = "Triaging…";

        wi.MarkClosed();

        Assert.Equal(WorkItemStatus.Closed, wi.Status);
        Assert.False(wi.RalphLoopActive);
        Assert.Equal("Upstream issue closed during Ralph run", wi.RalphLoopHaltReason);
        Assert.Null(wi.RalphLoopWaitingReason);
    }

    [Fact]
    public void MarkClosed_leaves_halt_reason_unset_when_ralph_was_already_inactive()
    {
        var (_, wi) = SeedWorkItem(WorkItemStatus.InProgress, ralphActive: false);
        wi.RalphLoopWaitingReason = "stale";

        wi.MarkClosed();

        Assert.Equal(WorkItemStatus.Closed, wi.Status);
        Assert.False(wi.RalphLoopActive);
        Assert.Null(wi.RalphLoopHaltReason);
        Assert.Null(wi.RalphLoopWaitingReason);
    }
}
