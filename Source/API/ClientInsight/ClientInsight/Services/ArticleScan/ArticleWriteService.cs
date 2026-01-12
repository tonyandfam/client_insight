using Dapper;
using Npgsql;
using ClientInsightAPI.Services.NewsProviders;

namespace ClientInsightAPI.Services.ArticleScan;

public sealed class ArticleWriteService
{
    private readonly NpgsqlDataSource _ds;
    public ArticleWriteService(NpgsqlDataSource ds) => _ds = ds;

    public async Task<long> UpsertArticleAsync(NpgsqlConnection conn, ArticleCandidate a, CancellationToken ct)
    {
        // Canonicalize URL before storage/upsert to avoid duplicates from tracking params
        var canonicalUrl = UrlCanonicalizer.Canonicalize(a.Url);

        var sql = @"
INSERT INTO app.articles(
    url,
    canonical_url,
    title,
    source,
    published_at,
    raw_json,
    retrieved_at,
    source_language,
    source_country,
    social_image_url
)
VALUES (
    @url,
    @canonical_url,
    @title,
    @source,
    @published_at,
    @raw_json::jsonb,
    now(),
    @source_language,
    @source_country,
    @social_image_url
)
ON CONFLICT (url) DO UPDATE SET
  canonical_url     = COALESCE(EXCLUDED.canonical_url, app.articles.canonical_url),
  title             = COALESCE(EXCLUDED.title, app.articles.title),
  source            = COALESCE(EXCLUDED.source, app.articles.source),
  published_at      = COALESCE(EXCLUDED.published_at, app.articles.published_at),
  raw_json          = COALESCE(EXCLUDED.raw_json, app.articles.raw_json),
  source_language   = COALESCE(EXCLUDED.source_language, app.articles.source_language),
  source_country    = COALESCE(EXCLUDED.source_country, app.articles.source_country),
  social_image_url  = COALESCE(EXCLUDED.social_image_url, app.articles.social_image_url),
  retrieved_at      = now()
RETURNING article_id;
";

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, new
        {
            url = canonicalUrl,
            canonical_url = canonicalUrl,
            title = a.Title,
            source = a.Source,
            published_at = a.PublishedAtUtc?.UtcDateTime,
            raw_json = a.RawJson ?? "{}",
            source_language = a.SourceLanguage,
            source_country = a.SourceCountry,
            social_image_url = a.SocialImageUrl
        }, cancellationToken: ct));
    }

    public async Task LinkClientArticleAsync(NpgsqlConnection conn, Guid clientId, long articleId, decimal? score, string? matchedOn, CancellationToken ct)
    {
        var sql = @"
            INSERT INTO app.client_articles(client_id, article_id, match_score, matched_on)
            VALUES (@clientId, @articleId, @score, @matchedOn)
            ON CONFLICT (client_id, article_id) DO UPDATE SET
              match_score = GREATEST(COALESCE(app.client_articles.match_score, 0), COALESCE(EXCLUDED.match_score, 0)),
              matched_on  = COALESCE(EXCLUDED.matched_on, app.client_articles.matched_on);
        ";

        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            clientId,
            articleId,
            score,
            matchedOn
        }, cancellationToken: ct));
    }
}
