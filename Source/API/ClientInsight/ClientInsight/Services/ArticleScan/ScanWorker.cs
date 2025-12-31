namespace ClientInsightAPI.Services.ArticleScan;

public sealed class ScanWorker : BackgroundService
{
    private readonly ScanQueue _queue;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ScanWorker> _log;

    public ScanWorker(ScanQueue queue, IServiceProvider sp, ILogger<ScanWorker> log)
    {
        _queue = queue;
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _sp.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<ArticleScanService>();
                await runner.RunJobAsync(item.JobId, item.CompanyId, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scan job crashed: JobId={JobId} CompanyId={CompanyId}", item.JobId, item.CompanyId);
                // Job failure handling is done inside ArticleScanService so we keep the worker simple.
            }
        }
    }
}
