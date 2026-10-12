using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadsFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueSourceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_source_records_SearchJobId_Strategy_SourceId",
                table: "source_records");

            migrationBuilder.CreateIndex(
                name: "IX_source_records_Strategy_SourceId",
                table: "source_records",
                columns: new[] { "Strategy", "SourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_source_records_Strategy_SourceId",
                table: "source_records");

            migrationBuilder.CreateIndex(
                name: "IX_source_records_SearchJobId_Strategy_SourceId",
                table: "source_records",
                columns: new[] { "SearchJobId", "Strategy", "SourceId" },
                unique: true);
        }
    }
}
