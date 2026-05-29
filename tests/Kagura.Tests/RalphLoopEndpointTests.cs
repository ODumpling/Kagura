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

    private static readonly RalphLoopActivationDto DefaultActivation =
        new(AutoApproveTriage: false, AutoReviewEnabled: true);

    [Fact]
    public async Task POST_ralph_loop_activates_with_body_flags()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved);

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync(
            $"/api/workitems/{wiId}/ralph-loop",
            new RalphLoopActivationDto(AutoApproveTriage: true, AutoReviewEnabled: false));

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.True(wi.RalphLoopActive);
        Assert.True(wi.AutoApproveTriage);
        Assert.False(wi.AutoReviewEnabled);
    }

    [Theory]
    [InlineData(WorkItemStatus.New)]
    [InlineData(WorkItemStatus.Triaged)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.Merged)]
    public async Task POST_ralph_loop_allows_entry_states(WorkItemStatus status)
    {
        var wiId = await SeedAsync(taskCount: 1, status: status, taskStatus: AgentTaskStatus.Approved);

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/workitems/{wiId}/ralph-loop", DefaultActivation);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Theory]
    [InlineData(WorkItemStatus.PullRequested)]
    [InlineData(WorkItemStatus.Cancelled)]
    [InlineData(WorkItemStatus.Closed)]
    [InlineData(WorkItemStatus.Done)]
    public async Task POST_ralph_loop_rejects_terminal_states(WorkItemStatus status)
    {
        var wiId = await SeedAsync(taskCount: 1, status: status, taskStatus: AgentTaskStatus.Merged);

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/workitems/{wiId}/ralph-loop", DefaultActivation);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_ralph_loop_returns_409_when_already_active()
    {
        var wiId = await SeedAsync(taskCount: 1, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved);
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
            wi.RalphLoopActive = true;
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/workitems/{wiId}/ralph-loop", DefaultActivation);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task POST_ralph_loop_returns_400_when_grill_active()
    {
        var wiId = await SeedAsync(taskCount: 1, status: WorkItemStatus.New, taskStatus: AgentTaskStatus.Proposed);
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
            wi.GrillStatus = GrillStatus.Active;
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync($"/api/workitems/{wiId}/ralph-loop", DefaultActivation);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_resume_clears_halt_waiting_triage_error_and_resets_failed_tasks()
    {
        var wiId = await SeedAsync(taskCount: 3, status: WorkItemStatus.InProgress, taskStatus: AgentTaskStatus.Approved);
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.Include(w => w.Tasks).SingleAsync(w => w.Id == wiId);
            wi.RalphLoopActive = false;
            wi.RalphLoopHaltReason = "Auto-review flagged 2 task(s) for human review.";
            wi.RalphLoopWaitingReason = "Waiting for you to review tasks.";
            wi.LastTriageError = "boom";
            var failed = wi.Tasks.First();
            failed.Status = AgentTaskStatus.Failed;
            failed.RetryAttempts = 5;
            failed.LastFailureReason = "old crash";
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop/resume", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi2 = await db2.WorkItems.Include(w => w.Tasks).SingleAsync(w => w.Id == wiId);
        Assert.True(wi2.RalphLoopActive);
        Assert.Null(wi2.RalphLoopHaltReason);
        Assert.Null(wi2.RalphLoopWaitingReason);
        Assert.Null(wi2.LastTriageError);
        Assert.All(wi2.Tasks, t =>
        {
            Assert.Equal(AgentTaskStatus.Approved, t.Status);
            Assert.Equal(0, t.RetryAttempts);
            Assert.Null(t.LastFailureReason);
        });
    }

    [Fact]
    public async Task POST_resume_returns_409_when_not_halted()
    {
        var wiId = await SeedAsync(taskCount: 1, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved);

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop/resume", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task POST_resume_returns_409_when_active()
    {
        var wiId = await SeedAsync(taskCount: 1, status: WorkItemStatus.InProgress, taskStatus: AgentTaskStatus.Running);
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
            wi.RalphLoopActive = true;
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop/resume", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task POST_cancel_clears_flag_and_sets_halt_reason_without_killing_runs()
    {
        var wiId = await SeedAsync(taskCount: 2, status: WorkItemStatus.InProgress, taskStatus: AgentTaskStatus.Running);
        Guid runId;
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.Include(w => w.Tasks).SingleAsync(w => w.Id == wiId);
            wi.RalphLoopActive = true;
            var run = new AgentRun
            {
                Kind = AgentRunKind.TaskAgent,
                WorkItemId = wi.Id,
                AgentTaskId = wi.Tasks.First().Id,
                Status = AgentRunStatus.Running,
            };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsync($"/api/workitems/{wiId}/ralph-loop/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi2 = await db2.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.False(wi2.RalphLoopActive);
        Assert.Equal("Cancelled by user.", wi2.RalphLoopHaltReason);
        var run2 = await db2.AgentRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal(AgentRunStatus.Running, run2.Status);
        Assert.Null(run2.EndedAt);
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
        Assert.Null(wi.RalphLoopHaltReason);
    }

    [Fact]
    public async Task PATCH_ralph_config_updates_both_flags()
    {
        var wiId = await SeedAsync(taskCount: 1, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved);

        using var client = _app.CreateClient();
        var resp = await client.PatchAsJsonAsync(
            $"/api/workitems/{wiId}/ralph-config",
            new RalphConfigUpdateDto(AutoApproveTriage: true, AutoReviewEnabled: false));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.True(wi.AutoApproveTriage);
        Assert.False(wi.AutoReviewEnabled);
    }

    [Fact]
    public async Task PATCH_ralph_config_leaves_unspecified_flag_untouched()
    {
        var wiId = await SeedAsync(taskCount: 1, status: WorkItemStatus.Triaged, taskStatus: AgentTaskStatus.Approved);
        using (var s = _app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.SingleAsync(w => w.Id == wiId);
            wi.AutoApproveTriage = true;
            wi.AutoReviewEnabled = true;
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var resp = await client.PatchAsJsonAsync(
            $"/api/workitems/{wiId}/ralph-config",
            new RalphConfigUpdateDto(AutoApproveTriage: null, AutoReviewEnabled: false));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var wi2 = await db2.WorkItems.SingleAsync(w => w.Id == wiId);
        Assert.True(wi2.AutoApproveTriage);
        Assert.False(wi2.AutoReviewEnabled);
    }

    [Fact]
    public async Task PATCH_ralph_config_returns_404_for_unknown_work_item()
    {
        using var client = _app.CreateClient();
        var resp = await client.PatchAsJsonAsync(
            $"/api/workitems/{Guid.NewGuid()}/ralph-config",
            new RalphConfigUpdateDto(true, true));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
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

    private record RalphLoopActivationDto(bool AutoApproveTriage, bool AutoReviewEnabled);

    private record RalphConfigUpdateDto(bool? AutoApproveTriage, bool? AutoReviewEnabled);

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
