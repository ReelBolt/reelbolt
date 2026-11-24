using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Marketing.Links;

/// <summary>How many of a set of accounts reached each step of the funnel.</summary>
public sealed record FunnelAccountSteps(
    long Signups, long Verified, long Onboarded, long FirstProject, long FirstRun, long FirstVideo, long Paid);

/// <summary>One tracked link's funnel in the window.</summary>
public sealed record TrackedLinkFunnelRow(
    Guid LinkId,
    string Code,
    string Channel,
    string Medium,
    string Campaign,
    string Placement,
    string DestinationPath,
    bool Archived,
    long? CostMinor,
    string? CostCurrency,
    long Clicks,
    long BotHits,
    long Visitors,
    FunnelAccountSteps Accounts);

/// <summary>
/// Every link's funnel, plus the accounts of the window that belong to no link: no link code at all, or a
/// <c>utm_content</c> that is no link's code. The link rows and <paramref name="Unattributed"/> together are
/// every account of the window, each counted exactly once.
/// </summary>
public sealed record TrackedLinkFunnel(IReadOnlyList<TrackedLinkFunnelRow> Links, FunnelAccountSteps Unattributed);

/// <summary>Reads the per-link funnel. An interface so the admin controller can be tested without PostgreSQL.</summary>
public interface ITrackedLinkFunnelReader
{
    /// <summary>
    /// The funnel of the window that starts at <paramref name="sinceDay"/> 00:00 UTC and runs to now.
    /// </summary>
    Task<TrackedLinkFunnel> ReadAsync(DateOnly sinceDay, CancellationToken ct);
}

