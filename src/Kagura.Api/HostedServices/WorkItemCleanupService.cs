using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.HostedServices;

public sealed class WorkItemCleanupOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

public sealed class WorkItemCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkItemCleanupOptions _options;
    private readonly ILogger<WorkItemCleanupService> _logger;
    private readonly TimeProvider _clock;

    public WorkItemCleanupService(
        IServiceScopeFactory scopeFactory,
        WorkItemCleanupOptions options,
        ILogger<WorkItemCleanupService> logger,
        TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await CleanupAsync(stoppingToken);
                if (removed > 0)
                    _logger.LogInformation("Cleaned up {Count} closed work items older than {Days}d", removed, _options.Retention.TotalDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Work-item cleanup pass failed");
            }

            try
            {
                await Task.Delay(_options.Interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - _options.Retention;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();

        var stale = await db.WorkItems
            .Where(w => w.ClosedAt != null && w.ClosedAt < cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        db.WorkItems.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }
}
