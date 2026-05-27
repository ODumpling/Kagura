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
    public async Task POST_triage_returns_200_and_persists_proposed_tasks()
    {
        var wiId = await SeedAsync();
        _app.Triage.Proposals = new[]
        {
            new TriagedTaskProposal("Wire markdown sync", "scan .md files", 0),
            new TriagedTaskProposal("Add triage endpoint", "shell out to claude", 1),
            new TriagedTaskProposal("Run agent in worktree", "spawn pty", 2),
        };

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/triage", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TriageResultDtoLocal>();
        Assert.NotNull(body);
        Assert.Equal(3, body!.TaskCount);
        Assert.Equal(new[] { "Wire markdown sync", "Add triage endpoint", "Run agent in worktree" },
            body.Tasks.Select(t => t.Title));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var persisted = await db.AgentTasks.Where(t => t.WorkItemId == wiId).ToListAsync();
        Assert.Equal(3, persisted.Count);
        Assert.All(persisted, t => Assert.Equal(AgentTaskStatus.Proposed, t.Status));
    }

    [Fact]
    public async Task POST_triage_then_approve_marks_workitem_triaged()
    {
        var wiId = await SeedAsync();
        _app.Triage.Proposals = new[] { new TriagedTaskProposal("One", "", 0) };

        using var client = _app.CreateClient();
        var triageResp = await client.PostAsync($"/api/workitems/{wiId}/triage", null);
        Assert.Equal(HttpStatusCode.OK, triageResp.StatusCode);

        var approveResp = await client.PostAsync($"/api/workitems/{wiId}/triage/approve", null);
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.Include(w => w.Tasks).SingleAsync(w => w.Id == wiId);
        Assert.Equal(WorkItemStatus.Triaged, wi.Status);
        Assert.NotNull(wi.TriagedAt);
        Assert.All(wi.Tasks, t => Assert.Equal(AgentTaskStatus.Approved, t.Status));
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

    private record AgentTaskDtoLocal(Guid Id, string Title, string Description, int Order, AgentTaskStatus Status, string? BranchName, string? WorktreePath);
    private record TriageResultDtoLocal(Guid WorkItemId, int TaskCount, IReadOnlyList<AgentTaskDtoLocal> Tasks);

    public sealed class FakeTriageService : ITriageService
    {
        public IReadOnlyList<TriagedTaskProposal> Proposals { get; set; } = Array.Empty<TriagedTaskProposal>();

        public Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
            string workItemTitle, string workItemBody, string? labels, CancellationToken ct = default)
            => Task.FromResult(Proposals);
    }

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _conn = new("Data Source=:memory:");
        public FakeTriageService Triage { get; } = new();

        public AppFactory() => _conn.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<KaguraDbContext>>();
                services.RemoveAll<KaguraDbContext>();
                services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite(_conn));

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
            if (disposing) _conn.Dispose();
        }
    }
}
