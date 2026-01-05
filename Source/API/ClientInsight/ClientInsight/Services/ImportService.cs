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

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Expected JSON array at root.");

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var companyId = el.GetProperty("companyId").GetGuid();
                companyIds.Add(companyId);

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

            // Merge staging -> normalized + assign nicknames
            await conn.ExecuteAsync(new CommandDefinition(MergeSql, new { batchId }, tx, cancellationToken: ct));

            // Replace mode: deactivate mappings not present in this batch (for companies in this import)
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

            return new
            {
                importBatchId = batchId,
                rowsImported = rows,
                companiesImported = companyIds.Count,
                mode = mode.ToString()
            };
        }

        private const string MergeSql = @"
            -- Upsert companies (tenants)
            INSERT INTO app.companies(company_id)
            SELECT DISTINCT company_id
            FROM app.import_staging_clients
            WHERE import_batch_id = @batchId
            ON CONFLICT (company_id) DO NOTHING;

            -- Assign color nicknames to any companies in THIS batch that don't have one yet
            WITH colors AS (
              SELECT ARRAY[
                'Red','Green','Blue','Yellow','Orange','Purple','Cyan','Magenta','Lime','Teal',
                'Indigo','Violet','Pink','Brown','Gray','Black','White','Maroon','Navy','Olive',
                'Gold','Silver','Coral','Turquoise','Lavender','Mint','Peach','Crimson','Sapphire','Emerald'
              ]::text[] AS arr
            ),
            existing AS (
              SELECT COUNT(*)::int AS n
              FROM app.companies
              WHERE nickname IS NOT NULL
            ),
            newcos AS (
              SELECT DISTINCT c.company_id, c.imported_at
              FROM app.companies c
              JOIN app.import_staging_clients s ON s.company_id = c.company_id
              WHERE s.import_batch_id = @batchId
                AND c.nickname IS NULL
            ),
            todo AS (
              SELECT
                company_id,
                (ROW_NUMBER() OVER (ORDER BY imported_at, company_id) + (SELECT n FROM existing)) AS rn
              FROM newcos
            )
            UPDATE app.companies c
            SET nickname = (SELECT arr[((todo.rn - 1) % 30) + 1] FROM colors)
            FROM todo
            WHERE c.company_id = todo.company_id;

            -- Upsert clients (DEDUPED to 1 row per client_key using DISTINCT ON)
            -- NOTE: now also carries companyRank into app.clients.company_rank
            WITH src AS (
              SELECT
                (s.row_json->>'name')                AS name,
                NULLIF(s.row_json->>'website','')    AS website,
                NULLIF(s.row_json->>'address','')    AS address,
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
            one_per_client AS (
              SELECT DISTINCT ON (client_key)
                client_key,
                name,
                website,
                address,
                norm_name,
                company_rank
              FROM keyed
              -- pick the ""best"" row when duplicates exist:
              ORDER BY client_key,
                       (company_rank IS NOT NULL) DESC,
                       company_rank ASC,
                       (website IS NOT NULL) DESC,
                       (address IS NOT NULL) DESC,
                       length(coalesce(name,'')) DESC
            )
            INSERT INTO app.clients(client_key, name, website, address, normalized_name, company_rank, updated_at)
            SELECT client_key, name, website, address, norm_name, company_rank, now()
            FROM one_per_client
            ON CONFLICT (client_key)
            DO UPDATE SET
              name = EXCLUDED.name,
              website = COALESCE(EXCLUDED.website, app.clients.website),
              address = COALESCE(EXCLUDED.address, app.clients.address),
              normalized_name = COALESCE(EXCLUDED.normalized_name, app.clients.normalized_name),
              -- keep the lowest non-null rank we've ever seen if null
              company_rank =
                CASE
                  WHEN EXCLUDED.company_rank IS NULL THEN app.clients.company_rank
                  WHEN app.clients.company_rank IS NULL THEN EXCLUDED.company_rank
                  ELSE LEAST(app.clients.company_rank, EXCLUDED.company_rank)
                END,
              updated_at = now();

            -- Upsert company_clients mapping (DEDUPED to 1 row per company_id+client_key)
            WITH src AS (
              SELECT
                s.company_id,
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
            agg AS (
              SELECT
                company_id,
                client_key,
                MIN(company_rank) AS company_rank
              FROM keyed
              GROUP BY company_id, client_key
            ),
            ids AS (
              SELECT a.company_id, a.company_rank, c.client_id
              FROM agg a
              JOIN app.clients c ON c.client_key = a.client_key
            )
            INSERT INTO app.company_clients(company_id, client_id, company_rank, is_active, last_seen_at)
            SELECT DISTINCT company_id, client_id, company_rank, true, now()
            FROM ids
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
