using Kagura.Api.Services;
using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Interactive;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.HostedServices;

// One Advance pass per work item. Pure orchestration over Db/Git/Runner; meant to be
// driven by RalphLoopService on a 5-second tick. Safe to call directly from tests.
public sealed class RalphLoopDriver
{
    private const int WaitingReasonPromptPreviewLength = 80;

    private readonly KaguraDbContext _db;
    private readonly IAgentRunner _runner;
    private readonly GitService _git;
    private readonly AgentRunnerOptions _runnerOptions;
    private readonly RalphLoopOptions _options;
    private readonly IAgentBroadcaster _broadcaster;
    private readonly ITriageKickoffService _triageKickoff;
    private readonly IAutoReviewKickoffService _autoReviewKickoff;
    private readonly IInteractivePromptService _prompts;
    private readonly ILogger<RalphLoopDriver> _log;
    private readonly TimeProvider _clock;

    public RalphLoopDriver(
        KaguraDbContext db,
        IAgentRunner runner,
        GitService git,
        AgentRunnerOptions runnerOptions,
        RalphLoopOptions options,
        IAgentBroadcaster broadcaster,
        ITriageKickoffService triageKickoff,
        IAutoReviewKickoffService autoReviewKickoff,
        IInteractivePromptService prompts,
        ILogger<RalphLoopDriver> log,
        TimeProvider clock)
    {
        _db = db;
        _runner = runner;
        _git = git;
        _runnerOptions = runnerOptions;
        _options = options;
        _broadcaster = broadcaster;
        _triageKickoff = triageKickoff;
        _autoReviewKickoff = autoReviewKickoff;
        _prompts = prompts;
        _log = log;
        _clock = clock;
    }

    public async Task AdvanceAsync(Guid workItemId, CancellationToken ct)
    {
        var wi = await _db.WorkItems
            .Include(w => w.Source)
            .Include(w => w.Tasks).ThenInclude(t => t.Runs)
            .Include(w => w.Runs)
            .Include(w => w.AutoReviewInteractions)
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);

        if (wi is null || !wi.RalphLoopActive) return;

        // Defensive exit for terminal states; the loop should already have been torn down.
        if (wi.Status is WorkItemStatus.Closed or WorkItemStatus.PullRequested
            or WorkItemStatus.Cancelled or WorkItemStatus.Done)
        {
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        // Protective sweeps run every tick regardless of the WI state, because a Running task
        // hanging or crashing must always advance even mid-triage / mid-auto-review.
        await ApplyTimeoutsAsync(wi, now, ct);
        if (await HandleCrashedTasksAsync(wi, now, ct))
            return; // halted

        // Decision tree — picks the next applicable step.
        var changed = await DriveStateAsync(wi, now, ct);

        if (changed || _db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(ct);
            await _broadcaster.WorkItemUpdatedAsync(wi.Id);
        }
    }

    private async Task<bool> DriveStateAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        // Step 2/3 — Triage flow for a New work item.
        if (wi.Status == WorkItemStatus.New)
        {
            return await HandleNewAsync(wi, now, ct);
        }

        // For Triaged / InProgress / Merged we look at the tasks.
        // Step 6 — AwaitingReview branches (auto-review delegation).
        if (wi.Tasks.Any(t => t.Status == AgentTaskStatus.AwaitingReview))
        {
            return await HandleAwaitingReviewAsync(wi, now, ct);
        }

        // Step 7 — all tasks merged → open the PR.
        if (wi.Tasks.Count > 0 && wi.Tasks.All(t => t.Status == AgentTaskStatus.Merged))
        {
            await FinishWithPrAsync(wi, now, ct);
            return true;
        }

