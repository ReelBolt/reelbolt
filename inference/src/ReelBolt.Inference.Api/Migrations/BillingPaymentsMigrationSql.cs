namespace ReelBolt.Inference.Api.Migrations;

/// <summary>
/// Raw SQL the billing-payments migration runs. Kept out of the migration file so the statements are
/// greppable and reviewable on their own, and so the backfill can be re-run by hand.
/// </summary>
public static class BillingPaymentsMigrationSql
{
    /// <summary>
    /// Grants the BI extract's read-only role SELECT on the new table, but only when that role exists:
    /// the role is created by the powerbi-readonly-role initiative (#427), which may land before or
    /// after this one. An unguarded GRANT would fail the migration — and with it the API's startup —
    /// on a deployment that does not have the role yet.
    /// </summary>
    public const string PowerBiGrant = """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
                GRANT SELECT ON TABLE billing_payments TO powerbi_ro;
            END IF;
        END $$;
        """;

    /// <summary>
    /// The one-shot backfill: every <c>transaction.completed</c> payload still in
    /// <c>billing_webhook_events</c> becomes a <c>billing_payments</c> row. Idempotent —
    /// <c>ON CONFLICT DO NOTHING</c> against the table's unique key — so it is safe to run again by hand,
    /// and it never overwrites a row the webhook processor already wrote.
    /// <para>
    /// <b>Why a loop and not a plain <c>INSERT … SELECT</c>.</b> A payload of another shape must never
    /// abort this statement: the statement runs inside the migration, so an error rolls the migration
    /// back and the API never starts. Regex-guarding every cast is necessary but not sufficient —
    /// PostgreSQL still raises <c>date/time field value out of range</c> on a syntactically valid but
    /// non-existent date (for example <c>2026-02-31</c>, which no regex short of a leap-year-aware
    /// monster can reject) and <c>value out of range for type bigint</c> on a long enough digit string.
    /// One such row would abort the whole <c>INSERT … SELECT</c>. Each row is therefore inserted under its
    /// own exception handler, which is the actual guarantee; the regex guards keep the expected shapes out
    /// of its way and record a null for a field that is merely the wrong shape.
    /// </para>
    /// <para>
    /// <b>What the handler catches, and what it does not.</b> Only the data-shape errors a payload can
    /// provoke are caught — the class-22 SQLSTATEs <c>invalid_text_representation</c>,
    /// <c>numeric_value_out_of_range</c>, <c>datetime_field_overflow</c>, <c>invalid_datetime_format</c>,
    /// <c>string_data_right_truncation</c> and <c>character_not_in_repertoire</c> — and every skipped row
    /// is reported with <c>RAISE WARNING</c> naming the provider and transaction, so a skip is visible in
    /// the migration's output rather than silent. Anything else (a missing grant, a column that no longer
    /// exists, any unanticipated failure) is deliberately <b>not</b> caught: it propagates and fails the
    /// migration loudly. On a money path a skip nobody can see is unrecoverable revenue; a failed deploy
    /// is not. A closing <c>RAISE WARNING</c> reports the totals, so the output says how many rows were
    /// promoted and how many were skipped rather than leaving it to be inferred.
    /// </para>
    /// <para>
    /// <b>Attribution is corroborated against our own rows.</b> The organization is resolved in
    /// <c>BillingEventProcessor</c>'s order: the matching <c>subscriptions</c> row (<c>provider</c> plus
    /// <c>provider_subscription_id</c> taken from the payload's <c>data.subscription_id</c>), else the
    /// payload's <c>custom_data.organization_id</c> <b>only when an <c>organizations</c> row with that id
    /// exists</b>, else the matching <c>billing_customers</c> row (<c>provider</c> plus
    /// <c>provider_customer_id</c> from <c>data.customer_id</c>), else NULL. The payload's claim on its own
    /// is never trusted: it can name an organization that does not exist, and it can contradict our own
    /// records. <c>provider_subscription_id</c> is written from the same <c>data.subscription_id</c> value
    /// the subscription join used, so the pair can never disagree — that one value is the single source of
    /// truth for both columns. When a <c>subscriptions</c> row matches and its organization differs from
    /// the payload's (existing) claim, the contradiction is reported with <c>RAISE WARNING</c> and the
    /// corroborated organization is the one written — never the contradiction, never both.
    /// </para>
    /// <para>
    /// <b>Every completed transaction is recorded, whatever the processor did with it.</b> The row is
    /// deliberately <b>not</b> gated on <c>billing_webhook_events.outcome = 'Applied'</c>. For a
    /// <c>transaction.completed</c> event the outcome is only ever <c>Applied</c> or <c>Unresolved</c>
    /// (<c>Duplicate</c> is never persisted and <c>Stale</c> comes only from <c>subscription.*</c> events),
    /// and <c>Unresolved</c> means the processor recorded the event but could not act on it — typically
    /// because no organization could be resolved — not that the provider refused the charge. Paddle
    /// captured that money regardless, so gating on <c>Applied</c> would drop real revenue and hide a
    /// support incident. The divergence is made visible instead: the closing <c>RAISE WARNING</c> counts
    /// the promoted rows whose outcome is not <c>Applied</c>.
    /// </para>
    /// <para>
    /// A <c>transaction.completed</c> payload is left out in exactly three ways, and the closing
    /// <c>RAISE WARNING</c> counts each so the census is complete: it states no parseable
    /// <c>grand_total</c> (a row claiming an amount the provider never stated is worse than no row); it
    /// states an amount but no <c>data.id</c>, so it cannot be keyed; or its promoted values do not fit
    /// their columns and the handler skips it. The amount is bounded to 18 digits (more than any real
    /// charge, and always inside <c>bigint</c>), so a nonsense amount counts as "no parseable
    /// <c>grand_total</c>" too. Only amount, currency and fee are promoted — nothing from
    /// <c>method_details</c>.
    /// </para>
    /// <para>
    /// <b>Limits.</b> It covers only what is still stored: a transaction whose payload was pruned, whose
    /// event never arrived, or which is only described by a <c>transaction.paid</c> event without a
    /// matching <c>completed</c> is not covered. A row whose organization no stored row corroborates (no
    /// matching <c>subscriptions</c>, no existing organization in the payload's <c>custom_data</c>, no
    /// matching <c>billing_customers</c>) stays unattributed; join <c>provider_subscription_id</c> to
    /// <c>subscriptions</c> for per-org revenue on those.
    /// </para>
    /// </summary>
    public const string Backfill = """
        DO $backfill$
        DECLARE
            r record;
            v_fee bigint;
            v_occurred timestamptz;
            v_inserted bigint;
            v_promoted bigint := 0;
            v_skipped bigint := 0;
            v_not_applied bigint := 0;
            v_no_amount bigint := 0;
            v_no_transaction_id bigint := 0;
        BEGIN
            FOR r IN
                SELECT
                    e.provider                                                              AS provider,
                    e.payload_json #>> '{data,id}'                                          AS transaction_id,
                    NULLIF(e.payload_json #>> '{data,subscription_id}', '')                 AS subscription_id,
                    e.payload_json #>> '{data,details,totals,grand_total}'                  AS grand_total,
                    COALESCE(
                        NULLIF(e.payload_json #>> '{data,currency_code}', ''),
                        NULLIF(e.payload_json #>> '{data,details,totals,currency_code}', '')) AS currency_code,
                    e.payload_json #>> '{data,details,totals,fee}'                          AS fee,
                    e.payload_json #>> '{occurred_at}'                                      AS occurred_at,
                    e.received_at                                                           AS received_at,
                    e.outcome                                                               AS outcome,
                    COALESCE(s.organization_id, o.id, c.organization_id)                    AS organization_id,
                    s.organization_id                                                       AS subscription_organization_id,
                    o.id                                                                    AS claimed_organization_id
                FROM billing_webhook_events e
                LEFT JOIN subscriptions s
                       ON s.provider = e.provider
                      AND s.provider_subscription_id = NULLIF(e.payload_json #>> '{data,subscription_id}', '')
                LEFT JOIN organizations o
                       ON o.id = CASE
                            WHEN e.payload_json #>> '{data,custom_data,organization_id}'
                                 ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                            THEN (e.payload_json #>> '{data,custom_data,organization_id}')::uuid
                          END
                LEFT JOIN billing_customers c
                       ON c.provider = e.provider
                      AND c.provider_customer_id = NULLIF(e.payload_json #>> '{data,customer_id}', '')
                WHERE e.event_type = 'transaction.completed'
                  AND e.payload_json #>> '{data,id}' IS NOT NULL
                  AND e.payload_json #>> '{data,details,totals,grand_total}' ~ '^[0-9]{1,18}$'
            LOOP
                BEGIN
                    IF r.subscription_organization_id IS NOT NULL
                       AND r.claimed_organization_id IS NOT NULL
                       AND r.subscription_organization_id <> r.claimed_organization_id THEN
                        RAISE WARNING 'billing_payments backfill: payload and subscription disagree on the organization (provider=%, transaction=%, subscription=%, subscription_organization=%, claimed_organization=%); the subscription wins',
                            r.provider, r.transaction_id, r.subscription_id, r.subscription_organization_id, r.claimed_organization_id;
                    END IF;

                    v_fee := CASE WHEN r.fee ~ '^[0-9]{1,18}$' THEN r.fee::bigint END;
                    BEGIN
                        v_occurred := CASE
                            WHEN r.occurred_at ~ '^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.\d{1,6})?Z$'
                            THEN r.occurred_at::timestamptz
                        END;
                    EXCEPTION
                        WHEN data_exception OR invalid_text_representation OR numeric_value_out_of_range
                          OR datetime_field_overflow OR invalid_datetime_format OR string_data_right_truncation
                          OR character_not_in_repertoire THEN
                            -- The right shape but not a real instant (2026-02-31): keep the row and fall
                            -- back to received_at rather than lose the money over a timestamp.
                            v_occurred := NULL;
                    END;

                    INSERT INTO billing_payments
                        (id, provider, provider_transaction_id, provider_subscription_id, organization_id,
                         amount_minor, currency_code, fee_minor, occurred_at, created_at)
                    VALUES
                        (gen_random_uuid(), r.provider, r.transaction_id, r.subscription_id, r.organization_id,
                         r.grand_total::bigint, r.currency_code, v_fee,
                         COALESCE(v_occurred, r.received_at), now())
                    ON CONFLICT (provider, provider_transaction_id) DO NOTHING;

                    GET DIAGNOSTICS v_inserted = ROW_COUNT;
                    IF v_inserted > 0 THEN
                        v_promoted := v_promoted + 1;
                        IF r.outcome IS DISTINCT FROM 'Applied' THEN
                            v_not_applied := v_not_applied + 1;
                        END IF;
                    END IF;
                EXCEPTION
                    WHEN data_exception OR invalid_text_representation OR numeric_value_out_of_range
                      OR datetime_field_overflow OR invalid_datetime_format OR string_data_right_truncation
                      OR character_not_in_repertoire THEN
                        -- A payload of another shape (an id or currency too long for its column): skip it,
                        -- never abort the migration — but say so, so the loss is visible.
                        RAISE WARNING 'billing_payments backfill: skipped a transaction.completed payload whose promoted values do not fit their columns (provider=%, transaction=%)', r.provider, r.transaction_id;
                        v_skipped := v_skipped + 1;
                        CONTINUE;
                END;
            END LOOP;

            SELECT count(*) INTO v_no_amount
            FROM billing_webhook_events e
            WHERE e.event_type = 'transaction.completed'
              AND COALESCE(e.payload_json #>> '{data,details,totals,grand_total}', '') !~ '^[0-9]{1,18}$';

            -- A payload that states an amount but no transaction id cannot be keyed, so it is left out
            -- of the loop above entirely. Count it here, so the census is complete.
            SELECT count(*) INTO v_no_transaction_id
            FROM billing_webhook_events e
            WHERE e.event_type = 'transaction.completed'
              AND e.payload_json #>> '{data,id}' IS NULL
              AND e.payload_json #>> '{data,details,totals,grand_total}' ~ '^[0-9]{1,18}$';

            RAISE WARNING 'billing_payments backfill: promoted % row(s) from retained transaction.completed payloads; skipped % whose promoted values did not fit their columns; left out % with no parseable grand_total; left out % with a parseable amount but no transaction id; % promoted row(s) came from webhook events the processor recorded but did not act on (outcome is not Applied)',
                v_promoted, v_skipped, v_no_amount, v_no_transaction_id, v_not_applied;
        END
        $backfill$;
        """;
}
