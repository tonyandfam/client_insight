using Dapper;
using Npgsql;
using ClientInsightAPI.Services.NewsProviders;

namespace ClientInsightAPI.Services;

public sealed class ArticleScanService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ScanJobService _jobs;
    private readonly INewsProvider _provider;

    public ArticleScanService(NpgsqlDataSource ds, ScanJobService jobs, INewsProvider provider)
    {
        _ds = ds;
        _jobs = jobs;
        _provider = provider;
    }

    public async Task RunJobAsync(Guid jobId, Guid companyId, CancellationToken ct)
    {
        try
        {
            await _jobs.MarkRunningAsync(jobId, ct);

            var opts = await _jobs.GetOptionsAsync(jobId, ct)
                       ?? new ScanOptions();

            var fromUtc = DateTimeOffset.UtcNow.AddDays(-Math.Abs(opts.DaysBack));

            // Load clients for this company
            var clients = await LoadClientsAsync(companyId, opts.ActiveOnly, opts.MaxClients, ct);

            var metrics = new ScanMetrics
            {
                ClientsScanned = clients.Count,
                ItemsFound = 0,
                ArticlesUpserted = 0,
                LinksCreated = 0
            };

            // Provider calls (currently Noop returns empty)
            foreach (var c in clients)
            {
                var items = await _provider.SearchAsync(c, fromUtc, limit: 10, ct);
                metrics.ItemsFound += items.Count;

                // Later: upsert articles + link to client
                // metrics.ArticlesUpserted += ...
                // metrics.LinksCreated += ...
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
