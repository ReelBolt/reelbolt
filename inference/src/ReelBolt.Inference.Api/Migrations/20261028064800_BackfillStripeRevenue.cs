using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class BackfillStripeRevenue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(StripeRevenueBackfillMigrationSql.FillTotals);

            migrationBuilder.Sql(StripeRevenueBackfillMigrationSql.InsertMissingInvoices);

            migrationBuilder.Sql(StripeRevenueBackfillMigrationSql.BackfillRefunds);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only: the rows it wrote are legitimate revenue facts and are kept.
        }
    }
}
