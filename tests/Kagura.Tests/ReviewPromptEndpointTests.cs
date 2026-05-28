using System.Net;
using System.Net.Http.Json;
using Kagura.Core.Domain;
using Kagura.Core.Review;
using Kagura.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Kagura.Tests;

public class ReviewPromptEndpointTests : IClassFixture<ReviewPromptEndpointTests.AppFactory>
{
    private readonly AppFactory _app;

    public ReviewPromptEndpointTests(AppFactory app) => _app = app;

    [Fact]
    public async Task GET_prompts_returns_pending_for_work_item()
    {
        var wiId = await SeedAsync();
        var coordinator = _app.Services.GetRequiredService<IReviewPromptCoordinator>();
        coordinator.Raise(wiId, taskId: null, runId: null, question: "Merge anyway?",
            new[] { new ReviewPromptOption("yes", "Yes", null), new ReviewPromptOption("no", "No", null) });

        using var client = _app.CreateClient();
        var resp = await client.GetAsync($"/api/workitems/{wiId}/auto-review/prompts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<List<ReviewPromptDtoLocal>>();
        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal("Merge anyway?", body[0].Question);
        Assert.Equal(2, body[0].Options.Count);
    }

    [Fact]
    public async Task POST_respond_resolves_prompt_and_returns_selection()
    {
        var wiId = await SeedAsync();
        var coordinator = _app.Services.GetRequiredService<IReviewPromptCoordinator>();
        var prompt = coordinator.Raise(wiId, null, null, "Merge anyway?",
            new[] { new ReviewPromptOption("yes", "Yes", null), new ReviewPromptOption("no", "No", null) });

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync(
            $"/api/workitems/{wiId}/auto-review/prompts/{prompt.Id}/respond",
            new { selectedOptionId = "no", notes = (string?)null });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ReviewPromptResponseDtoLocal>();
        Assert.NotNull(body);
        Assert.Equal(prompt.Id, body!.PromptId);
        Assert.Equal("no", body.SelectedOptionId);

        // Prompt is consumed.
        Assert.Empty(coordinator.GetPending(wiId));
    }

    [Fact]
    public async Task POST_respond_rejects_invalid_option()
    {
        var wiId = await SeedAsync();
        var coordinator = _app.Services.GetRequiredService<IReviewPromptCoordinator>();
        var prompt = coordinator.Raise(wiId, null, null, "Merge anyway?",
            new[] { new ReviewPromptOption("yes", "Yes", null) });

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync(
            $"/api/workitems/{wiId}/auto-review/prompts/{prompt.Id}/respond",
            new { selectedOptionId = "bogus", notes = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // Prompt is NOT consumed on a bad option.
        Assert.Single(coordinator.GetPending(wiId));
    }

    [Fact]
    public async Task POST_respond_404_when_prompt_does_not_belong_to_work_item()
    {
        var wiId = await SeedAsync();
        var otherWiId = await SeedAsync();
        var coordinator = _app.Services.GetRequiredService<IReviewPromptCoordinator>();
        var prompt = coordinator.Raise(otherWiId, null, null, "Q?",
            new[] { new ReviewPromptOption("ok", "OK", null) });

        using var client = _app.CreateClient();
        var resp = await client.PostAsJsonAsync(
            $"/api/workitems/{wiId}/auto-review/prompts/{prompt.Id}/respond",
            new { selectedOptionId = "ok", notes = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        // Cross-WI call must not consume the prompt.
        Assert.Single(coordinator.GetPending(otherWiId));
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
            Title = "wi",
            Body = "body",
        };
        db.Sources.Add(source);
        db.WorkItems.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    private record ReviewPromptOptionDtoLocal(string Id, string Label, string? Description);

    private record ReviewPromptDtoLocal(
        Guid Id,
        Guid WorkItemId,
        Guid? TaskId,
        Guid? RunId,
        string Question,
        IReadOnlyList<ReviewPromptOptionDtoLocal> Options,
        DateTime CreatedAt);

    private record ReviewPromptResponseDtoLocal(
        Guid PromptId,
        Guid WorkItemId,
        string SelectedOptionId,
        string? Notes,
        DateTime AnsweredAt);

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
