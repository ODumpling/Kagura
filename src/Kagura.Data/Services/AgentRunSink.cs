using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kagura.Data.Services;

public sealed class AgentRunSink : IAgentRunSink
{
    private readonly KaguraDbContext _db;
    private readonly IAgentBroadcaster _broadcaster;
    private readonly ILogger<AgentRunSink> _log;

    public AgentRunSink(KaguraDbContext db, IAgentBroadcaster broadcaster, ILogger<AgentRunSink> log)
    {
        _db = db;
        _broadcaster = broadcaster;
        _log = log;
    }

    public async Task RecordExitAsync(Guid runId, int? exitCode, AgentExitReason? overrideReason, CancellationToken ct = default)
    {
        var run = await _db.AgentRuns
            .Include(r => r.AgentTask)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            _log.LogWarning("AgentRunSink: run {RunId} not found on exit", runId);
            return;
        }

        if (run.Status is AgentRunStatus.Exited or AgentRunStatus.Killed or AgentRunStatus.Crashed)
            return;

        var reason = overrideReason ?? (run.AgentTask?.Status == AgentTaskStatus.AwaitingReview
            ? AgentExitReason.CompletedCleanly
            : AgentExitReason.Crashed);

        run.Status = reason switch
        {
            AgentExitReason.CompletedCleanly => AgentRunStatus.Exited,
            AgentExitReason.KilledByUser => AgentRunStatus.Killed,
            AgentExitReason.KilledByTimeout => AgentRunStatus.Killed,
            _ => AgentRunStatus.Crashed,
        };
        run.ExitCode = exitCode;
        run.EndedAt = DateTime.UtcNow;

        if (reason is AgentExitReason.Crashed or AgentExitReason.KilledByTimeout && run.AgentTask is not null)
        {
            run.AgentTask.LastFailureReason = reason == AgentExitReason.KilledByTimeout
                ? $"Killed after exceeding max run duration (exit code {exitCode?.ToString() ?? "null"})"
                : $"Agent process exited without calling /complete (exit code {exitCode?.ToString() ?? "null"})";
            run.AgentTask.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        if (run.AgentTask is not null)
            await _broadcaster.WorkItemUpdatedAsync(run.AgentTask.WorkItemId);
    }
}
