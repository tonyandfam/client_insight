using Dapper;
using Npgsql;

namespace ClientInsightAPI.Services.Llm;

public sealed class LlmSummaryService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<LlmSummaryService> _log;

    public LlmSummaryService(NpgsqlDataSource ds, ILogger<LlmSummaryService> log)
    {
        _ds = ds;
        _log = log;
    }

    // Dapper-friendly POCO (parameterless + settable props)
    public sealed class WorkItem
    {
        public Guid ClientId { get; set; }
        public long ArticleId { get; set; }

        public string ClientName { get; set; } = "";
        public string? ClientWebsite { get; set; }

        public string Url { get; set; } = "";
        public string? Title { get; set; }
        public string? Source { get; set; }

        // Use DateTime? to avoid constructor/type mapping issues with timestamptz
        public DateTime? PublishedAtUtc { get; set; }

        public string ExtractedText { get; set; } = "";
    }

    public async Task<IReadOnlyList<WorkItem>> ClaimBatchAsync(string workerId, LlmOptions opts, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1) Ensure state rows exist for candidates (idempotent insert)
        // Assumes you have app.article_contents with: article_id, status, extracted_text
        var ensureSql = @"
WITH candidates AS (
    SELECT ca.client_id, ca.article_id
    FROM app.client_articles ca
    JOIN app.article_contents ac ON ac.article_id = ca.article_id
    LEFT JOIN app.llm_summaries s ON s.client_id = ca.client_id AND s.article_id = ca.article_id
    WHERE ac.status = 'succeeded'
      AND ac.extracted_text IS NOT NULL
      AND length(ac.extracted_text) >= @minChars
      AND s.client_id IS NULL
)
INSERT INTO app.llm_summary_state (client_id, article_id)
SELECT client_id, article_id
FROM candidates
ON CONFLICT (client_id, article_id) DO NOTHING;
";
        await conn.ExecuteAsync(new CommandDefinition(
            ensureSql,
            new { minChars = opts.MinExtractedTextChars },
            tx,
            cancellationToken: ct));

        // 2) Claim a batch (pending / failed_retryable / stale processing)
        var claimSql = @"
WITH pick AS (
    SELECT st.client_id, st.article_id
    FROM app.llm_summary_state st
    WHERE
      (
        st.status IN ('pending', 'failed_retryable')
        OR (st.status = 'processing' AND st.claimed_at < now() - make_interval(mins => @staleMins))
      )
      AND st.attempts < @maxAttempts
    ORDER BY st.updated_at ASC
    LIMIT @batchSize
    FOR UPDATE SKIP LOCKED
)
UPDATE app.llm_summary_state st
SET status = 'processing',
    attempts = st.attempts + 1,
    claimed_at = now(),
    claimed_by = @workerId,
    last_error = NULL
FROM pick
WHERE st.client_id = pick.client_id
  AND st.article_id = pick.article_id
RETURNING st.client_id AS ClientId, st.article_id AS ArticleId;
";

        var claimed = (await conn.QueryAsync<(Guid ClientId, long ArticleId)>(
            new CommandDefinition(claimSql, new
            {
                workerId,
                batchSize = opts.BatchSize,
                staleMins = opts.ProcessingStaleMinutes,
                maxAttempts = opts.MaxAttempts
            }, tx, cancellationToken: ct))).AsList();

        if (claimed.Count == 0)
        {
            await tx.CommitAsync(ct);
            return Array.Empty<WorkItem>();
        }

        // 3) Load details for those claimed
        // Note: we intentionally use the ANY(...) approach here.
        // It can return a superset if clientIds/articleIds overlap, but we constrain by:
        // st.status='processing' AND st.claimed_by=@workerId (so it returns only what THIS worker claimed).
        var loadSql = @"
SELECT
  st.client_id       AS ClientId,
  st.article_id      AS ArticleId,
  cl.name            AS ClientName,
  cl.website         AS ClientWebsite,
  a.url              AS Url,
  a.title            AS Title,
  a.source           AS Source,
  a.published_at     AS PublishedAtUtc,
  ac.extracted_text  AS ExtractedText
FROM app.llm_summary_state st
JOIN app.clients cl ON cl.client_id = st.client_id
JOIN app.articles a ON a.article_id = st.article_id
JOIN app.article_contents ac ON ac.article_id = st.article_id
WHERE st.client_id = ANY(@clientIds)
  AND st.article_id = ANY(@articleIds)
  AND st.status = 'processing'
  AND st.claimed_by = @workerId;
";

        var clientIds = claimed.Select(x => x.ClientId).Distinct().ToArray();
        var articleIds = claimed.Select(x => x.ArticleId).Distinct().ToArray();

        var items = (await conn.QueryAsync<WorkItem>(
            new CommandDefinition(loadSql, new
            {
                workerId,
                clientIds,
                articleIds
            }, tx, cancellationToken: ct))).AsList();

        await tx.CommitAsync(ct);
        return items;
    }

    public async Task MarkSucceededAsync(
        Guid clientId,
        long articleId,
        string model,
        string promptVersion,
        int? inputTokens,
        int? outputTokens,
        string summary,
        int relevanceScore,
        string whyItMatters,
        string conversationAngle,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Upsert into llm_summaries (final result table)
        var upsertSql = @"
INSERT INTO app.llm_summaries
  (client_id, article_id, summary, why_it_matters, conversation_angle, model, created_at, relevance_score, prompt_version, input_tokens, output_tokens)
VALUES
  (@clientId, @articleId, @summary, @why, @angle, @model, now(), @score, @promptVersion, @inputTokens, @outputTokens)
ON CONFLICT (client_id, article_id) DO UPDATE SET
  summary = EXCLUDED.summary,
  why_it_matters = EXCLUDED.why_it_matters,
  conversation_angle = EXCLUDED.conversation_angle,
  model = EXCLUDED.model,
  relevance_score = EXCLUDED.relevance_score,
  prompt_version = EXCLUDED.prompt_version,
  input_tokens = EXCLUDED.input_tokens,
  output_tokens = EXCLUDED.output_tokens,
  created_at = now();
";
        await conn.ExecuteAsync(new CommandDefinition(upsertSql, new
        {
            clientId,
            articleId,
            summary,
            why = whyItMatters,
            angle = conversationAngle,
            model,
            score = relevanceScore,
            promptVersion,
            inputTokens,
            outputTokens
        }, tx, cancellationToken: ct));

        // Mark state succeeded
        var stateSql = @"
UPDATE app.llm_summary_state
SET status = 'succeeded',
    claimed_at = NULL,
    claimed_by = NULL,
    last_error = NULL
WHERE client_id = @clientId AND article_id = @articleId;
";
        await conn.ExecuteAsync(new CommandDefinition(stateSql, new { clientId, articleId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task MarkFailedAsync(Guid clientId, long articleId, string status, string error, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        if (status is not ("failed_retryable" or "failed_permanent"))
            status = "failed_retryable";

        if (string.IsNullOrWhiteSpace(error))
            error = "Unknown error";

        // prevent huge rows
        if (error.Length > 8000) error = error[..8000];

        var sql = @"
UPDATE app.llm_summary_state
SET status = @status,
    last_error = @error,
    claimed_at = NULL,
    claimed_by = NULL
WHERE client_id = @clientId AND article_id = @articleId;
";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { clientId, articleId, status, error }, cancellationToken: ct));
    }
}
