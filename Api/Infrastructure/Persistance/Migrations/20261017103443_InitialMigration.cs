using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "provisioning_service");

            migrationBuilder.CreateTable(
                name: "ProvisionedServices",
                schema: "provisioning_service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    StripeSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProvisioningStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionedServices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedServices_Name",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedServices_StripeSubscriptionId",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                column: "StripeSubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedServices_UserId",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisionedServices",
                schema: "provisioning_service");
        }
    }
}
