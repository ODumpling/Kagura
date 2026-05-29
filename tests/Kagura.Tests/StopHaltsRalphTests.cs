using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Data.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

/// <summary>
/// Acceptance tests for issue #70 Part B (Stop-halts-Ralph / Q13). When the user clicks Stop
/// on any orchestrated PTY Agent (Triage / Task / AutoReview / MergeResolver), the runner's
/// exit-recording sink must:
///   1. Mark the AgentRun as Killed.
///   2. Set <c>WorkItem.RalphLoopActive = false</c>.
///   3. Populate <c>WorkItem.RalphLoopHaltReason</c> with "User stopped &lt;Role&gt; Agent".
///
/// Grill is user-initiated, not orchestrator-spawned, so a user-Stop on a Grill Agent
/// MUST NOT halt Ralph — even if RalphLoopActive happens to be true for the work item.
///
/// These tests exercise <see cref="AgentRunSink.RecordExitAsync"/> with
/// <see cref="AgentExitReason.KilledByUser"/> directly because that's the contract
/// boundary the AgentRunner crosses on Stop — the runner-itself integration is covered
/// implicitly by the per-Role service tests that already exist.
/// </summary>
public class StopHaltsRalphTests
{
    private static readonly NullAgentBroadcaster _noopBroadcaster = new();

    [Theory]
    [InlineData(AgentRunKind.Triage, "Triage")]
    [InlineData(AgentRunKind.TaskAgent, "Task")]
    [InlineData(AgentRunKind.AutoReview, "AutoReview")]
    [InlineData(AgentRunKind.MergeResolver, "MergeResolver")]
    public async Task User_stop_on_orchestrated_role_halts_Ralph_with_role_specific_reason(
        AgentRunKind kind, string expectedRoleLabel)
    {
        using var db = TestDb.Create();
        var (wi, runId) = await SeedRunningAsync(db.Context, kind, ralphActive: true);

        var sink = new AgentRunSink(db.Context, _noopBroadcaster, NullLogger<AgentRunSink>.Instance);
        await sink.RecordExitAsync(runId, exitCode: null, AgentExitReason.KilledByUser);

        using var verify = db.NewContext();
        var refreshedWi = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(verify.WorkItems, w => w.Id == wi.Id);
        var refreshedRun = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(verify.AgentRuns, r => r.Id == runId);

        Assert.Equal(AgentRunStatus.Killed, refreshedRun.Status);
        Assert.False(refreshedWi.RalphLoopActive);
        Assert.Equal($"User stopped {expectedRoleLabel} Agent", refreshedWi.RalphLoopHaltReason);
        Assert.Null(refreshedWi.RalphLoopWaitingReason);
    }

    [Fact]
    public async Task User_stop_on_Grill_does_not_halt_Ralph_even_when_loop_is_active()
    {
        using var db = TestDb.Create();
        var (wi, runId) = await SeedRunningAsync(db.Context, AgentRunKind.Grill, ralphActive: true);

        var sink = new AgentRunSink(db.Context, _noopBroadcaster, NullLogger<AgentRunSink>.Instance);
        await sink.RecordExitAsync(runId, exitCode: null, AgentExitReason.KilledByUser);

        using var verify = db.NewContext();
        var refreshedWi = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(verify.WorkItems, w => w.Id == wi.Id);
        var refreshedRun = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(verify.AgentRuns, r => r.Id == runId);

        Assert.Equal(AgentRunStatus.Killed, refreshedRun.Status);
        // Grill is exempt from Stop-halts-Ralph: the user is in control, the orchestrator
        // wasn't driving it.
        Assert.True(refreshedWi.RalphLoopActive);
        Assert.Null(refreshedWi.RalphLoopHaltReason);
    }

    [Fact]
    public async Task User_stop_does_not_clobber_existing_halt_state_when_Ralph_already_inactive()
    {
        using var db = TestDb.Create();
        var (wi, runId) = await SeedRunningAsync(db.Context, AgentRunKind.Triage, ralphActive: false);
        // Ralph is already inactive — no halt-Ralph mutation should fire.
        var sink = new AgentRunSink(db.Context, _noopBroadcaster, NullLogger<AgentRunSink>.Instance);
        await sink.RecordExitAsync(runId, exitCode: null, AgentExitReason.KilledByUser);

        using var verify = db.NewContext();
        var refreshedWi = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(verify.WorkItems, w => w.Id == wi.Id);
        Assert.False(refreshedWi.RalphLoopActive);
        Assert.Null(refreshedWi.RalphLoopHaltReason); // still empty
    }

    [Fact]
    public async Task Clean_exit_on_orchestrated_role_does_not_halt_Ralph()
    {
        using var db = TestDb.Create();
        var (wi, runId) = await SeedRunningAsync(db.Context, AgentRunKind.AutoReview, ralphActive: true);

        var sink = new AgentRunSink(db.Context, _noopBroadcaster, NullLogger<AgentRunSink>.Instance);
        // CompletedCleanly is the Agent's own clean MCP-submission exit — must NOT halt Ralph.
        await sink.RecordExitAsync(runId, exitCode: 0, AgentExitReason.CompletedCleanly);

        using var verify = db.NewContext();
        var refreshedWi = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(verify.WorkItems, w => w.Id == wi.Id);
        Assert.True(refreshedWi.RalphLoopActive);
        Assert.Null(refreshedWi.RalphLoopHaltReason);
    }

    private static async Task<(WorkItem wi, Guid runId)> SeedRunningAsync(
        Kagura.Data.KaguraDbContext db,
        AgentRunKind kind,
        bool ralphActive)
    {
        var source = new Source { Name = "src-" + Guid.NewGuid().ToString("N")[..8], LocalRepoPath = "/tmp/r" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "stop-halts-ralph",
            RalphLoopActive = ralphActive,
        };
        var run = new AgentRun
        {
            Kind = kind,
            WorkItemId = wi.Id,
            Status = AgentRunStatus.Running,
        };
        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        return (wi, run.Id);
    }

}
