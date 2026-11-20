using KsdPaise.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KsdPaise.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261119194000_AddLoanPayments")]
public partial class AddLoanPayments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LoanPayments",
            columns: table => new
            {
                LoanPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                InstallmentNumber = table.Column<int>(type: "int", nullable: false),
                RazorpayOrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                RazorpayPaymentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                PaidAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LoanPayments", payment => payment.LoanPaymentId);
                table.ForeignKey(
                    name: "FK_LoanPayments_LoanApplications_ApplicationId",
                    column: payment => payment.ApplicationId,
                    principalTable: "LoanApplications",
                    principalColumn: "ApplicationId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LoanPayments_ApplicationId_InstallmentNumber",
            table: "LoanPayments",
            columns: new[] { "ApplicationId", "InstallmentNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LoanPayments_RazorpayOrderId",
            table: "LoanPayments",
            column: "RazorpayOrderId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LoanPayments_RazorpayPaymentId",
            table: "LoanPayments",
            column: "RazorpayPaymentId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "LoanPayments");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) =>
        new SnapshotModelBuilder().Build(modelBuilder);

    private sealed class SnapshotModelBuilder : AppDbContextModelSnapshot
    {
        public void Build(ModelBuilder modelBuilder) => BuildModel(modelBuilder);
    }
}
