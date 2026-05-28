using System.Net;
using System.Net.Http.Json;
using Kagura.Core.Domain;
using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Kagura.Tests;

public class TriageEndpointTests : IClassFixture<TriageEndpointTests.AppFactory>
{
    private readonly AppFactory _app;

    public TriageEndpointTests(AppFactory app) => _app = app;

    [Fact]
    public async Task POST_triage_returns_202_with_runId_within_100ms()
    {
        var wiId = await SeedAsync();
        _app.Triage.Reset();
        _app.Triage.Proposals = new[] { new TriagedTaskProposal("One", "", 0) };
        _app.Triage.Delay = TimeSpan.FromMilliseconds(500);

        using var client = _app.CreateClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/triage", content: null);
        sw.Stop();

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 100, $"Expected <100ms, got {sw.ElapsedMilliseconds}ms");

        var body = await resp.Content.ReadFromJsonAsync<TriageAcceptedDtoLocal>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.RunId);

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var run = await db.AgentRuns.SingleAsync(r => r.Id == body.RunId);
            Assert.Equal(AgentRunKind.Triage, run.Kind);
            Assert.Equal(wiId, run.WorkItemId);
        }

        // Drain the in-flight background run so it doesn't race subsequent tests on the
        // shared in-memory Sqlite connection.
        await WaitForRunToFinishAsync(body.RunId);
    }

    [Fact]
    public async Task POST_triage_persists_proposed_tasks_after_background_completion()
    {
        var wiId = await SeedAsync();
        _app.Triage.Reset();
        _app.Triage.Proposals = new[]
        {
            new TriagedTaskProposal("Wire markdown sync", "scan .md files", 0),
            new TriagedTaskProposal("Add triage endpoint", "shell out to claude", 1),
            new TriagedTaskProposal("Run agent in worktree", "spawn pty", 2),
        };
        _app.Triage.Delay = TimeSpan.Zero;

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/triage", content: null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var accepted = await resp.Content.ReadFromJsonAsync<TriageAcceptedDtoLocal>();
        await WaitForRunToFinishAsync(accepted!.RunId);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var persisted = await db.AgentTasks.Where(t => t.WorkItemId == wiId).OrderBy(t => t.Order).ToListAsync();
        Assert.Equal(3, persisted.Count);
        Assert.Equal(new[] { "Wire markdown sync", "Add triage endpoint", "Run agent in worktree" },
            persisted.Select(t => t.Title));
        Assert.All(persisted, t => Assert.Equal(AgentTaskStatus.Proposed, t.Status));

        var run = await db.AgentRuns.SingleAsync(r => r.Id == accepted.RunId);
        Assert.Equal(AgentRunStatus.Exited, run.Status);
        Assert.NotNull(run.EndedAt);
    }

    [Fact]
    public async Task POST_triage_then_approve_marks_workitem_triaged()
    {
        var wiId = await SeedAsync();
        _app.Triage.Reset();
        _app.Triage.Proposals = new[] { new TriagedTaskProposal("One", "", 0) };
        _app.Triage.Delay = TimeSpan.Zero;

        using var client = _app.CreateClient();
        var triageResp = await client.PostAsync($"/api/workitems/{wiId}/triage", null);
        Assert.Equal(HttpStatusCode.Accepted, triageResp.StatusCode);
        var accepted = await triageResp.Content.ReadFromJsonAsync<TriageAcceptedDtoLocal>();
        await WaitForRunToFinishAsync(accepted!.RunId);

        var approveResp = await client.PostAsync($"/api/workitems/{wiId}/triage/approve", null);
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.Include(w => w.Tasks).SingleAsync(w => w.Id == wiId);
        Assert.Equal(WorkItemStatus.Triaged, wi.Status);
        Assert.NotNull(wi.TriagedAt);
        Assert.All(wi.Tasks, t => Assert.Equal(AgentTaskStatus.Approved, t.Status));
    }

    [Fact]
    public async Task POST_triage_persists_parse_error_to_LastTriageError_and_surfaces_in_detail()
    {
        var wiId = await SeedAsync();
        _app.Triage.Reset();
        _app.Triage.Throw = new InvalidOperationException("could not parse JSON array");
        _app.Triage.Delay = TimeSpan.Zero;

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/triage", content: null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var accepted = await resp.Content.ReadFromJsonAsync<TriageAcceptedDtoLocal>();
        await WaitForRunToFinishAsync(accepted!.RunId);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.Equal("could not parse JSON array", wi.LastTriageError);

        var run = await db.AgentRuns.SingleAsync(r => r.Id == accepted.RunId);
        Assert.Equal(AgentRunStatus.Crashed, run.Status);

        var detailResp = await client.GetAsync($"/api/workitems/{wiId}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var detail = await detailResp.Content.ReadFromJsonAsync<WorkItemDetailLocal>();
        Assert.Equal("could not parse JSON array", detail!.LastTriageError);
    }

    private async Task WaitForRunToFinishAsync(Guid runId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not null && run.Status != AgentRunStatus.Running && run.Status != AgentRunStatus.Starting)
                return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Run {runId} did not finish in time");
    }

    private async Task<Guid> SeedAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Triage me",
            Body = "body",
        };
        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    private record TriageAcceptedDtoLocal(Guid RunId);
    private record WorkItemDetailLocal(Guid Id, string? LastTriageError);

    public sealed class FakeTriageService : ITriageService
    {
        public IReadOnlyList<TriagedTaskProposal> Proposals { get; set; } = Array.Empty<TriagedTaskProposal>();
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public Exception? Throw { get; set; }

        public void Reset()
        {
            Proposals = Array.Empty<TriagedTaskProposal>();
            Delay = TimeSpan.Zero;
            Throw = null;
        }

        public async Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
            string workItemTitle, string workItemBody, string? labels, CancellationToken ct = default)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Throw is not null) throw Throw;
            return Proposals;
        }
    }

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        // Use a named shared-cache in-memory database so the request-scoped DbContext, the
        // background-task scope, and the test polling scope can each open their own
        // SqliteConnection without racing on a single connection's CreateFunction calls.
        private readonly string _connStr = $"Data Source=triage-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keepAlive;
        public FakeTriageService Triage { get; } = new();

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
