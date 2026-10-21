using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Infrastructure.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class PaymentBeforeProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "StripeSubscriptionId",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                type: "timestamp with time zone",
                nullable: false,
                // Existing rows count as just updated, so the reconciler does not resend all their commands at once.
                defaultValueSql: "now()");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedServices_ProvisioningStatus_UpdatedAt",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                columns: new[] { "ProvisioningStatus", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProvisionedServices_ProvisioningStatus_UpdatedAt",
                schema: "provisioning_service",
                table: "ProvisionedServices");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "provisioning_service",
                table: "ProvisionedServices");

            migrationBuilder.AlterColumn<string>(
                name: "StripeSubscriptionId",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldNullable: true);
        }
    }
}
