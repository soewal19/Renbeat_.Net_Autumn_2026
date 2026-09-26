using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RoomBooking.Server.Infrastructure.Persistence;

#nullable disable

namespace RoomBooking.Server.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260926120000_AddAiBookingSource")]
public sealed class AddAiBookingSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<bool>(
        name: "IsAiGenerated", table: "Bookings", type: "bit", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "IsAiGenerated", table: "Bookings");
}
