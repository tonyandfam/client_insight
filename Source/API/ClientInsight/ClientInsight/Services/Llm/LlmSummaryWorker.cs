using Microsoft.Extensions.Options;

namespace ClientInsightAPI.Services.Llm;

public sealed class LlmSummaryWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<LlmSummaryWorker> _log;
    private readonly IOptions<LlmOptions> _opts;

    private readonly string _workerId = $"llm-{Environment.MachineName}-{Guid.NewGuid():N}";

    public LlmSummaryWorker(IServiceProvider sp, ILogger<LlmSummaryWorker> log, IOptions<LlmOptions> opts)
    {
        _sp = sp;
        _log = log;
        _opts = opts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("LlmSummaryWorker started. WorkerId={WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _opts.Value;
            if (!opts.Enabled)
            {
                await Task.Delay(opts.PollDelayMs, stoppingToken);
                continue;
            }

            try
            {
                using var scope = _sp.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<LlmSummaryService>();
                var openai = scope.ServiceProvider.GetRequiredService<OpenAiResponsesClient>();

                var batch = await svc.ClaimBatchAsync(_workerId, opts, stoppingToken);

                if (batch.Count == 0)
                {
                    await Task.Delay(opts.PollDelayMs, stoppingToken);
                    continue;
                }

                foreach (var item in batch)
                {
                    try
                    {
                        var trimmedText = TrimForPrompt(item.ExtractedText, opts.MaxInputChars);

                        _log.LogInformation("LLM processing ClientId={ClientId} ArticleId={ArticleId} Url={Url}",
                            item.ClientId, item.ArticleId, item.Url);

                        var result = await openai.SummarizeAndScoreAsync(
                            model: opts.Model,
                            maxOutputTokens: opts.MaxOutputTokens,
                            promptVersion: opts.PromptVersion,
                            clientName: item.ClientName,
                            clientWebsite: item.ClientWebsite,
                            articleUrl: item.Url,
                            articleTitle: item.Title,
                            articleSource: item.Source,
                            publishedAtUtc: item.PublishedAtUtc,
                            extractedText: trimmedText,
                            ct: stoppingToken);

                        await svc.MarkSucceededAsync(
                            clientId: item.ClientId,
                            articleId: item.ArticleId,
                            model: opts.Model,
                            promptVersion: opts.PromptVersion,
                            inputTokens: result.InputTokens,
                            outputTokens: result.OutputTokens,
                            summary: result.Summary,
                            relevanceScore: result.RelevanceScore,
                            whyItMatters: result.WhyItMatters,
                            conversationAngle: result.ConversationAngle,
                            ct: stoppingToken);

                        _log.LogInformation("LLM succeeded ClientId={ClientId} ArticleId={ArticleId} Score={Score} TokensIn={In} TokensOut={Out}",
                            item.ClientId, item.ArticleId, result.RelevanceScore, result.InputTokens, result.OutputTokens);
                    }
                    catch (Exception exItem)
                    {
                        // Retryable by default; promote to permanent if it looks like a hard failure
                        var status = exItem is HttpRequestException hre && hre.Message.Contains("HTTP 400")
                            ? "failed_permanent"
                            : "failed_retryable";

                        using var scopeFail = _sp.CreateScope();
                        var svcFail = scopeFail.ServiceProvider.GetRequiredService<LlmSummaryService>();

                        await svcFail.MarkFailedAsync(item.ClientId, item.ArticleId, status, exItem.Message, stoppingToken);

                        _log.LogError(exItem, "LLM failed ClientId={ClientId} ArticleId={ArticleId} Status={Status}", item.ClientId, item.ArticleId, status);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LlmSummaryWorker loop crashed.");
                await Task.Delay(2500, stoppingToken);
            }
        }
    }

    private static string TrimForPrompt(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Length <= maxChars) return text;

        // Keep the lead + a small tail (headlines/lede and conclusion often useful)
        var head = (int)(maxChars * 0.85);
        var tail = maxChars - head;

        var start = text[..head];
        var end = text[^tail..];

        return start + "\n\n...[TRUNCATED]...\n\n" + end;
    }
}
