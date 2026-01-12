using System.Net;
using System.Text;
using System.Text.Json;

namespace ClientInsightAPI.Services.Llm;

public sealed class OpenAiResponsesClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiResponsesClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public OpenAiResponsesClient(HttpClient http, ILogger<OpenAiResponsesClient> log)
    {
        _http = http;
        _log = log;
    }

    public sealed record LlmResult(
        string Summary,
        int RelevanceScore,
        string WhyItMatters,
        string ConversationAngle,
        int? InputTokens,
        int? OutputTokens);

    public async Task<LlmResult> SummarizeAndScoreAsync(
        string model,
        int maxOutputTokens,
        string promptVersion,
        string clientName,
        string? clientWebsite,
        string articleUrl,
        string? articleTitle,
        string? articleSource,
        DateTimeOffset? publishedAtUtc,
        string extractedText,
        CancellationToken ct)
    {
        // System/developer instructions go in `instructions` in Responses API. :contentReference[oaicite:2]{index=2}
        var instructions = $"""
You are generating content for architects who want a news feed about their clients.

Return an ENGLISH response ONLY.
Keep it concise and factual.
Do not invent details not present in the provided article text.

You must output JSON that matches the provided schema exactly.
Use this scoring rubric for relevance_score (0-10):
0-2: weak mention, no conversation value
3-4: client is mentioned but generic/low value
5-6: meaningful update (project, funding, contract, award, expansion)
7-8: strong conversation starter (impact, timeline, leadership change, controversy, milestone)
9-10: urgent/high impact (major win/loss, crisis, legal action, acquisition, shutdown, major project launch)

Conversation angle rules (VERY IMPORTANT):
- Output MUST be 1–2 sentences, max 160 characters total.
- It MUST be phrased as a question (end with '?').
- It MUST reference at least one concrete detail from the article (e.g., project, location, date, decision, stakeholder).
- Keep it professional, curious, and non-salesy. No emojis. No exclamation marks.
- Do NOT mention “relevance score”, “LLM”, “summary”, or “this article”.
- If relevance_score <= 2, set conversation_angle to "—" (a single em dash).
""";

        var userInput = $"""
CLIENT
- Name: {clientName}
- Website: {clientWebsite ?? "(unknown)"}

ARTICLE
- URL: {articleUrl}
- Title: {articleTitle ?? "(unknown)"}
- Source: {articleSource ?? "(unknown)"}
- PublishedAtUtc: {(publishedAtUtc?.ToString("u") ?? "(unknown)")}

ARTICLE_TEXT
{extractedText}
""";

        var schema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                summary = new { type = "string", description = "Concise English summary of the article (max ~6 bullets or a short paragraph)." },
                relevance_score = new { type = "integer", minimum = 0, maximum = 10 },
                why_it_matters = new { type = "string", description = "Why this matters to the architect as a conversation/business opportunity." },
                conversation_angle = new { type = "string", description = "A suggested angle/question the architect can use to start a conversation." }
            },
            required = new[] { "summary", "relevance_score", "why_it_matters", "conversation_angle" }
        };

        var payload = new
        {
            model,
            instructions,
            input = userInput,
            temperature = 0,
            max_output_tokens = maxOutputTokens,
            store = false, // saves cost/privacy headaches during dev; also avoids stored responses :contentReference[oaicite:3]{index=3}
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    strict = true,
                    name = "article_summary",
                    schema
                }
            },
            metadata = new
            {
                prompt_version = promptVersion
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);

        // Basic retry for transient errors (429/5xx)
        var delaysMs = new[] { 400, 1200, 3000 };

        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "responses")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                return ParseResponse(body);
            }

            var code = (int)resp.StatusCode;
            var isTransient = code is 429 or 500 or 502 or 503 or 504;

            if (isTransient && attempt < delaysMs.Length)
            {
                _log.LogWarning("OpenAI transient error {StatusCode}. Retrying... Attempt={Attempt}. BodyPreview={Preview}",
                    code, attempt + 1, Preview(body));
                await Task.Delay(delaysMs[attempt], ct);
                continue;
            }

            // Non-transient or out of retries
            throw new HttpRequestException($"OpenAI error HTTP {code} {resp.ReasonPhrase}. BodyPreview={Preview(body)}");
        }
    }

    private static LlmResult ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var sEl) ? sEl.GetString() : null;
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"OpenAI response status not completed: {status ?? "(null)"}");

        // Extract usage (input/output tokens) if present. :contentReference[oaicite:4]{index=4}
        int? inputTokens = null;
        int? outputTokens = null;
        if (root.TryGetProperty("usage", out var uEl) && uEl.ValueKind == JsonValueKind.Object)
        {
            if (uEl.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) inputTokens = itv;
            if (uEl.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) outputTokens = otv;
        }

        // Find assistant message output_text and parse as JSON
        if (!root.TryGetProperty("output", out var outEl) || outEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenAI response missing output[]");

        string? assistantText = null;

        foreach (var item in outEl.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
            if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)) continue;

            var role = item.TryGetProperty("role", out var rEl) ? rEl.GetString() : null;
            if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;

            if (!item.TryGetProperty("content", out var cEl) || cEl.ValueKind != JsonValueKind.Array) continue;

            foreach (var c in cEl.EnumerateArray())
            {
                var cType = c.TryGetProperty("type", out var ctEl) ? ctEl.GetString() : null;
                if (string.Equals(cType, "output_text", StringComparison.OrdinalIgnoreCase) &&
                    c.TryGetProperty("text", out var txtEl))
                {
                    assistantText = txtEl.GetString();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(assistantText))
            throw new InvalidOperationException("OpenAI response missing assistant output_text");

        // assistantText should be JSON matching schema
        using var parsed = JsonDocument.Parse(assistantText);
        var pr = parsed.RootElement;

        var summary = pr.GetProperty("summary").GetString() ?? "";
        var score = pr.GetProperty("relevance_score").GetInt32();
        var why = pr.GetProperty("why_it_matters").GetString() ?? "";
        var angle = pr.GetProperty("conversation_angle").GetString() ?? "";

        return new LlmResult(summary, score, why, angle, inputTokens, outputTokens);
    }

    private static string Preview(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "<empty>";
        var s = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 280 ? s[..280] : s;
    }
}