        // Step 4/5 — top up agent slots for any Approved tasks.
        await TopUpAgentSlotsAsync(wi, now, ct);
        return true;
    }

    // ---- Step 2/3: New ----

    private async Task<bool> HandleNewAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        // 3) If there are Proposed tasks, either auto-approve or stand by.
        var proposed = wi.Tasks.Where(t => t.Status == AgentTaskStatus.Proposed).ToList();
        if (proposed.Count > 0)
        {
            if (wi.AutoApproveTriage)
            {
                foreach (var t in proposed)
                {
                    t.Status = AgentTaskStatus.Approved;
                    t.UpdatedAt = now;
                }
                wi.Status = WorkItemStatus.Triaged;
                wi.TriagedAt = now;
                wi.RalphLoopWaitingReason = null;
                wi.UpdatedAt = now;
                return true;
            }

            SetWaitingReason(wi, "Waiting for you to approve proposed tasks.", now);
            return true;
        }

        // 2) No proposed tasks — drive triage.
        var latestTriage = LatestRun(wi.Runs, AgentRunKind.Triage);

        if (latestTriage is not null && latestTriage.Status == AgentRunStatus.Running)
        {
            SetWaitingReason(wi, "Triaging…", now);
            return true;
        }

        if (!string.IsNullOrEmpty(wi.LastTriageError))
        {
            Halt(wi, wi.LastTriageError!, now);
            return true;
        }

        // No run yet (or a previous run finished without proposals / error) — spawn one.
        var result = await _triageKickoff.KickoffAsync(wi.Id, ct);
        if (result.Error is not null)
        {
            Halt(wi, $"Failed to start triage: {result.Error}", now);
            return true;
        }

        // The kickoff inserted the AgentRun row in a separate DbContext, so refresh wi.Runs
        // by re-loading just the runs collection isn't strictly required — we just standby
        // and pick the run up next tick.
        SetWaitingReason(wi, "Triaging…", now);
        return true;
    }

    // ---- Step 6: AwaitingReview ----

    private async Task<bool> HandleAwaitingReviewAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        if (!wi.AutoReviewEnabled)
        {
            SetWaitingReason(wi, "Waiting for you to review tasks.", now);
            return true;
        }

        var latestAutoReview = LatestRun(wi.Runs, AgentRunKind.AutoReview);

        if (latestAutoReview is not null && latestAutoReview.Status == AgentRunStatus.Running)
        {
            var pendingPrompt = FindPendingPrompt(wi, latestAutoReview);
            if (pendingPrompt is not null)
            {
                var preview = Truncate(pendingPrompt, WaitingReasonPromptPreviewLength);
                SetWaitingReason(wi, $"Auto-review needs your input: {preview}", now);
            }
            else
            {
                SetWaitingReason(wi, "Auto-reviewing…", now);
            }
            return true;
        }

        if (latestAutoReview is not null && latestAutoReview.EndedAt is not null)
        {
            // A completed auto-review run that left some tasks still AwaitingReview with
            // ReviewNotes populated means the LLM flagged them for human attention.
            var flagged = wi.Tasks.Count(t =>
                t.Status == AgentTaskStatus.AwaitingReview &&
                !string.IsNullOrEmpty(t.ReviewNotes));
            if (flagged > 0)
            {
                Halt(wi, $"Auto-review flagged {flagged} task(s) for human review.", now);
                return true;
            }
        }

        // No run yet (or previous run finished cleanly) — spawn a fresh one.
        var result = await _autoReviewKickoff.KickoffAsync(wi.Id, ct);
        if (result.Error is not null)
        {
            Halt(wi, $"Failed to start auto-review: {result.Error}", now);
            return true;
        }
        SetWaitingReason(wi, "Auto-reviewing…", now);
        return true;
    }

    // ---- Step 5: timeout sweep ----

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
        }
    }

    // ---- Step 5: crashed-task retry sweep. Returns true if the loop halted. ----

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
            Halt(wi, haltReason!, now);
            await _db.SaveChangesAsync(ct);
            await _broadcaster.WorkItemUpdatedAsync(wi.Id);
            return true;
        }

        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(ct);
        return false;
    }

    // ---- Step 7: open the PR once every task is Merged ----

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
                wi.RalphLoopWaitingReason = null;
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
            Halt(wi, prError, DateTime.UtcNow);
        }
        else
        {
            try { await _git.RemoveWorkItemMergeWorktreeAsync(wi.Source.LocalRepoPath, wi, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Ralph: failed to clean up merge worktree for {WorkItemId}", wi.Id); }
        }
    }

    // ---- Step 4: top up agent slots ----

    private async Task TopUpAgentSlotsAsync(WorkItem wi, DateTime now, CancellationToken ct)
    {
        var inFlight = wi.Tasks.Count(t =>
            t.Status is AgentTaskStatus.Running or AgentTaskStatus.AwaitingReview);
        var capacity = _options.MaxConcurrentTasksPerWorkItem - inFlight;
        if (capacity <= 0)
        {
            // Clear any stale waiting reason while we wait for slots to free up; nothing to standby on.
            wi.RalphLoopWaitingReason = null;
            return;
        }

        var startable = wi.Tasks
            .Where(t => t.Status == AgentTaskStatus.Approved)
            .OrderBy(t => t.Order)
            .Take(capacity)
            .ToList();
        if (startable.Count == 0)
        {
            wi.RalphLoopWaitingReason = null;
            return;
        }

        if (wi.Status == WorkItemStatus.Triaged)
        {
            wi.Status = WorkItemStatus.InProgress;
            wi.UpdatedAt = now;
        }
        wi.RalphLoopWaitingReason = null;

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
                    WorkItemId = task.WorkItemId,
                    Kind = AgentRunKind.TaskAgent,
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
                    Halt(wi, $"Task '{task.Title}' failed to start after {task.RetryAttempts} attempts: {ex.Message}", DateTime.UtcNow);
                    await _db.SaveChangesAsync(ct);
                    await _broadcaster.WorkItemUpdatedAsync(wi.Id);
                    return;
                }
                await _db.SaveChangesAsync(ct);
            }
        }
    }

    // ---- helpers ----

    private static void SetWaitingReason(WorkItem wi, string reason, DateTime now)
    {
        if (wi.RalphLoopWaitingReason == reason && wi.UpdatedAt > DateTime.MinValue) return;
        wi.RalphLoopWaitingReason = reason;
        wi.UpdatedAt = now;
    }

    private static void Halt(WorkItem wi, string reason, DateTime now)
    {
        wi.RalphLoopActive = false;
        wi.RalphLoopHaltReason = reason;
        wi.RalphLoopWaitingReason = null;
        wi.UpdatedAt = now;
    }

    private static AgentRun? LatestRun(AgentTask task) =>
        task.Runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();

    private static AgentRun? LatestRun(IEnumerable<AgentRun> runs, AgentRunKind kind) =>
        runs.Where(r => r.Kind == kind).OrderByDescending(r => r.StartedAt).FirstOrDefault();

    private string? FindPendingPrompt(WorkItem wi, AgentRun run)
    {
        // Prefer persisted AutoReviewInteractions; fall back to the in-memory interactive
        // prompt service if no persisted row matches (the prompt service is what AskAsync
        // actually blocks on, so it is the authoritative "is there a pending prompt").
        var persisted = wi.AutoReviewInteractions
            .Where(i => i.AgentRunId == run.Id && i.IsPending)
            .OrderByDescending(i => i.Sequence)
            .FirstOrDefault();
        if (persisted is not null) return persisted.Prompt;

        var inMemory = _prompts.GetPending(run.Id).FirstOrDefault();
        return inMemory?.Question;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
