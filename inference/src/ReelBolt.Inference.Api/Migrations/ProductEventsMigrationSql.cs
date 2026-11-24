namespace ReelBolt.Inference.Api.Migrations;

/// <summary>
/// Raw SQL the product-events migration runs. Kept out of the migration file so the statements are
/// greppable and reviewable on their own, exactly as <see cref="BillingPaymentsMigrationSql"/> is.
/// </summary>
public static class ProductEventsMigrationSql
{
    /// <summary>
    /// Grants the BI extract's read-only role SELECT on the new table, but only when that role exists:
    /// the role is created by the powerbi-readonly-role initiative (#427), which may land before or
    /// after this one. An unguarded GRANT would fail the migration - and with it the API's startup - on
    /// a deployment that does not have the role yet.
    /// <para>
    /// This is the <c>billing_payments</c> precedent applied to a table created <em>after</em> the
    /// <c>AddPowerBiReadOnlyRoleGrants</c> migration, which is why the table needs its own grant: that
    /// migration already ran on a deployed database and will not pick this table up. Running the grant
    /// here as well as in <c>PowerBiReadOnlyRoleSql.GrantsSql</c> is deliberate - the registry block
    /// converges a database whose role already exists, and this one converges an already-deployed
    /// database without an operator re-running the script.
    /// </para>
    /// </summary>
    public const string PowerBiGrant = """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
                GRANT SELECT ON TABLE product_events TO powerbi_ro;
            END IF;
        END $$;
        """;

    /// <summary>
    /// The same guarded grant for <c>user_attribution</c> (product-analytics): the BI role reads the
    /// first-touch attribution table-wide, but only when the role exists on this database.
    /// </summary>
    public const string UserAttributionPowerBiGrant = """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
                GRANT SELECT ON TABLE user_attribution TO powerbi_ro;
            END IF;
        END $$;
        """;
}
