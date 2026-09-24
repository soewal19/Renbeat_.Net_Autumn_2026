using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RoomBooking.Server.Migrations;

/// <inheritdoc />
public partial class AddAiSkills : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AiSkills",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                InstructionsJson = table.Column<string>(type: "nvarchar(12000)", maxLength: 12000, nullable: false),
                ExamplesJson = table.Column<string>(type: "nvarchar(8000)", maxLength: 8000, nullable: false),
                Version = table.Column<int>(type: "int", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AiSkills", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_AiSkills_IsActive", table: "AiSkills", column: "IsActive");
        migrationBuilder.CreateIndex(name: "IX_AiSkills_Name", table: "AiSkills", column: "Name", unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "AiSkills");
}
