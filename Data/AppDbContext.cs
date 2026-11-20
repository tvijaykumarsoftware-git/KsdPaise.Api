using KsdPaise.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KsdPaise.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Borrower> Borrowers => Set<Borrower>();
    public DbSet<KycVerification> KycVerifications => Set<KycVerification>();
    public DbSet<KycDocument> KycDocuments => Set<KycDocument>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<LoanDocument> LoanDocuments => Set<LoanDocument>();
    public DbSet<LoanApplication> LoanApplications => Set<LoanApplication>();
    public DbSet<LoanPayment> LoanPayments => Set<LoanPayment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Borrower>()
            .HasIndex(b => b.MobileNumber)
            .IsUnique();

        modelBuilder.Entity<LoanApplication>()
            .HasIndex(l => l.ApplicationNumber)
            .IsUnique();

        modelBuilder.Entity<KycDocument>()
            .HasOne(document => document.Borrower)
            .WithMany(borrower => borrower.KycDocuments)
            .HasForeignKey(document => document.BorrowerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<KycDocument>()
            .HasIndex(document => new { document.BorrowerId, document.UploadedAt });

        modelBuilder.Entity<LoanPayment>()
            .HasOne(payment => payment.Application)
            .WithMany(application => application.Payments)
            .HasForeignKey(payment => payment.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LoanPayment>()
            .HasIndex(payment => new { payment.ApplicationId, payment.InstallmentNumber })
            .IsUnique();

        modelBuilder.Entity<LoanPayment>()
            .HasIndex(payment => payment.RazorpayOrderId)
            .IsUnique();

        modelBuilder.Entity<LoanPayment>()
            .HasIndex(payment => payment.RazorpayPaymentId)
            .IsUnique();
    }
}