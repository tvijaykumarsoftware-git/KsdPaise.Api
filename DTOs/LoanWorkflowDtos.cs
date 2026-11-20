using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace KsdPaise.Api.DTOs;

public record SendOtpRequest(string MobileNumber);
public record VerifyOtpRequest(string MobileNumber, string Otp, bool ConsentAccepted);
public record KycRequest(Guid BorrowerId, string PanNumber, string IdentityNumber, string FullName, string Email);
public record BankVerifyRequest(Guid BorrowerId, string AccountHolderName, string AccountNumber, string IfscCode, string BankName);
public record DocumentOcrRequest(Guid BorrowerId, string DocumentType, string FileBase64OrUrl, decimal MonthlyIncome, decimal ExistingEmi, string EmployerName);
public record SelfieVerifyRequest(Guid BorrowerId, string SelfieBase64, decimal FaceMatchScore);
public record EligibilityRequest(Guid BorrowerId, decimal RequestedAmount, int TenureMonths);
public record ESignRequest(Guid ApplicationId, string ESignOtp);
public record RazorpayOrderRequest(Guid ApplicationId, int InstallmentNumber);
public record RazorpayPaymentVerificationRequest(
    Guid ApplicationId,
    int InstallmentNumber,
    string RazorpayOrderId,
    string RazorpayPaymentId,
    string RazorpaySignature);

public class KycDocumentUploadRequest
{
    [Required]
    public Guid BorrowerId { get; set; }

    [Required]
    public string DocumentType { get; set; } = string.Empty;

    public string? OtherDocumentType { get; set; }

    [Required]
    public IFormFile? File { get; set; }
}