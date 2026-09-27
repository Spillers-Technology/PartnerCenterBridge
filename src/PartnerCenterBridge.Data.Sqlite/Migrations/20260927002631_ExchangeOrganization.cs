using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class ExchangeOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExchangeOrganization",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExchangeOrganizationVerifiedAt",
                table: "Tenants",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExchangeOrganization",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ExchangeOrganizationVerifiedAt",
                table: "Tenants");
        }
    }
}
