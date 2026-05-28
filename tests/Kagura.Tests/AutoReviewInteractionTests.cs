using System.Net;
using System.Net.Http.Json;
using Kagura.Api.Endpoints;
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

public class AutoReviewInteractionTests : IClassFixture<AutoReviewInteractionTests.AppFactory>
{
    private readonly AppFactory _app;

    public AutoReviewInteractionTests(AppFactory app) => _app = app;

    // The headline acceptance: an auto-review paused on a prompt can be reopened later and still
    // exposes the same pending question via the state endpoint.
    [Fact]
    public async Task Paused_auto_review_can_be_reopened_and_shows_same_pending_question()
    {
        var (wiId, runId) = await SeedAutoReviewRunAsync();
        using var client = _app.CreateClient();

        var createResp = await client.PostAsJsonAsync(
            $"/api/agents/{runId}/auto-review/prompts",
            new CreateAutoReviewPromptDto(null, "Two tasks both touch the auth middleware — merge both, or only the first?"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var stateResp = await client.GetFromJsonAsync<AutoReviewStateDto>($"/api/workitems/{wiId}/auto-review/state");
        Assert.NotNull(stateResp);
        Assert.Equal(runId, stateResp!.RunId);
        Assert.NotNull(stateResp.PendingPrompt);
        Assert.True(stateResp.PendingPrompt!.IsPending);
        Assert.Equal(
            "Two tasks both touch the auth middleware — merge both, or only the first?",
            stateResp.PendingPrompt.Prompt);
        Assert.Single(stateResp.Interactions);
    }

    [Fact]
    public async Task State_endpoint_returns_empty_when_no_auto_review_run_exists()
    {
        var wiId = await SeedWorkItemAsync();
        using var client = _app.CreateClient();

        var state = await client.GetFromJsonAsync<AutoReviewStateDto>($"/api/workitems/{wiId}/auto-review/state");

        Assert.NotNull(state);
        Assert.Null(state!.RunId);
        Assert.Null(state.PendingPrompt);
        Assert.Empty(state.Interactions);
    }

    [Fact]
    public async Task Responding_to_pending_prompt_clears_it_and_records_response()
    {
        var (wiId, runId) = await SeedAutoReviewRunAsync();
        using var client = _app.CreateClient();

        var created = await client.PostAsJsonAsync(
            $"/api/agents/{runId}/auto-review/prompts",
            new CreateAutoReviewPromptDto(null, "Approve risky migration?"));
        var dto = await created.Content.ReadFromJsonAsync<AutoReviewInteractionDto>();
        Assert.NotNull(dto);

        var respondResp = await client.PostAsJsonAsync(
            $"/api/auto-review/interactions/{dto!.Id}/respond",
            new RespondToAutoReviewPromptDto("Yes, merge it."));
        Assert.Equal(HttpStatusCode.OK, respondResp.StatusCode);

        var state = await client.GetFromJsonAsync<AutoReviewStateDto>($"/api/workitems/{wiId}/auto-review/state");
        Assert.Null(state!.PendingPrompt);
        Assert.Single(state.Interactions);
        Assert.Equal("Yes, merge it.", state.Interactions[0].Response);
        Assert.False(state.Interactions[0].IsPending);
        Assert.NotNull(state.Interactions[0].RespondedAt);
    }

    [Fact]
    public async Task Cannot_respond_twice_to_same_prompt()
    {
        var (_, runId) = await SeedAutoReviewRunAsync();
        using var client = _app.CreateClient();

        var created = await client.PostAsJsonAsync(
            $"/api/agents/{runId}/auto-review/prompts",
            new CreateAutoReviewPromptDto(null, "q?"));
        var dto = (await created.Content.ReadFromJsonAsync<AutoReviewInteractionDto>())!;

        var first = await client.PostAsJsonAsync(
            $"/api/auto-review/interactions/{dto.Id}/respond",
            new RespondToAutoReviewPromptDto("a1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/auto-review/interactions/{dto.Id}/respond",
            new RespondToAutoReviewPromptDto("a2"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Prompts_are_sequenced_per_run()
    {
        var (wiId, runId) = await SeedAutoReviewRunAsync();
        using var client = _app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var r = await client.PostAsJsonAsync(
                $"/api/agents/{runId}/auto-review/prompts",
                new CreateAutoReviewPromptDto(null, $"q{i}"));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        }

        var state = await client.GetFromJsonAsync<AutoReviewStateDto>($"/api/workitems/{wiId}/auto-review/state");
        Assert.Equal(3, state!.Interactions.Count);
        Assert.Equal(new[] { 0, 1, 2 }, state.Interactions.Select(i => i.Sequence));
        Assert.Equal(new[] { "q0", "q1", "q2" }, state.Interactions.Select(i => i.Prompt));
    }

    [Fact]
    public async Task Rejects_prompt_creation_for_non_AutoReview_runs()
    {
        var (_, _) = await SeedAutoReviewRunAsync();

        Guid taskAgentRunId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var wi = await db.WorkItems.FirstAsync();
            var taskAgent = new AgentRun
            {
                WorkItemId = wi.Id,
                Kind = AgentRunKind.TaskAgent,
                Status = AgentRunStatus.Running,
            };
            db.AgentRuns.Add(taskAgent);
            await db.SaveChangesAsync();
            taskAgentRunId = taskAgent.Id;
        }

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync(
            $"/api/agents/{taskAgentRunId}/auto-review/prompts",
            new CreateAutoReviewPromptDto(null, "should not stick"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task State_returns_only_latest_run_when_multiple_auto_reviews_exist()
    {
        var wiId = await SeedWorkItemAsync();
        Guid newerId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var older = new AgentRun
            {
                WorkItemId = wiId,
                Kind = AgentRunKind.AutoReview,
                Status = AgentRunStatus.Exited,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                EndedAt = DateTime.UtcNow.AddMinutes(-9),
            };
            var newer = new AgentRun
            {
                WorkItemId = wiId,
                Kind = AgentRunKind.AutoReview,
                Status = AgentRunStatus.Running,
                StartedAt = DateTime.UtcNow,
            };
            db.AgentRuns.AddRange(older, newer);
            await db.SaveChangesAsync();
            newerId = newer.Id;

            db.AutoReviewInteractions.Add(new AutoReviewInteraction
            {
                AgentRunId = older.Id,
                WorkItemId = wiId,
                Sequence = 0,
                Prompt = "stale",
                Response = "answered",
                RespondedAt = DateTime.UtcNow.AddMinutes(-9),
            });
            db.AutoReviewInteractions.Add(new AutoReviewInteraction
            {
                AgentRunId = newer.Id,
                WorkItemId = wiId,
                Sequence = 0,
                Prompt = "current pending question",
            });
            await db.SaveChangesAsync();
        }

        using var client = _app.CreateClient();
        var state = await client.GetFromJsonAsync<AutoReviewStateDto>($"/api/workitems/{wiId}/auto-review/state");

        Assert.Equal(newerId, state!.RunId);
        Assert.NotNull(state.PendingPrompt);
        Assert.Equal("current pending question", state.PendingPrompt!.Prompt);
        Assert.Single(state.Interactions);
    }

    private async Task<Guid> SeedWorkItemAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT-" + Guid.NewGuid().ToString("N")[..8],
            Title = "wi",
            Body = "",
        };
        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    private async Task<(Guid workItemId, Guid runId)> SeedAutoReviewRunAsync()
    {
        var wiId = await SeedWorkItemAsync();
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var run = new AgentRun
        {
            WorkItemId = wiId,
            Kind = AgentRunKind.AutoReview,
            Status = AgentRunStatus.Running,
        };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        return (wiId, run.Id);
    }

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _connStr = $"Data Source=autoreview-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keepAlive;

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
