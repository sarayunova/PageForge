using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PageForge.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrOutput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutputContentType",
                table: "OcrJobItems",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputFileName",
                table: "OcrJobItems",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OutputVersionId",
                table: "OcrJobItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OcrJobItems_OutputVersionId",
                table: "OcrJobItems",
                column: "OutputVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_OcrJobItems_DocumentVersions_OutputVersionId",
                table: "OcrJobItems",
                column: "OutputVersionId",
                principalTable: "DocumentVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OcrJobItems_DocumentVersions_OutputVersionId",
                table: "OcrJobItems");

            migrationBuilder.DropIndex(
                name: "IX_OcrJobItems_OutputVersionId",
                table: "OcrJobItems");

            migrationBuilder.DropColumn(
                name: "OutputContentType",
                table: "OcrJobItems");

            migrationBuilder.DropColumn(
                name: "OutputFileName",
                table: "OcrJobItems");

            migrationBuilder.DropColumn(
                name: "OutputVersionId",
                table: "OcrJobItems");
        }
    }
}
