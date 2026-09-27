using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PartnerCenterBridge.Data.Migrations
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
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExchangeOrganizationVerifiedAt",
                table: "Tenants",
                type: "timestamp with time zone",
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
