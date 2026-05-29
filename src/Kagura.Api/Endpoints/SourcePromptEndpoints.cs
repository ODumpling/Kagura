using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

/// <summary>
/// REST surface for the Source detail "Prompts" tab (issue #69 / ADR 0002).
///
/// GET  /api/sources/{id}/prompts            — one entry per Role with the resolved text and an
///                                              <c>isOverride</c> flag (true = customised row exists).
/// PUT  /api/sources/{id}/prompts/{role}     — upsert the override row with the request body's
///                                              promptText.
/// DELETE /api/sources/{id}/prompts/{role}   — delete the override row; subsequent reads fall
///                                              back to the lazy built-in default.
/// </summary>
public record RolePromptDto(
    Role Role,
    string PromptText,
    bool IsOverride,
    DateTime? UpdatedAt);

public record UpdateRolePromptDto(string PromptText);

public static class SourcePromptEndpoints
{
    public static IEndpointRouteBuilder MapSourcePromptEndpoints(this IEndpointRouteBuilder app)
    {
        // The five Roles surfaced in the UI per the issue spec, even those not yet migrated
        // to the PTY-Agent path. PromptResolver returns the built-in default for any Role
        // that doesn't have an override row.
        var allRoles = new[] { Role.Triage, Role.Task, Role.AutoReview, Role.Grill, Role.MergeResolver };

        var grp = app.MapGroup("/api/sources/{sourceId:guid}/prompts");

        grp.MapGet("", async (Guid sourceId, KaguraDbContext db, IPromptResolver resolver) =>
        {
            var src = await db.Sources
                .Include(s => s.PromptOverrides)
                .FirstOrDefaultAsync(s => s.Id == sourceId);
            if (src is null) return Results.NotFound();

            var rows = allRoles.Select(role =>
            {
                var ov = src.PromptOverrides.FirstOrDefault(o => o.Role == role);
                return new RolePromptDto(
                    Role: role,
                    PromptText: resolver.Resolve(src, role),
                    IsOverride: ov is not null,
                    UpdatedAt: ov?.UpdatedAt);
            }).ToList();
            return Results.Ok(rows);
        });

        grp.MapPut("/{role:int}", async (Guid sourceId, int role, UpdateRolePromptDto dto, KaguraDbContext db) =>
        {
            if (!Enum.IsDefined(typeof(Role), role)) return Results.BadRequest("Unknown role.");
            if (string.IsNullOrWhiteSpace(dto.PromptText))
                return Results.BadRequest("PromptText must not be empty — DELETE the override to reset to the built-in default.");

            var roleEnum = (Role)role;
            var sourceExists = await db.Sources.AnyAsync(s => s.Id == sourceId);
            if (!sourceExists) return Results.NotFound();

            var ov = await db.SourcePromptOverrides
                .FirstOrDefaultAsync(o => o.SourceId == sourceId && o.Role == roleEnum);
            if (ov is null)
            {
                ov = new SourcePromptOverride
                {
                    SourceId = sourceId,
                    Role = roleEnum,
                    PromptText = dto.PromptText,
                };
                db.SourcePromptOverrides.Add(ov);
            }
            else
            {
                ov.PromptText = dto.PromptText;
                ov.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new RolePromptDto(roleEnum, ov.PromptText, true, ov.UpdatedAt));
        });

        grp.MapDelete("/{role:int}", async (Guid sourceId, int role, KaguraDbContext db, IPromptResolver resolver) =>
        {
            if (!Enum.IsDefined(typeof(Role), role)) return Results.BadRequest("Unknown role.");
            var roleEnum = (Role)role;

            var src = await db.Sources
                .Include(s => s.PromptOverrides)
                .FirstOrDefaultAsync(s => s.Id == sourceId);
            if (src is null) return Results.NotFound();

            var ov = await db.SourcePromptOverrides
                .FirstOrDefaultAsync(o => o.SourceId == sourceId && o.Role == roleEnum);
            if (ov is not null)
            {
                db.SourcePromptOverrides.Remove(ov);
                await db.SaveChangesAsync();
                // Strip the now-deleted row from the in-memory collection so the resolver
                // call below falls through to the built-in default.
                src.PromptOverrides.RemoveAll(o => o.Role == roleEnum);
            }

            // Echo the now-default text back so the UI can render it without a follow-up GET.
            return Results.Ok(new RolePromptDto(
                Role: roleEnum,
                PromptText: resolver.Resolve(src, roleEnum),
                IsOverride: false,
                UpdatedAt: null));
        });

        return app;
    }
}
