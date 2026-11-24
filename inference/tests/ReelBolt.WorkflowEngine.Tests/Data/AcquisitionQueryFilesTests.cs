#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Data.BiReadOnlyRole;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Data.AcquisitionQueryFiles;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// One throwaway database for <see cref="AcquisitionQueryFilesTests"/>: migrated in the compose order
/// (Inference API, then engine), given the BI role by the operator script's two grant blocks, and seeded
/// once with the people, visitors and links the three acquisition files are read against.
/// <para>
/// Rows are written with SQL and foreign keys off (<c>session_replication_role = replica</c>): the tests
/// are about what the files mean, not how the rows were written. Every NOT NULL column a row does not name
/// is filled from its data type, so a column added to one of the tables later does not break the seeding.
/// Timestamps are fixed instants, so every expected value is exact. Follows <see cref="SoftOpenFunnelQueryTests"/>.
/// </para>
/// </summary>
public sealed class AcquisitionSeededDatabase : IAsyncLifetime
{
    private string? _admin;
    private string? _name;

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The instant every seeded timestamp is an offset of.</summary>
    public static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime At(int day, int hour = 0) => T0.AddDays(day).AddHours(hour);

    // ---- people -------------------------------------------------------------------------------------
    /// <summary>Cookie-attributed through link hn-launch; the whole path, paid. Telemetry granted.</summary>
    public Guid Paid { get; } = Guid.NewGuid();

    /// <summary>URL-attributed through link yt-demo; stopped after the first upload. Telemetry declined.</summary>
    public Guid StoppedAfterUpload { get; } = Guid.NewGuid();

    /// <summary>No attribution row at all; signed up and did nothing. Never asked about telemetry.</summary>
    public Guid Unattributed { get; } = Guid.NewGuid();

    /// <summary>An attribution row from before the capture column (null), no link code; skipped the welcome questions; shared an output.</summary>
    public Guid Legacy { get; } = Guid.NewGuid();

    /// <summary>Cookie-attributed through hn-launch, never verified; a run whose video step failed; checkout, never paid. Declined, later granted.</summary>
    public Guid FailedVideo { get; } = Guid.NewGuid();

    /// <summary>A hand-written URL whose utm_content is no link's code.</summary>
    public Guid HandWritten { get; } = Guid.NewGuid();

    /// <summary>An API-key service account, given every row a person could have: it must appear nowhere.</summary>
    public Guid ServiceAccount { get; } = Guid.NewGuid();

    /// <summary>
    /// A platform admin (the operator's own account, like the seeded first admin), given every row a person
    /// could have and a consented visitor id: not acquired, so it must appear nowhere either.
    /// </summary>
    public Guid PlatformAdmin { get; } = Guid.NewGuid();

    /// <summary>
    /// Cookie-attributed through hn-launch; starts from code, so the welcome flow sent them to the repository:
    /// they CONNECTED a source two hours in and uploaded a file only later. Their first run was cancelled.
    /// </summary>
    public Guid ConnectedRepository { get; } = Guid.NewGuid();

    // ---- visitors -----------------------------------------------------------------------------------
    /// <summary>Arrived through hn-launch, saw two pages, clicked two CTAs, opened and sent the signup form; became <see cref="Paid"/>.</summary>
    public Guid VisitorWhoSignedUp { get; } = Guid.NewGuid();

    /// <summary>Arrived through hn-launch, saw one page and left: the consented visitor who never signed up.</summary>
    public Guid VisitorWhoLeft { get; } = Guid.NewGuid();

    /// <summary>Arrived through yt-demo and saw two pages (one of them twice).</summary>
    public Guid VisitorTwoPages { get; } = Guid.NewGuid();

    /// <summary>No campaign; opened the signup form directly and never sent it.</summary>
    public Guid VisitorSignupOnly { get; } = Guid.NewGuid();

    /// <summary>A first touch and no event row at all.</summary>
    public Guid VisitorNoEvents { get; } = Guid.NewGuid();

    /// <summary>Events and no first-touch row: not a row of the visitor file.</summary>
    public Guid VisitorWithoutFirstTouch { get; } = Guid.NewGuid();

    /// <summary>The browser of <see cref="PlatformAdmin"/>: a consented visitor like any other, but the account it led to is not one.</summary>
    public Guid VisitorWhoIsAdmin { get; } = Guid.NewGuid();

    /// <summary>
    /// The browser of <see cref="ConnectedRepository"/>: arrived through hn-launch, saw one page, then created the
    /// account with GitHub from the sign-in page. That page sends no signup event, so the account is the only
    /// evidence that the visitor got past the signup form.
    /// </summary>
    public Guid VisitorFromSignIn { get; } = Guid.NewGuid();

    /// <summary>
    /// Arrived through yt-demo and sent the signup form, but the event for opening the form never arrived
    /// (it was lost) and no account exists.
    /// </summary>
    public Guid VisitorSentOnly { get; } = Guid.NewGuid();

    // ---- links --------------------------------------------------------------------------------------
    public Guid LinkHn { get; } = Guid.NewGuid();
    public Guid LinkYt { get; } = Guid.NewGuid();
    public Guid LinkBotOnly { get; } = Guid.NewGuid();
    public Guid LinkArchived { get; } = Guid.NewGuid();

    /// <summary>A placement note with a comma, a quote and a line break: operator free text the CSV must carry intact.</summary>
    public const string HnPlacement = "Show HN, \"first\" comment\nsecond line";

    private sealed record Raw(string Sql);

    public async Task InitializeAsync()
    {
        _admin = PostgresTestEnvironment.BaseConnectionString();
        if (_admin is null)
        {
            return;
        }

        _name = "reelbolt_acquisition_files_" + Guid.NewGuid().ToString("N")[..12];
        await using (NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(_admin))
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {_name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = PostgresTestEnvironment.ForDatabase(_admin, _name);
        await using (InferenceApiDbContext api = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(ConnectionString).Options))
        {
            await api.GetService<IMigrator>().MigrateAsync();
        }

        await using (WorkflowEngineDbContext engine = new(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(ConnectionString).Options))
        {
            await engine.GetService<IMigrator>().MigrateAsync();
        }

        await using NpgsqlConnection c = new(ConnectionString);
        await c.OpenAsync();
        await Exec(c, PowerBiReadOnlyRoleSql.Up);
        await Exec(c, PowerBiReadOnlyRoleEngineSql.Up);
        await Exec(c, "SET session_replication_role = replica");
        await SeedAsync(c);
        await Exec(c, "RESET session_replication_role");
    }

    public async Task DisposeAsync()
    {
        if (_admin is null || _name is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(_admin);
        await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using NpgsqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Filler(string dataType) => dataType switch
    {
        "uuid" => "gen_random_uuid()",
        "character varying" or "text" or "character" => "substr(gen_random_uuid()::text, 1, 8)",
        "timestamp with time zone" or "timestamp without time zone" => "now()",
        "integer" or "bigint" or "smallint" or "numeric" or "double precision" or "real" => "0",
        "boolean" => "false",
        "jsonb" => "'{}'::jsonb",
        "json" => "'{}'::json",
        "bytea" => "'\\x'::bytea",
        "date" => "current_date",
        _ => throw new InvalidOperationException($"No seed filler for a NOT NULL column of type {dataType}; extend Filler."),
    };

    private static async Task Insert(NpgsqlConnection c, string table, params (string Column, object? Value)[] values)
    {
        Dictionary<string, (string DataType, string? Default, bool Identity)> required = new();
        await using (NpgsqlCommand cmd = new(
            "SELECT column_name, data_type, column_default, is_identity = 'YES' FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = @t AND is_nullable = 'NO'", c))
        {
            cmd.Parameters.AddWithValue("t", table);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                required[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3));
            }
        }

        List<string> columns = new();
        List<string> expressions = new();
        List<NpgsqlParameter> parameters = new();
        foreach ((string column, object? value) in values)
        {
            columns.Add(column);
            if (value is Raw raw)
            {
                expressions.Add(raw.Sql);
            }
            else
            {
                string name = "p" + parameters.Count;
                expressions.Add("@" + name);
                parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
            }
        }

        foreach ((string column, (string dataType, string? columnDefault, bool identity)) in required)
        {
            if (columns.Contains(column) || identity || columnDefault is not null)
            {
                continue;
            }

            columns.Add(column);
            expressions.Add(Filler(dataType));
        }

        await using NpgsqlCommand insert = new(
            $"INSERT INTO {table} ({string.Join(", ", columns.Select(x => '"' + x + '"'))}) VALUES ({string.Join(", ", expressions)})", c);
        insert.Parameters.AddRange(parameters.ToArray());
        await insert.ExecuteNonQueryAsync();
    }

