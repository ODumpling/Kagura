using Kagura.Api.Services;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Merge;
using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kagura.Tests;

/// <summary>
/// Regression coverage for the AsyncLocal flow bug: pushing
/// <see cref="MergeResolverAgentContext"/> from inside an awaited async method does NOT
/// propagate the value back to the caller — mutations only flow downstream. The kickoff
/// service therefore has to invoke the resolver inside its own frame, with the push made
/// before the (in-frame) <c>await</c> on the resolver. If somebody refactors that back
/// to the old "BeginAsync returns a disposer" shape, this test snaps.
/// </summary>
public class MergeResolverKickoffServiceTests
{
    [Fact]
    public async Task ResolveAsync_makes_context_visible_to_resolver()
    {
        var connStr = $"Data Source=kickoff-asynclocal-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connStr);
        keepAlive.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite(connStr));
        services.AddScoped<IPromptSnapshotSink>(_ => new NoopPromptSnapshotSink());
        var context = new MergeResolverAgentContext();
        services.AddSingleton(context);
        var captor = new ContextCapturingResolver(context);
        services.AddSingleton<IMergeConflictResolver>(captor);
        services.AddSingleton<IMergeResolverKickoff, MergeResolverKickoffService>();

        await using var sp = services.BuildServiceProvider();
        await using (var s = sp.CreateAsyncScope())
            await s.ServiceProvider.GetRequiredService<KaguraDbContext>().Database.EnsureCreatedAsync();

        var source = new Source { Name = "src", LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "EXT",
            Title = "wi",
            Status = WorkItemStatus.InProgress,
        };
        var task = new AgentTask
        {
            WorkItemId = wi.Id,
            Title = "merge me",
            Description = "",
            Order = 0,
            Status = AgentTaskStatus.AwaitingReview,
        };
        await using (var s = sp.CreateAsyncScope())
        {
            var db = s.ServiceProvider.GetRequiredService<KaguraDbContext>();
            db.Sources.Add(source);
            db.WorkItems.Add(wi);
            db.AgentTasks.Add(task);
            await db.SaveChangesAsync();
        }

        var kickoff = sp.GetRequiredService<IMergeResolverKickoff>();
        var result = await kickoff.ResolveAsync(wi, task, "/tmp/worktree", CancellationToken.None);

        Assert.True(captor.SawContextSet, "Resolver must see MergeResolverAgentContext as set when invoked through the kickoff.");
        Assert.Same(wi, captor.SeenWorkItem);
        Assert.NotEqual(Guid.Empty, captor.SeenRunId);
        Assert.False(string.IsNullOrEmpty(captor.SeenPrompt));
        Assert.True(result.Success);

        // Context is popped after the resolver call returns.
        Assert.False(context.IsSet);
    }

    private sealed class NoopPromptSnapshotSink : IPromptSnapshotSink
    {
        public Task SaveAsync(Guid runId, string promptText, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ContextCapturingResolver : IMergeConflictResolver
    {
        private readonly MergeResolverAgentContext _context;
        public bool SawContextSet { get; private set; }
        public WorkItem? SeenWorkItem { get; private set; }
        public Guid SeenRunId { get; private set; }
        public string? SeenPrompt { get; private set; }

        public ContextCapturingResolver(MergeResolverAgentContext context) => _context = context;

        public Task<MergeResolutionResult> ResolveAsync(string worktreePath, string taskTitle, CancellationToken ct = default)
        {
            SawContextSet = _context.IsSet;
            SeenWorkItem = _context.WorkItem;
            SeenRunId = _context.RunId;
            SeenPrompt = _context.Prompt;
            return Task.FromResult(new MergeResolutionResult(true, "stub"));
        }
    }
}
