using System.Text.Json;
using Kagura.Api.Services;
using Kagura.Core.Domain;
using Kagura.Data;
using Kagura.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record SourceDto(
    Guid Id,
    string Name,
    SourceType Type,
    string LocalRepoPath,
    JsonElement Config,
    bool Enabled,
    bool AutoTriageOnImport,
    DateTime? LastSyncedAt,
    DateTime CreatedAt);

public record UpsertSourceDto(
    string Name,
    SourceType Type,
    string LocalRepoPath,
    JsonElement Config,
    bool Enabled = true,
    bool AutoTriageOnImport = false);

public static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/sources");

        grp.MapGet("", async (KaguraDbContext db) =>
        {
            var rows = await db.Sources.OrderBy(s => s.Name).ToListAsync();
            return Results.Ok(rows.Select(ToDto));
        });

        grp.MapGet("/{id:guid}", async (Guid id, KaguraDbContext db) =>
        {
            var s = await db.Sources.FindAsync(id);
            return s is null ? Results.NotFound() : Results.Ok(ToDto(s));
        });

        grp.MapPost("", async (UpsertSourceDto dto, KaguraDbContext db) =>
        {
            var s = new Source
            {
                Name = dto.Name,
                Type = dto.Type,
                LocalRepoPath = dto.LocalRepoPath,
                ConfigJson = dto.Config.GetRawText(),
                Enabled = dto.Enabled,
                AutoTriageOnImport = dto.AutoTriageOnImport,
            };
            db.Sources.Add(s);
            await db.SaveChangesAsync();
            return Results.Created($"/api/sources/{s.Id}", ToDto(s));
        });

        grp.MapPut("/{id:guid}", async (Guid id, UpsertSourceDto dto, KaguraDbContext db) =>
        {
            var s = await db.Sources.FindAsync(id);
            if (s is null) return Results.NotFound();
            s.Name = dto.Name;
            s.Type = dto.Type;
            s.LocalRepoPath = dto.LocalRepoPath;
            s.ConfigJson = dto.Config.GetRawText();
            s.Enabled = dto.Enabled;
            s.AutoTriageOnImport = dto.AutoTriageOnImport;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(s));
        });

        grp.MapDelete("/{id:guid}", async (Guid id, KaguraDbContext db) =>
        {
            var s = await db.Sources.FindAsync(id);
            if (s is null) return Results.NotFound();
            db.Sources.Remove(s);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        grp.MapPost("/{id:guid}/sync", async (
            Guid id,
            SourceSyncService svc,
            KaguraDbContext db,
            ITriageKickoffService triageKickoff,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            try
            {
                var result = await svc.SyncAsync(id, ct);
                await MaybeAutoTriageAsync(id, result, db, triageKickoff, loggerFactory, ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (NotImplementedException ex) { return Results.Problem(ex.Message, statusCode: 501); }
        });

        grp.MapPost("/sync-all", async (
            KaguraDbContext db,
            SourceSyncService svc,
            ITriageKickoffService triageKickoff,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var ids = await db.Sources.Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct);
            var results = new List<object>();
            foreach (var id in ids)
            {
                try
                {
                    var result = await svc.SyncAsync(id, ct);
                    await MaybeAutoTriageAsync(id, result, db, triageKickoff, loggerFactory, ct);
                    results.Add(new { id, ok = true, result });
                }
                catch (Exception ex) { results.Add(new { id, ok = false, error = ex.Message }); }
            }
            return Results.Ok(results);
        });

        return app;
    }

    // Per CONTEXT.md → "Auto-triage": if the Source opted in, spawn one Triage Agent per
    // newly-imported New WorkItem. Reuses ITriageKickoffService so the auto-spawn path is the
    // *same* code path the manual Triage button runs through — same lifecycle, same prompts,
    // same auto-dismiss-on-success / linger-on-failure semantics.
    private static async Task MaybeAutoTriageAsync(
        Guid sourceId,
        SyncResult result,
        KaguraDbContext db,
        ITriageKickoffService triageKickoff,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (result.NewlyImportedWorkItemIds.Count == 0) return;

        var source = await db.Sources
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null || !source.AutoTriageOnImport) return;

        var log = loggerFactory.CreateLogger("Kagura.Api.Endpoints.SourceEndpoints.AutoTriage");
        foreach (var wiId in result.NewlyImportedWorkItemIds)
        {
            try
            {
                var kickoff = await triageKickoff.KickoffAsync(wiId, ct);
                if (kickoff.WorkItemNotFound)
                {
                    log.LogWarning("Auto-triage skipped: work item {WorkItemId} not found", wiId);
                }
                else if (kickoff.Error is not null)
                {
                    log.LogWarning("Auto-triage refused for work item {WorkItemId}: {Error}", wiId, kickoff.Error);
                }
            }
            catch (Exception ex)
            {
                // One failing auto-triage spawn must not abort the rest of the sync result —
                // the sync itself already succeeded.
                log.LogError(ex, "Auto-triage spawn failed for work item {WorkItemId}", wiId);
            }
        }
    }

    private static SourceDto ToDto(Source s) => new(
        s.Id, s.Name, s.Type, s.LocalRepoPath,
        JsonDocument.Parse(string.IsNullOrWhiteSpace(s.ConfigJson) ? "{}" : s.ConfigJson).RootElement,
        s.Enabled, s.AutoTriageOnImport, s.LastSyncedAt, s.CreatedAt);
}
