using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Services;

/// <summary>
/// Persists the resolved prompt onto <c>AgentRun.PromptText</c> for the matching run.
/// No-op if the run row doesn't exist yet (e.g. tests using transient runs) — the snapshot
/// is best-effort audit data, not a correctness invariant.
/// </summary>
public sealed class DbPromptSnapshotSink : IPromptSnapshotSink
{
    private readonly KaguraDbContext _db;

    public DbPromptSnapshotSink(KaguraDbContext db) => _db = db;

    public async Task SaveAsync(Guid runId, string promptText, CancellationToken ct)
    {
        var run = await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;
        run.PromptText = promptText;
        await _db.SaveChangesAsync(ct);
    }
}
