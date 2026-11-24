namespace ReelBolt.Inference.Api.Migrations;

/// <summary>
/// Raw SQL of the <c>BackfillStripeRevenue</c> migration (data only, no schema change). It brings the
/// retained Stripe webhook payloads (<c>billing_webhook_events</c>, provider <c>stripe</c>, never deleted) to
/// the same state the live path now writes: the totals columns of existing Stripe payments, the zero-amount
/// (100%-off) invoices that were never recorded, and the refunds/lost disputes. Every statement is
/// idempotent; a malformed payload is skipped with a <c>RAISE WARNING</c>, never aborting the migration.
/// </summary>
public static class StripeRevenueBackfillMigrationSql
{
    /// <summary>
    /// Fills NULL totals columns on existing Stripe payments: an <c>invoice.paid</c> payment is keyed by the
    /// invoice id, a top-up by the checkout session's payment intent. Discount is the sum of
    /// <c>total_discount_amounts</c> (0 when the array is empty), tax is <c>total - total_excluding_tax</c>;
    /// a member the payload does not state stays NULL.
    /// </summary>
    public const string FillTotals = """
        UPDATE billing_payments p
        SET subtotal_minor = COALESCE(p.subtotal_minor, t.subtotal),
            discount_minor = COALESCE(p.discount_minor, t.discount),
            tax_minor      = COALESCE(p.tax_minor, t.tax),
            total_minor    = COALESCE(p.total_minor, t.total)
        FROM (
            SELECT DISTINCT ON (x.transaction_id)
                   x.transaction_id, x.subtotal, x.discount, x.tax, x.total
            FROM (
                SELECT e.payload_json #>> '{data,object,id}' AS transaction_id,
                       CASE WHEN e.payload_json #>> '{data,object,subtotal}' ~ '^[0-9]{1,18}$'
                            THEN (e.payload_json #>> '{data,object,subtotal}')::bigint END AS subtotal,
                       CASE WHEN jsonb_typeof(e.payload_json #> '{data,object,total_discount_amounts}') = 'array'
                            THEN (SELECT COALESCE(sum(CASE WHEN d ->> 'amount' ~ '^[0-9]{1,18}$' THEN (d ->> 'amount')::bigint ELSE 0 END), 0)
                                  FROM jsonb_array_elements(e.payload_json #> '{data,object,total_discount_amounts}') d) END AS discount,
                       CASE WHEN e.payload_json #>> '{data,object,total}' ~ '^-?[0-9]{1,18}$'
                             AND e.payload_json #>> '{data,object,total_excluding_tax}' ~ '^-?[0-9]{1,18}$'
                            THEN (e.payload_json #>> '{data,object,total}')::bigint
                               - (e.payload_json #>> '{data,object,total_excluding_tax}')::bigint END AS tax,
                       CASE WHEN e.payload_json #>> '{data,object,total}' ~ '^[0-9]{1,18}$'
                            THEN (e.payload_json #>> '{data,object,total}')::bigint END AS total,
                       e.received_at
                FROM billing_webhook_events e
                WHERE e.provider = 'stripe' AND e.event_type = 'invoice.paid'
                UNION ALL
                SELECT e.payload_json #>> '{data,object,payment_intent}',
                       CASE WHEN e.payload_json #>> '{data,object,amount_subtotal}' ~ '^[0-9]{1,18}$'
                            THEN (e.payload_json #>> '{data,object,amount_subtotal}')::bigint END,
                       CASE WHEN e.payload_json #>> '{data,object,total_details,amount_discount}' ~ '^[0-9]{1,18}$'
                            THEN (e.payload_json #>> '{data,object,total_details,amount_discount}')::bigint END,
                       CASE WHEN e.payload_json #>> '{data,object,total_details,amount_tax}' ~ '^[0-9]{1,18}$'
                            THEN (e.payload_json #>> '{data,object,total_details,amount_tax}')::bigint END,
                       CASE WHEN e.payload_json #>> '{data,object,amount_total}' ~ '^[0-9]{1,18}$'
                            THEN (e.payload_json #>> '{data,object,amount_total}')::bigint END,
                       e.received_at
                FROM billing_webhook_events e
                WHERE e.provider = 'stripe'
                  AND e.event_type IN ('checkout.session.completed', 'checkout.session.async_payment_succeeded')
                  AND e.payload_json #>> '{data,object,mode}' = 'payment'
            ) x
            WHERE x.transaction_id IS NOT NULL
            ORDER BY x.transaction_id, x.received_at
        ) t
        WHERE p.provider = 'stripe'
          AND p.provider_transaction_id = t.transaction_id
          AND (p.subtotal_minor IS NULL OR p.discount_minor IS NULL OR p.tax_minor IS NULL OR p.total_minor IS NULL);
        """;

