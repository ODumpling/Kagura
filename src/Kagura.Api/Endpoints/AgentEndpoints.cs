using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record StartAgentDto(Guid TaskId);

public record AgentRunDto(
    Guid RunId,
    Guid TaskId,
    Guid WorkItemId,
    string WorktreePath,
    int ProcessId,
    DateTime StartedAt,
    bool Alive,
    int? ExitCode);

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/agents");

        grp.MapGet("", (IAgentRunner runner) =>
            Results.Ok(runner.Active.Select(ToDto)));

        grp.MapPost("/start/{taskId:guid}", async (
            Guid taskId,
            KaguraDbContext db,
            IAgentRunner runner,
            CancellationToken ct) =>
        {
            var task = await db.AgentTasks
                .Include(t => t.WorkItem)
                .ThenInclude(w => w.Source)
                .FirstOrDefaultAsync(t => t.Id == taskId, ct);
            if (task is null) return Results.NotFound();

            if (task.Status is not (AgentTaskStatus.Approved or AgentTaskStatus.AwaitingReview))
                return Results.BadRequest(new { error = $"Task is in status {task.Status}; must be Approved" });

            var session = await runner.StartAsync(task.WorkItem, task, task.WorkItem.Source.LocalRepoPath, ct);

            task.Status = AgentTaskStatus.Running;
            task.BranchName ??= System.IO.Path.GetFileName(session.WorktreePath);
            task.WorktreePath = session.WorktreePath;
            task.UpdatedAt = DateTime.UtcNow;

            db.AgentRuns.Add(new AgentRun
            {
                Id = session.RunId,
                AgentTaskId = task.Id,
                Status = AgentRunStatus.Running,
                ProcessId = session.ProcessId,
                TranscriptLogPath = session.TranscriptLogPath,
            });

            if (task.WorkItem.Status == WorkItemStatus.Triaged)
            {
                task.WorkItem.Status = WorkItemStatus.InProgress;
                task.WorkItem.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(session));
        });

        grp.MapPost("/{runId:guid}/stop", async (Guid runId, IAgentRunner runner, KaguraDbContext db, CancellationToken ct) =>
        {
            var session = runner.Get(runId);
            await runner.StopAsync(runId);

            var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is not null)
            {
                run.Status = AgentRunStatus.Killed;
                run.EndedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        return app;
    }

    private static AgentRunDto ToDto(AgentSession s) => new(
        s.RunId, s.TaskId, s.WorkItemId, s.WorktreePath, s.ProcessId,
        s.StartedAt, s.Alive, s.ExitCode);
}
