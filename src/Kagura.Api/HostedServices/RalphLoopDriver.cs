using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.HostedServices;

// One Advance pass per work item. Pure orchestration over Db/Git/Runner; meant to be
// driven by RalphLoopService on a 5-second tick. Safe to call directly from tests.
public sealed class RalphLoopDriver
{
    private readonly KaguraDbContext _db;
    private readonly IAgentRunner _runner;
    private readonly GitService _git;
    private readonly AgentRunnerOptions _runnerOptions;
    private readonly RalphLoopOptions _options;
    private readonly IAgentBroadcaster _broadcaster;
    private readonly ILogger<RalphLoopDriver> _log;
    private readonly TimeProvider _clock;

    public RalphLoopDriver(
        KaguraDbContext db,
        IAgentRunner runner,
        GitService git,
        AgentRunnerOptions runnerOptions,
        RalphLoopOptions options,
        IAgentBroadcaster broadcaster,
        ILogger<RalphLoopDriver> log,
        TimeProvider clock)
    {
        _db = db;
        _runner = runner;
        _git = git;
        _runnerOptions = runnerOptions;
        _options = options;
        _broadcaster = broadcaster;
        _log = log;
        _clock = clock;
    }

    public async Task AdvanceAsync(Guid workItemId, CancellationToken ct)
    {
        var wi = await _db.WorkItems
            .Include(w => w.Source)
            .Include(w => w.Tasks).ThenInclude(t => t.Runs)
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);

        if (wi is null || !wi.RalphLoopActive) return;

        var now = _clock.GetUtcNow().UtcDateTime;

        // Step 1: kill any agent runs past MaxRunDuration; sink will record KilledByTimeout.
        await ApplyTimeoutsAsync(wi, now, ct);

        // Step 2: any task that is Running but whose latest run already exited (Crashed/Killed)
        // is a failed attempt — increment RetryAttempts, reset branch, or move to Failed.
        if (await HandleCrashedTasksAsync(wi, now, ct))
        {
            // halted
            return;
        }

        // Step 3: merge AwaitingReview tasks in Order. Strict in-order merge into the WI branch.
        if (await MergeAwaitingReviewInOrderAsync(wi, now, ct))
        {
            // halted on merge failure
            return;
        }

        // Step 4: if everything's Merged, open the PR and finish.
        if (wi.Tasks.All(t => t.Status == AgentTaskStatus.Merged) && wi.Tasks.Count > 0)
        {
            await FinishWithPrAsync(wi, now, ct);
            return;
        }

        // Step 5: top up agent slots — start the next Approved tasks in Order
        // up to MaxConcurrentTasksPerWorkItem in-flight.
        await TopUpAgentSlotsAsync(wi, now, ct);

