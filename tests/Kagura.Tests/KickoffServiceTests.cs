using Kagura.Api.Services;
using Kagura.Core.Domain;
using Kagura.Core.Review;
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

// Verifies the shared kickoff services the Ralph driver will consume can be resolved from DI
// and produce the same AgentRun row + background task as the HTTP endpoints.
public class KickoffServiceTests : IClassFixture<KickoffServiceTests.AppFactory>
{
    private readonly AppFactory _app;

    public KickoffServiceTests(AppFactory app) => _app = app;

    [Fact]
    public async Task TriageKickoffService_creates_running_agent_run_and_completes_in_background()
    {
        var wiId = await SeedWorkItemAsync();
        _app.Triage.Reset();
        _app.Triage.Proposals = new[] { new TriagedTaskProposal("X", "y", 0) };

        Guid runId;
        using (var scope = _app.Services.CreateScope())
        {
            var kickoff = scope.ServiceProvider.GetRequiredService<ITriageKickoffService>();
            var result = await kickoff.KickoffAsync(wiId);
            Assert.False(result.WorkItemNotFound);
            Assert.Null(result.Error);
            Assert.NotNull(result.RunId);
            runId = result.RunId!.Value;
        }

        await WaitForRunToFinishAsync(runId);

        using var verify = _app.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var run = await db.AgentRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal(AgentRunKind.Triage, run.Kind);
        Assert.Equal(wiId, run.WorkItemId);
        Assert.Equal(AgentRunStatus.Exited, run.Status);

        var tasks = await db.AgentTasks.Where(t => t.WorkItemId == wiId).ToListAsync();
        Assert.Single(tasks);
        Assert.Equal("X", tasks[0].Title);
    }

    [Fact]
    public async Task TriageKickoffService_returns_NotFound_for_missing_workitem()
    {
        using var scope = _app.Services.CreateScope();
        var kickoff = scope.ServiceProvider.GetRequiredService<ITriageKickoffService>();
        var result = await kickoff.KickoffAsync(Guid.NewGuid());

        Assert.True(result.WorkItemNotFound);
        Assert.Null(result.RunId);
    }

    [Fact]
    public async Task TriageKickoffService_returns_Invalid_for_closed_workitem()
    {
        var wiId = await SeedWorkItemAsync(status: WorkItemStatus.Closed);
        using var scope = _app.Services.CreateScope();
        var kickoff = scope.ServiceProvider.GetRequiredService<ITriageKickoffService>();
        var result = await kickoff.KickoffAsync(wiId);

        Assert.False(result.WorkItemNotFound);
        Assert.NotNull(result.Error);
        Assert.Null(result.RunId);
    }

    [Fact]
    public async Task AutoReviewKickoffService_returns_Invalid_when_no_awaiting_review_tasks()
    {
        var wiId = await SeedWorkItemAsync();
        using var scope = _app.Services.CreateScope();
        var kickoff = scope.ServiceProvider.GetRequiredService<IAutoReviewKickoffService>();
        var result = await kickoff.KickoffAsync(wiId);

        Assert.False(result.WorkItemNotFound);
        Assert.NotNull(result.Error);
        Assert.Null(result.RunId);
    }

    [Fact]
    public async Task AutoReviewKickoffService_creates_running_agent_run_when_queue_present()
    {
        var wiId = await SeedWorkItemWithAwaitingReviewAsync();

        Guid runId;
        using (var scope = _app.Services.CreateScope())
        {
            var kickoff = scope.ServiceProvider.GetRequiredService<IAutoReviewKickoffService>();
            var result = await kickoff.KickoffAsync(wiId);
            Assert.False(result.WorkItemNotFound);
            Assert.Null(result.Error);
            Assert.NotNull(result.RunId);
            runId = result.RunId!.Value;
        }

        using var verify = _app.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var run = await db.AgentRuns.SingleAsync(r => r.Id == runId);
        Assert.Equal(AgentRunKind.AutoReview, run.Kind);
        Assert.Equal(wiId, run.WorkItemId);
    }

    private async Task<Guid> SeedWorkItemAsync(WorkItemStatus status = WorkItemStatus.New)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Kickoff test",
            Body = "body",
            Status = status,
        };
        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    private async Task<Guid> SeedWorkItemWithAwaitingReviewAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Kickoff test ar",
            Body = "body",
            Status = WorkItemStatus.InProgress,
        };
        wi.Tasks.Add(new AgentTask
        {
            Title = "needs review",
            Description = "",
            Order = 0,
            Status = AgentTaskStatus.AwaitingReview,
        });
        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
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

    public sealed class FakeTriage : ITriageService
    {
        public IReadOnlyList<TriagedTaskProposal> Proposals { get; set; } = Array.Empty<TriagedTaskProposal>();
        public Exception? Throw { get; set; }

        public void Reset()
        {
            Proposals = Array.Empty<TriagedTaskProposal>();
            Throw = null;
        }

        public Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
            string workItemTitle, string workItemBody, string? labels,
            IReadOnlyList<ExistingTask>? existingTasks = null,
            CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(Proposals);
        }
    }

    public sealed class FakeReview : IReviewService
    {
        public ReviewVerdict Verdict { get; set; } = new(true, "stub");
        public Task<ReviewVerdict> ReviewAsync(
            Guid runId, string taskTitle, string taskDescription, string diff, CancellationToken ct = default)
            => Task.FromResult(Verdict);
    }

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _connStr = $"Data Source=kickoff-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keepAlive;
        public FakeTriage Triage { get; } = new();
        public FakeReview Review { get; } = new();

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
