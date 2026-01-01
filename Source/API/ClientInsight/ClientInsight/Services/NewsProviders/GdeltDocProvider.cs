using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ClientInsightAPI.Services.ArticleScan;

namespace ClientInsightAPI.Services.NewsProviders;

public sealed class GdeltDocProvider : INewsProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<GdeltDocProvider> _log;

    public string Name => "gdelt-doc";

    // Filter languages in CODE (avoid huge sourcelang OR clause that breaks GDELT).
    // GDELT "language" values are typically like "French", "English", etc.
    private static readonly HashSet<string> AllowedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "bulgarian",
        "croatian",
        "czech",
        "danish",
        "dutch",
        "english",
        "estonian",
        "finnish",
        "french",
        "german",
        "greek",
        "hungarian",
        "irish",
        "italian",
        "latvian",
        "lithuanian",
        "maltese",
        "polish",
        "portuguese",
        "romanian",
        "slovak",
        "slovenian",
        "spanish",
        "swedish"
    };

    public GdeltDocProvider(HttpClient http, ILogger<GdeltDocProvider> log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<IReadOnlyList<ArticleCandidate>> SearchAsync(
        ClientForScan client,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maxRecords,
        CancellationToken ct)
    {
        var q = BuildQuery(client);
        var url = BuildUrl(q, fromUtc, toUtc, maxRecords);

        var (resp, body) = await GetWithRetryAsync(url, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GDELT HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. URL={url}. BodyPreview={Preview(body)}");
        }

        // GDELT sometimes returns HTTP 200 with plain text / HTML throttling / etc.
        if (!LooksLikeJson(body))
        {
            _log.LogWarning("GDELT returned non-JSON body (HTTP 200). URL={Url}. Preview={Preview}", url, Preview(body));
            return Array.Empty<ArticleCandidate>();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("articles", out var articlesEl) ||
                articlesEl.ValueKind != JsonValueKind.Array)
            {
                // GDELT sometimes returns {} or other minimal JSON payloads with HTTP 200.
                // Treat as empty results (not warning-worthy unless you want to track frequency).
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    !doc.RootElement.EnumerateObject().Any())
                {
                    _log.LogDebug("GDELT returned empty JSON object ({{}}). URL={Url}", url);
                }
                else
                {
                    _log.LogDebug("GDELT JSON missing 'articles' array. URL={Url}. Preview={Preview}", url, Preview(body));
                }

                return Array.Empty<ArticleCandidate>();
            }


            var results = new List<ArticleCandidate>(Math.Min(maxRecords, 250));

            foreach (var a in articlesEl.EnumerateArray())
            {
                var urlStr = GetStringSafe(a, "url");
                if (string.IsNullOrWhiteSpace(urlStr)) continue;

                var title = GetStringSafe(a, "title");
                var domain = GetStringSafe(a, "domain");

                var language = GetStringSafe(a, "language");          // e.g. "French"
                var sourceCountry = GetStringSafe(a, "sourcecountry"); // e.g. "France"
                var socialImage = GetStringSafe(a, "socialimage");

                // ✅ Filter languages here (instead of in query)
                if (!IsAllowedLanguage(language))
                    continue;

                var publishedAt = GetSeenDateUtcSafe(a);

                var score = ComputeScore(client, title, domain);
                if (score <= 0.15m) continue;

                results.Add(new ArticleCandidate
                {
                    Url = urlStr!,
                    CanonicalUrl = null,
                    Title = title,
                    Source = domain,
                    PublishedAtUtc = publishedAt,

                    MatchScore = score,
                    MatchedOn = (DomainFromWebsite(client.Website) is not null && !string.IsNullOrWhiteSpace(domain))
                        ? "scored"
                        : "scored_name_only",

                    RawJson = a.GetRawText(),
                    SourceLanguage = language,
                    SourceCountry = sourceCountry,
                    SocialImageUrl = socialImage
                });
            }

            return results;
        }
        catch (JsonException jex)
        {
            _log.LogWarning(jex, "GDELT returned invalid JSON (HTTP 200). URL={Url}. Preview={Preview}", url, Preview(body));
            return Array.Empty<ArticleCandidate>();
        }
    }

    private static bool IsAllowedLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        return AllowedLanguages.Contains(language.Trim());
    }

    private static bool LooksLikeJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        var trimmed = body.TrimStart();
        if (trimmed.Length == 0) return false;

        return trimmed[0] == '{' || trimmed[0] == '[';
    }

    private static string? GetStringSafe(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var el)) return null;

        // Avoid InvalidOperationException: GetString() only works for ValueKind.String
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => null
        };
    }

    private static DateTimeOffset? GetSeenDateUtcSafe(JsonElement obj)
    {
        if (!obj.TryGetProperty("seendate", out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;

        var sd = el.GetString();
        if (string.IsNullOrWhiteSpace(sd)) return null;

        return TryParseGdeltSeenDate(sd!, out var dto) ? dto : null;
    }

    private static bool TryParseGdeltSeenDate(string s, out DateTimeOffset dto)
    {
        dto = default;

        // 1) Sometimes valid ISO
        if (DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            dto = parsed.ToUniversalTime();
            return true;
        }

        // 2) Common GDELT format: 20251203T180000Z
        var formats = new[]
        {
            "yyyyMMdd'T'HHmmss'Z'",
            "yyyyMMdd'T'HHmmss",
            "yyyyMMddHHmmss"
        };

        if (DateTimeOffset.TryParseExact(
                s,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exact))
        {
            dto = exact.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static string BuildQuery(ClientForScan client)
    {
        // ✅ IMPORTANT: no sourcelang clause anymore
        var namePhrase = Quote(client.Name);

        var domain = DomainFromWebsite(client.Website);
        return string.IsNullOrWhiteSpace(domain)
            ? namePhrase
            : $"({namePhrase} OR domainis:{domain})";
    }

    private static string BuildUrl(string query, DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxRecords)
    {
        var start = ToGdeltDateTime(fromUtc);
        var end = ToGdeltDateTime(toUtc);

        var mr = Math.Clamp(maxRecords, 1, 250);

        var qs = $"query={Uri.EscapeDataString(query)}" +
                 $"&mode=artlist&format=json" +
                 $"&startdatetime={start}&enddatetime={end}" +
                 $"&maxrecords={mr}&sort=datedesc";

        return "/api/v2/doc/doc?" + qs;
    }

    private static string Quote(string s) => $"\"{(s ?? "").Replace("\"", "")}\"";

    private static string ToGdeltDateTime(DateTimeOffset dto)
        => dto.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static string? DomainFromWebsite(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;

        var raw = website.Trim();

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

    private async Task<(HttpResponseMessage resp, string body)> GetWithRetryAsync(string url, CancellationToken ct)
    {
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

    private static decimal ComputeScore(ClientForScan client, string? title, string? domain)
    {
        var name = (client.Name ?? "").Trim();
        if (name.Length == 0) return 0;

        var score = 0m;

        if (ContainsPhrase(title, name)) score += 0.60m;

        var clientDomain = DomainFromWebsite(client.Website);
        if (!string.IsNullOrWhiteSpace(clientDomain) && !string.IsNullOrWhiteSpace(domain))
        {
            if (domain.Equals(clientDomain, StringComparison.OrdinalIgnoreCase) ||
                domain.EndsWith("." + clientDomain, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.50m;
            }
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 12) score -= 0.20m;

        var t = (title ?? "").ToLowerInvariant();
        if (t.Contains("careers") || t.Contains("jobs") || t.Contains("vacancy") || t.Contains("apply")) score -= 0.40m;
        if (t.Contains("directory") || t.Contains("listing")) score -= 0.30m;

        if (score < 0) score = 0;
        if (score > 1) score = 1;

        return score;
    }

    private static bool ContainsPhrase(string? text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
