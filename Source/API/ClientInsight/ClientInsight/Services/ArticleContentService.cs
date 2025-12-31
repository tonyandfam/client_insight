using Dapper;
using Npgsql;

namespace ClientInsightAPI.Services;

public sealed class ArticleContentService
{
    private readonly NpgsqlDataSource _ds;

    public ArticleContentService(NpgsqlDataSource ds) => _ds = ds;

    public sealed record ClaimedArticle(long ArticleId, string Url, int Attempts);

    /// <summary>
    /// Claim a batch of work from the DB. Safe for multiple instances.
    /// Includes "stale processing" recovery (claimed_at older than staleMinutes).
    /// Only claims articles that are linked to at least one client (client_articles).
    /// </summary>
    public async Task<IReadOnlyList<ClaimedArticle>> ClaimBatchAsync(
        int take,
        int staleMinutes,
        string workerId,
        CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        staleMinutes = Math.Clamp(staleMinutes, 1, 24 * 60);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
WITH candidates AS (
  SELECT a.article_id
  FROM app.articles a
  JOIN app.client_articles ca ON ca.article_id = a.article_id
  LEFT JOIN app.article_contents ac ON ac.article_id = a.article_id
  WHERE
    ac.article_id IS NULL
    OR ac.status IN ('pending', 'failed_retryable')
    OR (ac.status = 'processing' AND ac.claimed_at < now() - (@staleMinutes * interval '1 minute'))
  GROUP BY a.article_id
  ORDER BY max(ca.created_at) DESC
  LIMIT @take
),
upserted AS (
  INSERT INTO app.article_contents(article_id, status, attempts, claimed_at, claimed_by, last_error)
  SELECT c.article_id, 'processing', 1, now(), @workerId, NULL
  FROM candidates c
  ON CONFLICT (article_id) DO UPDATE SET
    status     = 'processing',
    attempts   = app.article_contents.attempts + 1,
    claimed_at = now(),
    claimed_by = EXCLUDED.claimed_by,
    last_error = NULL
  WHERE
    app.article_contents.status IN ('pending', 'failed_retryable')
    OR (app.article_contents.status = 'processing' AND app.article_contents.claimed_at < now() - (@staleMinutes * interval '1 minute'))
  RETURNING article_id
)
SELECT u.article_id        AS ArticleId,
       a.url              AS Url,
       ac.attempts        AS Attempts
FROM upserted u
JOIN app.articles a ON a.article_id = u.article_id
JOIN app.article_contents ac ON ac.article_id = u.article_id;
";

        var rows = await conn.QueryAsync<ClaimedArticle>(new CommandDefinition(
            sql,
            new { take, staleMinutes, workerId },
            cancellationToken: ct));

        return rows.AsList();
    }

    public async Task MarkSucceededAsync(
        long articleId,
        string? finalUrl,
        int httpStatus,
        string? contentType,
        string? extractedLanguage,
        int wordCount,
        string? sha256,
        string extractedText,
        string? rawHtml,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
UPDATE app.article_contents
SET status = 'succeeded',
    fetched_at = now(),
    final_url = @finalUrl,
    last_http_status = @httpStatus,
    last_content_type = @contentType,
    extracted_language = @extractedLanguage,
    word_count = @wordCount,
    content_sha256 = @sha256,
    extracted_text = @extractedText,
    raw_html = @rawHtml,
    last_error = NULL,
    claimed_at = NULL,
    claimed_by = NULL
WHERE article_id = @articleId;
";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            articleId,
            finalUrl,
            httpStatus,
            contentType,
            extractedLanguage,
            wordCount,
            sha256,
            extractedText,
            rawHtml
        }, cancellationToken: ct));
    }

    public async Task MarkFailedAsync(
        long articleId,
        string status, // failed_retryable | failed_permanent
        int? httpStatus,
        string? contentType,
        string error,
        CancellationToken ct)
    {
        // keep values controlled
        if (status is not ("failed_retryable" or "failed_permanent"))
            status = "failed_retryable";

        // prevent huge DB field spam
        if (error.Length > 8000) error = error[..8000];

        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
UPDATE app.article_contents
SET status = @status,
    fetched_at = COALESCE(fetched_at, now()),
    last_http_status = @httpStatus,
    last_content_type = @contentType,
    last_error = @error,
    claimed_at = NULL,
    claimed_by = NULL
WHERE article_id = @articleId;
";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            articleId,
            status,
            httpStatus,
            contentType,
            error
        }, cancellationToken: ct));
    }

    /// <summary>
    /// Optional: on startup, reset old "processing" rows back to pending.
    /// Not strictly required because ClaimBatchAsync treats stale processing as claimable,
    /// but this makes status nicer.
    /// </summary>
    public async Task<int> ResetStaleProcessingAsync(int staleMinutes, CancellationToken ct)
    {
        staleMinutes = Math.Clamp(staleMinutes, 1, 24 * 60);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
UPDATE app.article_contents
SET status = 'pending',
    claimed_at = NULL,
    claimed_by = NULL
WHERE status = 'processing'
  AND claimed_at < now() - (@staleMinutes * interval '1 minute');
";

        return await conn.ExecuteAsync(new CommandDefinition(sql, new { staleMinutes }, cancellationToken: ct));
    }
}