    private async Task SeedAsync(NpgsqlConnection c)
    {
        Guid Id() => Guid.NewGuid();

        Task User(Guid id, int day, DateTime? verifiedAt, string provider, bool service = false, bool admin = false) => Insert(c, "application_users",
            ("id", id),
            ("email", id.ToString("N") + "@example.test"),
            ("created_at", At(day)),
            ("email_verified_at", verifiedAt),
            ("auth_provider", provider),
            ("is_service_account", service),
            ("is_admin", admin));

        Task Attribution(
            Guid user, string label, string? source, string? medium, string? campaign, string? content, string? capture,
            Guid? visitor = null, string? term = null, string? referrer = null, string? landing = null, int day = 0) => Insert(c, "user_attribution",
            ("user_id", user), ("source_label", label), ("utm_source", source), ("utm_medium", medium), ("utm_campaign", campaign),
            ("utm_content", content), ("utm_term", term), ("referrer_host", referrer), ("landing_path", landing),
            ("visitor_id", visitor), ("first_touch_at", At(day)), ("created_at", At(day)), ("capture", capture));

        Task Profile(Guid user, string? persona, string? material, string? suggested, DateTime? answeredAt, DateTime? skippedAt) => Insert(c, "user_onboarding_profiles",
            ("user_id", user), ("persona", persona), ("material", material), ("suggested_persona", suggested),
            ("answered_at", answeredAt), ("skipped_at", skippedAt), ("created_at", At(0)), ("updated_at", At(0)));

        Task Project(Guid owner, DateTime at) => Insert(c, "projects", ("id", Id()), ("owner_id", owner), ("created_at", at));

        Task Execution(Guid id, Guid user, DateTime? queuedAt, DateTime? startedAt = null, string status = "Passed") => Insert(c, "workflow_executions",
            ("id", id), ("initiated_by_user_id", user), ("status", status), ("queued_at", queuedAt), ("started_at", startedAt));

        Task StepResult(Guid execution, string status, string? key, DateTime? completedAt, DateTime? executedAt = null) => Insert(c, "workflow_step_results",
            ("id", Id()), ("workflow_execution_id", execution), ("status", status), ("output_storage_key", key),
            ("completed_at", completedAt), ("executed_at", executedAt ?? completedAt ?? At(0)));

        Task Event(Guid? user, string name, string source, DateTime at, Guid? visitor = null, string? detail = null) => Insert(c, "product_events",
            ("user_id", user), ("event", name), ("source", source), ("occurred_at", at), ("visitor_id", visitor), ("detail", detail));

        Task Touch(Guid visitor, string label, string? source, string? content, DateTime at, string? landing = null, string? referrer = null) => Insert(c, "visitor_attribution",
            ("visitor_id", visitor), ("source_label", label), ("utm_source", source), ("utm_medium", source is null ? null : "community"),
            ("utm_campaign", source is null ? null : "launch-2026-11"), ("utm_content", content), ("utm_term", null),
            ("referrer_host", referrer), ("landing_path", landing), ("first_touch_at", at), ("created_at", at));

        Task Link(Guid id, string code, string channel, string medium, string placement, string path, string? persona, DateTime createdAt,
            long? costMinor = null, string? currency = null, DateTime? archivedAt = null) => Insert(c, "marketing_links",
            ("id", id), ("code", code), ("channel", channel), ("medium", medium), ("campaign", "launch-2026-11"), ("placement", placement),
            ("destination_path", path), ("persona", persona), ("cost_minor", costMinor), ("cost_currency", currency),
            ("created_at", createdAt), ("created_by_user_id", null), ("archived_at", archivedAt));

        Task Stats(Guid link, int day, int clicks, int bots) => Insert(c, "marketing_link_daily_stats",
            ("link_id", link), ("day", DateOnly.FromDateTime(At(day))), ("clicks", clicks), ("bot_hits", bots));

        static string VideoKey(Guid execution) => $"projects/{Guid.NewGuid()}/outputFiles/{execution}/{Guid.NewGuid():N}/out.mp4";

        // ---- links ----
        await Link(LinkHn, "hn-launch", "hackernews", "community", HnPlacement, "/for/software-teams", "software", At(0));
        await Link(LinkYt, "yt-demo", "youtube", "video", "Demo video description", "/for/creators", "creator", At(1), costMinor: 12500, currency: "EUR");
        await Link(LinkBotOnly, "x-thread", "x", "social", "Launch thread", "/", null, At(2));
        await Link(LinkArchived, "old-newsletter", "newsletter", "email", "October issue", "/pricing", null, At(3), archivedAt: At(9));
        await Stats(LinkHn, 1, clicks: 5, bots: 1);
        await Stats(LinkHn, 2, clicks: 3, bots: 0);
        await Stats(LinkYt, 2, clicks: 2, bots: 0);
        await Stats(LinkBotOnly, 2, clicks: 0, bots: 4);
        await Stats(LinkBotOnly, 3, clicks: 0, bots: 3);

        // ---- Paid: the whole path, cookie-attributed, the visitor is known ----
        await User(Paid, 1, At(1, 1), "Password");
        await Attribution(Paid, "hackernews", "hackernews", "community", "launch-2026-11", "hn-launch", "cookie",
            visitor: VisitorWhoSignedUp, term: "video+editor", referrer: "news.ycombinator.com", landing: "/for/software-teams", day: 1);
        await Profile(Paid, "software", "code", "software", At(1, 2), null);
        await Project(Paid, At(1, 3));
        await Project(Paid, At(4));
        await Event(Paid, "activation.file_uploaded", "server", At(1, 4));
        await Event(Paid, "activation.source_connected", "server", At(1, 9)); // later than the upload: the upload is the first material
        Guid paidRun = Id(), paidSecondRun = Id();
        await Execution(paidRun, Paid, At(1, 5));
        await Execution(paidSecondRun, Paid, null, startedAt: At(3), status: "Failed"); // a later run that failed is not the FIRST run's outcome
        await StepResult(paidRun, "Completed", $"projects/{Id()}/outputFiles/{paidRun}/step-1-frame.png", At(1, 6));
        await StepResult(paidRun, "Completed", VideoKey(paidRun), At(1, 7));
        await StepResult(paidSecondRun, "Completed", VideoKey(paidSecondRun), At(3, 1));
        await Event(Paid, "activation.output_shared", "server", At(2, 2));
        await Event(Paid, "activation.output_downloaded", "server", At(2, 1));
        await Event(Paid, "billing.checkout_started", "server", At(2, 3));
        await Event(Paid, "alpha.first_payment", "server", At(2, 4));
        await Event(Paid, "telemetry.consent.granted", "consent", At(1, 1));

        // ---- StoppedAfterUpload: URL capture (no cookie, so no visitor id), OAuth ----
        await User(StoppedAfterUpload, 2, At(2), "Google");
        await Attribution(StoppedAfterUpload, "youtube", "youtube", "video", "launch-2026-11", "yt-demo", "url", landing: "/app/signup", day: 2);
        await Profile(StoppedAfterUpload, "creator", "footage", null, At(2, 1), null);
        await Project(StoppedAfterUpload, At(2, 2));
        await Event(StoppedAfterUpload, "activation.file_uploaded", "server", At(2, 3));
        await Event(StoppedAfterUpload, "telemetry.consent.declined", "consent", At(2));

        // ---- Unattributed: nothing but the account ----
        await User(Unattributed, 3, null, "Password");

        // ---- Legacy: an attribution row older than the capture column; skipped the questions; shared only ----
        await User(Legacy, 0, At(0, 1), "Password");
        await Attribution(Legacy, "google", "google", "organic", null, null, null, referrer: "www.google.com", landing: "/", day: 0);
        await Profile(Legacy, null, null, "business", null, At(0, 2));
        await Event(Legacy, "activation.output_shared", "server", At(5));

        // ---- FailedVideo: cookie, same link; a run with no finished video; checkout without payment ----
        await User(FailedVideo, 4, null, "GitHub");
        await Attribution(FailedVideo, "hackernews", "hackernews", "community", "launch-2026-11", "hn-launch", "cookie", visitor: Id(), day: 4);
        Guid failedRun = Id();
        await Execution(failedRun, FailedVideo, At(4, 1), status: "Failed");
        await StepResult(failedRun, "Completed", $"projects/{Id()}/outputFiles/{failedRun}/step-1-frame.png", At(4, 2));
        await StepResult(failedRun, "Failed", VideoKey(failedRun), At(4, 3));
        await Event(FailedVideo, "billing.checkout_started", "server", At(4, 4));
        await Event(FailedVideo, "telemetry.consent.declined", "consent", At(4));
        await Event(FailedVideo, "telemetry.consent.granted", "consent", At(4, 5));

        // ---- HandWritten: a utm_content that is no link's code ----
        await User(HandWritten, 5, At(5), "Password");
        await Attribution(HandWritten, "partner-site", "partner-site", null, null, "not-a-link", "url", day: 5);

        // ---- ConnectedRepository: starts from code, so the first material is a connected source, not an upload ----
        await User(ConnectedRepository, 6, At(6), "GitHub");
        await Attribution(ConnectedRepository, "hackernews", "hackernews", "community", "launch-2026-11", "hn-launch", "cookie", visitor: VisitorFromSignIn, day: 6);
        await Profile(ConnectedRepository, "software", "code", "software", At(6, 1), null);
        await Project(ConnectedRepository, At(6, 1));
        await Event(ConnectedRepository, "activation.source_connected", "server", At(6, 2));
        await Event(ConnectedRepository, "activation.file_uploaded", "server", At(6, 5));
        Guid cancelledRun = Id();
        await Execution(cancelledRun, ConnectedRepository, At(6, 3), status: "Cancelled");

        // ---- PlatformAdmin: every row a person could have; the operator's own account was not acquired ----
        await User(PlatformAdmin, 1, At(1), "Password", admin: true);
        await Attribution(PlatformAdmin, "hackernews", "hackernews", "community", "launch-2026-11", "hn-launch", "cookie", visitor: VisitorWhoIsAdmin, day: 1);
        await Profile(PlatformAdmin, "software", "code", null, At(1), null);
        await Project(PlatformAdmin, At(1));
        Guid adminRun = Id();
        await Execution(adminRun, PlatformAdmin, At(1));
        await StepResult(adminRun, "Completed", VideoKey(adminRun), At(1));
        foreach (string name in new[] { "activation.file_uploaded", "activation.source_connected", "activation.output_downloaded", "billing.checkout_started", "alpha.first_payment" })
            await Event(PlatformAdmin, name, "server", At(1));

        // ---- ServiceAccount: every row a person could have; it is not a person ----
        await User(ServiceAccount, 1, At(1), "Password", service: true);
        await Attribution(ServiceAccount, "hackernews", "hackernews", "community", "launch-2026-11", "hn-launch", "cookie", day: 1);
        await Profile(ServiceAccount, "software", "code", null, At(1), null);
        await Project(ServiceAccount, At(1));
        Guid serviceRun = Id();
        await Execution(serviceRun, ServiceAccount, At(1));
        await StepResult(serviceRun, "Completed", VideoKey(serviceRun), At(1));
        foreach (string name in new[] { "activation.file_uploaded", "activation.output_downloaded", "billing.checkout_started", "alpha.first_payment" })
            await Event(ServiceAccount, name, "server", At(1));

        // ---- visitors ----
        await Touch(VisitorWhoSignedUp, "hackernews", "hackernews", "hn-launch", At(1), landing: "/for/software-teams", referrer: "news.ycombinator.com");
        await Event(null, "site.visit", "client", At(1), VisitorWhoSignedUp, "hackernews");
        await Event(null, "site.page_view", "client", At(1), VisitorWhoSignedUp, "for-software-teams");
        await Event(null, "site.page_view", "client", At(1, 1), VisitorWhoSignedUp, "pricing");
        await Event(null, "site.cta_click", "client", At(1, 3), VisitorWhoSignedUp, "pricing-creator");
        await Event(null, "site.cta_click", "client", At(1, 2), VisitorWhoSignedUp, "hero");
        await Event(null, "signup.started", "client", At(1, 4), VisitorWhoSignedUp, "hackernews");
        await Event(null, "signup.submitted", "client", At(1, 5), VisitorWhoSignedUp, null);

        await Touch(VisitorWhoLeft, "hackernews", "hackernews", "hn-launch", At(2), landing: "/for/software-teams");
        await Event(null, "site.visit", "client", At(2), VisitorWhoLeft, "hackernews");
        await Event(null, "site.page_view", "client", At(2), VisitorWhoLeft, "for-software-teams");

        await Touch(VisitorTwoPages, "youtube", "youtube", "yt-demo", At(3), landing: "/for/creators");
        await Event(null, "site.page_view", "client", At(3), VisitorTwoPages, "for-creators");
        await Event(null, "site.page_view", "client", At(3, 1), VisitorTwoPages, "home");
        await Event(null, "site.page_view", "client", At(3, 2), VisitorTwoPages, "for-creators");

        await Touch(VisitorSignupOnly, "direct", null, null, At(4));
        await Event(null, "signup.started", "client", At(4), VisitorSignupOnly, "direct");

        await Touch(VisitorNoEvents, "www.google.com", null, null, At(5), referrer: "www.google.com");

        await Touch(VisitorWhoIsAdmin, "direct", null, null, At(6));
        await Event(null, "site.visit", "client", At(6), VisitorWhoIsAdmin, "direct");

        // Created the account with a provider from the sign-in page: neither signup event exists.
        await Touch(VisitorFromSignIn, "hackernews", "hackernews", "hn-launch", At(5, 20), landing: "/for/software-teams");
        await Event(null, "site.visit", "client", At(5, 20), VisitorFromSignIn, "hackernews");
        await Event(null, "site.page_view", "client", At(5, 20), VisitorFromSignIn, "for-software-teams");

        // Sent the form; the event for opening it was lost, and no account came of it.
        await Touch(VisitorSentOnly, "youtube", "youtube", "yt-demo", At(7), landing: "/for/creators");
        await Event(null, "signup.submitted", "client", At(7, 1), VisitorSentOnly, null);

        await Event(null, "site.visit", "client", At(6), VisitorWithoutFirstTouch, "direct");
        await Event(null, "site.cta_click", "client", At(6), VisitorWithoutFirstTouch, "hero");
    }
}