    /// <summary>
    /// Inserts the <c>invoice.paid</c> payments that were never recorded (the 100%-off first invoice and every
    /// zero-amount renewal were dropped before). Organization attribution is the processor's order
    /// (subscription row, else the payload's own claim when that organization exists, else
    /// <c>billing_customers</c>); never a non-existent organization.
    /// </summary>
    public const string InsertMissingInvoices = """
        DO $backfill$
        DECLARE
            r record;
            v_inserted bigint;
            v_promoted bigint := 0;
            v_skipped bigint := 0;
        BEGIN
            FOR r IN
                SELECT e.payload_json #>> '{data,object,id}'                                   AS transaction_id,
                       COALESCE(NULLIF(e.payload_json #>> '{data,object,parent,subscription_details,subscription}', ''),
                                NULLIF(e.payload_json #>> '{data,object,lines,data,0,parent,subscription_item_details,subscription}', '')) AS subscription_id,
                       e.payload_json #>> '{data,object,amount_paid}'                          AS amount_paid,
                       NULLIF(upper(e.payload_json #>> '{data,object,currency}'), '')          AS currency_code,
                       e.payload_json #>> '{data,object,subtotal}'                             AS subtotal,
                       e.payload_json #>> '{data,object,total}'                                AS total,
                       e.payload_json #>> '{data,object,total_excluding_tax}'                  AS total_excluding_tax,
                       CASE WHEN jsonb_typeof(e.payload_json #> '{data,object,total_discount_amounts}') = 'array'
                            THEN (SELECT COALESCE(sum(CASE WHEN d ->> 'amount' ~ '^[0-9]{1,18}$' THEN (d ->> 'amount')::bigint ELSE 0 END), 0)
                                  FROM jsonb_array_elements(e.payload_json #> '{data,object,total_discount_amounts}') d) END AS discount,
                       e.payload_json #>> '{created}'                                          AS created,
                       e.received_at                                                           AS received_at,
                       COALESCE(s.organization_id, o.id, c.organization_id)                    AS organization_id
                FROM billing_webhook_events e
                LEFT JOIN subscriptions s
                       ON s.provider = e.provider
                      AND s.provider_subscription_id = COALESCE(NULLIF(e.payload_json #>> '{data,object,parent,subscription_details,subscription}', ''),
                                NULLIF(e.payload_json #>> '{data,object,lines,data,0,parent,subscription_item_details,subscription}', ''))
                LEFT JOIN organizations o
                       ON o.id = CASE
                            WHEN e.payload_json #>> '{data,object,parent,subscription_details,metadata,organization_id}'
                                 ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                            THEN (e.payload_json #>> '{data,object,parent,subscription_details,metadata,organization_id}')::uuid
                          END
                LEFT JOIN billing_customers c
                       ON c.provider = e.provider
                      AND c.provider_customer_id = NULLIF(e.payload_json #>> '{data,object,customer}', '')
                WHERE e.provider = 'stripe'
                  AND e.event_type = 'invoice.paid'
                  AND e.payload_json #>> '{data,object,billing_reason}' IN ('subscription_create', 'subscription_cycle', 'subscription_update')
                  AND e.payload_json #>> '{data,object,id}' IS NOT NULL
                  AND e.payload_json #>> '{data,object,amount_paid}' ~ '^[0-9]{1,18}$'
                ORDER BY e.received_at
            LOOP
                BEGIN
                    INSERT INTO billing_payments
                        (id, provider, provider_transaction_id, provider_subscription_id, organization_id,
                         amount_minor, currency_code, subtotal_minor, discount_minor, tax_minor, total_minor,
                         occurred_at, created_at)
                    VALUES
                        (gen_random_uuid(), 'stripe', r.transaction_id, r.subscription_id, r.organization_id,
                         r.amount_paid::bigint, r.currency_code,
                         CASE WHEN r.subtotal ~ '^[0-9]{1,18}$' THEN r.subtotal::bigint END,
                         r.discount,
                         CASE WHEN r.total ~ '^-?[0-9]{1,18}$' AND r.total_excluding_tax ~ '^-?[0-9]{1,18}$'
                              THEN r.total::bigint - r.total_excluding_tax::bigint END,
                         CASE WHEN r.total ~ '^[0-9]{1,18}$' THEN r.total::bigint END,
                         CASE WHEN r.created ~ '^[0-9]{1,11}$' THEN to_timestamp(r.created::bigint) ELSE r.received_at END,
                         now())
                    ON CONFLICT (provider, provider_transaction_id) DO NOTHING;

                    GET DIAGNOSTICS v_inserted = ROW_COUNT;
                    IF v_inserted > 0 THEN
                        v_promoted := v_promoted + 1;
                    END IF;
                EXCEPTION
                    WHEN data_exception OR invalid_text_representation OR numeric_value_out_of_range
                      OR datetime_field_overflow OR invalid_datetime_format OR string_data_right_truncation
                      OR character_not_in_repertoire THEN
                        RAISE WARNING 'stripe revenue backfill: skipped an invoice.paid payload whose values do not fit their columns (invoice=%)', r.transaction_id;
                        v_skipped := v_skipped + 1;
                        CONTINUE;
                END;
            END LOOP;

            RAISE WARNING 'stripe revenue backfill: inserted % missing invoice payment(s); skipped %', v_promoted, v_skipped;
        END
        $backfill$;
        """;

