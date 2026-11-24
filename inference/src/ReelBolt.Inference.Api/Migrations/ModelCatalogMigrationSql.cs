namespace ReelBolt.Inference.Api.Migrations;

/// <summary>
/// The raw-SQL data movement of the <c>AddModelCatalog</c> migration (docs/design/model-catalog.md §4.1).
/// Kept as named, public constants so a test can read exactly the statements the migration runs against a
/// real PostgreSQL (precedent: <see cref="OrganizationBackfill"/>). PostgreSQL-only (<c>md5()::uuid</c>,
/// <c>UPDATE ... FROM</c>, window functions). Each list runs once, inside its migration direction.
/// </summary>
public static class ModelCatalogMigrationSql
{
    /// <summary>
    /// The deterministic model id of a migrated provider row: <c>md5('model:' || id)::uuid</c>. The same
    /// expression in C#-readable form, for tests and diagnostics.
    /// </summary>
    public const string ModelIdExpression = "md5('model:' || {0}::text)::uuid";

    /// <summary>
    /// Up, between "tables created" and "old columns dropped": every <c>inference_providers</c> row becomes
    /// one model (deterministic id) and one model capability that KEEPS the row's id, so every reference
    /// (FK columns, step-config JSON, usage events, fingerprints) still names the same thing. 1:1, lossless.
    /// </summary>
    public static readonly IReadOnlyList<string> UpSplit = new[]
    {
        @"INSERT INTO inference_models (id, provider_id, organization_id, model_code, display_name, is_enabled, created_at, updated_at)
SELECT md5('model:' || p.id::text)::uuid, p.id, p.organization_id, p.model_name, p.name, true, p.created_at, p.updated_at
FROM inference_providers p;",

        @"INSERT INTO model_capabilities (id, model_id, organization_id, capability, is_default, is_enabled, price_source,
                                last_test_at, last_test_ok, last_test_error, created_at, updated_at)
SELECT p.id, md5('model:' || p.id::text)::uuid, p.organization_id, p.capability, p.is_default, p.is_enabled,
       CASE p.kind WHEN 'Higgsfield' THEN 'Oracle' ELSE 'Table' END,
       p.last_test_at, p.last_test_ok, p.last_test_error, p.created_at, p.updated_at
FROM inference_providers p;"
    };

    /// <summary>
    /// Up, with the new tables: <c>usage_events.price_id</c> and <c>variant</c>, only if missing, because Down keeps
    /// them (design §4.3: usage recorded while the catalog ran keeps its price id; the previous code ignores both).
    /// </summary>
    public const string UpUsageEventColumns =
        @"ALTER TABLE usage_events ADD COLUMN IF NOT EXISTS price_id uuid NULL;
ALTER TABLE usage_events ADD COLUMN IF NOT EXISTS variant character varying(64) NULL;";

    /// <summary>
    /// Down, first: refuses (and so rolls the whole Down back) when any model's organization differs from its provider
    /// account's, or any capability's from its model's. No admin path writes such a row; restoring one would copy one
    /// scope's account (endpoint and key) into another scope's provider row, so the rollback names it instead.
    /// </summary>
    public const string DownOrganizationGuard =
        @"DO $$
DECLARE
    mismatched bigint;
    first_id uuid;
BEGIN
    SELECT count(*), min(x.id::text)::uuid INTO mismatched, first_id
    FROM (
        SELECT m.id FROM inference_models m JOIN inference_providers p ON p.id = m.provider_id
        WHERE m.organization_id IS DISTINCT FROM p.organization_id
        UNION ALL
        SELECT c.id FROM model_capabilities c JOIN inference_models m ON m.id = c.model_id
        WHERE c.organization_id IS DISTINCT FROM m.organization_id
    ) x;
    IF mismatched > 0 THEN
        RAISE EXCEPTION 'AddModelCatalog Down refused: % model(s) or capabilit(y/ies) carry an organization different from their parent''s (first id %). Move or delete them so every model has its provider account''s organization and every capability its model''s, then roll back again.', mismatched, first_id;
    END IF;
END $$;";

    /// <summary>Up, last: the one pricing settings row (markup 1.2, credit value $0.006).</summary>
    public const string UpPricingSettings =
        @"INSERT INTO pricing_settings (id, markup, credit_usd_value, catalog_upgraded_at, updated_at)
