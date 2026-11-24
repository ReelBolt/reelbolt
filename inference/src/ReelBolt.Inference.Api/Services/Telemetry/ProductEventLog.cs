using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// The one writer the Inference API uses for <c>product_events</c> (initiative
/// <c>product-events-funnel</c>): a plain append for a row nobody has to de-duplicate (a consent
/// decision), and an atomic claim-and-append for a funnel event that must count at most once per user.
/// </summary>
public interface IProductEventLog
{
    /// <summary>
    /// Appends one row — a plain insert. Used for a consent decision, which is a recorded choice rather
    /// than a funnel step: every decision is a row, so a repeat is expected, not suppressed.
    /// </summary>
    Task AppendAsync(ProductEvent entry, CancellationToken ct);

    /// <summary>
    /// Claims <paramref name="eventName"/> for <paramref name="userId"/> and appends its
    /// <c>product_events</c> row in <b>one statement</b>, so a claim and its row can never diverge.
    /// Returns true <b>only</b> for the call that newly claimed the event; every later call writes
    /// nothing and returns false. The caller moves the OTLP counter only on true.
    /// </summary>
    Task<bool> ClaimAndAppendAsync(
        Guid userId, Guid? organizationId, string eventName, string source, CancellationToken ct);

    /// <summary>
    /// Like <see cref="ClaimAndAppendAsync"/> but the claim is keyed by <paramref name="claimKey"/> (so one
    /// event can be claimed once per detail) and the row carries a closed <paramref name="detail"/> token.
    /// Still one statement. Returns true only for the call that newly claimed the key.
    /// </summary>
    Task<bool> ClaimAndAppendWithDetailAsync(
        Guid userId, Guid? organizationId, string eventName, string claimKey, string? detail, string source, CancellationToken ct)
        => throw new NotSupportedException();

    /// <summary>
    /// Claims and appends the organization-activation funnel row (<c>alpha.first_payment</c>), at most one
    /// per organization. The partial unique index <c>ux_product_events_first_payment_organization</c> on
    /// <c>(organization_id, event)</c> is the claim, so this needs no advisory lock and no separate claim
    /// table: two paid transactions of one organization — or two concurrent writers — cannot write two
    /// rows. Returns true <b>only</b> for the call that newly wrote the row.
    /// </summary>
    Task<bool> ClaimAndAppendFirstPaymentAsync(Guid organizationId, Guid? userId, CancellationToken ct);

    /// <summary>
    /// Records one anonymous visitor event (the visit beacon) in <b>one statement</b>: the visitor's first
    /// touch is stored in <c>visitor_attribution</c> unless the visitor already has one (first touch wins,
    /// <c>ON CONFLICT DO NOTHING</c>), and <paramref name="entry"/> is appended unless a row with the same
    /// visitor, event and detail exists at or after <paramref name="dedupeSince"/>. Returns true only when
    /// the event row was written; a repeat inside the window writes no event and returns false.
    /// <paramref name="entry"/> must be a visitor-only row whose <see cref="ProductEvent.VisitorId"/> is
    /// <paramref name="firstTouch"/>'s visitor.
    /// </summary>
    Task<bool> RecordVisitAsync(
        VisitorAttribution firstTouch, ProductEvent entry, DateTimeOffset dedupeSince, CancellationToken ct)
        => throw new NotSupportedException();
}

/// <summary>
/// The PostgreSQL implementation, on the DbContext's own connection, so every API replica shares it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement, not two.</b> <see cref="ClaimAndAppendAsync"/> is a single
/// <c>WITH claimed AS (INSERT ... ON CONFLICT DO NOTHING RETURNING 1) INSERT ... SELECT ... FROM
/// claimed</c>. The claim and its row commit or roll back together: there is no window in which the
/// claim exists without its row (or the reverse), which is the whole reason the row is not written by
/// a second call after a separate claim succeeds.
/// </para>
/// <para>
/// <b>The claim table is shared, not replaced.</b> The statement writes the same
/// <c>alpha_funnel_claims</c> row <see cref="AlphaFunnelClaimStore"/> writes, and that store is
/// unchanged: the unique key <c>(user_id, event)</c> is still the de-duplication, and the OTLP counter
/// still moves only for the call that newly won the claim.
/// </para>
/// </remarks>
public sealed class ProductEventLog(InferenceApiDbContext db) : IProductEventLog
{
    /// <summary>The one-statement claim-and-append, as it runs against PostgreSQL.</summary>
    public const string ClaimAndAppendSql =
        "WITH claimed AS ("
        + "INSERT INTO alpha_funnel_claims (user_id, event, claimed_at) "
        + "VALUES (@p_user_id, @p_event, @p_occurred_at) "
        + "ON CONFLICT (user_id, event) DO NOTHING RETURNING 1"
        + ") "
        + "INSERT INTO product_events (user_id, organization_id, event, occurred_at, source) "
        + "SELECT @p_user_id, @p_organization_id, @p_event, @p_occurred_at, @p_source FROM claimed "
        + "RETURNING 1";

