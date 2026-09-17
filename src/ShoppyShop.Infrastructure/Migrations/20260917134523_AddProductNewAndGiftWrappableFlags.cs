using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShoppyShop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductNewAndGiftWrappableFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GiftWrappable",
                table: "Products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsNew",
                table: "Products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Rows seeded before this migration all land on the AddColumn default (false). Backfill
            // the exact values the updated catalogue.json seed assigns to these same product ids —
            // not a value recomputed from row order, which would disagree with the seed the moment
            // the two orderings differ (e.g. a database's own id collation vs. the seed file's
            // insertion order). Ids outside this list (added after the seed, if any) keep the
            // AddColumn default rather than a guessed value.
            migrationBuilder.Sql(
                """
                UPDATE "Products" AS p
                SET "IsNew" = v."IsNew", "GiftWrappable" = v."GiftWrappable"
                FROM (VALUES
                    ('beauty-1', true, true),
                    ('beauty-2', false, false),
                    ('beauty-3', false, false),
                    ('beauty-4', false, true),
                    ('beauty-5', false, false),
                    ('beauty-6', false, false),
                    ('beauty-7', true, true),
                    ('beauty-8', false, false),
                    ('beauty-9', false, false),
                    ('electronics-1', false, true),
                    ('electronics-2', false, false),
                    ('electronics-3', false, false),
                    ('electronics-4', true, true),
                    ('electronics-5', false, false),
                    ('electronics-6', false, false),
                    ('electronics-7', false, true),
                    ('electronics-8', false, false),
                    ('electronics-9', false, false),
                    ('fashion-1', true, true),
                    ('fashion-2', false, false),
                    ('fashion-3', false, false),
                    ('fashion-4', false, true),
                    ('fashion-5', false, false),
                    ('fashion-6', false, false),
                    ('fashion-7', true, true),
                    ('fashion-8', false, false),
                    ('fashion-9', false, false),
                    ('home-1', false, true),
                    ('home-2', false, false),
                    ('home-3', false, false),
                    ('home-4', true, true),
                    ('home-5', false, false),
                    ('home-6', false, false),
                    ('home-7', false, true),
                    ('home-8', false, false),
                    ('home-9', false, false),
                    ('accessories-1', true, true),
                    ('accessories-2', false, false),
                    ('accessories-3', false, false),
                    ('accessories-4', false, true),
                    ('accessories-5', false, false),
                    ('accessories-6', false, false),
                    ('accessories-7', true, true),
                    ('accessories-8', false, false),
                    ('accessories-9', false, false),
                    ('gifts-1', false, true),
                    ('gifts-2', false, false),
                    ('gifts-3', false, false),
                    ('gifts-4', true, true),
                    ('gifts-5', false, false),
                    ('gifts-6', false, false),
                    ('gifts-7', false, true),
                    ('gifts-8', false, false),
                    ('gifts-9', false, false)
                ) AS v("Id", "IsNew", "GiftWrappable")
                WHERE p."Id" = v."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GiftWrappable",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsNew",
                table: "Products");
        }
    }
}
