using ClientInsightAPI.Services.NewsProviders;
using Dapper;
using Npgsql;
using System.Net;
using Elmah.Io.Client;

namespace ClientInsightAPI.Services;

public sealed class ArticleScanService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ScanJobService _jobs;
    private readonly INewsProvider _provider;
    private readonly ArticleWriteService _writer;
    private readonly ClientScanStateService _state;
    private readonly ILogger<ArticleScanService> _log;

    // slice size prevents “max 250 results” from silently hiding older hits inside a large window
    private static readonly TimeSpan Slice = TimeSpan.FromHours(12);

    public ArticleScanService(
        NpgsqlDataSource ds,
        ScanJobService jobs,
        INewsProvider provider,
        ArticleWriteService writer,
        ClientScanStateService state,
        ILogger<ArticleScanService> log)
    {
        _ds = ds;
        _jobs = jobs;
        _provider = provider;
        _writer = writer;
        _state = state;
        _log = log;
    }

    public async Task RunJobAsync(Guid jobId, Guid companyId, CancellationToken ct)
    {
        try
        {
            await _jobs.MarkRunningAsync(jobId, ct);

            var opts = await _jobs.GetOptionsAsync(jobId, ct) ?? new ScanOptions();

            var clients = await LoadClientsAsync(companyId, opts.ActiveOnly, opts.MaxClients, ct);

            var metrics = new ScanMetrics();

            foreach (var client in clients)
            {
                metrics.ClientsScanned++;

                await _state.MarkRunStartedAsync(client.ClientId, _provider.Name, ct);

                try
                {
                    var lastSuccess = await _state.GetLastSuccessAsync(client.ClientId, _provider.Name, ct);
                    var (fromUtc, toUtc) = _state.ComputeWindow(lastSuccess);

                    // Allow “backdating” via job options if you want:
                    // e.g. first run you can call scan with daysBack=30, and this will still cap to 1 month.
                    // If you want more than 1 month later, we can add a backfill override.
                    var requestedFrom = DateTimeOffset.UtcNow.AddDays(-Math.Abs(opts.DaysBack));
                    if (requestedFrom < fromUtc) fromUtc = requestedFrom;

                    var urlDedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    for (var sliceStart = fromUtc; sliceStart < toUtc; sliceStart = sliceStart.Add(Slice))
                    {
                        var sliceEnd = sliceStart.Add(Slice);
                        if (sliceEnd > toUtc) sliceEnd = toUtc;

                        var items = await _provider.SearchAsync(client, sliceStart, sliceEnd, maxRecords: 250, ct);
                        metrics.ItemsFound += items.Count;

                        foreach (var a in items)
                        {
                            if (!urlDedup.Add(a.Url)) continue;

                            var articleId = await _writer.UpsertArticleAsync(a, ct);
                            await _writer.LinkClientArticleAsync(client.ClientId, articleId, a.MatchScore, a.MatchedOn, ct);

                            metrics.LinksCreated++;
                            // “Upserted” may include existing URLs; keep it as a useful counter anyway
                            metrics.ArticlesUpserted++;
                        }
                    }

                    await _state.MarkRunSucceededAsync(client.ClientId, _provider.Name, DateTimeOffset.UtcNow, ct);
                }
                catch (Exception exClient)
                {
                 //   string errorMessage = $"Error: ({exClient.Message}), Stack: ({exClient.StackTrace.ToString()})" ;

                    await _state.MarkRunFailedAsync(client.ClientId, _provider.Name, exClient.Message, ct);
                    // keep scanning other clients
                }
            }

            await _jobs.MarkSucceededAsync(jobId, metrics, ct);
        }
        catch (Exception ex)
        {
            await _jobs.MarkFailedAsync(jobId, ex, ct);
        }
    }

    private async Task<List<ClientForScan>> LoadClientsAsync(Guid companyId, bool activeOnly, int maxClients, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            SELECT
              cl.client_id AS ClientId,
              cl.name AS Name,
              cl.website AS Website,
              cl.address AS Address
            FROM app.company_clients cc
            JOIN app.clients cl ON cl.client_id = cc.client_id
            WHERE cc.company_id = @companyId
              AND (@activeOnly = false OR cc.is_active = true)
            ORDER BY cc.company_rank NULLS LAST, cl.name
            LIMIT @maxClients;
        ";

        var rows = await conn.QueryAsync<ClientForScan>(
            new CommandDefinition(sql, new { companyId, activeOnly, maxClients }, cancellationToken: ct)
        );

        return rows.AsList();
    }
}
