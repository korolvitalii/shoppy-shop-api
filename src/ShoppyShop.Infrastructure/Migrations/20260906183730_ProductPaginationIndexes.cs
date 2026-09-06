using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShoppyShop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProductPaginationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Products_GroupId_Id",
                table: "Products",
                columns: new[] { "GroupId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_Name_Id",
                table: "Products",
                columns: new[] { "Name", "Id" });

            // The price sorts order by COALESCE("SalePrice", "Price"), an expression rather than a
            // column, which EF's model builder cannot describe — so this one is raw SQL.
            //
            // One index covers both directions: the descending sort tiebreaks on "Id" DESC too, so
            // it is exactly this index read backwards (verified as "Index Scan Backward" with the
            // row comparison still applied as an Index Cond). A descending sort that kept an
            // ascending tiebreak would mix directions within the key and need a second index.
            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_Products_EffectivePrice_Id"
                ON "Products" (COALESCE("SalePrice", "Price") ASC, "Id" ASC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_GroupId_Id",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_Name_Id",
                table: "Products");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Products_EffectivePrice_Id";""");
        }
    }
}
