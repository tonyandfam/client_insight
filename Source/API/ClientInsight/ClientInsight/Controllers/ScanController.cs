using ClientInsightAPI.Services;
using ClientInsightAPI.Services.NewsProviders;
using Microsoft.AspNetCore.Mvc;

namespace ClientInsightAPI.Controllers;

[ApiController]
[Route("api/scan")]
public sealed class ScanController : ControllerBase
{
    private readonly ScanQueue _queue;
    private readonly ScanJobService _jobs;
    private readonly INewsProvider _provider;

    public ScanController(ScanQueue queue, ScanJobService jobs, INewsProvider provider)
    {
        _queue = queue;
        _jobs = jobs;
        _provider = provider;
    }

    [HttpPost("company/{companyId:guid}")]
    public async Task<IActionResult> ScanCompany(
        Guid companyId,
        [FromQuery] int daysBack = 30,
        [FromQuery] int maxClients = 50,
        [FromQuery] bool activeOnly = true,
        CancellationToken ct = default)
    {
        var opts = new ScanOptions
        {
            DaysBack = daysBack,
            MaxClients = maxClients,
            ActiveOnly = activeOnly
        };

        var jobId = await _jobs.CreateJobAsync(companyId, opts, _provider.Name, ct);
        await _queue.EnqueueAsync(new ScanWorkItem(jobId, companyId), ct);

        return Accepted(new { jobId, companyId, status = "queued", provider = _provider.Name });
    }

    [HttpGet("jobs/{jobId:guid}")]
    public async Task<IActionResult> GetJob(Guid jobId, CancellationToken ct)
    {
        var job = await _jobs.GetJobAsync(jobId, ct);
        if (job is null) return NotFound(new { message = "Job not found", jobId });
        return Ok(job);
    }
}
