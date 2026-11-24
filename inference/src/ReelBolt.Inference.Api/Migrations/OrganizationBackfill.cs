using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Migrations;

/// <summary>
/// The raw-SQL data backfill of the <c>AddOrganizations</c> migration (plan WP A1, decision D4).
/// Kept as a named, public constant so a test can run exactly the statements the migration runs
/// against a real PostgreSQL. PostgreSQL-only (<c>gen_random_uuid()</c>, <c>UPDATE ... FROM</c>).
/// It runs once, inside the migration, and is not meant to be re-run.
/// </summary>
public static class OrganizationBackfill
{
    private static readonly string Platform = WellKnownOrganizations.PlatformId.ToString();
    private static readonly string Default = WellKnownOrganizations.DefaultId.ToString();

    /// <summary>
    /// Statements run in order. Preconditions: the organization tables exist and the
    /// <c>organization_id</c> columns on projects/assistant_threads are still nullable.
    /// </summary>
    public static readonly IReadOnlyList<string> Statements = new[]
    {
        // 1. The two well-known orgs. Default is a Team org that self-hosted users are invited into.
        $@"INSERT INTO organizations (id, name, slug, kind, created_by_user_id, compute_preference, created_at, updated_at)
VALUES
 ('{Platform}', 'Platform', '{WellKnownOrganizations.PlatformSlug}', 'Platform', NULL, 'Auto', now(), now()),
 ('{Default}', 'Default', '{WellKnownOrganizations.DefaultSlug}', 'Team', NULL, 'Auto', now(), now());",

        // 2. One Personal workspace per existing user, with the user as its Owner.
        @"INSERT INTO organizations (id, name, slug, kind, created_by_user_id, compute_preference, created_at, updated_at)
SELECT gen_random_uuid(), 'Personal', 'personal-' || replace(u.id::text, '-', ''), 'Personal', u.id, 'Auto', now(), now()
FROM application_users u;",

        @"INSERT INTO organization_memberships (organization_id, user_id, role, created_at)
SELECT o.id, o.created_by_user_id, 'Owner', now()
FROM organizations o
WHERE o.kind = 'Personal';",

        // 3. Everyone joins Default (admins Owner, the rest Member); admins also own Platform.
        $@"INSERT INTO organization_memberships (organization_id, user_id, role, created_at)
SELECT '{Default}', u.id, CASE WHEN u.is_admin THEN 'Owner' ELSE 'Member' END, now()
FROM application_users u;",

        $@"INSERT INTO organization_memberships (organization_id, user_id, role, created_at)
SELECT '{Platform}', u.id, 'Owner', now()
FROM application_users u
WHERE u.is_admin;",

        // 4. Rows move to their owner's Personal workspace. Providers are left null (platform-managed).
        @"UPDATE projects p SET organization_id = o.id
FROM organizations o
WHERE o.kind = 'Personal' AND o.created_by_user_id = p.owner_id;",

        // A custom agent with no owner (its owner was deleted: SetNull) goes to Default.
        $@"UPDATE agent_definitions a SET organization_id = COALESCE(
    (SELECT o.id FROM organizations o WHERE o.kind = 'Personal' AND o.created_by_user_id = a.owner_id),
    '{Default}')
WHERE NOT a.is_built_in;",

        @"UPDATE assistant_threads t SET organization_id = o.id
FROM organizations o
WHERE o.kind = 'Personal' AND o.created_by_user_id = t.user_id;",

        @"UPDATE application_users u SET last_active_organization_id = o.id
FROM organizations o
WHERE o.kind = 'Personal' AND o.created_by_user_id = u.id;"
    };
}
