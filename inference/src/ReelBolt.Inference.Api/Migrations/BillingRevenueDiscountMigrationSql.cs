namespace ReelBolt.Inference.Api.Migrations;

/// <summary>
/// Raw SQL of the <c>AddBillingRevenueDiscount</c> migration, kept out of the generated file so it stays
/// greppable and reviewable, exactly as <see cref="BillingPaymentsMigrationSql"/> is. The migration adds
/// the Paddle totals breakdown to <c>billing_payments</c> and the <c>billing_refunds</c> table; these
/// statements grant the new table to the BI role and promote both from the retained webhook payloads.
/// Nothing here touches a customer-facing charge, checkout, discount or price.
/// </summary>
public static class BillingRevenueDiscountMigrationSql
{
    /// <summary>Guarded like <see cref="BillingPaymentsMigrationSql.PowerBiGrant"/>: only when the role exists.</summary>
    public const string PowerBiGrant = """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
                GRANT SELECT ON TABLE billing_refunds TO powerbi_ro;
            END IF;
        END $$;
        """;

    /// <summary>
    /// Fills the new totals columns of every existing payment from the retained
    /// <c>transaction.completed</c> payload of the same transaction. A member the payload did not send, or
    /// sent as something that is not a non-negative integer, stays NULL (never 0 for unknown). Only NULL
    /// columns are written, so a re-run changes nothing.
    /// </summary>
    public const string BackfillPaymentTotals = """
        UPDATE billing_payments p
        SET subtotal_minor = COALESCE(p.subtotal_minor, CASE WHEN t.subtotal ~ '^[0-9]{1,18}$' THEN t.subtotal::bigint END),
            discount_minor = COALESCE(p.discount_minor, CASE WHEN t.discount ~ '^[0-9]{1,18}$' THEN t.discount::bigint END),
            tax_minor      = COALESCE(p.tax_minor,      CASE WHEN t.tax      ~ '^[0-9]{1,18}$' THEN t.tax::bigint      END),
            total_minor    = COALESCE(p.total_minor,    CASE WHEN t.total    ~ '^[0-9]{1,18}$' THEN t.total::bigint    END),
            earnings_minor = COALESCE(p.earnings_minor, CASE WHEN t.earnings ~ '^[0-9]{1,18}$' THEN t.earnings::bigint END)
        FROM (
            SELECT DISTINCT ON (e.provider, e.payload_json #>> '{data,id}')
                   e.provider                                              AS provider,
                   e.payload_json #>> '{data,id}'                          AS transaction_id,
                   e.payload_json #>> '{data,details,totals,subtotal}'     AS subtotal,
                   e.payload_json #>> '{data,details,totals,discount}'     AS discount,
                   e.payload_json #>> '{data,details,totals,tax}'          AS tax,
                   e.payload_json #>> '{data,details,totals,total}'        AS total,
                   e.payload_json #>> '{data,details,totals,earnings}'     AS earnings
            FROM billing_webhook_events e
            WHERE e.event_type = 'transaction.completed'
              AND e.payload_json #>> '{data,id}' IS NOT NULL
            ORDER BY e.provider, e.payload_json #>> '{data,id}', e.received_at
        ) t
        WHERE t.provider = p.provider
          AND t.transaction_id = p.provider_transaction_id
          AND (p.subtotal_minor IS NULL OR p.discount_minor IS NULL OR p.tax_minor IS NULL
               OR p.total_minor IS NULL OR p.earnings_minor IS NULL);
        """;

    /// <summary>
    /// Promotes every retained approved Paddle <c>refund</c>/<c>chargeback</c> adjustment into
    /// <c>billing_refunds</c> (what the buyer got back is <c>data.totals.total</c>, its tax
    /// <c>data.totals.tax</c>). Idempotent on (provider, adjustment id). A payload whose values do not fit
    /// their columns is skipped with a <c>RAISE WARNING</c> naming it, never aborting the migration.
    /// </summary>
    public const string BackfillRefunds = """
        DO $backfill$
        DECLARE
            r record;
            v_occurred timestamptz;
            v_inserted bigint;
            v_promoted bigint := 0;
            v_skipped bigint := 0;
        BEGIN
            FOR r IN
                SELECT e.provider                                              AS provider,
                       e.payload_json #>> '{data,id}'                          AS adjustment_id,
                       NULLIF(e.payload_json #>> '{data,transaction_id}', '')  AS transaction_id,
                       e.payload_json #>> '{data,totals,total}'                AS total,
                       e.payload_json #>> '{data,totals,tax}'                  AS tax,
                       COALESCE(
                           NULLIF(e.payload_json #>> '{data,currency_code}', ''),
                           NULLIF(e.payload_json #>> '{data,totals,currency_code}', '')) AS currency_code,
                       e.payload_json #>> '{occurred_at}'                      AS occurred_at,
                       e.received_at                                           AS received_at
                FROM billing_webhook_events e
                WHERE e.event_type IN ('adjustment.created', 'adjustment.updated')
                  AND e.payload_json #>> '{data,action}' IN ('refund', 'chargeback')
                  AND e.payload_json #>> '{data,status}' = 'approved'
                  AND e.payload_json #>> '{data,id}' IS NOT NULL
                  AND e.payload_json #>> '{data,totals,total}' ~ '^[0-9]{1,18}$'
                ORDER BY e.received_at
            LOOP
                BEGIN
                    BEGIN
                        v_occurred := CASE
                            WHEN r.occurred_at ~ '^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.\d{1,6})?Z$'
                            THEN r.occurred_at::timestamptz
                        END;
                    EXCEPTION
                        WHEN data_exception OR invalid_text_representation OR numeric_value_out_of_range
                          OR datetime_field_overflow OR invalid_datetime_format THEN
                            v_occurred := NULL;
                    END;

                    INSERT INTO billing_refunds
                        (id, provider, provider_adjustment_id, provider_transaction_id,
                         amount_minor, tax_minor, currency_code, occurred_at, created_at)
                    VALUES
                        (gen_random_uuid(), r.provider, r.adjustment_id, r.transaction_id,
                         r.total::bigint,
                         CASE WHEN r.tax ~ '^[0-9]{1,18}$' THEN r.tax::bigint END,
                         r.currency_code, COALESCE(v_occurred, r.received_at), now())
                    ON CONFLICT (provider, provider_adjustment_id) DO NOTHING;

                    GET DIAGNOSTICS v_inserted = ROW_COUNT;
                    IF v_inserted > 0 THEN
                        v_promoted := v_promoted + 1;
                    END IF;
                EXCEPTION
                    WHEN data_exception OR invalid_text_representation OR numeric_value_out_of_range
                      OR datetime_field_overflow OR invalid_datetime_format OR string_data_right_truncation
                      OR character_not_in_repertoire THEN
                        RAISE WARNING 'billing_refunds backfill: skipped an adjustment payload whose values do not fit their columns (provider=%, adjustment=%)', r.provider, r.adjustment_id;
                        v_skipped := v_skipped + 1;
                        CONTINUE;
                END;
            END LOOP;

            RAISE WARNING 'billing_refunds backfill: promoted % row(s) from retained adjustment payloads; skipped %', v_promoted, v_skipped;
        END
        $backfill$;
        """;
}
