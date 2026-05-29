using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record AutoReviewInteractionDto(
    Guid Id,
    Guid AgentRunId,
    Guid WorkItemId,
    Guid? AgentTaskId,
    int Sequence,
    string Prompt,
    string? Response,
    DateTime CreatedAt,
    DateTime? RespondedAt,
    bool IsPending);

public record AutoReviewStateDto(
    Guid? RunId,
    AgentRunStatus? RunStatus,
    DateTime? RunStartedAt,
    DateTime? RunEndedAt,
    AutoReviewInteractionDto? PendingPrompt,
    IReadOnlyList<AutoReviewInteractionDto> Interactions);

public record CreateAutoReviewPromptDto(Guid? AgentTaskId, string Prompt);

public record RespondToAutoReviewPromptDto(string Response);

public static class AutoReviewInteractionEndpoints
{
    // Persistence surface for interactive auto-review. The Run loop (task 01) writes pending
    // prompts via POST /api/agents/{runId}/auto-review/prompts; the UI (task 02) reads the
    // pending question + transcript via GET /api/workitems/{id}/auto-review/state and posts the
    // user's answer via POST /api/auto-review/interactions/{id}/respond.
    public static IEndpointRouteBuilder MapAutoReviewInteractionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workitems/{workItemId:guid}/auto-review/state", async (
            Guid workItemId,
            KaguraDbContext db,
            CancellationToken ct) =>
        {
            var run = await db.AgentRuns
                .Where(r => r.WorkItemId == workItemId && r.Kind == AgentRunKind.AutoReview)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct);

            if (run is null)
                return Results.Ok(new AutoReviewStateDto(
                    RunId: null,
                    RunStatus: null,
                    RunStartedAt: null,
                    RunEndedAt: null,
                    PendingPrompt: null,
                    Interactions: Array.Empty<AutoReviewInteractionDto>()));

            var interactions = await db.AutoReviewInteractions
                .Where(i => i.AgentRunId == run.Id)
                .OrderBy(i => i.Sequence)
                .ToListAsync(ct);

            var dtos = interactions.Select(ToDto).ToList();
            var pending = dtos.LastOrDefault(d => d.IsPending);

            return Results.Ok(new AutoReviewStateDto(
                RunId: run.Id,
                RunStatus: run.Status,
                RunStartedAt: run.StartedAt,
                RunEndedAt: run.EndedAt,
                PendingPrompt: pending,
                Interactions: dtos));
        });

        app.MapPost("/api/agents/{runId:guid}/auto-review/prompts", async (
            Guid runId,
            CreateAutoReviewPromptDto body,
            KaguraDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Prompt))
                return Results.BadRequest(new { error = "Prompt cannot be empty." });

            var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null) return Results.NotFound();
            if (run.Kind != AgentRunKind.AutoReview)
                return Results.BadRequest(new { error = $"Run {runId} is {run.Kind}; only AutoReview runs accept prompts." });

            var nextSeq = await db.AutoReviewInteractions
                .Where(i => i.AgentRunId == runId)
                .Select(i => (int?)i.Sequence)
                .MaxAsync(ct) ?? -1;

            var interaction = new AutoReviewInteraction
            {
                AgentRunId = runId,
                WorkItemId = run.WorkItemId,
                AgentTaskId = body.AgentTaskId,
                Sequence = nextSeq + 1,
                Prompt = body.Prompt,
            };
            db.AutoReviewInteractions.Add(interaction);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/auto-review/interactions/{interaction.Id}", ToDto(interaction));
        });

        app.MapPost("/api/auto-review/interactions/{interactionId:guid}/respond", async (
            Guid interactionId,
            RespondToAutoReviewPromptDto body,
            KaguraDbContext db,
            CancellationToken ct) =>
        {
            if (body.Response is null)
                return Results.BadRequest(new { error = "Response cannot be null." });

            var interaction = await db.AutoReviewInteractions.FirstOrDefaultAsync(i => i.Id == interactionId, ct);
            if (interaction is null) return Results.NotFound();
            if (interaction.Response is not null)
                return Results.BadRequest(new { error = "Prompt has already been answered." });

            interaction.Response = body.Response;
            interaction.RespondedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToDto(interaction));
        });

        return app;
    }

    internal static AutoReviewInteractionDto ToDto(AutoReviewInteraction i) => new(
        i.Id, i.AgentRunId, i.WorkItemId, i.AgentTaskId, i.Sequence,
        i.Prompt, i.Response, i.CreatedAt, i.RespondedAt, i.Response is null);
}
