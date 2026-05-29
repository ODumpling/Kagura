using System.Net;
using System.Net.Http.Json;
using Kagura.Core.Domain;
using Kagura.Core.Review;
using Kagura.Core.Sources;
using Kagura.Core.Triage;
using Kagura.Data;
using Kagura.Data.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

// Issue #68: when a Source has AutoTriageOnImport = true, POST /api/sources/{id}/sync must
// spawn a Triage Agent for each newly-imported `New` WorkItem — re-using the same code path
// the manual Triage button hits (ITriageKickoffService → AgentRun row + background task).
// When the toggle is OFF, behaviour must be unchanged.
public class AutoTriageOnImportTests : IClassFixture<AutoTriageOnImportTests.AppFactory>
{
    private readonly AppFactory _app;

    public AutoTriageOnImportTests(AppFactory app) => _app = app;

    [Fact]
    public async Task SyncResult_carries_only_freshly_imported_workitem_ids()
    {
        using var test = TestDb.Create();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo", Type = SourceType.Markdown };
        test.Context.Sources.Add(source);
        // EXT-1 exists already, so a future sync should not consider it "newly imported".
        test.Context.WorkItems.Add(new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-1",
            Title = "pre-existing",
            Body = "",
        });
        await test.Context.SaveChangesAsync();

        var provider = new StaticIssueProvider(new[]
        {
            new FetchedIssue("EXT-1", "pre-existing", "", null, null),
            new FetchedIssue("EXT-NEW", "freshly imported", "", null, null),
        });
        using var svcDb = test.NewContext();
        var svc = new SourceSyncService(svcDb, new SingleProviderFactory(provider), NullLogger<SourceSyncService>.Instance);

        var result = await svc.SyncAsync(source.Id);

        Assert.Equal(1, result.Added);
        Assert.Single(result.NewlyImportedWorkItemIds);

