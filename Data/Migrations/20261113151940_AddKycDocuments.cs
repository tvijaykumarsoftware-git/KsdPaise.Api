using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KsdPaise.Api.Data.Migrations;

public partial class AddKycDocuments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "KycDocuments",
            columns: table => new
            {
                KycDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BorrowerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DocumentType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                OtherDocumentType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                StorageFileName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KycDocuments", document => document.KycDocumentId);
                table.ForeignKey(
                    name: "FK_KycDocuments_Borrowers_BorrowerId",
                    column: document => document.BorrowerId,
                    principalTable: "Borrowers",
                    principalColumn: "BorrowerId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_KycDocuments_BorrowerId_UploadedAt",
            table: "KycDocuments",
            columns: new[] { "BorrowerId", "UploadedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "KycDocuments");
    }
}
