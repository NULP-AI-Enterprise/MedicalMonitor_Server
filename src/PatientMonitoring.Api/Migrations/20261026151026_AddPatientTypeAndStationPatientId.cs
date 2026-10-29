using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PatientMonitoring.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientTypeAndStationPatientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PatientType",
                table: "Patients",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "Adu");

            migrationBuilder.AddColumn<string>(
                name: "StationPatientId",
                table: "Patients",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PatientType",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "StationPatientId",
                table: "Patients");
        }
    }
}
