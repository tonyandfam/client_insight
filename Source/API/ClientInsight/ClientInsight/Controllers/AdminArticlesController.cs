using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using ClientInsightAPI.Services.ArticleScan;

namespace ClientInsightAPI.Controllers;

[ApiController]
[Route("api/admin/articles")]
public sealed class AdminArticlesController : ControllerBase
{
    private readonly NpgsqlDataSource _ds;

    public AdminArticlesController(NpgsqlDataSource ds) => _ds = ds;

    /// <summary>
    /// Backfills canonical_url (and original_url if empty) for articles that have not been canonicalized yet.
    /// Run this repeatedly until updated = 0.
    /// </summary>
    [HttpPost("canonicalize")]
    public async Task<IActionResult> Canonicalize([FromQuery] int batchSize = 500, CancellationToken ct = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 5000);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Grab a batch that still needs canonical_url
        var rows = (await conn.QueryAsync<Row>(@"
            SELECT article_id AS ArticleId, url AS Url
            FROM app.articles
            WHERE canonical_url IS NULL OR canonical_url = ''
            ORDER BY article_id
            LIMIT @batchSize;
        ", new { batchSize })).AsList();

        if (rows.Count == 0)
            return Ok(new { updated = 0, message = "No more rows to canonicalize." });

        // Build update parameters in memory
        var updates = new List<object>(rows.Count);
        foreach (var r in rows)
        {
            var canon = UrlCanonicalizer.Canonicalize(r.Url);
            updates.Add(new
            {
                articleId = r.ArticleId,
                canonicalUrl = canon,
                originalUrl = r.Url
            });
        }

        // Update in a single round-trip
        // - sets canonical_url
        // - sets original_url only if empty
        var updated = await conn.ExecuteAsync(@"
            UPDATE app.articles a
            SET
              canonical_url = u.canonical_url,
              original_url  = COALESCE(NULLIF(a.original_url,''), u.original_url)
            FROM (SELECT @articleId::bigint AS article_id,
                         @canonicalUrl::text AS canonical_url,
                         @originalUrl::text AS original_url) u
            WHERE a.article_id = u.article_id;
        ", updates);

        return Ok(new { updated, batchSize });
    }

    private sealed class Row
    {
        public long ArticleId { get; set; }
        public string Url { get; set; } = "";
    }
}
