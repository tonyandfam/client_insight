using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ClientInsightAPI.Services;

public sealed class ArticleContentWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ArticleContentWorker> _log;
    private readonly IHttpClientFactory _httpFactory;

    // tune these
    private const int BatchSize = 10;
    private const int StaleMinutes = 30;
    private const int MaxAttempts = 5;

    public ArticleContentWorker(IServiceProvider sp, ILogger<ArticleContentWorker> log, IHttpClientFactory httpFactory)
    {
        _sp = sp;
        _log = log;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

        _log.LogInformation("ArticleContentWorker started. WorkerId={WorkerId}", workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ArticleContentService>();

                var batch = await svc.ClaimBatchAsync(BatchSize, StaleMinutes, workerId, stoppingToken);

                if (batch.Count == 0)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                foreach (var item in batch)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    // Too many attempts -> mark permanent and skip
                    if (item.Attempts > MaxAttempts)
                    {
                        await svc.MarkFailedAsync(item.ArticleId, "failed_permanent", null, null,
                            $"Max attempts exceeded ({MaxAttempts}).", stoppingToken);
                        continue;
                    }

                    await ProcessOneAsync(item, svc, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ArticleContentWorker loop crashed. Will continue.");
                await Task.Delay(2000, stoppingToken);
            }
        }

        _log.LogInformation("ArticleContentWorker stopped.");
    }

    private async Task ProcessOneAsync(ArticleContentService.ClaimedArticle item, ArticleContentService svc, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("article-content");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            var status = (int)resp.StatusCode;
            var contentType = resp.Content.Headers.ContentType?.MediaType;

            // Handle common retry/permanent decisions
            if ((int)resp.StatusCode is 429 or 503 or 504)
            {
                await svc.MarkFailedAsync(item.ArticleId, "failed_retryable", status, contentType,
                    $"HTTP {status} (retryable).", ct);
                return;
            }

            if ((int)resp.StatusCode is 404 or 410)
            {
                await svc.MarkFailedAsync(item.ArticleId, "failed_permanent", status, contentType,
                    $"HTTP {status} (not found).", ct);
                return;
            }

            if ((int)resp.StatusCode is 403 or 451)
            {
                // Often paywall / blocked. You can choose retryable for first few attempts if you want.
                await svc.MarkFailedAsync(item.ArticleId, "failed_permanent", status, contentType,
                    $"HTTP {status} (blocked/paywalled).", ct);
                return;
            }

            if (!resp.IsSuccessStatusCode)
            {
                await svc.MarkFailedAsync(item.ArticleId, "failed_retryable", status, contentType,
                    $"HTTP {status}.", ct);
                return;
            }

            // Only accept HTML for now
            if (contentType is null || !contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                await svc.MarkFailedAsync(item.ArticleId, "failed_permanent", status, contentType,
                    $"Unsupported content type: {contentType ?? "<null>"}", ct);
                return;
            }

            var html = await resp.Content.ReadAsStringAsync(ct);

            var extracted = HtmlTextExtractor.Extract(html);
            if (extracted.Length < 400)
            {
                await svc.MarkFailedAsync(item.ArticleId, "failed_retryable", status, contentType,
                    $"Extracted text too short ({extracted.Length} chars).", ct);
                return;
            }

            var wordCount = CountWords(extracted);
            var sha = Sha256Hex(extracted);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString();

            await svc.MarkSucceededAsync(
                articleId: item.ArticleId,
                finalUrl: finalUrl,
                httpStatus: status,
                contentType: contentType,
                extractedLanguage: null, // we can add language detection later
                wordCount: wordCount,
                sha256: sha,
                extractedText: extracted,
                rawHtml: null, // set to html if you want to store it
                ct: ct);

            _log.LogInformation("Content fetched. ArticleId={ArticleId} Words={Words} Url={Url}", item.ArticleId, wordCount, item.Url);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            await svc.MarkFailedAsync(item.ArticleId, "failed_retryable", null, null, $"Timeout: {ex.Message}", ct);
        }
        catch (HttpRequestException ex)
        {
            await svc.MarkFailedAsync(item.ArticleId, "failed_retryable", null, null, $"HTTP error: {ex.Message}", ct);
        }
        catch (Exception ex)
        {
            // store full stack for debugging
            await svc.MarkFailedAsync(item.ArticleId, "failed_retryable", null, null, ex.ToString(), ct);
        }
    }

    private static int CountWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length;
    }

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