    /// <summary>The detail-carrying claim-and-append (claim key separate from the event name).</summary>
    public const string ClaimAndAppendWithDetailSql =
        "WITH claimed AS ("
        + "INSERT INTO alpha_funnel_claims (user_id, event, claimed_at) "
        + "VALUES (@p_user_id, @p_claim_key, @p_occurred_at) "
        + "ON CONFLICT (user_id, event) DO NOTHING RETURNING 1"
        + ") "
        + "INSERT INTO product_events (user_id, organization_id, event, occurred_at, source, detail) "
        + "SELECT @p_user_id, @p_organization_id, @p_event, @p_occurred_at, @p_source, @p_detail FROM claimed "
        + "RETURNING 1";

    public async Task<bool> ClaimAndAppendWithDetailAsync(
        Guid userId, Guid? organizationId, string eventName, string claimKey, string? detail, string source, CancellationToken ct)
    {
        DateTimeOffset occurredAt = DateTimeOffset.UtcNow;
        int rows = await db.Database.ExecuteSqlRawAsync(
            ClaimAndAppendWithDetailSql,
            [
                new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Value = userId },
                new NpgsqlParameter("p_claim_key", NpgsqlDbType.Varchar) { Value = claimKey },
                new NpgsqlParameter("p_event", NpgsqlDbType.Varchar) { Value = eventName },
                new NpgsqlParameter("p_occurred_at", NpgsqlDbType.TimestampTz) { Value = occurredAt },
                new NpgsqlParameter("p_organization_id", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value },
                new NpgsqlParameter("p_source", NpgsqlDbType.Varchar) { Value = source },
                new NpgsqlParameter("p_detail", NpgsqlDbType.Varchar) { Value = (object?)detail ?? DBNull.Value },
            ],
            ct);
        return rows > 0;
    }

    public async Task AppendAsync(ProductEvent entry, CancellationToken ct)
    {
        db.ProductEvents.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ClaimAndAppendAsync(
        Guid userId, Guid? organizationId, string eventName, string source, CancellationToken ct)
    {
        // One timestamp for both the claim and the row: the observation time is the claim time.
        DateTimeOffset occurredAt = DateTimeOffset.UtcNow;

        // Npgsql cannot infer a type for a null Guid? on its own, and a bare DBNull binds as text and
        // fails the uuid column (42804), so the organization id is passed explicitly typed.
        int rows = await db.Database.ExecuteSqlRawAsync(
            ClaimAndAppendSql,
            [
                new NpgsqlParameter("p_user_id", NpgsqlDbType.Uuid) { Value = userId },
                new NpgsqlParameter("p_event", NpgsqlDbType.Varchar) { Value = eventName },
                new NpgsqlParameter("p_occurred_at", NpgsqlDbType.TimestampTz) { Value = occurredAt },
                new NpgsqlParameter("p_organization_id", NpgsqlDbType.Uuid)
                {
                    Value = (object?)organizationId ?? DBNull.Value,
                },
                new NpgsqlParameter("p_source", NpgsqlDbType.Varchar) { Value = source },
            ],
            ct);

        return rows > 0;
    }

    /// <summary>
    /// The visit beacon's one statement. The data-modifying CTE always runs, so the first event seen for
    /// a visitor stores their first touch even when the event row itself is a repeat; the outer insert is
    /// skipped when the same (visitor, event, detail) was recorded inside the window, and its row count is
    /// what the command reports. <c>IS NOT DISTINCT FROM</c> because a <c>signup.submitted</c> row has no
    /// detail and two nulls are the same detail here.
    /// </summary>
    public const string RecordVisitSql =
        "WITH touch AS ("
        + "INSERT INTO visitor_attribution (visitor_id, source_label, utm_source, utm_medium, utm_campaign, "
        + "utm_content, utm_term, referrer_host, landing_path, first_touch_at, created_at) "
        + "VALUES (@p_visitor_id, @p_source_label, @p_utm_source, @p_utm_medium, @p_utm_campaign, "
        + "@p_utm_content, @p_utm_term, @p_referrer_host, @p_landing_path, @p_first_touch_at, @p_created_at) "
        + "ON CONFLICT (visitor_id) DO NOTHING"
        + ") "
        + "INSERT INTO product_events (visitor_id, event, occurred_at, source, detail) "
        + "SELECT @p_visitor_id, @p_event, @p_occurred_at, @p_source, @p_detail "
        + "WHERE NOT EXISTS ("
        + "SELECT 1 FROM product_events e "
        + "WHERE e.visitor_id = @p_visitor_id AND e.event = @p_event "
        + "AND e.detail IS NOT DISTINCT FROM @p_detail AND e.occurred_at >= @p_since)";

    /// <inheritdoc />
    public async Task<bool> RecordVisitAsync(
        VisitorAttribution firstTouch, ProductEvent entry, DateTimeOffset dedupeSince, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firstTouch);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.VisitorId is not { } visitorId || visitorId != firstTouch.VisitorId || entry.UserId is not null || entry.OrganizationId is not null)
            throw new ArgumentException("A visit is a visitor-only row of the first touch's visitor.", nameof(entry));

        // Every nullable text value is bound with its type: a bare DBNull binds as text, and the
        // IS NOT DISTINCT FROM comparison needs to know what it compares.
        static NpgsqlParameter Text(string name, string? value) =>
            new(name, NpgsqlDbType.Varchar) { Value = (object?)value ?? DBNull.Value };

        int rows = await db.Database.ExecuteSqlRawAsync(
            RecordVisitSql,
            [
                new NpgsqlParameter("p_visitor_id", NpgsqlDbType.Uuid) { Value = visitorId },
                Text("p_source_label", firstTouch.SourceLabel),
                Text("p_utm_source", firstTouch.UtmSource),
                Text("p_utm_medium", firstTouch.UtmMedium),
                Text("p_utm_campaign", firstTouch.UtmCampaign),
                Text("p_utm_content", firstTouch.UtmContent),
                Text("p_utm_term", firstTouch.UtmTerm),
                Text("p_referrer_host", firstTouch.ReferrerHost),
                Text("p_landing_path", firstTouch.LandingPath),
                new NpgsqlParameter("p_first_touch_at", NpgsqlDbType.TimestampTz) { Value = firstTouch.FirstTouchAt },
                new NpgsqlParameter("p_created_at", NpgsqlDbType.TimestampTz) { Value = firstTouch.CreatedAt },
                new NpgsqlParameter("p_occurred_at", NpgsqlDbType.TimestampTz) { Value = entry.OccurredAt },
                Text("p_event", entry.Event),
                Text("p_source", entry.Source),
                Text("p_detail", entry.Detail),
                new NpgsqlParameter("p_since", NpgsqlDbType.TimestampTz) { Value = dedupeSince },
            ],
            ct);
        return rows > 0;
    }

    /// <summary>The event the organization-activation claim is about (product-events-funnel T-004).</summary>
    public const string FirstPaymentEvent = "alpha.first_payment";

    /// <summary>
    /// The organization-activation claim-and-append, as it runs against PostgreSQL and SQLite: the partial
    /// unique index on <c>(organization_id, event)</c> is named as the conflict target, so a repeat — or a
    /// concurrent writer — inserts nothing and the statement reports zero rows. One statement, so the
    /// claim and its row can never diverge. A person-less row takes a second form that never passes a
    /// null <c>Guid</c>: Npgsql cannot type a bare DBNull, which would bind as text and fail the uuid
    /// column (42804).
    /// </summary>
    private const string ClaimFirstPaymentSql =
        "INSERT INTO product_events (user_id, organization_id, event, occurred_at, source) "
        + "VALUES (@p0, @p1, '" + FirstPaymentEvent + "', @p2, '" + ProductEventSources.Server + "') "
        + "ON CONFLICT (organization_id, event) WHERE event = '" + FirstPaymentEvent + "' DO NOTHING";

    private const string ClaimFirstPaymentNoPersonSql =
        "INSERT INTO product_events (organization_id, event, occurred_at, source) "
        + "VALUES (@p0, '" + FirstPaymentEvent + "', @p1, '" + ProductEventSources.Server + "') "
        + "ON CONFLICT (organization_id, event) WHERE event = '" + FirstPaymentEvent + "' DO NOTHING";

    public async Task<bool> ClaimAndAppendFirstPaymentAsync(Guid organizationId, Guid? userId, CancellationToken ct)
    {
        // One timestamp for the row, as ClaimAndAppendAsync does for its claim: the observation time is now.
        DateTimeOffset occurredAt = DateTimeOffset.UtcNow;
        int rows = userId is { } user
            ? await db.Database.ExecuteSqlRawAsync(
                ClaimFirstPaymentSql, new object[] { user, organizationId, occurredAt }, ct)
            : await db.Database.ExecuteSqlRawAsync(
                ClaimFirstPaymentNoPersonSql, new object[] { organizationId, occurredAt }, ct);

        return rows > 0;
    }
}
