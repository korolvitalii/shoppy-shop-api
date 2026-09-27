using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShoppyShop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshSessionExpiryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RefreshSessions_ExpiresAt",
                table: "RefreshSessions",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshSessions_ExpiresAt",
                table: "RefreshSessions");
        }
    }
}
