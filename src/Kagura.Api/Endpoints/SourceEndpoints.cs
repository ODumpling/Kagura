using System.Text.Json;
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
    DateTime? LastSyncedAt,
    DateTime CreatedAt);

public record UpsertSourceDto(
    string Name,
    SourceType Type,
    string LocalRepoPath,
    JsonElement Config,
    bool Enabled = true);

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

        grp.MapPost("/{id:guid}/sync", async (Guid id, SourceSyncService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.SyncAsync(id, ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (NotImplementedException ex) { return Results.Problem(ex.Message, statusCode: 501); }
        });

        grp.MapPost("/sync-all", async (KaguraDbContext db, SourceSyncService svc, CancellationToken ct) =>
        {
            var ids = await db.Sources.Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct);
            var results = new List<object>();
            foreach (var id in ids)
            {
                try { results.Add(new { id, ok = true, result = await svc.SyncAsync(id, ct) }); }
                catch (Exception ex) { results.Add(new { id, ok = false, error = ex.Message }); }
            }
            return Results.Ok(results);
        });

        return app;
    }

    private static SourceDto ToDto(Source s) => new(
        s.Id, s.Name, s.Type, s.LocalRepoPath,
        JsonDocument.Parse(string.IsNullOrWhiteSpace(s.ConfigJson) ? "{}" : s.ConfigJson).RootElement,
        s.Enabled, s.LastSyncedAt, s.CreatedAt);
}
