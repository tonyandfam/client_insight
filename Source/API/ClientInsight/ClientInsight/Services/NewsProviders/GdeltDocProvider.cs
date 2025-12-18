using System.Globalization;
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

        // GDELT sometimes returns HTML/text on throttling or errors.
        // We'll read as string, validate it's JSON, and retry a few transient codes.
        var (resp, body) = await GetWithRetryAsync(url, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var preview = Preview(body);
            throw new HttpRequestException(
                $"GDELT HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. URL={url}. BodyPreview={preview}");
        }

        var trimmed = body.TrimStart();

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            var preview = Preview(body);
            throw new InvalidOperationException($"GDELT returned non-JSON response. URL={url}. Preview={preview}");
        }


        using var doc = JsonDocument.Parse(body);

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
                    // Common format is ISO-ish; try general parse first
                    if (DateTimeOffset.TryParse(sd, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                        publishedAt = dto.ToUniversalTime();
                    else if (TryParseYmdHms(sd, out var dto2))
                        publishedAt = dto2;
                }
            }

            var score = ComputeScore(client, title, snippet, domain);
            if (score <= 0.15m)
                continue;

            results.Add(new ArticleCandidate
            {
                Url = urlStr!,
                CanonicalUrl = null,
                Title = title,
                Snippet = snippet,
                Source = domain,
                PublishedAtUtc = publishedAt,
                MatchScore = score,
                MatchedOn = (DomainFromWebsite(client.Website) is not null && !string.IsNullOrWhiteSpace(domain))
        ? "scored"
        : "scored_name_only",
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

        // Example: ("William Davis" OR domainis:williamdavis.co.uk)
        return $"({namePhrase} OR domainis:{domain})";
    }

    private static string BuildUrl(string query, DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxRecords)
    {
        // DOC API endpoint:
        // https://api.gdeltproject.org/api/v2/doc/doc
        //
        // Parameters:
        //  query=<expression>
        //  mode=artlist
        //  format=json
        //  startdatetime/enddatetime (UTC) in YYYYMMDDHHMMSS
        //  maxrecords up to 250
        //  sort=datedesc
        var start = ToGdeltDateTime(fromUtc);
        var end = ToGdeltDateTime(toUtc);

        var mr = Math.Clamp(maxRecords, 1, 250);

        var qs = $"query={Uri.EscapeDataString(query)}" +
                 $"&mode=artlist&format=json" +
                 $"&startdatetime={start}&enddatetime={end}" +
                 $"&maxrecords={mr}&sort=datedesc";

        return "/api/v2/doc/doc?" + qs;
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "")}\"";

    private static string ToGdeltDateTime(DateTimeOffset dto)
        => dto.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

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

        if (!DateTime.TryParseExact(
                s,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
            return false;

        dto = new DateTimeOffset(dt, TimeSpan.Zero);
        return true;
    }

    private async Task<(HttpResponseMessage resp, string body)> GetWithRetryAsync(string url, CancellationToken ct)
    {
        // Basic exponential-ish backoff for transient throttling / gateway errors.
        // NOTE: keep conservative to avoid hammering.
        var delaysMs = new[] { 500, 1500, 4000 };

        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            string body;

            try
            {
                resp = await _http.GetAsync(url, ct);
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch when (attempt < delaysMs.Length)
            {
                await Task.Delay(delaysMs[attempt], ct);
                continue;
            }

            var code = (int)resp.StatusCode;
            var isTransient = code is 429 or 503 or 504;

            if (isTransient && attempt < delaysMs.Length)
            {
                await Task.Delay(delaysMs[attempt], ct);
                continue;
            }

            return (resp, body);
        }
    }

    private static string Preview(string body)
    {
        if (string.IsNullOrEmpty(body)) return "<empty>";
        body = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return body.Length > 300 ? body[..300] : body;
    }

    private static decimal ComputeScore(ClientForScan client, string? title, string? snippet, string? domain)
    {
        var name = (client.Name ?? "").Trim();
        if (name.Length == 0) return 0;

        var score = 0m;

        // Exact phrase in title/snippet is strong
        if (ContainsPhrase(title, name)) score += 0.60m;
        if (ContainsPhrase(snippet, name)) score += 0.30m;

        // Domain match is strong
        var clientDomain = DomainFromWebsite(client.Website);
        if (!string.IsNullOrWhiteSpace(clientDomain) && !string.IsNullOrWhiteSpace(domain))
        {
            // GDELT returns domain as host like "example.com"
            if (domain.Equals(clientDomain, StringComparison.OrdinalIgnoreCase) ||
                domain.EndsWith("." + clientDomain, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.50m;
            }
        }

        // Penalize very short / missing title
        if (string.IsNullOrWhiteSpace(title) || title!.Trim().Length < 12) score -= 0.20m;

        // Penalize job/listing noise
        var t = (title ?? "").ToLowerInvariant();
        if (t.Contains("careers") || t.Contains("jobs") || t.Contains("vacancy") || t.Contains("apply")) score -= 0.40m;
        if (t.Contains("directory") || t.Contains("listing")) score -= 0.30m;

        // Clamp 0..1
        if (score < 0) score = 0;
        if (score > 1) score = 1;

        return score;
    }

    private static bool ContainsPhrase(string? text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Case-insensitive contains of the exact phrase
        return text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0;
    }

}
