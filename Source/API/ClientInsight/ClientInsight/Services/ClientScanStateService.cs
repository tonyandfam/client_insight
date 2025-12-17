using Dapper;
using Npgsql;

namespace ClientInsightAPI.Services;

public sealed class ClientScanStateService
{
    private readonly NpgsqlDataSource _ds;
    public ClientScanStateService(NpgsqlDataSource ds) => _ds = ds;

    public async Task<DateTimeOffset?> GetLastSuccessAsync(Guid clientId, string provider, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            SELECT last_success_at
            FROM app.client_scan_state
            WHERE client_id = @clientId AND provider = @provider;
        ";

        var dt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(sql, new { clientId, provider }, cancellationToken: ct));
        return dt.HasValue ? new DateTimeOffset(dt.Value, TimeSpan.Zero) : null;
    }

    public async Task MarkRunStartedAsync(Guid clientId, string provider, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            INSERT INTO app.client_scan_state(client_id, provider, last_run_at, updated_at)
            VALUES (@clientId, @provider, now(), now())
            ON CONFLICT (client_id, provider) DO UPDATE SET
              last_run_at = now(),
              updated_at = now();
        ";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { clientId, provider }, cancellationToken: ct));
    }

    public async Task MarkRunSucceededAsync(Guid clientId, string provider, DateTimeOffset successAtUtc, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            INSERT INTO app.client_scan_state(client_id, provider, last_success_at, last_error, updated_at)
            VALUES (@clientId, @provider, @successAt, NULL, now())
            ON CONFLICT (client_id, provider) DO UPDATE SET
              last_success_at = @successAt,
              last_error = NULL,
              updated_at = now();
        ";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            clientId,
            provider,
            successAt = successAtUtc.UtcDateTime
        }, cancellationToken: ct));
    }

    public async Task MarkRunFailedAsync(Guid clientId, string provider, string error, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            INSERT INTO app.client_scan_state(client_id, provider, last_error, updated_at)
            VALUES (@clientId, @provider, @error, now())
            ON CONFLICT (client_id, provider) DO UPDATE SET
              last_error = @error,
              updated_at = now();
        ";

        await conn.ExecuteAsync(new CommandDefinition(sql, new { clientId, provider, error }, cancellationToken: ct));
    }

    public (DateTimeOffset fromUtc, DateTimeOffset toUtc) ComputeWindow(DateTimeOffset? lastSuccessUtc)
    {
        var now = DateTimeOffset.UtcNow;
        var monthAgo = now.AddMonths(-1);

        // If never scanned: backfill 1 month.
        var from = lastSuccessUtc ?? monthAgo;

        // If last success is older than 1 month: cap at 1 month back.
        if (from < monthAgo) from = monthAgo;

        return (from, now);
    }
}
