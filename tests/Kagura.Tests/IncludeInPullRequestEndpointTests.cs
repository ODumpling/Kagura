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

public class IncludeInPullRequestEndpointTests : IClassFixture<IncludeInPullRequestEndpointTests.AppFactory>
{
    private readonly AppFactory _app;

    public IncludeInPullRequestEndpointTests(AppFactory app) => _app = app;

    [Fact]
    public async Task PATCH_include_persists_false_for_target_task()
    {
        var (wiId, taskId, _) = await SeedAsync();

        using var client = _app.CreateClient();
        var resp = await client.PatchAsJsonAsync(
            $"/api/workitems/{wiId}/tasks/{taskId}/include",
            new UpdateIncludeRequest(false));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<AgentTaskDtoLocal>();
        Assert.NotNull(body);
        Assert.Equal(taskId, body!.Id);
        Assert.False(body.IncludeInPullRequest);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var persisted = await db.AgentTasks.SingleAsync(t => t.Id == taskId);
        Assert.False(persisted.IncludeInPullRequest);
    }

    [Fact]
    public async Task PATCH_include_toggles_back_to_true()
    {
        var (wiId, taskId, _) = await SeedAsync();

        using var client = _app.CreateClient();
        await client.PatchAsJsonAsync(
            $"/api/workitems/{wiId}/tasks/{taskId}/include",
            new UpdateIncludeRequest(false));

        var resp = await client.PatchAsJsonAsync(
            $"/api/workitems/{wiId}/tasks/{taskId}/include",
            new UpdateIncludeRequest(true));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var persisted = await db.AgentTasks.SingleAsync(t => t.Id == taskId);
        Assert.True(persisted.IncludeInPullRequest);
    }

    [Fact]
    public async Task PATCH_include_returns_404_for_unknown_task()
    {
        var (wiId, _, _) = await SeedAsync();

        using var client = _app.CreateClient();
        var resp = await client.PatchAsJsonAsync(
            $"/api/workitems/{wiId}/tasks/{Guid.NewGuid()}/include",
            new UpdateIncludeRequest(false));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PATCH_include_does_not_touch_sibling_task()
    {
        var (wiId, taskId, siblingId) = await SeedAsync();

        using var client = _app.CreateClient();
        await client.PatchAsJsonAsync(
            $"/api/workitems/{wiId}/tasks/{taskId}/include",
            new UpdateIncludeRequest(false));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var sibling = await db.AgentTasks.SingleAsync(t => t.Id == siblingId);
        Assert.True(sibling.IncludeInPullRequest);
    }

    [Fact]
    public async Task GET_workitem_exposes_IncludeInPullRequest_on_tasks()
    {
        var (wiId, _, _) = await SeedAsync();

        using var client = _app.CreateClient();
        var resp = await client.GetAsync($"/api/workitems/{wiId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<WorkItemDetailDtoLocal>();
        Assert.NotNull(body);
        Assert.All(body!.Tasks, t => Assert.True(t.IncludeInPullRequest));
    }

    private async Task<(Guid workItemId, Guid taskId, Guid siblingTaskId)> SeedAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "wi",
            Body = "body",
        };
        var task = new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "t1",
            Order = 0,
            Status = AgentTaskStatus.AwaitingReview,
        };
        var sibling = new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "t2",
            Order = 1,
            Status = AgentTaskStatus.AwaitingReview,
        };
        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        db.AgentTasks.AddRange(task, sibling);
        await db.SaveChangesAsync();
        return (wi.Id, task.Id, sibling.Id);
    }

    private record UpdateIncludeRequest(bool IncludeInPullRequest);

    private record AgentTaskDtoLocal(
        Guid Id,
        string Title,
        string Description,
        int Order,
        AgentTaskStatus Status,
        string? BranchName,
        string? WorktreePath,
        bool IncludeInPullRequest);

    private record WorkItemDetailDtoLocal(Guid Id, IReadOnlyList<AgentTaskDtoLocal> Tasks);

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