/// <summary>
/// What the acquisition tests share: the three file paths, the extractor's flattening, and the reading of the CSV
/// PostgreSQL writes.
/// </summary>
internal static class AcquisitionQueryFiles
{
    public const string PersonFile = "infra/powerbi/acquisition-person.sql";
    public const string VisitorFile = "infra/powerbi/acquisition-visitor.sql";
    public const string LinksFile = "infra/powerbi/acquisition-links.sql";

    /// <summary>The video-file test of <c>soft-open-funnel.sql</c> and of the admin funnel: the definition of record.</summary>
    public const string CanonicalVideoPattern = @"^projects/[^/]+/outputFiles/.+\.(mp4|mov|webm|mkv)$";

    /// <summary>The same test as the acquisition files write it: no dollar sign and no backslash.</summary>
    public const string FileVideoPattern = "^projects/[^/]+/outputFiles/.+[.](mp4|mov|webm|mkv)(?!.)";

    // ---- the extractor's view of a file -------------------------------------------------------------

    /// <summary>
    /// <c>grep -vE '^\s*--' file | tr '\n' ' ' | sed 's/  */ /g; s/^ //; s/ *$//; s/; *$//'</c>, as
    /// reelbolt-bi tools/extract.sh does before it wraps the result in <c>\copy ( ... )</c>.
    /// </summary>
    public static string Flatten(string fileText)
    {
        IEnumerable<string> kept = fileText.Split('\n').Where(line => !Regex.IsMatch(line, @"^\s*--"));
        string joined = string.Join(' ', kept);
        joined = Regex.Replace(joined, " +", " ");
        joined = Regex.Replace(joined, "^ ", string.Empty);
        joined = Regex.Replace(joined, " *$", string.Empty);
        return Regex.Replace(joined, "; *$", string.Empty);
    }

