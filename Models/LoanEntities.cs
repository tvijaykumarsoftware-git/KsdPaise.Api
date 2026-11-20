using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KsdPaise.Api.Models;

public class Borrower
{
    [Key]
    public Guid BorrowerId { get; set; } = Guid.NewGuid();
    [Required, MaxLength(15)]
    public string MobileNumber { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public bool IsMobileVerified { get; set; }
    public DateTime? ConsentTimestamp { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public KycVerification? KycVerification { get; set; }
    public ICollection<KycDocument> KycDocuments { get; set; } = new List<KycDocument>();
    public ICollection<BankAccount> BankAccounts { get; set; } = new List<BankAccount>();
    public ICollection<LoanDocument> LoanDocuments { get; set; } = new List<LoanDocument>();
    public ICollection<LoanApplication> LoanApplications { get; set; } = new List<LoanApplication>();
}

public class KycVerification
{
    [Key]
    public Guid KycId { get; set; } = Guid.NewGuid();
    public Guid BorrowerId { get; set; }
    [Required, MaxLength(10)]
    public string PanNumber { get; set; } = string.Empty;
    public bool IsPanVerified { get; set; }
    public string? IdentityHash { get; set; }
    [MaxLength(4)]
    public string? MaskedIdentityLast4 { get; set; }
    public bool IsDigiLockerVerified { get; set; }
    public string? DigiLockerTxnId { get; set; }
    public int? CibilScore { get; set; }
    public string? SelfieStorageUrl { get; set; }
    [Column(TypeName = "decimal(5,2)")]
    public decimal? FaceMatchScore { get; set; }
    public bool IsLivenessPassed { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

public class KycDocument
{
    [Key]
    public Guid KycDocumentId { get; set; } = Guid.NewGuid();
    public Guid BorrowerId { get; set; }
    [Required, MaxLength(32)]
    public string DocumentType { get; set; } = string.Empty;
    [MaxLength(32)]
    public string? OtherDocumentType { get; set; }
    [Required, MaxLength(255)]
    public string OriginalFileName { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string StorageFileName { get; set; } = string.Empty;
    [Required, MaxLength(128)]
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public Borrower Borrower { get; set; } = null!;
}

public class BankAccount
{
    [Key]
    public Guid BankAccountId { get; set; } = Guid.NewGuid();
    public Guid BorrowerId { get; set; }
    [Required]
    public string AccountHolderName { get; set; } = string.Empty;
    [Required]
    public string AccountNumber { get; set; } = string.Empty;
    [Required, MaxLength(11)]
    public string IfscCode { get; set; } = string.Empty;
    [Required]
    public string BankName { get; set; } = string.Empty;
    public string? PennyDropUtr { get; set; }
    public bool IsPennyDropVerified { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

public class LoanDocument
{
    [Key]
    public Guid DocumentId { get; set; } = Guid.NewGuid();
    public Guid BorrowerId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ExtractedMonthlyIncome { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ExtractedExistingEmi { get; set; }
    public string? EmployerName { get; set; }
    public string OcrStatus { get; set; } = "PENDING";
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public class LoanApplication
{
    [Key]
    public Guid ApplicationId { get; set; } = Guid.NewGuid();
    [Required]
    public string ApplicationNumber { get; set; } = string.Empty;
    public Guid BorrowerId { get; set; }
    public string PartnerNbfcName { get; set; } = "K$DPaise Partner Capital NBFC Ltd.";
    [Column(TypeName = "decimal(18,2)")]
    public decimal RequestedAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? SanctionedAmount { get; set; }
    public int TenureMonths { get; set; }
    [Column(TypeName = "decimal(5,2)")]
    public decimal AnnualInterestRate { get; set; }
    [Column(TypeName = "decimal(5,2)")]
    public decimal ProcessingFeePercent { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal GstAmount { get; set; }
    [Column(TypeName = "decimal(5,2)")]
    public decimal AnnualPercentageRate { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyEmi { get; set; }
    public int CoolingOffDays { get; set; } = 3;
    public bool IsESigned { get; set; }
    public string? ESignTransactionId { get; set; }
    public DateTime? ESignedAt { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string? DisbursementUtr { get; set; }
    public DateTime? DisbursedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<LoanPayment> Payments { get; set; } = new List<LoanPayment>();
}

public class LoanPayment
{
    [Key]
    public Guid LoanPaymentId { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public int InstallmentNumber { get; set; }
    [Required, MaxLength(64)]
    public string RazorpayOrderId { get; set; } = string.Empty;
    [Required, MaxLength(64)]
    public string RazorpayPaymentId { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [Required, MaxLength(3)]
    public string Currency { get; set; } = "INR";
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public LoanApplication Application { get; set; } = null!;
}