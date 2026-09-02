using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PageForge.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OcrJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<int>(type: "integer", nullable: false),
                    TargetFormat = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PagesProcessed = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcrJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OcrJobs_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OcrUsages",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PagesProcessed = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcrUsages", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "OcrJobItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OcrJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PagesProcessed = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcrJobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OcrJobItems_DocumentVersions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "DocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OcrJobItems_OcrJobs_OcrJobId",
                        column: x => x.OcrJobId,
                        principalTable: "OcrJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OcrJobItems_DocumentVersionId",
                table: "OcrJobItems",
                column: "DocumentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_OcrJobItems_OcrJobId_DocumentVersionId",
                table: "OcrJobItems",
                columns: new[] { "OcrJobId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_OcrJobItems_Status",
                table: "OcrJobItems",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OcrJobs_IdempotencyKey",
                table: "OcrJobs",
                column: "IdempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_OcrJobs_OwnerId",
                table: "OcrJobs",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OcrJobs_Status",
                table: "OcrJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OcrJobItems");

            migrationBuilder.DropTable(
                name: "OcrUsages");

            migrationBuilder.DropTable(
                name: "OcrJobs");
        }
    }
}
