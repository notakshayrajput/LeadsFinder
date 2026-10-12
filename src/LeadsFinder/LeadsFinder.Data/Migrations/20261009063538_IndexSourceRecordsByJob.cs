using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadsFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexSourceRecordsByJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_source_records_SearchJobId",
                table: "source_records",
                column: "SearchJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_source_records_SearchJobId",
                table: "source_records");
        }
    }
}
