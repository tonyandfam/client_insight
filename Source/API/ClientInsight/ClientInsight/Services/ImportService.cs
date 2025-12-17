namespace ClientInsightAPI.Services
{
    using ClientInsightAPI.Controllers;
    using Dapper;
    using Npgsql;
    using System.Text.Json;

    public sealed class ImportService
    {
        private readonly NpgsqlDataSource _ds;

        public ImportService(NpgsqlDataSource ds) => _ds = ds;

        public async Task<object> ImportJsonAsync(IFormFile file, ImportMode mode, CancellationToken ct)
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var batchId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
            INSERT INTO app.import_runs(status, source_name)
            VALUES ('running', @src)
            RETURNING import_batch_id;
        ", new { src = file.FileName }, tx, cancellationToken: ct));

            int rows = 0;
            var companyIds = new HashSet<Guid>();

            await using var stream = file.OpenReadStream();
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Expecting array of objects
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var companyId = el.GetProperty("companyId").GetGuid();
                companyIds.Add(companyId);

                // store raw row_json
                await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO app.import_staging_clients(import_batch_id, company_id, row_json)
                VALUES (@batch, @company, @json::jsonb);
            ", new
                {
                    batch = batchId,
                    company = companyId,
                    json = el.GetRawText()
                }, tx, cancellationToken: ct));

                rows++;
            }

            // 1) Merge staging -> companies/clients/company_clients
            await conn.ExecuteAsync(new CommandDefinition(MergeSql, new { batchId }, tx, cancellationToken: ct));

            // 2) Replace mode: deactivate mappings not present in this batch (for companies in this import)
            if (mode == ImportMode.Replace)
            {
                await conn.ExecuteAsync(new CommandDefinition(ReplaceDeactivateSql, new { batchId }, tx, cancellationToken: ct));
            }

            await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE app.import_runs
            SET status='success', completed_at=now(), row_count=@rows
            WHERE import_batch_id=@batchId;
        ", new { rows, batchId }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);

            return new { importBatchId = batchId, rowsImported = rows, companiesImported = companyIds.Count, mode = mode.ToString() };
        }

        private const string MergeSql = @"
    -- Upsert companies
    INSERT INTO app.companies(company_id)
    SELECT DISTINCT company_id
    FROM app.import_staging_clients
    WHERE import_batch_id = @batchId
    ON CONFLICT (company_id) DO NOTHING;

    -- Upsert clients (dedupe via client_key)
    WITH src AS (
      SELECT
        s.company_id,
        (s.row_json->>'name')          AS name,
        NULLIF(s.row_json->>'website','') AS website,
        NULLIF(s.row_json->>'address','') AS address,
        NULLIF(s.row_json->>'companyRank','')::int AS company_rank,

        lower(regexp_replace(coalesce(s.row_json->>'name',''), '\s+', ' ', 'g')) AS norm_name,
        lower(regexp_replace(coalesce(s.row_json->>'address',''), '\s+', ' ', 'g')) AS norm_addr,
        lower(regexp_replace(coalesce(s.row_json->>'website',''), '\s+', '', 'g')) AS norm_web
      FROM app.import_staging_clients s
      WHERE s.import_batch_id = @batchId
    ),
    keyed AS (
      SELECT
        *,
        encode(digest(coalesce(norm_name,'') || '|' || coalesce(norm_web,'') || '|' || coalesce(norm_addr,''), 'sha256'), 'hex') AS client_key
      FROM src
    ),
    upsert_clients AS (
      INSERT INTO app.clients(client_key, name, website, address, normalized_name, updated_at)
      SELECT DISTINCT client_key, name, website, address, norm_name, now()
      FROM keyed
      ON CONFLICT (client_key)
      DO UPDATE SET
        name = EXCLUDED.name,
        website = COALESCE(EXCLUDED.website, app.clients.website),
        address = COALESCE(EXCLUDED.address, app.clients.address),
        normalized_name = COALESCE(EXCLUDED.normalized_name, app.clients.normalized_name),
        updated_at = now()
      RETURNING client_id, client_key
    )
    INSERT INTO app.company_clients(company_id, client_id, company_rank, is_active, last_seen_at)
    SELECT k.company_id, c.client_id, k.company_rank, true, now()
    FROM keyed k
    JOIN app.clients c ON c.client_key = k.client_key
    ON CONFLICT (company_id, client_id)
    DO UPDATE SET
      company_rank = EXCLUDED.company_rank,
      is_active = true,
      last_seen_at = now();
    ";

        private const string ReplaceDeactivateSql = @"
    -- Deactivate any previous mappings for companies in this import that were NOT present in this batch
    WITH imported_companies AS (
      SELECT DISTINCT company_id
      FROM app.import_staging_clients
      WHERE import_batch_id = @batchId
    ),
    imported_pairs AS (
      SELECT DISTINCT
        s.company_id,
        encode(digest(
          lower(regexp_replace(coalesce(s.row_json->>'name',''), '\s+', ' ', 'g')) || '|' ||
          lower(regexp_replace(coalesce(s.row_json->>'website',''), '\s+', '', 'g')) || '|' ||
          lower(regexp_replace(coalesce(s.row_json->>'address',''), '\s+', ' ', 'g'))
        , 'sha256'), 'hex') AS client_key
      FROM app.import_staging_clients s
      WHERE import_batch_id = @batchId
    ),
    imported_client_ids AS (
      SELECT p.company_id, c.client_id
      FROM imported_pairs p
      JOIN app.clients c ON c.client_key = p.client_key
    )
    UPDATE app.company_clients cc
    SET is_active = false
    WHERE cc.company_id IN (SELECT company_id FROM imported_companies)
      AND NOT EXISTS (
        SELECT 1
        FROM imported_client_ids ic
        WHERE ic.company_id = cc.company_id
          AND ic.client_id = cc.client_id
      );
    ";
    }

}
