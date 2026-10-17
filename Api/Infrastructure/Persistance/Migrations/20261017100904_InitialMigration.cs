using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
                name: "Services",
                schema: "provisioning_service",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DockerImageName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StripePriceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProvisionedServices",
                schema: "provisioning_service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ServiceName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ServiceVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StripeSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProvisioningStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionedServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisionedServices_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "provisioning_service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceVersions",
                schema: "provisioning_service",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServiceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceVersions_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "provisioning_service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisionedServices_ServiceId",
                schema: "provisioning_service",
                table: "ProvisionedServices",
                column: "ServiceId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Services_DockerImageName",
                schema: "provisioning_service",
                table: "Services",
                column: "DockerImageName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceVersions_ServiceId_Version",
                schema: "provisioning_service",
                table: "ServiceVersions",
                columns: new[] { "ServiceId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisionedServices",
                schema: "provisioning_service");

            migrationBuilder.DropTable(
                name: "ServiceVersions",
                schema: "provisioning_service");

            migrationBuilder.DropTable(
                name: "Services",
                schema: "provisioning_service");
        }
    }
}
