using Microsoft.AspNetCore.Mvc;
using ClientInsightAPI.Services;

namespace ClientInsightAPI.Controllers;

[ApiController]
[Route("api/companies")]
public sealed class CompanyFeedController : ControllerBase
{
    private readonly CompanyFeedService _feed;

    public CompanyFeedController(CompanyFeedService feed) => _feed = feed;

    /// <summary>
    /// Returns a deduped feed for a company: one "best match" client per article.
    /// </summary>
    /// <param name="companyId">Company (tenant) id.</param>
    /// <param name="days">How far back to look (default 7).</param>
    /// <param name="limit">Max items to return (default 100, max 500).</param>
    /// <param name="activeOnly">If true, only active company_clients are considered (default true).</param>
    [HttpGet("{companyId:guid}/feed")]
    public async Task<ActionResult<IReadOnlyList<CompanyClientFeedArticleDto>>> GetCompanyFeed(
        Guid companyId,
        [FromQuery] int days = 7,
        [FromQuery] int limit = 100,
        [FromQuery] bool activeOnly = true,
        [FromQuery] decimal? minScore = null,
        CancellationToken ct = default)
    {
        var query = new CompanyFeedQuery
        {
            Days = days,
            Limit = limit,
            ActiveOnly = activeOnly
        };

        var items = await _feed.GetCompanyFeedAsync(companyId, query, ct);
        return Ok(items);
    }
}
