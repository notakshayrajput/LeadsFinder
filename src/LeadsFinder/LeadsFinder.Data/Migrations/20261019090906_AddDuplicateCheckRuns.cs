using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadsFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateCheckRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "duplicate_check_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SearchJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ReportJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_duplicate_check_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_duplicate_check_runs_search_jobs_SearchJobId",
                        column: x => x.SearchJobId,
                        principalTable: "search_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_duplicate_check_runs_SearchJobId_CreatedAtUtc",
                table: "duplicate_check_runs",
                columns: new[] { "SearchJobId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "duplicate_check_runs");
        }
    }
}
