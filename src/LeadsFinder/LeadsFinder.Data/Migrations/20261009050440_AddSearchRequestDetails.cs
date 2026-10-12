using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LeadsFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchRequestDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "search_jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<string>(
                name: "PlaceType",
                table: "search_jobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "any_lodging");

            migrationBuilder.AddColumn<bool>(
                name: "RequireAddress",
                table: "search_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireEmail",
                table: "search_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequirePhone",
                table: "search_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShortagePolicy",
                table: "search_jobs",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "return_fewer");

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAtUtc",
                table: "search_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetCount",
                table: "search_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.Sql("UPDATE search_jobs SET \"CreatedAtUtc\" = \"ScheduledAtUtc\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "PlaceType",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "RequireAddress",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "RequireEmail",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "RequirePhone",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "ShortagePolicy",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "search_jobs");

            migrationBuilder.DropColumn(
                name: "TargetCount",
                table: "search_jobs");
        }
    }
}
