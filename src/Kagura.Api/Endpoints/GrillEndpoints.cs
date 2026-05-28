using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Grill;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record WorkItemCommentDto(
    Guid Id,
    Guid WorkItemId,
    WorkItemCommentRole Role,
    string Content,
    DateTime CreatedAt);

public record PostGrillCommentDto(string Content);

public record GrillStateDto(
    Guid WorkItemId,
    GrillStatus Status,
    string? OriginalBody,
    IReadOnlyList<WorkItemCommentDto> Comments);

public record FinalizeGrillResultDto(
    Guid WorkItemId,
    string Body,
    string? OriginalBody,
    GrillStatus Status);

public static class GrillEndpoints
{
    public static IEndpointRouteBuilder MapGrillEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/workitems/{workItemId:guid}/grill");

        grp.MapGet("", async (Guid workItemId, KaguraDbContext db, CancellationToken ct) =>
        {
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();

            var comments = await db.WorkItemComments
                .Where(c => c.WorkItemId == workItemId)
                .OrderBy(c => c.CreatedAt)
                .Select(c => new WorkItemCommentDto(c.Id, c.WorkItemId, c.Role, c.Content, c.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(new GrillStateDto(wi.Id, wi.GrillStatus, wi.OriginalBody, comments));
        });

        grp.MapPost("/comments", async (
            Guid workItemId,
            PostGrillCommentDto dto,
            KaguraDbContext db,
            IGrillService grill,
            IAgentBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Content))
                return Results.BadRequest(new { error = "Comment must not be empty." });

            var wi = await db.WorkItems
                .Include(w => w.Comments)
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();
            if (wi.Status == WorkItemStatus.Closed)
                return Results.BadRequest(new { error = "Work item is closed." });
            if (wi.GrillStatus == GrillStatus.Finalized)
                return Results.BadRequest(new { error = "Grill session has already been finalized." });

            var now = DateTime.UtcNow;

            var userComment = new WorkItemComment
            {
                WorkItemId = wi.Id,
                Role = WorkItemCommentRole.User,
                Content = dto.Content.Trim(),
                CreatedAt = now,
            };
            db.WorkItemComments.Add(userComment);
            wi.GrillStatus = GrillStatus.Active;
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            var history = await db.WorkItemComments
                .Where(c => c.WorkItemId == wi.Id)
                .OrderBy(c => c.CreatedAt)
                .Select(c => new GrillTurn(c.Role, c.Content))
                .ToListAsync(ct);

            var reply = await grill.RespondAsync(wi.Title, wi.Body, wi.Labels, history, ct);

            var assistantComment = new WorkItemComment
            {
                WorkItemId = wi.Id,
                Role = WorkItemCommentRole.Assistant,
                Content = reply,
                CreatedAt = DateTime.UtcNow,
            };
            db.WorkItemComments.Add(assistantComment);
            wi.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.Ok(new[]
            {
                new WorkItemCommentDto(userComment.Id, wi.Id, userComment.Role, userComment.Content, userComment.CreatedAt),
                new WorkItemCommentDto(assistantComment.Id, wi.Id, assistantComment.Role, assistantComment.Content, assistantComment.CreatedAt),
            });
        });

        grp.MapPost("/start", async (
            Guid workItemId,
            KaguraDbContext db,
            IGrillService grill,
            IAgentBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Comments)
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();
            if (wi.Status == WorkItemStatus.Closed)
                return Results.BadRequest(new { error = "Work item is closed." });
            if (wi.GrillStatus == GrillStatus.Finalized)
                return Results.BadRequest(new { error = "Grill session has already been finalized." });
            if (wi.Comments.Count > 0)
                return Results.BadRequest(new { error = "Grill already in progress." });

            var reply = await grill.RespondAsync(wi.Title, wi.Body, wi.Labels, Array.Empty<GrillTurn>(), ct);

            var now = DateTime.UtcNow;
            var assistantComment = new WorkItemComment
            {
                WorkItemId = wi.Id,
                Role = WorkItemCommentRole.Assistant,
                Content = reply,
                CreatedAt = now,
            };
            db.WorkItemComments.Add(assistantComment);
            wi.GrillStatus = GrillStatus.Active;
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.Ok(new WorkItemCommentDto(
                assistantComment.Id, wi.Id, assistantComment.Role, assistantComment.Content, assistantComment.CreatedAt));
        });

        grp.MapPost("/finalize", async (
            Guid workItemId,
            KaguraDbContext db,
            IGrillService grill,
            IAgentBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Comments)
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();
            if (wi.Status == WorkItemStatus.Closed)
                return Results.BadRequest(new { error = "Work item is closed." });
            if (wi.Comments.Count == 0)
                return Results.BadRequest(new { error = "Nothing to finalize — no grill conversation yet." });

            var history = wi.Comments
                .OrderBy(c => c.CreatedAt)
                .Select(c => new GrillTurn(c.Role, c.Content))
                .ToList();

            var originalForSynth = wi.OriginalBody ?? wi.Body;
            var rewritten = await grill.SynthesizeAsync(wi.Title, originalForSynth, wi.Labels, history, ct);

            var now = DateTime.UtcNow;
            wi.OriginalBody ??= wi.Body;
            wi.Body = rewritten;
            wi.GrillStatus = GrillStatus.Finalized;
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.Ok(new FinalizeGrillResultDto(wi.Id, wi.Body, wi.OriginalBody, wi.GrillStatus));
        });

        grp.MapPost("/reset", async (
            Guid workItemId,
            KaguraDbContext db,
            IAgentBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Comments)
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();

            db.WorkItemComments.RemoveRange(wi.Comments);

            if (wi.GrillStatus == GrillStatus.Finalized && wi.OriginalBody is not null)
            {
                wi.Body = wi.OriginalBody;
            }
            wi.OriginalBody = null;
            wi.GrillStatus = GrillStatus.None;
            wi.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.NoContent();
        });

        return app;
    }
}
