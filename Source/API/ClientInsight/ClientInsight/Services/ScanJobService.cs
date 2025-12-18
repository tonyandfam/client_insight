using Dapper;
using Npgsql;

namespace ClientInsightAPI.Services;

public sealed class ScanJobService
{
    private readonly NpgsqlDataSource _ds;

    public ScanJobService(NpgsqlDataSource ds) => _ds = ds;

    /// <summary>
    /// Create a new scan job row in the database (status defaults to 'queued' in DB).
    /// </summary>
    public async Task<Guid> CreateJobAsync(Guid companyId, ScanOptions options, string? provider, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var jobId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
            INSERT INTO app.scan_jobs(company_id, days_back, max_clients, active_only, provider)
            VALUES (@companyId, @daysBack, @maxClients, @activeOnly, @provider)
            RETURNING job_id;
        ", new
        {
            companyId,
            daysBack = options.DaysBack,
            maxClients = options.MaxClients,
            activeOnly = options.ActiveOnly,
            provider
        }, cancellationToken: ct));

        return jobId;
    }

    /// <summary>
    /// Returns a job with status + metrics.
    /// </summary>
    public async Task<ScanJobDto?> GetJobAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        return await conn.QuerySingleOrDefaultAsync<ScanJobDto>(new CommandDefinition(@"
            SELECT
              job_id AS JobId,
              company_id AS CompanyId,
              requested_at AS RequestedAt,
              started_at AS StartedAt,
              completed_at AS CompletedAt,
              status AS Status,
              error AS Error,
              days_back AS DaysBack,
              max_clients AS MaxClients,
              active_only AS ActiveOnly,
              provider AS Provider,
              clients_scanned AS ClientsScanned,
              items_found AS ItemsFound,
              articles_upserted AS ArticlesUpserted,
              links_created AS LinksCreated
            FROM app.scan_jobs
            WHERE job_id = @jobId;
        ", new { jobId }, cancellationToken: ct));
    }

    /// <summary>
    /// Reads scan options for a job.
    /// </summary>
    public async Task<ScanOptions?> GetOptionsAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        return await conn.QuerySingleOrDefaultAsync<ScanOptions>(new CommandDefinition(@"
            SELECT
              days_back AS DaysBack,
              max_clients AS MaxClients,
              active_only AS ActiveOnly
            FROM app.scan_jobs
            WHERE job_id = @jobId;
        ", new { jobId }, cancellationToken: ct));
    }

    /// <summary>
    /// Mark a job as running. Sets started_at if not already set.
    /// </summary>
    public async Task MarkRunningAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE app.scan_jobs
            SET status = 'running',
                started_at = COALESCE(started_at, now()),
                error = NULL
            WHERE job_id = @jobId;
        ", new { jobId }, cancellationToken: ct));
    }

    /// <summary>
    /// Mark a job as succeeded and persist metrics.
    /// </summary>
    public async Task MarkSucceededAsync(Guid jobId, ScanMetrics metrics, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE app.scan_jobs
            SET status = 'succeeded',
                completed_at = now(),
                clients_scanned = @clientsScanned,
                items_found = @itemsFound,
                articles_upserted = @articlesUpserted,
                links_created = @linksCreated,
                error = NULL
            WHERE job_id = @jobId;
        ", new
        {
            jobId,
            clientsScanned = metrics.ClientsScanned,
            itemsFound = metrics.ItemsFound,
            articlesUpserted = metrics.ArticlesUpserted,
            linksCreated = metrics.LinksCreated
        }, cancellationToken: ct));
    }

    /// <summary>
    /// Mark a job as failed with an error message.
    /// </summary>
    public async Task MarkFailedAsync(Guid jobId, Exception ex, CancellationToken ct)
    {
        await MarkFailedAsync(jobId, ex.Message, ct);
    }

    /// <summary>
    /// Mark a job as failed with a specific error string.
    /// </summary>
    public async Task MarkFailedAsync(Guid jobId, string error, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE app.scan_jobs
            SET status = 'failed',
                completed_at = now(),
                error = @err
            WHERE job_id = @jobId;
        ", new { jobId, err = error }, cancellationToken: ct));
    }

    // ============================
    // Recovery helpers (important!)
    // ============================

    public sealed record RecoverableJob(Guid JobId, Guid CompanyId);

    /// <summary>
    /// Returns jobs that should be re-enqueued on app startup.
    /// Single-instance assumption: any 'running' job at startup is interrupted and must be resumed.
    /// </summary>
    public async Task<IReadOnlyList<RecoverableJob>> GetRecoverableJobsAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            SELECT job_id AS JobId, company_id AS CompanyId
            FROM app.scan_jobs
            WHERE status IN ('queued','running')
            ORDER BY requested_at ASC
            LIMIT 200;
        ";

        var rows = await conn.QueryAsync<RecoverableJob>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task MarkQueuedAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE app.scan_jobs
            SET status = 'queued',
                error = NULL
            WHERE job_id = @jobId;
        ", new { jobId }, cancellationToken: ct));
    }
}

public sealed class ScanOptions
{
    public int DaysBack { get; set; } = 30;
    public int MaxClients { get; set; } = 50;
    public bool ActiveOnly { get; set; } = true;
}

public sealed class ScanMetrics
{
    public int ClientsScanned { get; set; }
    public int ItemsFound { get; set; }
    public int ArticlesUpserted { get; set; }
    public int LinksCreated { get; set; }
}

public sealed class ScanJobDto
{
    public Guid JobId { get; set; }
    public Guid CompanyId { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }

    public int DaysBack { get; set; }
    public int MaxClients { get; set; }
    public bool ActiveOnly { get; set; }
    public string? Provider { get; set; }

    public int? ClientsScanned { get; set; }
    public int? ItemsFound { get; set; }
    public int? ArticlesUpserted { get; set; }
    public int? LinksCreated { get; set; }
}