        await _db.SaveChangesAsync(ct);
        await _broadcaster.WorkItemUpdatedAsync(wi.Id);
    }

    private async Task ApplyTimeoutsAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        foreach (var task in wi.Tasks.Where(t => t.Status == AgentTaskStatus.Running))
        {
            var latestRun = LatestRun(task);
            if (latestRun is null) continue;
            if (latestRun.EndedAt is not null) continue;
            if ((now - latestRun.StartedAt) <= _runnerOptions.MaxRunDuration) continue;

            _log.LogWarning("Ralph: killing run {RunId} for task {TaskId} after exceeding {Max}",
                latestRun.Id, task.Id, _runnerOptions.MaxRunDuration);

            _runner.MarkExitReason(latestRun.Id, AgentExitReason.KilledByTimeout);
            try
            {
                await _runner.StopAsync(latestRun.Id);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ralph: StopAsync failed for run {RunId}", latestRun.Id);
            }
            // The sink writes the AgentRun row on OnExit. The next tick observes Status != Running and counts the failure.
        }
    }

    // Returns true if the loop halted (and saved + broadcast).
    private async Task<bool> HandleCrashedTasksAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        var halted = false;
        string? haltReason = null;

        foreach (var task in wi.Tasks.Where(t => t.Status == AgentTaskStatus.Running).ToList())
        {
            var latestRun = LatestRun(task);
            if (latestRun is null) continue;
            if (latestRun.Status is not (AgentRunStatus.Crashed or AgentRunStatus.Killed)) continue;

            task.RetryAttempts++;
            task.UpdatedAt = now;

            if (task.RetryAttempts >= _options.MaxRetryAttempts)
            {
                task.Status = AgentTaskStatus.Failed;
                halted = true;
                haltReason ??= $"Task '{task.Title}' failed after {task.RetryAttempts} attempts. " +
                               $"Last failure: {task.LastFailureReason ?? "unknown"}";
                continue;
            }

            // Clean slate for the next attempt: drop the worktree and reset the task branch.
            try
            {
                await _git.ResetTaskBranchAsync(wi.Source.LocalRepoPath, wi, task, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ralph: failed to reset task branch for {TaskId}", task.Id);
            }
            task.WorktreePath = null;
            task.Status = AgentTaskStatus.Approved;
        }

        if (halted)
        {
            wi.RalphLoopActive = false;
            wi.RalphLoopHaltReason = haltReason;
            wi.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await _broadcaster.WorkItemUpdatedAsync(wi.Id);
            return true;
        }

        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(ct);
        return false;
    }

    // Returns true if a merge failure halted the loop.
    private async Task<bool> MergeAwaitingReviewInOrderAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        foreach (var task in wi.Tasks.OrderBy(t => t.Order))
        {
            if (task.Status == AgentTaskStatus.Merged) continue;
            if (task.Status != AgentTaskStatus.AwaitingReview) break; // strict in-order: stop at first not-ready slot

            try
            {
                var result = await _git.MergeTaskBranchAsync(wi.Source.LocalRepoPath, wi, task, ct);
                if (!string.IsNullOrEmpty(task.WorktreePath))
                    await _git.RemoveWorktreeAsync(wi.Source.LocalRepoPath, task.WorktreePath, ct);
                task.Status = AgentTaskStatus.Merged;
                if (result.Outcome == MergeOutcome.MergedByAgent)
                    task.ReviewNotes = $"Conflicts resolved by AI: {result.Notes}";
                task.UpdatedAt = now;
                wi.BranchName ??= _git.WorkItemBranchName(wi);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ralph: merge failed for task {TaskId}", task.Id);
                wi.RalphLoopActive = false;
                wi.RalphLoopHaltReason = $"Merge failed for task '{task.Title}': {ex.Message}";
                wi.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.WorkItemUpdatedAsync(wi.Id);
                return true;
            }
        }

        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(ct);
        return false;
    }

    private async Task FinishWithPrAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        wi.BranchName ??= _git.WorkItemBranchName(wi);
        if (wi.Status != WorkItemStatus.PullRequested)
            wi.Status = WorkItemStatus.Merged;
        wi.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        string? prError = null;
        try
        {
            var prUrl = await _git.OpenPullRequestAsync(wi.Source.LocalRepoPath, wi, ct);
            if (prUrl is not null)
            {
                wi.PullRequestUrl = prUrl;
                wi.Status = WorkItemStatus.PullRequested;
                wi.RalphLoopActive = false;
                wi.RalphLoopHaltReason = null;
                wi.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                prError = "PR creation failed (push or `gh pr create` failed — check API logs).";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ralph: OpenPullRequestAsync threw for work item {WorkItemId}", wi.Id);
            prError = ex.Message;
        }

        if (prError is not null)
        {
            wi.RalphLoopActive = false;
            wi.RalphLoopHaltReason = prError;
            wi.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            try { await _git.RemoveWorkItemMergeWorktreeAsync(wi.Source.LocalRepoPath, wi, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Ralph: failed to clean up merge worktree for {WorkItemId}", wi.Id); }
        }

        await _db.SaveChangesAsync(ct);
        await _broadcaster.WorkItemUpdatedAsync(wi.Id);
    }

    private async Task TopUpAgentSlotsAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        var inFlight = wi.Tasks.Count(t =>
            t.Status is AgentTaskStatus.Running or AgentTaskStatus.AwaitingReview);
        var capacity = _options.MaxConcurrentTasksPerWorkItem - inFlight;
        if (capacity <= 0) return;

        var startable = wi.Tasks
            .Where(t => t.Status == AgentTaskStatus.Approved)
            .OrderBy(t => t.Order)
            .Take(capacity)
            .ToList();
        if (startable.Count == 0) return;

        if (wi.Status == WorkItemStatus.Triaged)
        {
            wi.Status = WorkItemStatus.InProgress;
            wi.UpdatedAt = now;
        }

        foreach (var task in startable)
        {
            try
            {
                var session = await _runner.StartAsync(wi, task, wi.Source.LocalRepoPath, ct);
                task.Status = AgentTaskStatus.Running;
                task.BranchName ??= Path.GetFileName(session.WorktreePath);
                task.WorktreePath = session.WorktreePath;
                task.UpdatedAt = DateTime.UtcNow;

                _db.AgentRuns.Add(new AgentRun
                {
                    Id = session.RunId,
                    AgentTaskId = task.Id,
                    Status = AgentRunStatus.Running,
                    ProcessId = session.ProcessId,
                    TranscriptLogPath = session.TranscriptLogPath,
                });
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ralph: failed to start task {TaskId}", task.Id);
                task.RetryAttempts++;
                task.LastFailureReason = $"Failed to start agent: {ex.Message}";
                task.UpdatedAt = DateTime.UtcNow;

                if (task.RetryAttempts >= _options.MaxRetryAttempts)
                {
                    task.Status = AgentTaskStatus.Failed;
                    wi.RalphLoopActive = false;
                    wi.RalphLoopHaltReason = $"Task '{task.Title}' failed to start after {task.RetryAttempts} attempts: {ex.Message}";
                    wi.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    await _broadcaster.WorkItemUpdatedAsync(wi.Id);
                    return;
                }
                await _db.SaveChangesAsync(ct);
            }
        }
    }

    private static AgentRun? LatestRun(AgentTask task) =>
        task.Runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();
}