    /// <summary>
    /// Promotes retained succeeded Stripe refunds (<c>refund.created/updated</c>) and lost disputes
    /// (<c>charge.dispute.closed</c>) into <c>billing_refunds</c>; tax is NULL (Stripe states none).
    /// </summary>
    public const string BackfillRefunds = """
        DO $backfill$
        DECLARE
            r record;
            v_inserted bigint;
            v_promoted bigint := 0;
            v_skipped bigint := 0;
        BEGIN
            FOR r IN
                SELECT e.payload_json #>> '{data,object,id}'                          AS adjustment_id,
                       COALESCE(NULLIF(e.payload_json #>> '{data,object,payment_intent}', ''),
                                NULLIF(e.payload_json #>> '{data,object,payment_intent,id}', '')) AS transaction_id,
                       e.payload_json #>> '{data,object,amount}'                      AS amount,
                       NULLIF(upper(e.payload_json #>> '{data,object,currency}'), '') AS currency_code,
                       e.payload_json #>> '{created}'                                 AS created,
                       e.received_at                                                  AS received_at
                FROM billing_webhook_events e
                WHERE e.provider = 'stripe'
                  AND ((e.event_type IN ('refund.created', 'refund.updated')
                        AND e.payload_json #>> '{data,object,status}' = 'succeeded')
                    OR (e.event_type = 'charge.dispute.closed'
                        AND e.payload_json #>> '{data,object,status}' = 'lost'))
                  AND e.payload_json #>> '{data,object,id}' IS NOT NULL
                  AND e.payload_json #>> '{data,object,amount}' ~ '^[0-9]{1,18}$'
                ORDER BY e.received_at
            LOOP
                BEGIN
                    INSERT INTO billing_refunds
                        (id, provider, provider_adjustment_id, provider_transaction_id,
                         amount_minor, tax_minor, currency_code, occurred_at, created_at)
                    VALUES
                        (gen_random_uuid(), 'stripe', r.adjustment_id, r.transaction_id,
                         r.amount::bigint, NULL, r.currency_code,
                         CASE WHEN r.created ~ '^[0-9]{1,11}$' THEN to_timestamp(r.created::bigint) ELSE r.received_at END,
                         now())
                    ON CONFLICT (provider, provider_adjustment_id) DO NOTHING;

                    GET DIAGNOSTICS v_inserted = ROW_COUNT;
                    IF v_inserted > 0 THEN
                        v_promoted := v_promoted + 1;
                    END IF;
                EXCEPTION
                    WHEN data_exception OR invalid_text_representation OR numeric_value_out_of_range
                      OR datetime_field_overflow OR invalid_datetime_format OR string_data_right_truncation
                      OR character_not_in_repertoire THEN
                        RAISE WARNING 'stripe revenue backfill: skipped a refund/dispute payload whose values do not fit their columns (adjustment=%)', r.adjustment_id;
                        v_skipped := v_skipped + 1;
                        CONTINUE;
                END;
            END LOOP;

            RAISE WARNING 'stripe revenue backfill: promoted % refund row(s); skipped %', v_promoted, v_skipped;
        END
        $backfill$;
        """;
}
