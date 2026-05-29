using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Merge;
using Kagura.Core.Review;
using Kagura.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

// End-to-end coverage for the auto-review pause/resume cycle: POST /auto-review kicks off a
// background pipeline that, when the stubbed reviewer says don't auto-merge, blocks on
// IInteractivePromptService.AskAsync until the user responds via POST /respond. We exercise
// both available choices ("skip" leaves the task AwaitingReview; "merge" overrides and merges).
public class AutoReviewPipelineTests : IClassFixture<AutoReviewPipelineTests.AppFactory>
{
    private readonly AppFactory _app;

    public AutoReviewPipelineTests(AppFactory app) => _app = app;

    [Fact]
    public async Task Pipeline_pauses_on_prompt_and_resumes_with_skip_leaving_task_unmerged()
    {
        var (wiId, taskId) = await SeedRepoAndWorkItemAsync();
        _app.Reviewer.NextVerdict = new ReviewVerdict(AutoMerge: false, Reasoning: "Diff is too large.");

        using var client = _app.CreateClient();
        var runId = await StartAutoReviewAsync(client, wiId);

        var pending = await WaitForPromptAsync(client, runId)
            ?? throw new InvalidOperationException("Pipeline never raised an interactive prompt — interactive code path missing?");
        Assert.Equal(new[] { "merge", "skip" }, pending.Choices?.ToArray());
        Assert.Contains("Diff is too large.", pending.Question);

        var respond = await client.PostAsJsonAsync(
            $"/api/agents/{runId}/prompts/{pending.Id}/respond",
            new PromptAnswer("skip"));
        Assert.Equal(HttpStatusCode.NoContent, respond.StatusCode);

        await WaitForRunFinishedAsync(runId);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var task = await db.AgentTasks.SingleAsync(t => t.Id == taskId);
        Assert.Equal(AgentTaskStatus.AwaitingReview, task.Status);
        Assert.Equal("Diff is too large.", task.ReviewNotes);
    }

    [Fact]
    public async Task Pipeline_pauses_on_prompt_and_resumes_with_merge_completes_the_merge()
    {
        var (wiId, taskId) = await SeedRepoAndWorkItemAsync();
        _app.Reviewer.NextVerdict = new ReviewVerdict(AutoMerge: false, Reasoning: "Tests look thin.");

        using var client = _app.CreateClient();
        var runId = await StartAutoReviewAsync(client, wiId);

        var pending = await WaitForPromptAsync(client, runId)
            ?? throw new InvalidOperationException("Pipeline never raised an interactive prompt — interactive code path missing?");

        var respond = await client.PostAsJsonAsync(
            $"/api/agents/{runId}/prompts/{pending.Id}/respond",
            new PromptAnswer("merge"));
        Assert.Equal(HttpStatusCode.NoContent, respond.StatusCode);

        await WaitForRunFinishedAsync(runId);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var task = await db.AgentTasks.SingleAsync(t => t.Id == taskId);
        Assert.Equal(AgentTaskStatus.Merged, task.Status);
        Assert.NotNull(task.ReviewNotes);
        Assert.StartsWith("User overrode reviewer", task.ReviewNotes);
    }

