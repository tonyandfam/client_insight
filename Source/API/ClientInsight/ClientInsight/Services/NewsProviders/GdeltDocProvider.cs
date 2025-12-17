using System.Net.Http;
using System.Text.Json;

namespace ClientInsightAPI.Services.NewsProviders;

public sealed class GdeltDocProvider : INewsProvider
{
    private readonly HttpClient _http;

    public string Name => "gdelt-doc";

    public GdeltDocProvider(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<ArticleCandidate>> SearchAsync(
        ClientForScan client,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxRecords,
        CancellationToken ct)
    {
        // Build query:
        // - Always require quoted name (phrase)
        // - If website exists, add OR domainis:<domain>
        var q = BuildQuery(client);

        var url = BuildUrl(q, fromUtc, toUtc, maxRecords);

        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("articles", out var articlesEl) ||
            articlesEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<ArticleCandidate>();

        var results = new List<ArticleCandidate>(Math.Min(maxRecords, 250));

        foreach (var a in articlesEl.EnumerateArray())
        {
            var urlStr = a.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(urlStr)) continue;

            var title = a.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            var snippet = a.TryGetProperty("snippet", out var sEl) ? sEl.GetString() : null;

            var domain = a.TryGetProperty("domain", out var dEl) ? dEl.GetString() : null;

            // GDELT commonly uses 'seendate' as a datetime string (format varies); parse defensively
            DateTimeOffset? publishedAt = null;
            if (a.TryGetProperty("seendate", out var sdEl))
            {
                var sd = sdEl.GetString();
                if (!string.IsNullOrWhiteSpace(sd))
                {
                    if (DateTimeOffset.TryParse(sd, out var dto))
                        publishedAt = dto.ToUniversalTime();
                    else if (TryParseYmdHms(sd, out var dto2))
                        publishedAt = dto2;
                }
            }

            results.Add(new ArticleCandidate
            {
                Url = urlStr!,
                CanonicalUrl = null,
                Title = title,
                Snippet = snippet,
                Source = domain,                
                PublishedAtUtc = publishedAt,
                MatchScore = 1.0m,
                MatchedOn = DomainFromWebsite(client.Website) is null ? "name" : "name_or_domain",
                RawJson = a.GetRawText()
            });
        }

        return results;
    }

    private static string BuildQuery(ClientForScan client)
    {
        var namePhrase = Quote(client.Name);

        var domain = DomainFromWebsite(client.Website);
        if (string.IsNullOrWhiteSpace(domain))
            return namePhrase;

        return $"({namePhrase} OR domainis:{domain})";
    }

    private static string BuildUrl(string query, DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxRecords)
    {
        // DOC API endpoint
        // https://api.gdeltproject.org/api/v2/doc/doc
        // Using:
        //  mode=artlist
        //  format=json
        //  startdatetime/enddatetime (UTC) in YYYYMMDDHHMMSS
        //  maxrecords up to 250
        //  sort=datedesc
        var start = ToGdeltDateTime(fromUtc);
        var end = ToGdeltDateTime(toUtc);

        var mr = Math.Clamp(maxRecords, 1, 250);

        // NOTE: we do not set sourcelang, so it can return multilingual coverage.
        // If you want English-only later: add &query=<q>%20sourcelang:english
        var qs = $"query={Uri.EscapeDataString(query)}" +
                 $"&mode=artlist&format=json" +
                 $"&startdatetime={start}&enddatetime={end}" +
                 $"&maxrecords={mr}&sort=datedesc";

        return "/api/v2/doc/doc?" + qs;
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "")}\"";

    private static string ToGdeltDateTime(DateTimeOffset dto)
        => dto.ToUniversalTime().ToString("yyyyMMddHHmmss");

    private static string? DomainFromWebsite(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;

        var raw = website.Trim();

        // allow "www.example.com" without scheme
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) return null;

        return host.StartsWith("www.") ? host[4..] : host;
    }

    private static bool TryParseYmdHms(string s, out DateTimeOffset dto)
    {
        // Handles "yyyyMMddHHmmss" if ever encountered
        dto = default;
        if (s.Length != 14) return false;
        if (!DateTime.TryParseExact(s, "yyyyMMddHHmmss", null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return false;

        dto = new DateTimeOffset(dt, TimeSpan.Zero);
        return true;
    }
}
