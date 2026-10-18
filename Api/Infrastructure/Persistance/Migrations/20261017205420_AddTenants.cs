using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class AddTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProvisionedServices_Name",
                schema: "provisioning_service",
                table: "ProvisionedServices");

            migrationBuilder.AddColumn<string>(
                name: "Tenant",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "UserTenants",
                schema: "provisioning_service",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tenant = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTenants", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedServices_Tenant_Name",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                columns: new[] { "Tenant", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTenants_Tenant",
                schema: "provisioning_service",
                table: "UserTenants",
                column: "Tenant",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTenants",
                schema: "provisioning_service");

            migrationBuilder.DropIndex(
                name: "IX_ProvisionedServices_Tenant_Name",
                schema: "provisioning_service",
                table: "ProvisionedServices");

            migrationBuilder.DropColumn(
                name: "Tenant",
                schema: "provisioning_service",
                table: "ProvisionedServices");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedServices_Name",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                column: "Name",
                unique: true);
        }
    }
}