    [Fact]
    public async Task Responding_to_unknown_prompt_returns_404()
    {
        var (_, _) = await SeedRepoAndWorkItemAsync();
        using var client = _app.CreateClient();

        var resp = await client.PostAsJsonAsync(
            $"/api/agents/{Guid.NewGuid()}/prompts/{Guid.NewGuid()}/respond",
            new PromptAnswer("merge"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<Guid> StartAutoReviewAsync(HttpClient client, Guid wiId)
    {
        var startResp = await client.PostAsync($"/api/workitems/{wiId}/auto-review", null);
        Assert.Equal(HttpStatusCode.Accepted, startResp.StatusCode);
        var started = await startResp.Content.ReadFromJsonAsync<StartedRun>();
        Assert.NotNull(started);
        Assert.NotEqual(Guid.Empty, started!.RunId);
        return started.RunId;
    }

    private static async Task<PendingPrompt?> WaitForPromptAsync(HttpClient client, Guid runId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var prompts = await client.GetFromJsonAsync<List<PendingPrompt>>($"/api/agents/{runId}/prompts");
            if (prompts is { Count: > 0 }) return prompts[0];
            await Task.Delay(25);
        }
        return null;
    }

    private async Task WaitForRunFinishedAsync(Guid runId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            if (run.Status is AgentRunStatus.Exited or AgentRunStatus.Crashed or AgentRunStatus.Killed)
                return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Auto-review run did not reach a terminal status within 10s.");
    }

    private async Task<(Guid workItemId, Guid taskId)> SeedRepoAndWorkItemAsync()
    {
        var repo = Path.Combine(Path.GetTempPath(), "kagura-arp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        _app.TempRepos.Add(repo);

        RunGit(repo, "init", "-b", "main");
        RunGit(repo, "config", "user.email", "test@kagura.local");
        RunGit(repo, "config", "user.name", "test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "initial\n");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "initial");

        Guid wiId, taskId;
        string workItemBranch, taskBranch;

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<GitService>();

            var source = new Source { Name = "src-" + Guid.NewGuid().ToString("N")[..8], LocalRepoPath = repo };
            var wi = new WorkItem
            {
                SourceId = source.Id,
                ExternalId = "ARP-" + Guid.NewGuid().ToString("N")[..8],
                Title = "auto-review pipeline test",
                Status = WorkItemStatus.InProgress,
            };
            var task = new AgentTask
            {
                Title = "Add feature",
                Description = "Add a new feature.",
                Order = 0,
                Status = AgentTaskStatus.AwaitingReview,
            };
            wi.Tasks.Add(task);
            db.Sources.Add(source);
            db.WorkItems.Add(wi);
            await db.SaveChangesAsync();

            wiId = wi.Id;
            taskId = task.Id;
            workItemBranch = git.WorkItemBranchName(wi);
            taskBranch = git.TaskBranchName(wi, task);
        }

        // Lay down the work-item and task branches with a real diff so MergeTaskBranchAsync
        // has something to merge when the user picks "merge".
        RunGit(repo, "branch", workItemBranch);
        RunGit(repo, "checkout", "-b", taskBranch, workItemBranch);
        File.WriteAllText(Path.Combine(repo, "feature.txt"), "feature added\n");
        RunGit(repo, "add", ".");
        RunGit(repo, "commit", "-m", "add feature");
        RunGit(repo, "checkout", "main");

        return (wiId, taskId);
    }

    private static void RunGit(string repo, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} exited {p.ExitCode}\nstdout: {stdout}\nstderr: {stderr}");
    }

    private sealed record StartedRun([property: JsonPropertyName("runId")] Guid RunId);
    private sealed record PromptAnswer([property: JsonPropertyName("answer")] string Answer);
    private sealed record PendingPrompt(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("runId")] Guid RunId,
        [property: JsonPropertyName("question")] string Question,
        [property: JsonPropertyName("choices")] List<string>? Choices,
        [property: JsonPropertyName("createdAt")] DateTime CreatedAt);

    public sealed class StubReviewService : IReviewService
    {
        public ReviewVerdict NextVerdict { get; set; } = new(true, "stub default");
        public Task<ReviewVerdict> ReviewAsync(
            Guid runId, string taskTitle, string taskDescription, string diff, CancellationToken ct = default)
            => Task.FromResult(NextVerdict);
    }

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _connStr = $"Data Source=arp-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keepAlive;

        public StubReviewService Reviewer { get; } = new();
        public string WorktreesRoot { get; }
        public List<string> TempRepos { get; } = new();

        public AppFactory()
        {
            _keepAlive = new SqliteConnection(_connStr);
            _keepAlive.Open();
            WorktreesRoot = Path.Combine(Path.GetTempPath(), "kagura-arp-wt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorktreesRoot);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<KaguraDbContext>>();
                services.RemoveAll<KaguraDbContext>();
                services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite(_connStr));

                services.RemoveAll<IReviewService>();
                services.AddSingleton<IReviewService>(Reviewer);

                // Pin the GitService at a deterministic temp worktrees root so test cleanup
                // can find and remove the worktrees we created.
                services.RemoveAll<GitService>();
                services.AddSingleton(sp => new GitService(
                    WorktreesRoot,
                    sp.GetRequiredService<IMergeConflictResolver>(),
                    NullLogger<GitService>.Instance));
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
            if (!disposing) return;
            _keepAlive.Dispose();
            foreach (var r in TempRepos) TryDelete(r);
            TryDelete(WorktreesRoot);
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
