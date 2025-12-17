using Dapper;
using Npgsql;

namespace ClientInsightAPI.Services;

public sealed class CompanyReadService
{
    private readonly NpgsqlDataSource _ds;

    public CompanyReadService(NpgsqlDataSource ds) => _ds = ds;

    public async Task<List<CompanyListItemDto>> GetCompaniesAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            SELECT
                c.company_id   AS CompanyId,
                c.nickname     AS Nickname,
                c.imported_at  AS ImportedAt,
                COALESCE(SUM(CASE WHEN cc.is_active THEN 1 ELSE 0 END), 0)::int AS ActiveClientCount,
                COALESCE(COUNT(cc.client_id), 0)::int AS TotalClientCount
            FROM app.companies c
            LEFT JOIN app.company_clients cc
                ON cc.company_id = c.company_id
            GROUP BY c.company_id, c.nickname, c.imported_at
            ORDER BY c.imported_at DESC, c.company_id;
        ";

        var rows = await conn.QueryAsync<CompanyListItemDto>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> CompanyExistsAsync(Guid companyId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            SELECT EXISTS (
                SELECT 1
                FROM app.companies
                WHERE company_id = @companyId
            );
        ";

        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { companyId }, cancellationToken: ct));
    }

    public async Task<List<CompanyClientDto>> GetCompanyClientsAsync(Guid companyId, bool activeOnly, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        var sql = @"
            SELECT
                cc.company_id    AS CompanyId,
                cl.client_id     AS ClientId,
                cl.name          AS Name,
                cl.website       AS Website,
                cl.address       AS Address,
                cc.company_rank  AS CompanyRank,
                cc.is_active     AS IsActive,
                cc.first_seen_at AS FirstSeenAt,
                cc.last_seen_at  AS LastSeenAt
            FROM app.company_clients cc
            JOIN app.clients cl
                ON cl.client_id = cc.client_id
            WHERE cc.company_id = @companyId
              AND (@activeOnly = false OR cc.is_active = true)
            ORDER BY
                cc.company_rank NULLS LAST,
                cl.name;
        ";

        var rows = await conn.QueryAsync<CompanyClientDto>(
            new CommandDefinition(sql, new { companyId, activeOnly }, cancellationToken: ct)
        );

        return rows.AsList();
    }
}

/// <summary>
/// Dapper-friendly DTOs: settable properties and DateTime for timestamptz columns.
/// Property names match SQL aliases.
/// </summary>
public sealed class CompanyListItemDto
{
    public Guid CompanyId { get; set; }
    public string? Nickname { get; set; }
    public DateTime ImportedAt { get; set; } // timestamptz -> DateTime works reliably
    public int ActiveClientCount { get; set; }
    public int TotalClientCount { get; set; }
}

public sealed class CompanyClientDto
{
    public Guid CompanyId { get; set; }
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Website { get; set; }
    public string? Address { get; set; }
    public int? CompanyRank { get; set; }
    public bool IsActive { get; set; }
    public DateTime FirstSeenAt { get; set; } // timestamptz -> DateTime
    public DateTime LastSeenAt { get; set; }  // timestamptz -> DateTime
}