    public static string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    /// <summary>PostgreSQL CSV: a field is quoted when it needs it, a quote inside is doubled, and an unquoted empty field is NULL.</summary>
    public static List<List<string?>> ParseCsv(string text)
    {
        List<List<string?>> records = new();
        List<string?> record = new();
        StringBuilder field = new();
        bool quoted = false, wasQuoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    wasQuoted = true;
                    break;
                case ',':
                    record.Add(field.Length == 0 && !wasQuoted ? null : field.ToString());
                    field.Clear();
                    wasQuoted = false;
                    break;
                case '\n':
                    record.Add(field.Length == 0 && !wasQuoted ? null : field.ToString());
                    field.Clear();
                    wasQuoted = false;
                    records.Add(record);
                    record = new List<string?>();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        return records;
    }

    /// <summary>A timestamp as the CSV writes it in UTC (<c>2026-09-02 01:00:00+00</c>), or null.</summary>
    public static DateTime? Ts(string? value)
    {
        if (value is null) return null;
        string text = Regex.IsMatch(value, @"[+-]\d\d$") ? value + ":00" : value + "+00:00";
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime;
    }

    public static bool Flag(string? value) => value switch
    {
        "t" => true,
        "f" => false,
        _ => throw new InvalidOperationException($"Not a CSV boolean: '{value}'"),
    };

