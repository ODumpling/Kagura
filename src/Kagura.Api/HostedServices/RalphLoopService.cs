using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.HostedServices;

public sealed class RalphLoopOptions
{
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxRetryAttempts { get; set; } = 3;
    public int MaxConcurrentTasksPerWorkItem { get; set; } = 3;
}

public sealed class RalphLoopService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RalphLoopOptions _options;
    private readonly ILogger<RalphLoopService> _log;
    private readonly TimeProvider _clock;

    public RalphLoopService(
        IServiceScopeFactory scopeFactory,
        RalphLoopOptions options,
        ILogger<RalphLoopService> log,
        TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ralph loop tick failed");
            }

            try
            {
                await Task.Delay(_options.TickInterval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        Guid[] active;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
            active = await db.WorkItems
                .Where(w => w.RalphLoopActive)
                .Select(w => w.Id)
                .ToArrayAsync(ct);
        }

        foreach (var id in active)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = _scopeFactory.CreateScope();
            var driver = scope.ServiceProvider.GetRequiredService<RalphLoopDriver>();
            try
            {
                await driver.AdvanceAsync(id, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ralph loop advance failed for work item {WorkItemId}", id);
            }
        }
    }
}
