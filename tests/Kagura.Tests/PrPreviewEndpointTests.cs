using System.Net;
using System.Net.Http.Json;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Kagura.Tests;

public class PrPreviewEndpointTests : IClassFixture<PrPreviewEndpointTests.AppFactory>
{
    private readonly AppFactory _app;

    public PrPreviewEndpointTests(AppFactory app) => _app = app;

    [Fact]
    public async Task GET_pr_preview_returns_empty_payload_when_no_task_ids()
    {
        var (wiId, _) = await SeedAsync(new (AgentTaskStatus, int)[]
        {
            (AgentTaskStatus.AwaitingReview, 0),
        });
        _app.Preview.LastInvocation = null;

        using var client = _app.CreateClient();
        var resp = await client.GetAsync($"/api/workitems/{wiId}/pr-preview");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PrPreviewResponseLocal>();
        Assert.NotNull(body);
        Assert.Empty(body!.IncludedTaskIds);
        Assert.Equal(string.Empty, body.UnifiedDiff);
        Assert.Empty(body.ConflictedFiles);
        Assert.Empty(body.TaskSnapshots);
        Assert.Equal(0, body.Stats.FilesChanged);
        Assert.Equal(0, body.Stats.Additions);
        Assert.Equal(0, body.Stats.Deletions);
        Assert.Null(_app.Preview.LastInvocation);
    }

    [Fact]
    public async Task GET_pr_preview_filters_out_non_awaiting_review_tasks()
    {
        var (wiId, taskIds) = await SeedAsync(new (AgentTaskStatus, int)[]
        {
            (AgentTaskStatus.AwaitingReview, 0),
            (AgentTaskStatus.Running, 1),
            (AgentTaskStatus.Proposed, 2),
            (AgentTaskStatus.Approved, 3),
            (AgentTaskStatus.Merged, 4),
            (AgentTaskStatus.Failed, 5),
            (AgentTaskStatus.Cancelled, 6),
            (AgentTaskStatus.AwaitingReview, 7),
        });
        _app.Preview.Result = new PrPreviewResult(
            UnifiedDiff: "diff --git a/x b/x\n+hello\n",
            ConflictedFiles: new[] { new ConflictedFile("conflict.txt", new[] { taskIds[0] }) },
            BaseSha: "base123",
            HeadSha: "head456",
            TaskSnapshots: new Dictionary<Guid, string>
            {
                [taskIds[0]] = "tip-0",
                [taskIds[7]] = "tip-7",
            },
            Stats: new PrPreviewDiffStats(2, 5, 1));
        _app.Preview.LastInvocation = null;

        var query = string.Join('&', taskIds.Select(id => $"taskIds={id}"));
        using var client = _app.CreateClient();
        var resp = await client.GetAsync($"/api/workitems/{wiId}/pr-preview?{query}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PrPreviewResponseLocal>();
        Assert.NotNull(body);

        Assert.Equal(new[] { taskIds[0], taskIds[7] }, body!.IncludedTaskIds);
        Assert.NotNull(_app.Preview.LastInvocation);
        Assert.Equal(new[] { taskIds[0], taskIds[7] },
            _app.Preview.LastInvocation!.Select(t => t.Id));
        Assert.All(_app.Preview.LastInvocation,
            t => Assert.Equal(AgentTaskStatus.AwaitingReview, t.Status));

        Assert.Equal("diff --git a/x b/x\n+hello\n", body.UnifiedDiff);
        Assert.Equal("base123", body.BaseSha);
        Assert.Equal("head456", body.HeadSha);
        Assert.Single(body.ConflictedFiles);
        Assert.Equal("conflict.txt", body.ConflictedFiles[0].Path);
        Assert.Equal(new[] { taskIds[0] }, body.ConflictedFiles[0].TaskIds);
        Assert.Equal(2, body.TaskSnapshots.Count);
        Assert.Equal(2, body.Stats.FilesChanged);
        Assert.Equal(5, body.Stats.Additions);
        Assert.Equal(1, body.Stats.Deletions);
    }

    [Fact]
    public async Task GET_pr_preview_short_circuits_when_filtered_list_is_empty()
    {
        var (wiId, taskIds) = await SeedAsync(new (AgentTaskStatus, int)[]
        {
            (AgentTaskStatus.Running, 0),
            (AgentTaskStatus.Proposed, 1),
        });
        _app.Preview.LastInvocation = null;

        var query = string.Join('&', taskIds.Select(id => $"taskIds={id}"));
        using var client = _app.CreateClient();
        var resp = await client.GetAsync($"/api/workitems/{wiId}/pr-preview?{query}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<PrPreviewResponseLocal>();
        Assert.NotNull(body);
        Assert.Empty(body!.IncludedTaskIds);
        Assert.Empty(body.ConflictedFiles);
        Assert.Empty(body.TaskSnapshots);
        Assert.Equal(0, body.Stats.FilesChanged);
        Assert.Null(_app.Preview.LastInvocation);
    }

    [Fact]
    public async Task GET_pr_preview_returns_404_for_unknown_work_item()
    {
        using var client = _app.CreateClient();
        var resp = await client.GetAsync($"/api/workitems/{Guid.NewGuid()}/pr-preview");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<(Guid WorkItemId, List<Guid> TaskIds)> SeedAsync(
        IReadOnlyList<(AgentTaskStatus Status, int Order)> tasks)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Preview me",
            Body = "body",
        };
        db.Sources.Add(source);
        db.WorkItems.Add(wi);

        var ids = new List<Guid>();
        foreach (var (status, order) in tasks)
        {
            var t = new AgentTask
            {
                WorkItemId = wi.Id,
                Title = $"task-{order}",
                Order = order,
                Status = status,
            };
            db.AgentTasks.Add(t);
            ids.Add(t.Id);
        }
        await db.SaveChangesAsync();
        return (wi.Id, ids);
    }

    public sealed class FakePrPreviewService : IPrPreviewService
    {
        public PrPreviewResult Result { get; set; } = PrPreviewResult.Empty();
        public IReadOnlyList<AgentTask>? LastInvocation { get; set; }

        public Task<PrPreviewResult> ComputePreviewAsync(
            WorkItem workItem, IReadOnlyList<AgentTask> includedTasks, CancellationToken ct = default)
        {
            LastInvocation = includedTasks.ToList();
            return Task.FromResult(Result);
        }
    }

    private record PrPreviewResponseLocal(
        Guid WorkItemId,
        IReadOnlyList<Guid> IncludedTaskIds,
        string UnifiedDiff,
        IReadOnlyList<ConflictedFileLocal> ConflictedFiles,
        string? BaseSha,
        string? HeadSha,
        IReadOnlyDictionary<Guid, string> TaskSnapshots,
        PrPreviewStatsLocal Stats);
    private record ConflictedFileLocal(string Path, IReadOnlyList<Guid> TaskIds);
    private record PrPreviewStatsLocal(int FilesChanged, int Additions, int Deletions);

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _conn = new("Data Source=:memory:");
        public FakePrPreviewService Preview { get; } = new();

        public AppFactory() => _conn.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<KaguraDbContext>>();
                services.RemoveAll<KaguraDbContext>();
                services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite(_conn));

                services.RemoveAll<IPrPreviewService>();
                services.AddSingleton<IPrPreviewService>(Preview);
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
            if (disposing) _conn.Dispose();
        }
    }
}
