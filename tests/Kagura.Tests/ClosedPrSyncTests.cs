using Kagura.Api.HostedServices;
using Kagura.Core.Domain;
using Kagura.Core.Sources;
using Kagura.Data;
using Kagura.Data.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

public class ClosedPrSyncTests
{
    [Fact]
    public async Task Sync_marks_workitem_Closed_when_PR_closed_upstream()
    {
        using var test = TestDb.Create();
        var source = await SeedSourceWithItems(test, new[] { "EXT-1", "EXT-2" });

        // Provider now only returns EXT-1 (EXT-2 was closed upstream).
        var provider = new StubIssueProvider(SourceType.Markdown, new[]
        {
            new FetchedIssue("EXT-1", "still open", "body", null, null),
        });
        var svc = NewSyncService(test, provider);

        var result = await svc.SyncAsync(source.Id);

        Assert.Equal(1, result.Closed);

        using var verify = test.NewContext();
        var open = await verify.WorkItems.SingleAsync(w => w.ExternalId == "EXT-1");
        var closed = await verify.WorkItems.SingleAsync(w => w.ExternalId == "EXT-2");

        Assert.NotEqual(WorkItemStatus.Closed, open.Status);
        Assert.Equal(WorkItemStatus.Closed, closed.Status);
    }

    [Fact]
    public async Task Sync_sets_ClosedAt_when_marking_workitem_Closed()
    {
        using var test = TestDb.Create();
        var source = await SeedSourceWithItems(test, new[] { "EXT-1" });
        var before = DateTime.UtcNow;

        var provider = new StubIssueProvider(SourceType.Markdown, Array.Empty<FetchedIssue>());
        var svc = NewSyncService(test, provider);

        await svc.SyncAsync(source.Id);

        using var verify = test.NewContext();
        var closed = await verify.WorkItems.SingleAsync(w => w.ExternalId == "EXT-1");
        Assert.Equal(WorkItemStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAt);
        Assert.InRange(closed.ClosedAt!.Value, before, DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Cleanup_deletes_old_closed_workitems_with_their_tasks_but_keeps_newer_ones()
    {
        using var test = TestDb.Create();
        var now = new DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc);
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };

        var old = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "OLD",
            Title = "old closed",
            Status = WorkItemStatus.Closed,
            ClosedAt = now.AddDays(-8),
        };
        old.Tasks.Add(new AgentTask { Title = "old task" });

        var recent = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "RECENT",
            Title = "recently closed",
            Status = WorkItemStatus.Closed,
            ClosedAt = now.AddDays(-3),
        };
        recent.Tasks.Add(new AgentTask { Title = "recent task" });

        var openItem = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "OPEN",
            Title = "still open",
            Status = WorkItemStatus.New,
            ClosedAt = null,
        };

        test.Context.Sources.Add(source);
        test.Context.WorkItems.AddRange(old, recent, openItem);
        await test.Context.SaveChangesAsync();

        var oldId = old.Id;
        var recentId = recent.Id;
        var openId = openItem.Id;
        var oldTaskId = old.Tasks[0].Id;

        var clock = new FixedTimeProvider(now);
        var scopeFactory = BuildScopeFactory(test);
        var cleanup = new WorkItemCleanupService(
            scopeFactory,
            new WorkItemCleanupOptions { Retention = TimeSpan.FromDays(7) },
            NullLogger<WorkItemCleanupService>.Instance,
            clock);

        var removed = await cleanup.CleanupAsync(CancellationToken.None);

        Assert.Equal(1, removed);

        using var verify = test.NewContext();
        Assert.Null(await verify.WorkItems.FirstOrDefaultAsync(w => w.Id == oldId));
        Assert.Null(await verify.AgentTasks.FirstOrDefaultAsync(t => t.Id == oldTaskId));
        Assert.NotNull(await verify.WorkItems.FirstOrDefaultAsync(w => w.Id == recentId));
        Assert.NotNull(await verify.WorkItems.FirstOrDefaultAsync(w => w.Id == openId));
    }

    private static async Task<Source> SeedSourceWithItems(TestDb test, IEnumerable<string> externalIds)
    {
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo", Type = SourceType.Markdown };
        test.Context.Sources.Add(source);
        foreach (var ext in externalIds)
        {
            test.Context.WorkItems.Add(new WorkItem
            {
                SourceId = source.Id,
                ExternalId = ext,
                Title = ext,
                Body = "",
            });
        }
        await test.Context.SaveChangesAsync();
        return source;
    }

    private static SourceSyncService NewSyncService(TestDb test, IIssueProvider provider)
    {
        var factory = new StubProviderFactory(provider);
        return new SourceSyncService(test.NewContext(), factory, NullLogger<SourceSyncService>.Instance);
    }

    private static IServiceScopeFactory BuildScopeFactory(TestDb test)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped(_ => test.NewContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class StubIssueProvider : IIssueProvider
    {
        private readonly IReadOnlyList<FetchedIssue> _issues;
        public StubIssueProvider(SourceType type, IEnumerable<FetchedIssue> issues)
        {
            Type = type;
            _issues = issues.ToList();
        }
        public SourceType Type { get; }
        public Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default)
            => Task.FromResult(_issues);
    }

    private sealed class StubProviderFactory : IIssueProviderFactory
    {
        private readonly IIssueProvider _provider;
        public StubProviderFactory(IIssueProvider provider) => _provider = provider;
        public IIssueProvider Get(SourceType type) => _provider;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime utcNow) => _now = new DateTimeOffset(utcNow, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
