using ClientInsightAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClientInsightAPI.Controllers;

[ApiController]
[Route("api/companies")]
public sealed class CompaniesController : ControllerBase
{
    private readonly CompanyReadService _read;

    public CompaniesController(CompanyReadService read) => _read = read;

    [HttpGet]
    public async Task<ActionResult<List<CompanyListItemDto>>> GetCompanies(CancellationToken ct)
    {
        var items = await _read.GetCompaniesAsync(ct);
        return Ok(items);
    }

    [HttpGet("{companyId:guid}/clients")]
    public async Task<ActionResult<List<CompanyClientDto>>> GetCompanyClients(
        Guid companyId,
        [FromQuery] bool activeOnly = true,
        CancellationToken ct = default)
    {
        if (!await _read.CompanyExistsAsync(companyId, ct))
            return NotFound(new { message = "Company not found", companyId });

        var items = await _read.GetCompanyClientsAsync(companyId, activeOnly, ct);
        return Ok(items);
    }
}
