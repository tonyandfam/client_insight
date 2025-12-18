using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClientInsightAPI.Services;

public sealed class ScanRecoveryWorker : BackgroundService
{
    private readonly ILogger<ScanRecoveryWorker> _log;
    private readonly IServiceProvider _sp;
    private readonly ScanQueue _queue;

    public ScanRecoveryWorker(ILogger<ScanRecoveryWorker> log, IServiceProvider sp, ScanQueue queue)
    {
        _log = log;
        _sp = sp;
        _queue = queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once on startup
        using var scope = _sp.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ScanJobService>();

        var recover = await jobs.GetRecoverableJobsAsync(stoppingToken);

        if (recover.Count == 0)
        {
            _log.LogInformation("No scan jobs to recover.");
            return;
        }

        _log.LogWarning("Recovering {Count} scan jobs into in-memory queue...", recover.Count);

        foreach (var j in recover)
        {
            // Ensure status shows queued again
            await jobs.MarkQueuedAsync(j.JobId, stoppingToken);

            // Re-enqueue into in-memory worker queue
            await _queue.EnqueueAsync(new ScanWorkItem(j.JobId, j.CompanyId), stoppingToken);
        }

        _log.LogWarning("Recovered {Count} scan jobs.", recover.Count);
    }
}
