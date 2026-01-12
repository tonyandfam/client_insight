using Dapper;
using Npgsql;
using Microsoft.AspNetCore.Mvc;
using ClientInsightAPI.Services.NewsProviders;
using ClientInsightAPI.Services.ArticleScan;

namespace ClientInsightAPI.Controllers;

[ApiController]
[Route("api/test/gdelt")]
public sealed class GdeltTestController : ControllerBase
{
    private readonly NpgsqlDataSource _ds;
    private readonly INewsProvider _provider;
    private readonly ArticleWriteService _writer;
    private readonly ClientScanStateService _state;

    public GdeltTestController(NpgsqlDataSource ds, INewsProvider provider, ArticleWriteService writer, ClientScanStateService state)
    {
        _ds = ds;
        _provider = provider;
        _writer = writer;
        _state = state;
    }

    [HttpPost("client/{clientId:guid}")]
    public async Task<IActionResult> ScanOneClient(Guid clientId, [FromQuery] int daysBack = 30, CancellationToken ct = default)
    {
        var client = await LoadClientAsync(clientId, ct);
        if (client is null) return NotFound(new { message = "Client not found", clientId });

        await _state.MarkRunStartedAsync(clientId, _provider.Name, ct);

        try
        {
            var lastSuccess = await _state.GetLastSuccessAsync(clientId, _provider.Name, ct);
            var (fromUtc, toUtc) = _state.ComputeWindow(lastSuccess);

            // Allow backdating up to 1 month via query param
            var requestedFrom = DateTimeOffset.UtcNow.AddDays(-Math.Abs(daysBack));
            if (requestedFrom < fromUtc) fromUtc = requestedFrom;

            var urlDedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int found = 0, linked = 0, upserted = 0;

            // single call for test endpoint (you can add slicing later if you want)
            var items = await _provider.SearchAsync(client, fromUtc, toUtc, maxRecords: 250, ct);
            found = items.Count;

            await using var writeConn = await _ds.OpenConnectionAsync(ct);

            foreach (var a in items)
            {
                if (!urlDedup.Add(a.Url)) continue;

                var articleId = await _writer.UpsertArticleAsync(writeConn, a, ct);
                await _writer.LinkClientArticleAsync(writeConn,clientId, articleId, a.MatchScore, a.MatchedOn, ct);

                linked++;
                upserted++;
            }

            await _state.MarkRunSucceededAsync(clientId, _provider.Name, DateTimeOffset.UtcNow, ct);

            return Ok(new
            {
                provider = _provider.Name,
                clientId,
                fromUtc,
                toUtc,
                found,
                articlesUpserted = upserted,
                linksCreated = linked
            });
        }
        catch (Exception ex)
        {
            await _state.MarkRunFailedAsync(clientId, _provider.Name, ex.Message, ct);
            return Problem(ex.Message);
        }
    }

    [HttpPost("company/{companyId:guid}")]
    public async Task<IActionResult> ScanCompanySync(Guid companyId, [FromQuery] int maxClients = 50, CancellationToken ct = default)
    {
        // Convenience endpoint for debugging: scan clients synchronously.
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var clients = (await conn.QueryAsync<ClientForScan>(new CommandDefinition(@"
            SELECT
              cl.client_id AS ClientId,
              cl.name AS Name,
              cl.website AS Website,
              cl.address AS Address
            FROM app.company_clients cc
            JOIN app.clients cl ON cl.client_id = cc.client_id
            WHERE cc.company_id = @companyId
              AND cc.is_active = true
            ORDER BY cc.company_rank NULLS LAST, cl.name
            LIMIT @maxClients;
        ", new { companyId, maxClients }, cancellationToken: ct))).AsList();

        if (clients.Count == 0)
            return NotFound(new { message = "No active clients for company (or company not found)", companyId });

        int clientsScanned = 0, itemsFound = 0, upserted = 0, linked = 0;

        await using var writeConn = await _ds.OpenConnectionAsync(ct);

        foreach (var client in clients)
        {
            clientsScanned++;

            var lastSuccess = await _state.GetLastSuccessAsync(client.ClientId, _provider.Name, ct);
            var (fromUtc, toUtc) = _state.ComputeWindow(lastSuccess);

            var items = await _provider.SearchAsync(client, fromUtc, toUtc, maxRecords: 250, ct);
            itemsFound += items.Count;

            foreach (var a in items)
            {
                var articleId = await _writer.UpsertArticleAsync(writeConn, a, ct);
                await _writer.LinkClientArticleAsync(writeConn, client.ClientId, articleId, a.MatchScore, a.MatchedOn, ct);
                upserted++;
                linked++;
            }

            await _state.MarkRunSucceededAsync(client.ClientId, _provider.Name, DateTimeOffset.UtcNow, ct);
        }

        return Ok(new { companyId, provider = _provider.Name, clientsScanned, itemsFound, articlesUpserted = upserted, linksCreated = linked });
    }

    private async Task<ClientForScan?> LoadClientAsync(Guid clientId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        return await conn.QuerySingleOrDefaultAsync<ClientForScan>(new CommandDefinition(@"
            SELECT
              client_id AS ClientId,
              name AS Name,
              website AS Website,
              address AS Address
            FROM app.clients
            WHERE client_id = @clientId;
        ", new { clientId }, cancellationToken: ct));
    }
}
