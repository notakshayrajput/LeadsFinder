using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadsFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "search_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "search_jobs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntilUtc",
                table: "search_jobs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "LeaseUntilUtc",
                table: "search_jobs");
        }
    }
}