/// <summary>
/// The per-link funnel as ONE PostgreSQL statement (<see cref="FunnelSql"/>), on the DbContext's own
/// connection. It is raw SQL on purpose: <c>infra/powerbi/acquisition-links.sql</c> and
/// <c>infra/powerbi/acquisition-person.sql</c> read the same funnel for BI, and the two must count the
/// same thing, so the step definitions below are the definitions of record for both.
/// </summary>
/// <remarks>
/// <para><b>The window.</b> <c>days = N</c> is the last N UTC calendar days, today included: the window
/// starts at 00:00 UTC, N - 1 days ago. Clicks are counted per UTC day, so a calendar window is the only
/// one in which clicks, visitors and accounts cover exactly the same time.</para>
/// <para><b>Counts that need no account.</b></para>
/// <list type="bullet">
/// <item><description><c>clicks</c>, <c>botHits</c>: the sum of <c>marketing_link_daily_stats</c> over the
/// days of the window. A consent-free total.</description></item>
/// <item><description><c>visitors</c>: <c>visitor_attribution</c> rows whose <c>utm_content</c> is the link
/// code and whose <c>first_touch_at</c> is in the window. One row per visitor id, so this is a count of
/// distinct consented visitors: a floor, never a total.</description></item>
/// </list>
/// <para><b>Account steps.</b> The cohort is every account CREATED in the window: an
/// <c>application_users</c> row that is neither an API-key service account nor a platform admin (the
/// operator's own accounts, the seeded first admin included, were not acquired; an account an admin created
/// for somebody else is a person and counts). An account belongs to the link whose code equals its
/// <c>user_attribution.utm_content</c>. Every other account of the cohort is <c>unattributed</c>: no
/// <c>user_attribution</c> row, a null <c>utm_content</c>, or a <c>utm_content</c> that is no link's code
/// (a hand-written URL, an ad platform's own value, a first touch older than the link). So the link rows
/// and the unattributed row add up to the cohort, with nobody left out and nobody counted twice. Each step
/// counts the accounts of the cohort that EVER
/// reached it (the step itself may be later than the window, and steps are independent, not a strict
/// sequence: a person can pay before finishing a video). The definitions are those of
/// <c>infra/powerbi/soft-open-funnel.sql</c> wherever that file has the step:</para>
/// <list type="bullet">
/// <item><description><c>signups</c>: the cohort itself (<c>application_users.created_at</c> in the window).</description></item>
/// <item><description><c>verified</c>: <c>application_users.email_verified_at IS NOT NULL</c>.</description></item>
/// <item><description><c>onboarded</c>: a <c>user_onboarding_profiles</c> row with <c>answered_at IS NOT NULL</c>
/// (a skip is not an answer).</description></item>
/// <item><description><c>firstProject</c>: a <c>projects</c> row with <c>owner_id</c> = the user.</description></item>
/// <item><description><c>firstRun</c>: a <c>workflow_executions</c> row with <c>initiated_by_user_id</c> = the
/// user. As in the soft-open funnel, hidden editor and graphics workflows also leave execution rows and
/// cannot be told apart: <c>workflow_definitions</c> is not readable by the BI role, and the two reads
/// must agree.</description></item>
/// <item><description><c>firstVideo</c>: a <c>Completed</c> <c>workflow_step_results</c> row of an execution
/// the user initiated whose <c>output_storage_key</c> is a video file under the project's
/// <c>outputFiles/</c> (<c>~* '^projects/[^/]+/outputFiles/.+\.(mp4|mov|webm|mkv)$'</c>).</description></item>
/// <item><description><c>paid</c>: a <c>product_events</c> row <c>alpha.first_payment</c> with
/// <c>user_id</c> = the user (the payment step writes it once per organization, for its earliest Owner).</description></item>
/// </list>
/// <para><b>Engine tables.</b> <c>workflow_executions</c> and <c>workflow_step_results</c> are owned and
/// migrated by the WorkflowEngine; this API only reads them (it maps both <c>ExcludeFromMigrations()</c>),
/// which is all this statement does. Every column read here is one <c>powerbi_ro</c> is granted, so the
/// statement runs unchanged as the BI role.</para>
/// <para><b>Completeness.</b> Clicks are complete. Visitors need cookie consent. Account steps are server
/// facts and complete, but an account carries a link code only when the first-touch cookie was consented
/// or URL capture is on (<c>Attribution:UrlCaptureEnabled</c>); the rest are in <c>unattributed</c>, with
/// the accounts whose <c>utm_content</c> matches no link.</para>
/// <para><b>How it is written.</b> Every step is a set of user ids computed once (one pass over its table)
/// and joined to the cohort; the planner hash-joins them. A step is never a per-account <c>EXISTS</c>:
/// evaluated per account, that scans <c>workflow_executions</c> (which has no index on
/// <c>initiated_by_user_id</c>) once for every account. A test EXPLAINs the statement and fails when a
/// per-row sub-select comes back.</para>
/// </remarks>
public sealed class TrackedLinkFunnelReader(InferenceApiDbContext db) : ITrackedLinkFunnelReader
{
    /// <summary>
    /// The statement. Two parameters: <c>@since</c> (timestamptz, the window's first instant) and
    /// <c>@since_day</c> (date, the same instant's UTC day). One row per link (<c>row_kind = 'link'</c>,
    /// newest link first) and exactly one last row for the accounts that belong to no link
    /// (<c>'unattributed'</c>: no code, or a code no link has). <c>marketing_links.code</c> is unique, so
    /// the join that finds an account's link never multiplies an account.
    /// </summary>
    public const string FunnelSql = """
        WITH cohort AS (
            SELECT
                u.id AS user_id,
                ua.utm_content AS code,
                (u.email_verified_at IS NOT NULL) AS verified
            FROM application_users u
            LEFT JOIN user_attribution ua ON ua.user_id = u.id
            WHERE u.created_at >= @since
              AND NOT u.is_service_account
              AND NOT u.is_admin
        ),
        answered AS (
            SELECT user_id
            FROM user_onboarding_profiles
            WHERE answered_at IS NOT NULL
        ),
        owners AS (
            SELECT DISTINCT owner_id AS user_id
            FROM projects
        ),
        runners AS (
            SELECT DISTINCT initiated_by_user_id AS user_id
            FROM workflow_executions
            WHERE initiated_by_user_id IS NOT NULL
        ),
        filmers AS (
            SELECT DISTINCT e.initiated_by_user_id AS user_id
            FROM workflow_step_results sr
            JOIN workflow_executions e ON e.id = sr.workflow_execution_id
            WHERE e.initiated_by_user_id IS NOT NULL
              AND sr.status = 'Completed'
              AND sr.output_storage_key ~* '^projects/[^/]+/outputFiles/.+\.(mp4|mov|webm|mkv)$'
        ),
        payers AS (
            SELECT DISTINCT user_id
            FROM product_events
            WHERE user_id IS NOT NULL AND event = 'alpha.first_payment'
        ),
        steps AS (
            SELECT
                c.code AS code,
                l.id AS link_id,
                c.verified AS verified,
                (an.user_id IS NOT NULL) AS onboarded,
                (ow.user_id IS NOT NULL) AS first_project,
                (ru.user_id IS NOT NULL) AS first_run,
                (fi.user_id IS NOT NULL) AS first_video,
                (pa.user_id IS NOT NULL) AS paid
            FROM cohort c
            LEFT JOIN marketing_links l ON l.code = c.code
            LEFT JOIN answered an ON an.user_id = c.user_id
            LEFT JOIN owners ow ON ow.user_id = c.user_id
            LEFT JOIN runners ru ON ru.user_id = c.user_id
            LEFT JOIN filmers fi ON fi.user_id = c.user_id
            LEFT JOIN payers pa ON pa.user_id = c.user_id
        ),
        accounts AS (
            SELECT
                link_id,
                count(*) AS signups,
                count(*) FILTER (WHERE verified) AS verified,
                count(*) FILTER (WHERE onboarded) AS onboarded,
                count(*) FILTER (WHERE first_project) AS first_project,
                count(*) FILTER (WHERE first_run) AS first_run,
                count(*) FILTER (WHERE first_video) AS first_video,
                count(*) FILTER (WHERE paid) AS paid
            FROM steps
            WHERE link_id IS NOT NULL
            GROUP BY link_id
        ),
        clicks AS (
            SELECT link_id, sum(clicks) AS clicks, sum(bot_hits) AS bot_hits
            FROM marketing_link_daily_stats
            WHERE day >= @since_day
            GROUP BY link_id
        ),
        visitors AS (
            SELECT utm_content AS code, count(*) AS visitors
            FROM visitor_attribution
            WHERE utm_content IS NOT NULL AND first_touch_at >= @since
            GROUP BY utm_content
        )
        SELECT
            'link'::text AS row_kind,
            l.created_at AS created_at,
            l.id AS link_id,
            l.code AS code,
            l.channel AS channel,
            l.medium AS medium,
            l.campaign AS campaign,
            l.placement AS placement,
            l.destination_path AS destination_path,
            (l.archived_at IS NOT NULL) AS archived,
            l.cost_minor AS cost_minor,
            l.cost_currency AS cost_currency,
            coalesce(c.clicks, 0)::bigint AS clicks,
            coalesce(c.bot_hits, 0)::bigint AS bot_hits,
            coalesce(v.visitors, 0)::bigint AS visitors,
            coalesce(a.signups, 0)::bigint AS signups,
            coalesce(a.verified, 0)::bigint AS verified,
            coalesce(a.onboarded, 0)::bigint AS onboarded,
            coalesce(a.first_project, 0)::bigint AS first_project,
            coalesce(a.first_run, 0)::bigint AS first_run,
            coalesce(a.first_video, 0)::bigint AS first_video,
            coalesce(a.paid, 0)::bigint AS paid
        FROM marketing_links l
        LEFT JOIN clicks c ON c.link_id = l.id
        LEFT JOIN visitors v ON v.code = l.code
        LEFT JOIN accounts a ON a.link_id = l.id
        UNION ALL
        SELECT
            'unattributed'::text,
            NULL::timestamptz,
            NULL::uuid,
            NULL::text,
            NULL::text,
            NULL::text,
            NULL::text,
            NULL::text,
            NULL::text,
            false,
            NULL::bigint,
            NULL::text,
            0::bigint,
            0::bigint,
            0::bigint,
            count(*)::bigint,
            (count(*) FILTER (WHERE verified))::bigint,
            (count(*) FILTER (WHERE onboarded))::bigint,
            (count(*) FILTER (WHERE first_project))::bigint,
            (count(*) FILTER (WHERE first_run))::bigint,
            (count(*) FILTER (WHERE first_video))::bigint,
            (count(*) FILTER (WHERE paid))::bigint
        FROM steps
        WHERE link_id IS NULL
        ORDER BY row_kind, created_at DESC, code
        """;

    public async Task<TrackedLinkFunnel> ReadAsync(DateOnly sinceDay, CancellationToken ct)
    {
        List<TrackedLinkFunnelRow> links = [];
        FunnelAccountSteps unattributed = new(0, 0, 0, 0, 0, 0, 0);

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = FunnelSql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            AddParameter(command, "since", new DateTime(sinceDay, TimeOnly.MinValue, DateTimeKind.Utc));
            AddParameter(command, "since_day", sinceDay);

            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                FunnelAccountSteps accounts = new(
                    reader.GetInt64(15), reader.GetInt64(16), reader.GetInt64(17), reader.GetInt64(18),
                    reader.GetInt64(19), reader.GetInt64(20), reader.GetInt64(21));
                if (reader.GetString(0) != "link")
                {
                    unattributed = accounts;
                    continue;
                }

                links.Add(new TrackedLinkFunnelRow(
                    reader.GetGuid(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetBoolean(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetInt64(12),
                    reader.GetInt64(13),
                    reader.GetInt64(14),
                    accounts));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return new TrackedLinkFunnel(links, unattributed);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
