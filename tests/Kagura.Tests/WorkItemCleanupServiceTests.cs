using Kagura.Api.HostedServices;
using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

internal sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}

public class WorkItemCleanupServiceTests
{
    [Fact]
    public async Task Cleanup_removes_workitems_closed_more_than_seven_days_ago_with_tasks()
    {
        using var test = TestDb.Create();
        var now = new DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(now);

        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/r" };
        var stale = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "STALE",
            Title = "stale",
            ClosedAt = now.AddDays(-8),
            Tasks =
            {
                new AgentTask { Title = "t1" },
                new AgentTask { Title = "t2" },
            },
        };
        var fresh = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "FRESH",
            Title = "fresh",
            ClosedAt = now.AddDays(-3),
            Tasks = { new AgentTask { Title = "t3" } },
        };
        var open = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "OPEN",
            Title = "open",
            ClosedAt = null,
        };
        test.Context.Sources.Add(source);
        test.Context.WorkItems.AddRange(stale, fresh, open);
        await test.Context.SaveChangesAsync();

        var staleTaskIds = stale.Tasks.Select(t => t.Id).ToArray();

        var services = new ServiceCollection();
        services.AddScoped(_ => test.NewContext());
        await using var sp = services.BuildServiceProvider();

        var sut = new WorkItemCleanupService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new WorkItemCleanupOptions { Retention = TimeSpan.FromDays(7) },
            NullLogger<WorkItemCleanupService>.Instance,
            clock);

        var removed = await sut.CleanupAsync(CancellationToken.None);

        Assert.Equal(1, removed);

        using var verify = test.NewContext();
        var remainingIds = await verify.WorkItems.Select(w => w.ExternalId).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { "FRESH", "OPEN" }, remainingIds);

        var orphanedTasks = await verify.AgentTasks.Where(t => staleTaskIds.Contains(t.Id)).CountAsync();
        Assert.Equal(0, orphanedTasks);
    }
}
