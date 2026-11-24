using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using ReelBolt.Inference.Api.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// product-events-funnel T-009, the brief's acceptance criterion 3: a consent decline must be
/// <em>demonstrated</em> distinguishable from a drop-off, not asserted. One Postgres-gated test runs
/// <see cref="FunnelQuery"/> over four seeded people and reads the outcome off the data:
/// <list type="number">
/// <item>a person who declined and sent no client events reads <c>deliberate_absence</c> — a stated
/// fact, not a silent zero;</item>
/// <item>a person who consented and sent no client events reads <c>measurable</c> and is counted as
/// dropped;</item>
/// <item>a person who was never asked (no consent row) reads <c>not_measurable</c> and leaves the
/// client-step denominator;</item>
/// <item>a person who declined and <em>later</em> granted reads <c>measurable</c> — the latest
/// consent row wins, so a stale decline cannot mark a now-consented person unmeasurable forever.</item>
/// </list>
/// <para>
/// The rows are seeded with SQL, not through the ingest controller: this test is about what the data
/// <em>means</em>, not how it was written. It follows <see cref="ProductEventsSchemaTests"/> — a
/// per-test database, <see cref="PostgresTestEnvironment"/>, and
/// <c>DROP DATABASE ... WITH (FORCE)</c> in <c>finally</c>.
/// </para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ProductEventsFunnelQueryTests
{
    /// <summary>
    /// The funnel query. One statement, runnable by <c>powerbi_ro</c> (it reads only
    /// <c>product_events</c> and nothing else), returning per person: the furthest <c>server</c> step
    /// reached, the furthest <c>client</c> step reached, and a <c>measurability</c> column derived from
    /// the <em>latest</em> <c>consent</c> row (<c>declined</c> → <c>deliberate_absence</c>,
    /// <c>granted</c> → <c>measurable</c>, none → <c>not_measurable</c>).
    /// <para>
    /// This is the artifact: <c>docs/product-events.md</c> quotes it verbatim and states that it is the
    /// exact statement the test below runs. The three outcomes are the funnel's honesty — a person who
    /// said no is neither a drop-off nor a silent zero, and a person who was never asked never enters
    /// the client-step denominator.
    /// </para>
    /// </summary>
    public const string FunnelQuery = @"
WITH step_rank (event, source, step) AS (
    VALUES
        ('alpha.signup',                 'server', 1),
        ('alpha.email_verified',         'server', 2),
        ('alpha.workspace_created',      'server', 3),
        ('alpha.invite_accepted',        'server', 4),
        ('alpha.first_payment',          'server', 5),
        ('alpha.first_project',          'client', 1),
        ('alpha.first_run',              'client', 2),
        ('alpha.first_export_initiated', 'client', 3)
),
person AS (
    SELECT DISTINCT user_id
    FROM product_events
    WHERE user_id IS NOT NULL
),
latest_consent AS (
    SELECT DISTINCT ON (user_id) user_id, event
    FROM product_events
    WHERE source = 'consent'
    ORDER BY user_id, occurred_at DESC, id DESC
),
reached AS (
    SELECT e.user_id, r.source, max(r.step) AS step
    FROM product_events e
    JOIN step_rank r ON r.event = e.event AND r.source = e.source
    WHERE e.user_id IS NOT NULL
    GROUP BY e.user_id, r.source
)
SELECT
    p.user_id,
    coalesce(max(CASE r.source WHEN 'server' THEN r.step END), 0) AS furthest_server_step,
    coalesce(max(CASE r.source WHEN 'client' THEN r.step END), 0) AS furthest_client_step,
    CASE
        WHEN lc.event = 'telemetry.consent.declined' THEN 'deliberate_absence'
        WHEN lc.event = 'telemetry.consent.granted'  THEN 'measurable'
        ELSE 'not_measurable'
    END AS measurability
FROM person p
LEFT JOIN reached r ON r.user_id = p.user_id
LEFT JOIN latest_consent lc ON lc.user_id = p.user_id
GROUP BY p.user_id, lc.event
ORDER BY p.user_id;";

    [PostgresFact]
    public async Task A_consent_decline_reads_as_deliberate_absence_while_a_consented_silence_reads_as_a_drop_off()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_funnel_query_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            // The query runs against the real schema, so the migration must have applied first: this
            // proves the query and the table agree, not merely that SQL is syntactically valid.
            await using (InferenceApiDbContext api = new(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using NpgsqlConnection c = new(csb.ConnectionString);
            await c.OpenAsync();

            // The rows are written with SQL: this test is about what the data means, not how it was
            // written, so it does not drive the ingest controller.
            async Task SeedAsync(Guid userId, string eventName, string source, DateTimeOffset occurredAt)
            {
                await using NpgsqlCommand insert = new(
                    "INSERT INTO product_events (user_id, organization_id, event, occurred_at, source) "
                    + "VALUES (@user_id, NULL, @event, @occurred_at, @source)", c);
                insert.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Uuid) { Value = userId });
                insert.Parameters.AddWithValue("event", eventName);
                insert.Parameters.AddWithValue("occurred_at", occurredAt);
                insert.Parameters.AddWithValue("source", source);
                await insert.ExecuteNonQueryAsync();
            }

            Guid declined = Guid.NewGuid();
            Guid consented = Guid.NewGuid();
            Guid neverAsked = Guid.NewGuid();
            Guid changedMind = Guid.NewGuid();
            DateTimeOffset t0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

            // 1. Declined: signed up, told us no, and (correctly) sent nothing else.
            await SeedAsync(declined, "alpha.signup", "server", t0);
            await SeedAsync(declined, "telemetry.consent.declined", "consent", t0.AddMinutes(1));

            // 2. Consented: three server steps in, granted telemetry, but no client step yet.
            await SeedAsync(consented, "alpha.signup", "server", t0);
            await SeedAsync(consented, "alpha.email_verified", "server", t0.AddMinutes(2));
            await SeedAsync(consented, "alpha.workspace_created", "server", t0.AddMinutes(3));
            await SeedAsync(consented, "telemetry.consent.granted", "consent", t0.AddMinutes(1));

            // 3. Never asked: signed up, no consent row at all, no client events.
            await SeedAsync(neverAsked, "alpha.signup", "server", t0);

            // 4. Changed their mind: declined first, then granted three days later.
            await SeedAsync(changedMind, "alpha.signup", "server", t0);
            await SeedAsync(changedMind, "telemetry.consent.declined", "consent", t0.AddMinutes(1));
            await SeedAsync(changedMind, "telemetry.consent.granted", "consent", t0.AddDays(3));

            Dictionary<Guid, FunnelOutcome> outcomes = new();
            await using (NpgsqlCommand query = new(FunnelQuery, c))
            await using (NpgsqlDataReader reader = await query.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    outcomes[reader.GetGuid(0)] = new FunnelOutcome(
                        reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3));
                }
            }

            outcomes.Should().HaveCount(4, "the query returns one row per person in product_events");

            // 1. Declined, no client events -> deliberate absence, not a drop-off.
            FunnelOutcome declinedOutcome = outcomes[declined];
            declinedOutcome.Measurability.Should().Be("deliberate_absence",
                "a person who told us no is a stated fact, not a silent zero");
            declinedOutcome.FurthestServerStep.Should().Be(1);
            declinedOutcome.FurthestClientStep.Should().Be(0);

            // 2. Consented, no client events -> measurable, and counted as dropped.
            FunnelOutcome consentedOutcome = outcomes[consented];
            consentedOutcome.Measurability.Should().Be("measurable",
                "a consented person's silence is a real drop-off, because their client events would have arrived");
            consentedOutcome.FurthestServerStep.Should().Be(3, "the furthest server step is the maximum, not a count");
            consentedOutcome.FurthestClientStep.Should().Be(0);

            // 3. Never asked -> not measurable, excluded from the client-step denominator.
            outcomes[neverAsked].Measurability.Should().Be("not_measurable",
                "a person who was never asked is neither granted nor declined, so nothing can be measured");

            // 4. Declined then granted -> measurable: the latest consent row wins.
            outcomes[changedMind].Measurability.Should().Be("measurable",
                "a stale decline must not mark a now-consented person unmeasurable forever");

            // The drop-off reading the query exists to prevent: a person counts as dropped only when
            // they are measurable (they consented) and reached no client step. The declined person is
            // not in that set, and the never-asked person never enters the denominator at all.
            HashSet<Guid> denominator = outcomes
                .Where(o => o.Value.Measurability == "measurable")
                .Select(o => o.Key)
                .ToHashSet();
            denominator.Should().BeEquivalentTo(
                new[] { consented, changedMind },
                "only consented people enter the client-step denominator; the never-asked person is excluded");

            HashSet<Guid> dropped = outcomes
                .Where(o => o.Value.Measurability == "measurable" && o.Value.FurthestClientStep == 0)
                .Select(o => o.Key)
                .ToHashSet();
            dropped.Should().Contain(consented, "a consented person with no client step really did drop off");
            dropped.Should().NotContain(declined, "the declined person is a deliberate absence, never a drop-off");
            dropped.Should().NotContain(neverAsked, "the never-asked person is excluded from the denominator");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>One person's row from <see cref="FunnelQuery"/>.</summary>
    private readonly record struct FunnelOutcome(int FurthestServerStep, int FurthestClientStep, string Measurability);
}