VALUES (1, 1.2, 0.006, NULL, now());";

    /// <summary>
    /// Down, after the FKs to <c>model_capabilities</c> are dropped and the old columns are re-added (with
    /// their defaults) but BEFORE the old default index and the FKs to <c>inference_providers</c> come back.
    /// Every model capability becomes (again) one provider row with the capability's id: rows that kept their
    /// id are restored in place, the others get a provider row copied from their parent account (joined on the
    /// organization too, after <see cref="DownOrganizationGuard"/> has refused any mismatch). Providers no
    /// capability names are then deleted. Prices are lost. The system compute provider has no pre-catalog
    /// equivalent (its capability is not a value the previous code can read), so it is removed first.
    /// </summary>
    public static readonly IReadOnlyList<string> DownRestore = new[]
    {
        // 0. Refuse a catalog whose levels disagree on the organization (DownOrganizationGuard).
        DownOrganizationGuard,

        // 1. The system compute provider (cascades to its model and capability).
        @"DELETE FROM inference_providers WHERE is_system;",

        // 2. Free every name, so copies and restored names never collide with a name about to go away.
        @"UPDATE inference_providers SET name = 'tmp:' || id::text;",

        // 3. A provider row for every capability that is not already one, copied from its parent account.
        @"INSERT INTO inference_providers (id, organization_id, name, kind, endpoint, api_key_encrypted, api_key_last_four,
                                 is_enabled, is_system, personal_oauth_mode, timeout_seconds, extra_headers_json,
                                 created_at, updated_at, capability, model_name, is_default)
SELECT c.id, c.organization_id, 'tmp:' || c.id::text, p.kind, p.endpoint, p.api_key_encrypted, p.api_key_last_four,
       p.is_enabled, false, p.personal_oauth_mode, p.timeout_seconds, p.extra_headers_json,
       c.created_at, c.updated_at, c.capability, m.model_code, false
FROM model_capabilities c
JOIN inference_models m ON m.id = c.model_id AND m.organization_id IS NOT DISTINCT FROM c.organization_id
JOIN inference_providers p ON p.id = m.provider_id AND p.organization_id IS NOT DISTINCT FROM m.organization_id
WHERE NOT EXISTS (SELECT 1 FROM inference_providers x WHERE x.id = c.id);",

        // 4. Every capability's own row takes the capability's columns, the model's code and display name, and
        //    its parent account's connection fields (a no-op for a row that is its own parent). A row is enabled
        //    only if all three levels were. A model serving several capabilities names its first one (the one
        //    that kept the model's original provider id, else the oldest) plainly and the others
        //    'display name (Capability)', so names stay unique per organization.
        @"UPDATE inference_providers r
SET capability = s.capability,
    model_name = s.model_code,
    is_default = s.is_default,
    is_enabled = s.is_enabled,
    last_test_at = s.last_test_at,
    last_test_ok = s.last_test_ok,
    last_test_error = s.last_test_error,
    kind = s.kind,
    endpoint = s.endpoint,
    api_key_encrypted = s.api_key_encrypted,
    api_key_last_four = s.api_key_last_four,
    personal_oauth_mode = s.personal_oauth_mode,
    timeout_seconds = s.timeout_seconds,
    extra_headers_json = s.extra_headers_json,
    name = CASE WHEN s.rank = 1 THEN s.display_name ELSE s.display_name || ' (' || s.capability || ')' END
FROM (
    SELECT c.id, c.capability, c.is_default, c.is_enabled AND m.is_enabled AND p.is_enabled AS is_enabled,
           c.last_test_at, c.last_test_ok, c.last_test_error, m.model_code, m.display_name,
           p.kind, p.endpoint, p.api_key_encrypted, p.api_key_last_four, p.personal_oauth_mode,
           p.timeout_seconds, p.extra_headers_json,
           row_number() OVER (PARTITION BY c.model_id
                              ORDER BY (c.id = m.provider_id) DESC, c.created_at, c.id) AS rank
    FROM model_capabilities c
    JOIN inference_models m ON m.id = c.model_id AND m.organization_id IS NOT DISTINCT FROM c.organization_id
    JOIN inference_providers p ON p.id = m.provider_id AND p.organization_id IS NOT DISTINCT FROM m.organization_id
) s
WHERE r.id = s.id;",

        // 5. Accounts no capability names (a consolidation survivor's merged-away shells, emptied accounts).
        @"DELETE FROM inference_providers p WHERE NOT EXISTS (SELECT 1 FROM model_capabilities c WHERE c.id = p.id);"
    };
}
