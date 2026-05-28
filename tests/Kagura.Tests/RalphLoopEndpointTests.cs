using System.Net;
using System.Net.Http.Json;
using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Kagura.Tests;

public class RalphLoopEndpointTests : IClassFixture<RalphLoopEndpointTests.AppFactory>
{
    private readonly AppFactory _app;

    public RalphLoopEndpointTests(AppFactory app) => _app = app;

    [Fact]
    public async Task POST_ralph_loop_flips_flag_and_clears_halt_reason()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved,
            ralphHalt: "previous halt reason");

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop", null);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.True(wi.RalphLoopActive);
        Assert.Null(wi.RalphLoopHaltReason);
    }

    [Fact]
    public async Task POST_ralph_loop_resets_Failed_tasks_to_Approved()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.InProgress, taskStatus: AgentTaskStatus.Approved);
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var failed = await db.AgentTasks.Where(t => t.WorkItemId == wiId).FirstAsync();
            failed.Status = AgentTaskStatus.Failed;
            failed.RetryAttempts = 3;
            failed.LastFailureReason = "old crash";
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var tasks = await db2.AgentTasks.Where(t => t.WorkItemId == wiId).ToListAsync();
        Assert.All(tasks, t => Assert.Equal(AgentTaskStatus.Approved, t.Status));
        Assert.All(tasks, t => Assert.Equal(0, t.RetryAttempts));
        Assert.All(tasks, t => Assert.Null(t.LastFailureReason));
    }

    [Fact]
    public async Task POST_ralph_loop_accepts_work_item_with_more_than_three_tasks()
    {
        var wiId = await SeedAsync(taskCount: 5, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved);

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.True(wi.RalphLoopActive);
    }

    [Fact]
    public async Task POST_ralph_loop_rejects_PullRequested_work_item()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.PullRequested, taskStatus: AgentTaskStatus.Merged);

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_ralph_loop_rejects_when_all_tasks_already_merged()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.Merged, taskStatus: AgentTaskStatus.Merged);

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_cancel_clears_flag_and_sets_halt_reason()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.InProgress, taskStatus: AgentTaskStatus.Running);
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
            wi.RalphLoopActive = true;
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi2 = await db2.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.False(wi2.RalphLoopActive);
        Assert.Equal("Cancelled by user.", wi2.RalphLoopHaltReason);
    }

    [Fact]
    public async Task POST_cancel_is_no_op_when_loop_not_active()
    {
        var wiId = await SeedAsync(taskCount: 1, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved);

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.False(wi.RalphLoopActive);
        Assert.Null(wi.RalphLoopHaltReason); // not set since loop wasn't active
    }

    [Fact]
    public async Task GET_workitem_exposes_RalphLoop_fields()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved,
            ralphHalt: "merge conflict");

        using var client = _app.CreateClient();
        var resp = await client.GetAsync($"/api/workitems/{wiId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<WorkItemDetailDtoLocal>();
        Assert.NotNull(body);
        Assert.False(body!.RalphLoopActive);
        Assert.Equal("merge conflict", body.RalphLoopHaltReason);
    }

    private async Task<Guid> SeedAsync(
        int taskCount,
        WorkItemStatus status,
        AgentTaskStatus taskStatus,
        string? ralphHalt = null)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid().ToString("N")[..8], LocalRepoPath = "/tmp/r" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "ralph-test",
            Status = status,
            RalphLoopHaltReason = ralphHalt,
        };
        for (var i = 0; i < taskCount; i++)
            wi.Tasks.Add(new AgentTask { Title = $"t{i}", Order = i, Status = taskStatus });

        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    private record WorkItemDetailDtoLocal(Guid Id, bool RalphLoopActive, string? RalphLoopHaltReason);

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _conn = new("Data Source=:memory:");

        public AppFactory() => _conn.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<KaguraDbContext>>();
                services.RemoveAll<KaguraDbContext>();
                services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite(_conn));
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
