namespace ClientInsightAPI.Services;

public sealed class ArticleContentRecoveryWorker : BackgroundService
{
    private readonly ILogger<ArticleContentRecoveryWorker> _log;
    private readonly IServiceProvider _sp;

    public ArticleContentRecoveryWorker(ILogger<ArticleContentRecoveryWorker> log, IServiceProvider sp)
    {
        _log = log;
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ArticleContentService>();

        var reset = await svc.ResetStaleProcessingAsync(staleMinutes: 30, stoppingToken);
        if (reset > 0)
            _log.LogWarning("Reset {Count} stale article_contents rows from processing -> pending.", reset);
        else
            _log.LogInformation("No stale article_contents rows to reset.");
    }
}
