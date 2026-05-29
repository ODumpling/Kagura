using Kagura.Core.Review;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record RaiseReviewPromptRequest(
    Guid? TaskId,
    Guid? RunId,
    string Question,
    IReadOnlyList<ReviewPromptOption> Options);

public record SubmitReviewPromptResponseRequest(
    string SelectedOptionId,
    string? Notes);

public static class ReviewPromptEndpoints
{
    public static IEndpointRouteBuilder MapReviewPromptEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/workitems/{workItemId:guid}/auto-review/prompts");

        grp.MapGet("", async (
            Guid workItemId,
            IReviewPromptCoordinator coordinator,
            KaguraDbContext db,
            CancellationToken ct) =>
        {
            var exists = await db.WorkItems.AnyAsync(w => w.Id == workItemId, ct);
            if (!exists) return Results.NotFound();
            return Results.Ok(coordinator.GetPending(workItemId));
        });

        // Raise a prompt — typically called by the auto-review service when it needs user input.
        // Exposed as a public endpoint so task 01 (and integration callers) can drive it from any
        // process; the in-memory coordinator is the source of truth for pending prompts.
        grp.MapPost("", async (
            Guid workItemId,
            RaiseReviewPromptRequest dto,
            IReviewPromptCoordinator coordinator,
            KaguraDbContext db,
            CancellationToken ct) =>
        {
            var exists = await db.WorkItems.AnyAsync(w => w.Id == workItemId, ct);
            if (!exists) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(dto.Question))
                return Results.BadRequest(new { error = "Question must not be empty." });
            if (dto.Options is null || dto.Options.Count == 0)
                return Results.BadRequest(new { error = "At least one option is required." });

            var prompt = coordinator.Raise(workItemId, dto.TaskId, dto.RunId, dto.Question, dto.Options);
            return Results.Created($"/api/workitems/{workItemId}/auto-review/prompts/{prompt.Id}", prompt);
        });

        grp.MapPost("/{promptId:guid}/respond", (
            Guid workItemId,
            Guid promptId,
            SubmitReviewPromptResponseRequest dto,
            IReviewPromptCoordinator coordinator) =>
        {
            if (string.IsNullOrWhiteSpace(dto.SelectedOptionId))
                return Results.BadRequest(new { error = "SelectedOptionId is required." });

            // Guard ownership before resolving so a wrong-workitem call does not consume the prompt.
            var pending = coordinator.GetPending(workItemId);
            if (pending.All(p => p.Id != promptId))
                return Results.NotFound(new { error = "Prompt not found on this work item." });

            if (!coordinator.TryResolve(promptId, dto.SelectedOptionId, dto.Notes, out var response))
                return Results.BadRequest(new { error = "Selected option is invalid or prompt was already resolved." });

            return Results.Ok(response);
        });

        return app;
    }
}