    public static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(AcquisitionQueryFilesTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>
/// The three acquisition files of <c>infra/powerbi/</c> (docs/design/new-user-funnel.md section 8): one row
/// per account, per consented visitor and per tracked link. The tests READ THE FILES, so the SQL that is
/// documented and copied into the BI repository is the SQL that is tested. Each file is flattened exactly
/// the way the BI extractor flattens it (full-line comments dropped, lines joined, a trailing semicolon
/// removed) and run as <c>COPY ( ... ) TO STDOUT WITH (FORMAT csv, HEADER true)</c> under
/// <c>SET ROLE powerbi_ro</c>: that proves in one go that the file is one statement, that it survives the
/// flattening, that every column it reads is granted to the BI role, and that its numbers are what the
/// seeded rows say.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class AcquisitionQueryFilesTests(AcquisitionSeededDatabase database) : IClassFixture<AcquisitionSeededDatabase>
{
    private static DateTime At(int day, int hour = 0) => AcquisitionSeededDatabase.At(day, hour);

    /// <summary>Runs a file as the BI role, the way the extractor does, and returns the CSV as header and rows.</summary>
    private async Task<(string[] Header, List<Dictionary<string, string?>> Rows)> ExtractAsync(string relativePath)
    {
        string sql = Flatten(ReadFile(relativePath));
        await using NpgsqlConnection bi = new(database.ConnectionString);
        await bi.OpenAsync();
        await using (NpgsqlCommand setup = new("SET ROLE powerbi_ro; SET TimeZone = 'UTC'", bi))
        {
            await setup.ExecuteNonQueryAsync();
        }

        string csv;
        using (TextReader reader = await bi.BeginTextExportAsync($"COPY ( {sql} ) TO STDOUT WITH (FORMAT csv, HEADER true)"))
        {
            csv = await reader.ReadToEndAsync();
        }

        List<List<string?>> records = ParseCsv(csv);
        string[] header = records[0].Select(h => h!).ToArray();
        List<Dictionary<string, string?>> rows = records.Skip(1)
            .Select(r => header.Select((name, i) => (name, value: r[i])).ToDictionary(x => x.name, x => x.value))
            .ToList();
        return (header, rows);
    }

    // ---- one row per account ------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_person_file_is_one_row_per_account_with_its_attribution_persona_and_the_first_time_of_each_step()
    {
        (string[] header, List<Dictionary<string, string?>> rows) = await ExtractAsync(PersonFile);

        header.Should().Equal(
            "user_id", "signup_at", "auth_provider", "source_label", "utm_source", "utm_medium", "utm_campaign", "link_code", "utm_term",
            "referrer_host", "landing_path", "capture", "persona", "suggested_persona", "material",
            "verified_at", "onboarded_at", "welcome_skipped_at", "first_project_at", "first_upload_at", "first_run_at", "first_run_outcome",
            "first_video_at", "first_download_or_share_at", "checkout_started_at", "first_payment_at", "measurability");
        header.Should().NotContain(new[] { "email", "display_name" }, "no address and no name reaches BI through this file");

        Dictionary<Guid, Dictionary<string, string?>> byUser = rows.ToDictionary(r => Guid.Parse(r["user_id"]!));
        byUser.Keys.Should().BeEquivalentTo(
            new[]
            {
                database.Paid, database.StoppedAfterUpload, database.Unattributed, database.Legacy, database.FailedVideo, database.HandWritten,
                database.ConnectedRepository,
            },
            "one row per account; an API-key service account is not a person and a platform admin was not acquired");
        byUser.Should().NotContainKey(database.PlatformAdmin, "the operator's own account is in no acquisition number");
        byUser.Should().NotContainKey(database.ServiceAccount);
        rows.Select(r => Ts(r["signup_at"])).Should().BeInAscendingOrder();

        // The paid user: cookie-attributed, every step, each at its FIRST time.
        Dictionary<string, string?> paid = byUser[database.Paid];
        (paid["auth_provider"], paid["source_label"], paid["utm_source"], paid["utm_medium"], paid["utm_campaign"], paid["link_code"], paid["utm_term"])
            .Should().Be(("Password", "hackernews", "hackernews", "community", "launch-2026-11", "hn-launch", "video+editor"));
        (paid["referrer_host"], paid["landing_path"], paid["capture"]).Should().Be(("news.ycombinator.com", "/for/software-teams", "cookie"));
        (paid["persona"], paid["suggested_persona"], paid["material"]).Should().Be(("software", "software", "code"));
        Ts(paid["signup_at"]).Should().Be(At(1));
        Ts(paid["verified_at"]).Should().Be(At(1, 1));
        Ts(paid["onboarded_at"]).Should().Be(At(1, 2));
        Ts(paid["first_project_at"]).Should().Be(At(1, 3), "the earlier of two projects");
        paid["welcome_skipped_at"].Should().BeNull("they answered; they never pressed Skip");
        Ts(paid["first_upload_at"]).Should().Be(At(1, 4), "the upload came before the connected source: the earlier of the two is the first material");
        Ts(paid["first_run_at"]).Should().Be(At(1, 5), "the earlier of two runs; a run with no queue time counts from its start");
        paid["first_run_outcome"].Should().Be("Passed", "the outcome of the FIRST run; the later run that failed is not it");
        Ts(paid["first_video_at"]).Should().Be(At(1, 7), "the frame image an hour earlier is not a video");
        Ts(paid["first_download_or_share_at"]).Should().Be(At(2, 1), "the download came before the share");
        Ts(paid["checkout_started_at"]).Should().Be(At(2, 3));
        Ts(paid["first_payment_at"]).Should().Be(At(2, 4));
        paid["measurability"].Should().Be("measurable");

        // The user who stopped after the upload: URL capture, so attributed without a cookie.
        Dictionary<string, string?> stopped = byUser[database.StoppedAfterUpload];
        (stopped["auth_provider"], stopped["source_label"], stopped["link_code"], stopped["capture"], stopped["landing_path"])
            .Should().Be(("Google", "youtube", "yt-demo", "url", "/app/signup"));
        (stopped["persona"], stopped["suggested_persona"], stopped["material"]).Should().Be(("creator", null, "footage"));
        Ts(stopped["verified_at"]).Should().Be(At(2));
        Ts(stopped["onboarded_at"]).Should().Be(At(2, 1));
        Ts(stopped["first_project_at"]).Should().Be(At(2, 2));
        Ts(stopped["first_upload_at"]).Should().Be(At(2, 3));
        foreach (string later in new[] { "first_run_at", "first_run_outcome", "first_video_at", "first_download_or_share_at", "checkout_started_at", "first_payment_at" })
            stopped[later].Should().BeNull("they stopped after the upload: {0} never happened", later);
        stopped["measurability"].Should().Be("deliberate_absence", "they declined telemetry: a stated fact, and every step above is a server fact anyway");

        // The unattributed signup: no attribution row is 'none', never a guess.
        Dictionary<string, string?> none = byUser[database.Unattributed];
        (none["source_label"], none["capture"], none["link_code"], none["utm_source"], none["referrer_host"])
            .Should().Be(("unattributed", "none", null, null, null));
        (none["persona"], none["suggested_persona"], none["material"]).Should().Be((null, null, null));
        Ts(none["signup_at"]).Should().Be(At(3));
        foreach (string step in header.Where(h => h.EndsWith("_at", StringComparison.Ordinal) && h != "signup_at"))
            none[step].Should().BeNull(step);
        none["first_run_outcome"].Should().BeNull("no run, no outcome");
        none["measurability"].Should().Be("not_measurable");

        // An attribution row older than the capture column was written by the cookie: it was the only writer.
        Dictionary<string, string?> legacy = byUser[database.Legacy];
        (legacy["source_label"], legacy["capture"], legacy["link_code"], legacy["auth_provider"]).Should().Be(("google", "cookie", null, "Password"));
        (legacy["persona"], legacy["suggested_persona"], legacy["material"]).Should().Be((null, "business", null));
        legacy["onboarded_at"].Should().BeNull("a skip is not an answer");
        Ts(legacy["welcome_skipped_at"]).Should().Be(At(0, 2), "but a skip is told apart from a welcome page nobody answered");
        (none["onboarded_at"], none["welcome_skipped_at"]).Should().Be((null, null), "never reached, or left open: neither answered nor skipped");
        Ts(legacy["first_download_or_share_at"]).Should().Be(At(5), "a share counts when there was no download");
        legacy["first_upload_at"].Should().BeNull();

        // A run whose only video step Failed is a run, not a video; checkout without payment; latest consent wins.
        Dictionary<string, string?> failed = byUser[database.FailedVideo];
        (failed["link_code"], failed["capture"], failed["verified_at"], failed["first_project_at"]).Should().Be(("hn-launch", "cookie", null, null));
        Ts(failed["first_run_at"]).Should().Be(At(4, 1), "steps are independent: a run without a project row still counts");
        failed["first_run_outcome"].Should().Be("Failed", "a first run that failed is told apart from one never started");
        failed["first_video_at"].Should().BeNull();
        Ts(failed["checkout_started_at"]).Should().Be(At(4, 4));
        failed["first_payment_at"].Should().BeNull();
        failed["measurability"].Should().Be("measurable", "declined, then granted: the latest choice wins");

        // The person who starts from code is sent to the repository: connecting it IS adding material. Reading
        // the upload event alone, they looked like somebody who ran without ever adding anything.
        Dictionary<string, string?> connected = byUser[database.ConnectedRepository];
        (connected["persona"], connected["material"], connected["link_code"]).Should().Be(("software", "code", "hn-launch"));
        Ts(connected["first_upload_at"]).Should().Be(At(6, 2), "the connected source, three hours before their first upload");
        Ts(connected["first_run_at"]).Should().Be(At(6, 3));
        connected["first_run_outcome"].Should().Be("Cancelled");
        connected["first_video_at"].Should().BeNull();
        Ts(connected["first_upload_at"]).Should().BeBefore(Ts(connected["first_run_at"])!.Value);

        // A hand-written utm_content is reported as it is, whether or not a link has that code.
        (byUser[database.HandWritten]["link_code"], byUser[database.HandWritten]["capture"]).Should().Be(("not-a-link", "url"));
    }

    // ---- one row per consented visitor --------------------------------------------------------------

    [PostgresFact]
    public async Task The_visitor_file_is_one_row_per_first_touch_with_the_furthest_step_before_signup()
    {
        (string[] header, List<Dictionary<string, string?>> rows) = await ExtractAsync(VisitorFile);

        header.Should().Equal(
            "visitor_id", "first_touch_at", "source_label", "utm_source", "utm_medium", "utm_campaign", "link_code", "utm_term",
            "referrer_host", "landing_path", "pages_viewed", "viewed_second_page", "clicked_cta", "first_cta_location",
            "reached_signup", "submitted_signup", "became_account", "user_id", "furthest_step");

        Dictionary<Guid, Dictionary<string, string?>> byVisitor = rows.ToDictionary(r => Guid.Parse(r["visitor_id"]!));
        byVisitor.Keys.Should().BeEquivalentTo(
            new[]
            {
                database.VisitorWhoSignedUp, database.VisitorWhoLeft, database.VisitorTwoPages, database.VisitorSignupOnly, database.VisitorNoEvents,
                database.VisitorWhoIsAdmin, database.VisitorFromSignIn, database.VisitorSentOnly,
            },
            "one row per visitor_attribution row; events without a first touch are not a visitor row");
        rows.Select(r => Ts(r["first_touch_at"])).Should().BeInAscendingOrder();

        (int Pages, bool Second, bool Cta, string? FirstCta, bool Reached, bool Submitted, bool Account, string? UserId, string Furthest) Steps(Guid visitor)
        {
            Dictionary<string, string?> r = byVisitor[visitor];
            return (int.Parse(r["pages_viewed"]!, CultureInfo.InvariantCulture), Flag(r["viewed_second_page"]), Flag(r["clicked_cta"]),
                r["first_cta_location"], Flag(r["reached_signup"]), Flag(r["submitted_signup"]), Flag(r["became_account"]), r["user_id"], r["furthest_step"]!);
        }

        // The consented visitor who never signed up.
        Steps(database.VisitorWhoLeft).Should().Be((1, false, false, null, false, false, false, null, "visited"));
        Dictionary<string, string?> left = byVisitor[database.VisitorWhoLeft];
        (left["source_label"], left["utm_source"], left["utm_medium"], left["utm_campaign"], left["link_code"], left["landing_path"])
            .Should().Be(("hackernews", "hackernews", "community", "launch-2026-11", "hn-launch", "/for/software-teams"));
        Ts(left["first_touch_at"]).Should().Be(At(2));

        // The visitor who became the paid account: every step, and the EARLIEST call to action.
        Steps(database.VisitorWhoSignedUp).Should().Be(
            (2, true, true, "hero", true, true, true, database.Paid.ToString(), "became_account"));
        byVisitor[database.VisitorWhoSignedUp]["referrer_host"].Should().Be("news.ycombinator.com");

        // Two distinct pages, one of them seen twice: distinct page keys, not events.
        Steps(database.VisitorTwoPages).Should().Be((2, true, false, null, false, false, false, null, "viewed_second_page"));
        byVisitor[database.VisitorTwoPages]["link_code"].Should().Be("yt-demo");

        // Opened the signup form with no campaign and never sent it.
        Steps(database.VisitorSignupOnly).Should().Be((0, false, false, null, true, false, false, null, "reached_signup"));
        (byVisitor[database.VisitorSignupOnly]["source_label"], byVisitor[database.VisitorSignupOnly]["link_code"]).Should().Be(("direct", null));

        // The operator's own browser is a consented visitor like any other, but the platform-admin account it
        // led to is not an account of the funnel: user_id must always have a row in the person file.
        Steps(database.VisitorWhoIsAdmin).Should().Be((0, false, false, null, false, false, false, null, "visited"));

        // A first touch with no events left: zeros and false, never NULL.
        Steps(database.VisitorNoEvents).Should().Be((0, false, false, null, false, false, false, null, "visited"));
        (byVisitor[database.VisitorNoEvents]["source_label"], byVisitor[database.VisitorNoEvents]["referrer_host"]).Should().Be(("www.google.com", "www.google.com"));

        // An account created with a provider from the sign-in page sent neither signup event. The account is a
        // server fact, so it counts for the two steps before it; the second page and the call to action, which
        // nothing proves, stay false.
        Steps(database.VisitorFromSignIn).Should().Be(
            (1, false, false, null, true, true, true, database.ConnectedRepository.ToString(), "became_account"));

        // The form was sent and the event for opening it never arrived: a sent form was an opened form.
        Steps(database.VisitorSentOnly).Should().Be((0, false, false, null, true, true, false, null, "submitted_signup"));
    }

    [PostgresFact]
    public async Task From_the_signup_form_on_no_visitor_step_is_reached_by_more_visitors_than_the_step_before_it()
    {
        (_, List<Dictionary<string, string?>> visitors) = await ExtractAsync(VisitorFile);
        (_, List<Dictionary<string, string?>> links) = await ExtractAsync(LinksFile);

        // Row by row: an account implies a sent form, and a sent form implies an opened one. Without the rule
        // two of the seeded visitors break it (VisitorFromSignIn has an account and no event; VisitorSentOnly
        // has the later event and not the earlier one).
        foreach (Dictionary<string, string?> row in visitors)
        {
            if (Flag(row["became_account"])) Flag(row["submitted_signup"]).Should().BeTrue("visitor {0} became an account", row["visitor_id"]);
            if (Flag(row["submitted_signup"])) Flag(row["reached_signup"]).Should().BeTrue("visitor {0} sent the form", row["visitor_id"]);
        }

        visitors.Count(r => Flag(r["became_account"])).Should().Be(2, "VisitorWhoSignedUp and VisitorFromSignIn");
        visitors.Count(r => Flag(r["submitted_signup"])).Should().Be(3, "those two and VisitorSentOnly");
        visitors.Count(r => Flag(r["reached_signup"])).Should().Be(4, "those three and VisitorSignupOnly");

        // The links file counts the same visitors with the same rule, link by link.
        foreach (Dictionary<string, string?> link in links)
        {
            string code = link["code"]!;
            List<Dictionary<string, string?>> ofLink = visitors.Where(v => v["link_code"] == code).ToList();
            long Count(string column) => long.Parse(link[column]!, CultureInfo.InvariantCulture);

            Count("visitors").Should().Be(ofLink.Count, code);
            Count("visitors_second_page").Should().Be(ofLink.Count(v => Flag(v["viewed_second_page"])), code);
            Count("visitors_cta").Should().Be(ofLink.Count(v => Flag(v["clicked_cta"])), code);
            Count("visitors_signup_started").Should().Be(ofLink.Count(v => Flag(v["reached_signup"])), code);
            Count("visitors_signup_submitted").Should().Be(ofLink.Count(v => Flag(v["submitted_signup"])), code);

            Count("visitors_signup_submitted").Should().BeGreaterThanOrEqualTo(ofLink.Count(v => Flag(v["became_account"])), "link {0}", code);
            Count("visitors_signup_started").Should().BeGreaterThanOrEqualTo(Count("visitors_signup_submitted"), "link {0}", code);
            Count("visitors").Should().BeGreaterThanOrEqualTo(Count("visitors_signup_started"), "link {0}", code);
        }
    }

    // ---- one row per tracked link -------------------------------------------------------------------

    private static readonly string[] LinkCounts =
    [
        "clicks", "bot_hits", "visitors", "visitors_second_page", "visitors_cta", "visitors_signup_started", "visitors_signup_submitted",
        "signups", "verified", "onboarded", "first_project", "first_upload", "first_run", "first_video", "first_download_or_share",
        "checkout_started", "paid",
    ];

    [PostgresFact]
    public async Task The_links_file_is_one_row_per_link_with_clicks_visitors_and_account_steps()
    {
        (string[] header, List<Dictionary<string, string?>> rows) = await ExtractAsync(LinksFile);

        header.Should().Equal(new[]
        {
            "link_id", "code", "channel", "medium", "campaign", "placement", "destination_path", "persona", "created_at", "archived",
            "cost_minor", "cost_currency",
        }.Concat(LinkCounts));
        rows.Select(r => r["code"]).Should().Equal(new[] { "old-newsletter", "x-thread", "yt-demo", "hn-launch" }, "one row per link, newest first");

        Dictionary<string, Dictionary<string, string?>> byCode = rows.ToDictionary(r => r["code"]!);
        long[] Counts(string code) => LinkCounts.Select(name => long.Parse(byCode[code][name]!, CultureInfo.InvariantCulture)).ToArray();

        // hn-launch: 8 clicks and 1 bot hit over two days; three consented visitors: one went all the way, one
        // left, and one created the account with a provider from the sign-in page (no signup event, so the
        // account is what counts them in the two signup steps);
        // three accounts (the service account and the platform admin that carry the same code are not accounts).
        Counts("hn-launch").Should().Equal(
            8, 1,          // clicks, bot_hits
            3, 1, 1, 2, 2, // visitors, second page, cta, signup started, signup submitted
            3,             // signups: Paid, FailedVideo and ConnectedRepository
            2,             // verified: Paid, ConnectedRepository
            2,             // onboarded: Paid, ConnectedRepository
            2,             // first_project: Paid, ConnectedRepository
            2,             // first_upload: Paid (an upload), ConnectedRepository (a connected source)
            3,             // first_run: all three
            1,             // first_video: Paid (FailedVideo's video step failed, ConnectedRepository's run was cancelled)
            1,             // first_download_or_share: Paid
            2,             // checkout_started: Paid, FailedVideo
            1);            // paid: Paid
        Dictionary<string, string?> hn = byCode["hn-launch"];
        Guid.Parse(hn["link_id"]!).Should().Be(database.LinkHn);
        (hn["channel"], hn["medium"], hn["campaign"], hn["destination_path"], hn["persona"], hn["archived"], hn["cost_minor"], hn["cost_currency"])
            .Should().Be(("hackernews", "community", "launch-2026-11", "/for/software-teams", "software", "f", null, null));
        hn["placement"].Should().Be(AcquisitionSeededDatabase.HnPlacement, "the operator's note survives the CSV with its comma, quotes and line break");
        Ts(hn["created_at"]).Should().Be(At(0));

        // yt-demo: one visitor who saw a second page and one whose sent form is the only event (it counts as
        // opened too); one URL-captured account that stopped after the upload.
        Counts("yt-demo").Should().Equal(2, 0, 2, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0);
        (byCode["yt-demo"]["cost_minor"], byCode["yt-demo"]["cost_currency"], byCode["yt-demo"]["persona"]).Should().Be(("12500", "EUR", "creator"));

        // The bot-only link: previews and crawlers are counted, and are never clicks.
        Counts("x-thread").Should().Equal(0, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        byCode["x-thread"]["persona"].Should().BeNull();

        // An archived link with no counter row at all still has its row, all zeros.
        Counts("old-newsletter").Should().Equal(Enumerable.Repeat(0L, LinkCounts.Length));
        byCode["old-newsletter"]["archived"].Should().Be("t");

        // The account whose utm_content is no link's code belongs to no row.
        rows.Sum(r => long.Parse(r["signups"]!, CultureInfo.InvariantCulture)).Should().Be(4);
    }

    [PostgresFact]
    public async Task No_file_evaluates_a_sub_select_per_row()
    {
        // The links file used to test each step with a per-account EXISTS. Whether that is fast is the
        // planner's choice: when it does not hash the sub-select, workflow_executions (no index on
        // initiated_by_user_id) is scanned once for every attributed account, in the statement the BI
        // extract runs on every refresh. Every step is now a set computed once and joined, so no plan of any
        // of the three files may contain a SubPlan, whatever the planner estimates.
        await using NpgsqlConnection bi = new(database.ConnectionString);
        await bi.OpenAsync();
        await using (NpgsqlCommand role = new("SET ROLE powerbi_ro", bi))
        {
            await role.ExecuteNonQueryAsync();
        }

        foreach (string file in new[] { LinksFile, PersonFile, VisitorFile })
        {
            foreach (string workMem in new[] { "4MB", "64kB" })
            {
                await using (NpgsqlCommand mem = new($"SET work_mem = '{workMem}'", bi))
                {
                    await mem.ExecuteNonQueryAsync();
                }

                List<string> plan = new();
                await using (NpgsqlCommand explain = new("EXPLAIN (COSTS OFF) " + Flatten(ReadFile(file)), bi))
                await using (NpgsqlDataReader reader = await explain.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
                }

                plan.Should().NotContain(line => line.Contains("SubPlan", StringComparison.Ordinal),
                    "{0} at work_mem {1} must join pre-computed sets, never run a sub-select per row:\n{2}", file, workMem, string.Join('\n', plan));
            }
        }

        // And the text itself: the two files that read runs do it in a FROM, never inside an EXISTS.
        foreach (string file in new[] { LinksFile, PersonFile })
        {
            string sql = Flatten(ReadFile(file));
            sql.Should().NotContain("EXISTS", "{0}: a step is a joined set, not a correlated sub-select", file);
            sql.Should().Contain("FROM workflow_executions");
        }
    }

    [PostgresFact]
    public async Task The_links_file_counts_what_the_admin_funnel_endpoint_counts()
    {
        (_, List<Dictionary<string, string?>> rows) = await ExtractAsync(LinksFile);
        Dictionary<string, Dictionary<string, string?>> file = rows.ToDictionary(r => r["code"]!);

        // The endpoint's own statement over the same rows, with a window that holds everything.
        Dictionary<string, long[]> endpoint = new();
        long[]? unattributed = null;
        await using NpgsqlConnection c = new(database.ConnectionString);
        await c.OpenAsync();
        await using (NpgsqlCommand role = new("SET ROLE powerbi_ro", c))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = new(TrackedLinkFunnelReader.FunnelSql, c))
        {
            cmd.Parameters.AddWithValue("since", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            cmd.Parameters.AddWithValue("since_day", new DateOnly(2000, 1, 1));
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                long[] counts = Enumerable.Range(12, 10).Select(reader.GetInt64).ToArray();
                if (reader.GetString(0) == "link") endpoint[reader.GetString(3)] = counts;
                else unattributed = counts;
            }
        }

        // clicks, botHits, visitors, signups, verified, onboarded, firstProject, firstRun, firstVideo, paid
        string[] shared = ["clicks", "bot_hits", "visitors", "signups", "verified", "onboarded", "first_project", "first_run", "first_video", "paid"];
        endpoint.Keys.Should().BeEquivalentTo(file.Keys);
        foreach ((string code, long[] counts) in endpoint)
        {
            shared.Select(name => long.Parse(file[code][name]!, CultureInfo.InvariantCulture)).Should().Equal(
                counts, "link {0}: the BI file and the admin endpoint must count the same thing", code);
        }

        // The endpoint's unattributed row is the person file's accounts that belong to no link: no link code,
        // or a utm_content that is no link's code. So the endpoint's rows together are every account.
        (_, List<Dictionary<string, string?>> people) = await ExtractAsync(PersonFile);
        List<Dictionary<string, string?>> noCode = people.Where(p => p["link_code"] is null || !file.ContainsKey(p["link_code"]!)).ToList();
        unattributed.Should().NotBeNull();
        unattributed![3].Should().Be(noCode.Count).And.Be(3, "Unattributed, Legacy and HandWritten (a code no link has)");
        (endpoint.Values.Sum(counts => counts[3]) + unattributed[3]).Should().Be(people.Count, "every account is in exactly one row of the endpoint");
        unattributed[4].Should().Be(noCode.Count(p => p["verified_at"] is not null));
        unattributed[5].Should().Be(noCode.Count(p => p["onboarded_at"] is not null));
        unattributed[6].Should().Be(noCode.Count(p => p["first_project_at"] is not null));
        unattributed[7].Should().Be(noCode.Count(p => p["first_run_at"] is not null));
        unattributed[8].Should().Be(noCode.Count(p => p["first_video_at"] is not null));
        unattributed[9].Should().Be(noCode.Count(p => p["first_payment_at"] is not null));

        // And the person file agrees with the links file, link by link, on every account step.
        (string Column, string Count)[] steps =
        [
            ("verified_at", "verified"), ("onboarded_at", "onboarded"), ("first_project_at", "first_project"), ("first_upload_at", "first_upload"),
            ("first_run_at", "first_run"), ("first_video_at", "first_video"), ("first_download_or_share_at", "first_download_or_share"),
            ("checkout_started_at", "checkout_started"), ("first_payment_at", "paid"),
        ];
        foreach ((string code, Dictionary<string, string?> row) in file)
        {
            List<Dictionary<string, string?>> ofLink = people.Where(p => p["link_code"] == code).ToList();
            long.Parse(row["signups"]!, CultureInfo.InvariantCulture).Should().Be(ofLink.Count, code);
            foreach ((string column, string count) in steps)
                long.Parse(row[count]!, CultureInfo.InvariantCulture).Should().Be(ofLink.Count(p => p[column] is not null), "{0} of link {1}", count, code);
        }
    }

    [PostgresFact]
    public async Task The_video_file_test_written_without_a_dollar_sign_matches_exactly_what_the_canonical_one_matches()
    {
        (string Key, bool Video)[] keys =
        [
            ("projects/7d0c/outputFiles/run/step/out.mp4", true),
            ("projects/7d0c/outputFiles/out.MOV", true),
            ("PROJECTS/7d0c/OUTPUTFILES/a/b.WebM", true),
            ("projects/7d0c/outputFiles/a.b.mkv", true),
            ("projects/7d0c/outputFiles/x.mp4/y.mov", true),
            ("projects/7d0c/outputFiles/line\nbreak.mp4", true),
            ("projects/7d0c/outputFiles/.mp4", false),
            ("projects/7d0c/outputFiles/out.mp4.txt", false),
            ("projects/7d0c/outputFiles/out.mp4 ", false),
            ("projects/7d0c/outputFiles/out.mp4\n", false),
            ("projects/7d0c/outputFiles/outmp4", false),
            ("projects/7d0c/outputFiles/out.mp3", false),
            ("projects/7d0c/outputFiles/step-1-frame.png", false),
            ("projects/7d0c/userFiles/out.mp4", false),
            ("projects/7d0c/agentFiles/generated/out.mp4", false),
            ("projects//outputFiles/out.mp4", false),
            ("projects/a/b/outputFiles/out.mp4", false),
            ("x/projects/7d0c/outputFiles/out.mp4", false),
            (string.Empty, false),
        ];

        await using NpgsqlConnection c = new(database.ConnectionString);
        await c.OpenAsync();
        foreach ((string key, bool video) in keys)
        {
            await using NpgsqlCommand cmd = new("SELECT @k ~* @canonical, @k ~* @ours", c);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("canonical", CanonicalVideoPattern);
            cmd.Parameters.AddWithValue("ours", FileVideoPattern);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetBoolean(0).Should().Be(video, "the canonical pattern on '{0}'", key);
            reader.GetBoolean(1).Should().Be(video, "the acquisition files' pattern on '{0}'", key);
        }
    }
}

/// <summary>
/// What the three acquisition files ARE, checked without a database: each survives the extractor's flattening
/// into one <c>psql \copy</c> line, reads granted tables only, and names no withheld column.
/// </summary>
public class AcquisitionQueryFilesShapeTests
{
    public static TheoryData<string> Files => new() { PersonFile, VisitorFile, LinksFile };

    [Fact]
    public void The_two_files_that_test_for_a_video_use_the_pattern_proven_equal_to_the_canonical_one()
    {
        ReadFile("infra/powerbi/soft-open-funnel.sql").Should().Contain(CanonicalVideoPattern, "the soft-open funnel is where the definition comes from");
        TrackedLinkFunnelReader.FunnelSql.Should().Contain(CanonicalVideoPattern, "the admin funnel uses the same definition");
        Flatten(ReadFile(PersonFile)).Should().Contain("~* '" + FileVideoPattern + "'");
        Flatten(ReadFile(LinksFile)).Should().Contain("~* '" + FileVideoPattern + "'");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_file_survives_the_extractors_flattening_into_one_psql_copy_line(string relativePath)
    {
        string text = ReadFile(relativePath);
        text.Should().NotContain("\r", "the extractor splits on line feeds only");

        string[] code = text.Split('\n').Where(line => !Regex.IsMatch(line, @"^\s*--")).ToArray();
        foreach (string line in code)
        {
            line.Should().NotContain("--", "only full-line comments: after the lines are joined, an inline comment would swallow the rest of the statement");
            line.Should().NotContain("/*", "no block comments either");
            line.Should().NotContain("\t", "the flattening collapses spaces, not tabs");
        }

        string sql = Flatten(text);
        foreach ((string forbidden, string why) in new[]
        {
            ("\"", "the statement travels inside a double-quoted ssh argument"),
            ("$", "the remote shell would expand it"),
            ("`", "the remote shell would run it"),
            ("\\", "the remote shell and psql both give a backslash a meaning"),
            (";", "one statement: \\copy ( ... ) takes a single SELECT, and only a trailing semicolon is stripped"),
            ("\n", "one line"),
        })
        {
            sql.Should().NotContain(forbidden, why);
        }

        sql.Should().MatchRegex("^(WITH|SELECT) ", "the file is one SELECT, optionally led by WITH");
        sql.Should().NotMatchRegex(@"(?i)\b(insert|update|delete|drop|alter|create|grant|revoke|truncate|copy|set|call|do)\b",
            "the file is a read-only query");
        sql.Should().NotContain("  ", "the flattened statement has single spaces only");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_file_reads_no_withheld_column_and_no_table_the_bi_role_is_not_granted(string relativePath)
    {
        string sql = Flatten(ReadFile(relativePath));
        foreach (string withheld in new[]
        {
            "password_hash", "signup_ip", "user_request", "error_message", "result_json", "error_details", "input_json", "output_json",
            "api_key_encrypted", "system_prompt", "run_brief_json", "style_config_json", "agent_snapshot_json", "config_json",
            "feedback_reports", "alpha_funnel_claims", "user_onboarding_states", "project_files", "workflow_definitions",
            "display_name", ".email ", " email,",
        })
        {
            sql.Should().NotContain(withheld, "the BI role is not granted it, or it identifies a person, and the funnel must never need it");
        }

        // Every table named after FROM or JOIN is one the role may read.
        HashSet<string> granted = PowerBiReadOnlyRoleSql.FullSelectTables
            .Concat(PowerBiReadOnlyRoleSql.ColumnSelectTables.Keys)
            .Concat(PowerBiReadOnlyRoleEngineSql.ColumnSelectTables.Keys)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> cteNames = Regex.Matches(sql, @"(?:WITH|,) (\w+) AS \(").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        string[] read = Regex.Matches(sql, @"\b(?:FROM|JOIN) (\w+)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        read.Should().NotBeEmpty();
        read.Where(t => !cteNames.Contains(t)).Should().OnlyContain(t => granted.Contains(t), "a file may read granted tables only");
    }

    [Fact]
    public void The_flattening_used_here_is_the_extractors()
    {
        // The line of reelbolt-bi tools/extract.sh this class mirrors, applied to a small file by hand.
        const string file = "-- header\n  -- indented comment\nSELECT 1 AS a,\n       2   AS b\nFROM t\n-- trailing comment\nWHERE x = 'y';  \n";
        Flatten(file).Should().Be("SELECT 1 AS a, 2 AS b FROM t WHERE x = 'y'");
        Flatten("SELECT 1\n").Should().Be("SELECT 1");
    }

    [Fact]
    public void The_csv_reader_tells_null_from_empty_and_reads_quoted_fields()
    {
        ParseCsv("a,b,c\n1,,\"\"\n\"x, \"\"y\"\"\nz\",2,3\n").Should().BeEquivalentTo(new[]
        {
            new string?[] { "a", "b", "c" },
            new string?[] { "1", null, string.Empty },
            new string?[] { "x, \"y\"\nz", "2", "3" },
        }, o => o.WithStrictOrdering());
        Ts("2026-09-02 01:00:00+00").Should().Be(new DateTime(2026, 9, 2, 1, 0, 0, DateTimeKind.Utc));
        Ts(null).Should().BeNull();
    }
}