        using var verify = test.NewContext();
        var freshlyImported = await verify.WorkItems.SingleAsync(w => w.ExternalId == "EXT-NEW");
        Assert.Equal(freshlyImported.Id, result.NewlyImportedWorkItemIds[0]);
    }

    [Fact]
    public async Task Sync_with_AutoTriageOnImport_enabled_spawns_triage_for_each_new_workitem()
    {
        _app.Triage.Reset();
        _app.Provider.Set(Array.Empty<FetchedIssue>());

        var sourceId = await SeedSourceAsync(autoTriageOnImport: true);
        _app.Provider.Set(new[]
        {
            new FetchedIssue("EXT-1", "first", "body1", null, null),
            new FetchedIssue("EXT-2", "second", "body2", null, null),
        });

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/sources/{sourceId}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The Triage agent runs (kicked by ITriageKickoffService) eventually produce AgentRun rows
        // with Kind = Triage scoped to each newly-imported WorkItem.
        await WaitForTriageRunsAsync(sourceId, expected: 2);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var workItemIds = await db.WorkItems.Where(w => w.SourceId == sourceId).Select(w => w.Id).ToListAsync();
        var triageRuns = await db.AgentRuns
            .Where(r => r.Kind == AgentRunKind.Triage && workItemIds.Contains(r.WorkItemId))
            .ToListAsync();
        Assert.Equal(2, triageRuns.Count);
    }

    [Fact]
    public async Task Sync_with_AutoTriageOnImport_disabled_does_not_spawn_triage()
    {
        _app.Triage.Reset();
        _app.Provider.Set(Array.Empty<FetchedIssue>());

        var sourceId = await SeedSourceAsync(autoTriageOnImport: false);
        _app.Provider.Set(new[]
        {
            new FetchedIssue("EXT-1", "first", "body1", null, null),
        });

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/sources/{sourceId}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Give any (incorrectly spawned) background triage a moment to land in the DB.
        await Task.Delay(150);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var workItemIds = await db.WorkItems.Where(w => w.SourceId == sourceId).Select(w => w.Id).ToListAsync();
        var triageRuns = await db.AgentRuns
            .Where(r => r.Kind == AgentRunKind.Triage && workItemIds.Contains(r.WorkItemId))
            .CountAsync();
        Assert.Equal(0, triageRuns);
    }

    [Fact]
    public async Task Sync_with_AutoTriageOnImport_does_not_re_triage_existing_New_items()
    {
        _app.Triage.Reset();
        _app.Provider.Set(Array.Empty<FetchedIssue>());

        // Seed source with toggle ON and an existing New work item that's already there.
        var sourceId = await SeedSourceAsync(autoTriageOnImport: true);
        Guid existingWiId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = new WorkItem
            {
                SourceId = sourceId,
                ExternalId = "EXT-OLD",
                Title = "already here",
                Body = "",
                Status = WorkItemStatus.New,
            };
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();
            existingWiId = wi.Id;
        }

        // Now sync — the provider returns the existing item plus one new one. Only the new one
        // should get an auto-triage run.
        _app.Provider.Set(new[]
        {
            new FetchedIssue("EXT-OLD", "already here", "", null, null),
            new FetchedIssue("EXT-FRESH", "fresh", "", null, null),
        });

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/sources/{sourceId}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await WaitForTriageRunsAsync(sourceId, expected: 1);

        using var scope2 = _app.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var existingRuns = await db2.AgentRuns.CountAsync(r => r.WorkItemId == existingWiId && r.Kind == AgentRunKind.Triage);
        Assert.Equal(0, existingRuns);
    }

    private async Task<Guid> SeedSourceAsync(bool autoTriageOnImport)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source
        {
            Name = "src-" + Guid.NewGuid(),
            LocalRepoPath = "/tmp/repo",
            Type = SourceType.Markdown,
            AutoTriageOnImport = autoTriageOnImport,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private async Task WaitForTriageRunsAsync(Guid sourceId, int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var workItemIds = await db.WorkItems.Where(w => w.SourceId == sourceId).Select(w => w.Id).ToListAsync();
            var count = await db.AgentRuns.CountAsync(r => r.Kind == AgentRunKind.Triage && workItemIds.Contains(r.WorkItemId));
            if (count >= expected) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Expected {expected} triage runs for source {sourceId} but they did not appear in time");
    }

    public sealed class StaticIssueProvider : IIssueProvider
    {
        private IReadOnlyList<FetchedIssue> _issues;
        public StaticIssueProvider(IReadOnlyList<FetchedIssue> issues) => _issues = issues;
        public SourceType Type => SourceType.Markdown;
        public Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default)
            => Task.FromResult(_issues);
        public void Set(IReadOnlyList<FetchedIssue> issues) => _issues = issues;
    }

    public sealed class SingleProviderFactory : IIssueProviderFactory
    {
        private readonly IIssueProvider _provider;
        public SingleProviderFactory(IIssueProvider p) => _provider = p;
        public IIssueProvider Get(SourceType type) => _provider;
    }

    public sealed class FakeTriage : ITriageService
    {
        public IReadOnlyList<TriagedTaskProposal> Proposals { get; set; } = Array.Empty<TriagedTaskProposal>();

        public void Reset() => Proposals = Array.Empty<TriagedTaskProposal>();

        public Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
            string workItemTitle, string workItemBody, string? labels,
            IReadOnlyList<ExistingTask>? existingTasks = null,
            CancellationToken ct = default)
            => Task.FromResult(Proposals);
    }

    public sealed class FakeReview : IReviewService
    {
        public Task<ReviewVerdict> ReviewAsync(Guid runId, string taskTitle, string taskDescription, string diff, CancellationToken ct = default)
            => Task.FromResult(new ReviewVerdict(true, "stub"));
    }

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _connStr = $"Data Source=auto-triage-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keepAlive;
        public FakeTriage Triage { get; } = new();
        public FakeReview Review { get; } = new();
        public StaticIssueProvider Provider { get; } = new StaticIssueProvider(Array.Empty<FetchedIssue>());

        public AppFactory()
        {
            _keepAlive = new SqliteConnection(_connStr);
            _keepAlive.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<KaguraDbContext>>();
                services.RemoveAll<KaguraDbContext>();
                services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite(_connStr));

                services.RemoveAll<ITriageService>();
                services.AddSingleton<ITriageService>(Triage);

                services.RemoveAll<IReviewService>();
                services.AddSingleton<IReviewService>(Review);

                services.RemoveAll<IIssueProviderFactory>();
                services.AddSingleton<IIssueProviderFactory>(_ => new SingleProviderFactory(Provider));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<KaguraDbContext>().Database.EnsureCreated();
            return host;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _keepAlive.Dispose();
        }
    }
}
