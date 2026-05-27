using Kagura.Core.Domain;
using Kagura.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kagura.Data.Services;

public record SyncResult(int Added, int Updated, int Closed, int Total);

public class SourceSyncService
{
    private readonly KaguraDbContext _db;
    private readonly IIssueProviderFactory _factory;
    private readonly ILogger<SourceSyncService> _log;

    public SourceSyncService(KaguraDbContext db, IIssueProviderFactory factory, ILogger<SourceSyncService> log)
    {
        _db = db;
        _factory = factory;
        _log = log;
    }

    public async Task<SyncResult> SyncAsync(Guid sourceId, CancellationToken ct = default)
    {
        var source = await _db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId, ct)
            ?? throw new KeyNotFoundException($"Source {sourceId} not found");

        var provider = _factory.Get(source.Type);
        var fetched = await provider.FetchIssuesAsync(source, ct);

        var existing = await _db.WorkItems
            .Where(w => w.SourceId == source.Id)
            .ToDictionaryAsync(w => w.ExternalId, ct);

        var added = 0;
        var updated = 0;
        var closed = 0;
        var now = DateTime.UtcNow;

        var fetchedIds = new HashSet<string>(fetched.Select(f => f.ExternalId), StringComparer.Ordinal);

        foreach (var f in fetched)
        {
            if (existing.TryGetValue(f.ExternalId, out var w))
            {
                if (w.Title != f.Title || w.Body != f.Body || w.Labels != f.Labels)
                {
                    w.Title = f.Title;
                    w.Body = f.Body;
                    w.Labels = f.Labels;
                    w.UpdatedAt = now;
                    updated++;
                }
            }
            else
            {
                _db.WorkItems.Add(new WorkItem
                {
                    SourceId = source.Id,
                    ExternalId = f.ExternalId,
                    Title = f.Title,
                    Body = f.Body,
                    Url = f.Url,
                    Labels = f.Labels,
                });
                added++;
            }
        }

        // Providers return the upstream open set. Any tracked work item missing from that set
        // was closed upstream (the linked issue/PR was closed or merged).
        foreach (var (externalId, w) in existing)
        {
            if (w.Status == WorkItemStatus.Closed) continue;
            if (fetchedIds.Contains(externalId)) continue;
            w.MarkClosed(now);
            closed++;
        }

        source.LastSyncedAt = now;
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Synced source {Name}: +{Added} ~{Updated} -{Closed}", source.Name, added, updated, closed);

        return new SyncResult(added, updated, closed, fetched.Count);
    }
}
