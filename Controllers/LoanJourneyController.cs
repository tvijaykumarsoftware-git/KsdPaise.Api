using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;
using KsdPaise.Api.Data;
using KsdPaise.Api.DTOs;
using KsdPaise.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace KsdPaise.Api.Controllers;

[ApiController]
[Route("api/v1/loan")]
public class LoanJourneyController : ControllerBase
{
    private const long MaxKycDocumentSizeBytes = 5 * 1024 * 1024;
    private const long MaxKycUploadRequestSizeBytes = MaxKycDocumentSizeBytes + 64 * 1024;
    private static readonly HashSet<string> KycDocumentTypes = new(StringComparer.Ordinal)
    {
        "PAN_CARD",
        "AADHAAR_CARD",
        "OTHER"
    };
    private static readonly HashSet<string> OtherDocumentTypes = new(StringComparer.Ordinal)
    {
        "ELECTRICITY_BILL",
        "GAS_BILL",
        "RENT_AGREEMENT",
        "HOUSE_PROPERTY_BILL",
        "WATER_BILL"
    };

    private readonly AppDbContext _db;
    private readonly string _privateKycStoragePath;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LoanJourneyController> _logger;

    public LoanJourneyController(
        AppDbContext db,
        IWebHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LoanJourneyController> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _privateKycStoragePath = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, "App_Data", "PrivateKycDocuments"));
    }

    // STEP 1: Mobile OTP Login / Sign Up
    [HttpPost("auth/send-otp")]
    public IActionResult SendOtp([FromBody] SendOtpRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.MobileNumber) ||
            req.MobileNumber.Length != 10 ||
            !req.MobileNumber.All(char.IsDigit))
            return BadRequest(new { message = "Enter a valid 10-digit mobile number." });

        // Integrate SMS Gateway (Twilio/MSG91) here
        return Ok(new { message = "OTP sent successfully", demoOtp = "123456" });
    }

    [HttpPost("auth/verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.MobileNumber) ||
            req.MobileNumber.Length != 10 ||
            !req.MobileNumber.All(char.IsDigit))
            return BadRequest(new { message = "Enter a valid 10-digit mobile number." });

        if (!req.ConsentAccepted)
            return BadRequest(new { message = "Consent is required to continue." });

        if (req.Otp != "123456")
            return Unauthorized(new { message = "Invalid OTP." });

        var borrower = await _db.Borrowers.FirstOrDefaultAsync(b => b.MobileNumber == req.MobileNumber);
        if (borrower == null)
        {
            borrower = new Borrower
            {
                MobileNumber = req.MobileNumber,
                IsMobileVerified = true,
                ConsentTimestamp = DateTime.UtcNow
            };
            _db.Borrowers.Add(borrower);
        }
        else
        {
            borrower.IsMobileVerified = true;
            borrower.ConsentTimestamp = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        var kyc = await _db.KycVerifications
            .FirstOrDefaultAsync(record => record.BorrowerId == borrower.BorrowerId);
        var bankAccount = await _db.BankAccounts
            .Where(record => record.BorrowerId == borrower.BorrowerId)
            .OrderByDescending(record => record.VerifiedAt)
            .FirstOrDefaultAsync();
        var incomeDocument = await _db.LoanDocuments
            .Where(record => record.BorrowerId == borrower.BorrowerId)
            .OrderByDescending(record => record.UploadedAt)
            .FirstOrDefaultAsync();
        var application = await _db.LoanApplications
            .Where(record => record.BorrowerId == borrower.BorrowerId)
            .OrderByDescending(record => record.CreatedAt)
            .FirstOrDefaultAsync();
        var kycDocuments = await _db.KycDocuments
            .Where(document => document.BorrowerId == borrower.BorrowerId)
            .OrderByDescending(document => document.UploadedAt)
            .Select(document => new
            {
                document.KycDocumentId,
                document.DocumentType,
                document.OtherDocumentType,
                FileName = document.OriginalFileName,
                document.ContentType,
                document.FileSizeBytes,
                document.UploadedAt
            })
            .ToListAsync();

        var completedSteps = new List<int>();
        var repaymentSchedule = application?.IsESigned == true && application.DisbursedAt.HasValue
            ? BuildRepaymentSchedule(
                application.SanctionedAmount ?? 0m,
                application.MonthlyEmi,
                application.AnnualInterestRate,
                application.TenureMonths,
                application.DisbursedAt.Value)
            : new List<RepaymentScheduleItem>();
        var paidInstallments = application == null
            ? new List<int>()
            : await GetPaidInstallmentsAsync(application.ApplicationId, HttpContext.RequestAborted);

        if (kyc?.IsPanVerified == true && kyc.IsDigiLockerVerified)
            completedSteps.Add(2);
        if (bankAccount?.IsPennyDropVerified == true)
            completedSteps.Add(3);
        if (incomeDocument?.OcrStatus == "VERIFIED")
            completedSteps.Add(4);
        if (kyc?.IsLivenessPassed == true)
            completedSteps.Add(5);
        if (application != null)
            completedSteps.Add(6);
        if (application?.IsESigned == true)
            completedSteps.Add(7);

        return Ok(new
        {
            borrower.BorrowerId,
            borrower.MobileNumber,
            borrower.FullName,
            Profile = new
            {
                FullName = borrower.FullName,
                Email = borrower.Email,
                PanNumber = kyc?.PanNumber,
                IdentityLast4 = kyc?.MaskedIdentityLast4,
                AccountHolderName = bankAccount?.AccountHolderName,
                AccountNumber = bankAccount?.AccountNumber,
                IfscCode = bankAccount?.IfscCode,
                BankName = bankAccount?.BankName,
                MonthlyIncome = incomeDocument?.ExtractedMonthlyIncome,
                ExistingEmi = incomeDocument?.ExtractedExistingEmi,
                EmployerName = incomeDocument?.EmployerName
            },
            CompletedSteps = completedSteps,
            Application = application,
            RepaymentSchedule = repaymentSchedule,
            PaidInstallments = paidInstallments,
            KycDocuments = kycDocuments,
            Message = "Authenticated"
        });
    }

    [HttpPost("kyc/documents")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxKycUploadRequestSizeBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxKycUploadRequestSizeBytes)]
    public async Task<IActionResult> UploadKycDocument([FromForm] KycDocumentUploadRequest req)
    {
        if (req.BorrowerId == Guid.Empty ||
            !await _db.Borrowers.AnyAsync(borrower => borrower.BorrowerId == req.BorrowerId))
            return NotFound(new { message = "Borrower not found." });

        if (!KycDocumentTypes.Contains(req.DocumentType))
            return BadRequest(new { message = "Select a valid KYC document type." });

        if (req.DocumentType == "OTHER" &&
            (string.IsNullOrWhiteSpace(req.OtherDocumentType) ||
             !OtherDocumentTypes.Contains(req.OtherDocumentType)))
            return BadRequest(new { message = "Select a valid address-proof document type." });

        if (req.File == null || req.File.Length == 0)
            return BadRequest(new { message = "Choose a document file to upload." });

        if (req.File.Length > MaxKycDocumentSizeBytes)
            return BadRequest(new { message = "KYC documents must be 5 MB or smaller." });

        var originalFileName = Path.GetFileName(req.File.FileName);
        if (originalFileName.Length > 255)
            return BadRequest(new { message = "The selected file name is too long." });

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!await IsSupportedKycFileAsync(req.File, extension))
            return BadRequest(new { message = "Upload a valid PDF, JPEG, or PNG document." });

        Directory.CreateDirectory(_privateKycStoragePath);
        var storageFileName = $"{Guid.NewGuid():N}{extension}";
        var storagePath = Path.Combine(_privateKycStoragePath, storageFileName);

        await using (var output = new FileStream(
            storagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            await req.File.CopyToAsync(output);
        }

        var document = new KycDocument
        {
            BorrowerId = req.BorrowerId,
            DocumentType = req.DocumentType,
            OtherDocumentType = req.DocumentType == "OTHER" ? req.OtherDocumentType : null,
            OriginalFileName = originalFileName,
            StorageFileName = storageFileName,
            ContentType = req.File.ContentType,
            FileSizeBytes = req.File.Length
        };
        _db.KycDocuments.Add(document);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            System.IO.File.Delete(storagePath);
            throw;
        }

        return Ok(new
        {
            document.KycDocumentId,
            document.DocumentType,
            document.OtherDocumentType,
            FileName = document.OriginalFileName,
            document.ContentType,
            document.FileSizeBytes,
            document.UploadedAt
        });
    }

    private static async Task<bool> IsSupportedKycFileAsync(IFormFile file, string extension)
    {
        var header = new byte[8];
        await using var stream = file.OpenReadStream();
        var bytesRead = await stream.ReadAsync(header.AsMemory());

        return extension switch
        {
            ".pdf" => bytesRead >= 5 &&
                      file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) &&
                      header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 &&
                      header[3] == 0x46 && header[4] == 0x2D,
            ".jpg" or ".jpeg" => bytesRead >= 3 &&
                                 file.ContentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) &&
                                 header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => bytesRead == 8 &&
                      file.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) &&
                      header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E &&
                      header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A &&
                      header[6] == 0x1A && header[7] == 0x0A,
            _ => false
        };
    }

    // STEP 2 & 3: DigiLocker + PAN / National ID + Soft Bureau Check
    [HttpPost("kyc/verify")]
    public async Task<IActionResult> VerifyKyc([FromBody] KycRequest req)
    {
        var borrower = await _db.Borrowers.FindAsync(req.BorrowerId);
        if (borrower == null) return NotFound("Borrower not found.");

        borrower.FullName = req.FullName;
        borrower.Email = req.Email;

        // Hash sensitive government ID before persistence
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(req.IdentityNumber + borrower.BorrowerId));
        var hashedId = Convert.ToHexString(hashBytes);
        var last4 = req.IdentityNumber.Length >= 4 ? req.IdentityNumber[^4..] : "XXXX";

        var kyc = await _db.KycVerifications.FirstOrDefaultAsync(k => k.BorrowerId == req.BorrowerId);
        if (kyc == null)
        {
            kyc = new KycVerification { BorrowerId = req.BorrowerId };
            _db.KycVerifications.Add(kyc);
        }

        kyc.PanNumber = req.PanNumber.ToUpperInvariant();
        kyc.IsPanVerified = true;
        kyc.IdentityHash = hashedId;
        kyc.MaskedIdentityLast4 = last4;
        kyc.IsDigiLockerVerified = true;
        kyc.DigiLockerTxnId = $"DL-{Guid.NewGuid().ToString("N")[..10].ToUpper()}";
        kyc.CibilScore = 768; // Simulated CIBIL/Experian Soft Pull
        kyc.VerifiedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new
        {
            kyc.KycId,
            kyc.PanNumber,
            kyc.MaskedIdentityLast4,
            kyc.DigiLockerTxnId,
            kyc.CibilScore,
            Status = "VERIFIED"
        });
    }

    // STEP 4: Bank Penny Drop Verification
    [HttpPost("bank/penny-drop")]
    public async Task<IActionResult> VerifyBankPennyDrop([FromBody] BankVerifyRequest req)
    {
        var bank = new BankAccount
        {
            BorrowerId = req.BorrowerId,
            AccountHolderName = req.AccountHolderName,
            AccountNumber = req.AccountNumber,
            IfscCode = req.IfscCode.ToUpperInvariant(),
            BankName = req.BankName,
            PennyDropUtr = $"IMPS{Random.Shared.Next(10000000, 99999999)}",
            IsPennyDropVerified = true,
            VerifiedAt = DateTime.UtcNow
        };

        _db.BankAccounts.Add(bank);
        await _db.SaveChangesAsync();
        return Ok(bank);
    }

    // STEP 5: Document Upload & OCR Income Extraction
    [HttpPost("documents/verify-ocr")]
    public async Task<IActionResult> VerifyDocumentOcr([FromBody] DocumentOcrRequest req)
    {
        var doc = new LoanDocument
        {
            BorrowerId = req.BorrowerId,
            DocumentType = req.DocumentType,
            FileUrl = req.FileBase64OrUrl,
            ExtractedMonthlyIncome = req.MonthlyIncome,
            ExtractedExistingEmi = req.ExistingEmi,
            EmployerName = req.EmployerName,
            OcrStatus = "VERIFIED"
        };

        _db.LoanDocuments.Add(doc);
        await _db.SaveChangesAsync();
        return Ok(doc);
    }

    // STEP 6: Selfie Liveness & AI Face Match
    [HttpPost("kyc/selfie")]
    public async Task<IActionResult> VerifySelfie([FromBody] SelfieVerifyRequest req)
    {
        var kyc = await _db.KycVerifications.FirstOrDefaultAsync(k => k.BorrowerId == req.BorrowerId);
        if (kyc == null) return BadRequest("Complete KYC first.");

        kyc.SelfieStorageUrl = $"https://storage.ksdpaise.in/selfies/{req.BorrowerId}.jpg";
        kyc.FaceMatchScore = req.FaceMatchScore;
        kyc.IsLivenessPassed = req.FaceMatchScore >= 85.0m;

        await _db.SaveChangesAsync();
        return Ok(new { kyc.IsLivenessPassed, kyc.FaceMatchScore });
    }

    // STEP 7: Underwriting & Loan Eligibility + Key Fact Statement (KFS)
    [HttpPost("eligibility/calculate")]
    public async Task<IActionResult> CalculateEligibility([FromBody] EligibilityRequest req)
    {
        var kyc = await _db.KycVerifications.FirstOrDefaultAsync(k => k.BorrowerId == req.BorrowerId);
        var doc = await _db.LoanDocuments
            .Where(d => d.BorrowerId == req.BorrowerId)
            .OrderByDescending(d => d.UploadedAt)
            .FirstOrDefaultAsync();

        decimal monthlyIncome = doc?.ExtractedMonthlyIncome ?? 65000m;
        decimal existingEmi = doc?.ExtractedExistingEmi ?? 5000m;
        int cibil = kyc?.CibilScore ?? 750;

        // FOIR (Fixed Obligation to Income Ratio) Underwriting Engine (Max 55% FOIR)
        decimal maxAllowedEmi = (monthlyIncome * 0.55m) - existingEmi;
        decimal annualRate = cibil >= 750 ? 16.50m : 19.75m;
        decimal monthlyRate = annualRate / 12m / 100m;

        double r = (double)monthlyRate;
        int n = req.TenureMonths;
        decimal emiFactor = (decimal)((r * Math.Pow(1 + r, n)) / (Math.Pow(1 + r, n) - 1));

        decimal maxEligiblePrincipal = Math.Min(1000000m, Math.Round(maxAllowedEmi / emiFactor, 0));
        decimal sanctionedAmount = Math.Min(req.RequestedAmount, maxEligiblePrincipal);
        decimal finalEmi = Math.Round(sanctionedAmount * emiFactor, 2);

        decimal pfPercent = 2.0m;
        decimal pfAmount = Math.Round(sanctionedAmount * (pfPercent / 100m), 2);
        decimal gst = Math.Round(pfAmount * 0.18m, 2);
        decimal apr = Math.Round(annualRate + 1.45m, 2);

        var application = new LoanApplication
        {
            ApplicationNumber = $"KSD-{DateTime.UtcNow.Year}-{Random.Shared.Next(100000, 999999)}",
            BorrowerId = req.BorrowerId,
            RequestedAmount = req.RequestedAmount,
            SanctionedAmount = sanctionedAmount,
            TenureMonths = req.TenureMonths,
            AnnualInterestRate = annualRate,
            ProcessingFeePercent = pfPercent,
            GstAmount = gst,
            AnnualPercentageRate = apr,
            MonthlyEmi = finalEmi,
            Status = "OFFER_GENERATED"
        };

        _db.LoanApplications.Add(application);
        await _db.SaveChangesAsync();

        var repaymentSchedule = BuildRepaymentSchedule(
            application.SanctionedAmount ?? 0m,
            application.MonthlyEmi,
            application.AnnualInterestRate,
            application.TenureMonths,
            application.CreatedAt);

        return Ok(new
        {
            application.ApplicationId,
            application.ApplicationNumber,
            application.BorrowerId,
            application.PartnerNbfcName,
            application.RequestedAmount,
            application.SanctionedAmount,
            application.TenureMonths,
            application.AnnualInterestRate,
            application.ProcessingFeePercent,
            application.GstAmount,
            application.AnnualPercentageRate,
            application.MonthlyEmi,
            application.CoolingOffDays,
            application.IsESigned,
            application.ESignTransactionId,
            application.ESignedAt,
            application.Status,
            application.DisbursementUtr,
            application.DisbursedAt,
            application.CreatedAt,
            repaymentSchedule
        });
    }

    // STEP 8 & 9: e-Sign Verification & Sanction Letter Generation
    [HttpPost("esign/complete")]
    public async Task<IActionResult> CompleteESign([FromBody] ESignRequest req)
    {
        if (req.ESignOtp != "123456")
            return BadRequest(new { message = "Invalid e-Sign OTP." });

        var app = await _db.LoanApplications.FindAsync(req.ApplicationId);
        if (app == null) return NotFound("Application not found.");

        if (!app.IsESigned)
        {
            app.IsESigned = true;
            app.ESignTransactionId = $"ESIGN-{Guid.NewGuid().ToString("N")[..12].ToUpper()}";
            app.ESignedAt = DateTime.UtcNow;
            app.Status = "SANCTIONED";
            app.DisbursementUtr = $"UTR{DateTime.UtcNow:yyyyMMdd}{Random.Shared.Next(10000, 99999)}";
            app.DisbursedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            await SendSanctionEmailAsync(app);
        }
        if (app.DisbursedAt is not DateTime disbursedAt)
            return Conflict(new { message = "The loan has no recorded disbursement date." });

        var repaymentSchedule = BuildRepaymentSchedule(
            app.SanctionedAmount ?? 0m,
            app.MonthlyEmi,
            app.AnnualInterestRate,
            app.TenureMonths,
            disbursedAt);
        var paidInstallments = await GetPaidInstallmentsAsync(app.ApplicationId, HttpContext.RequestAborted);

        return Ok(new
        {
            app.ApplicationId,
            app.ApplicationNumber,
            app.BorrowerId,
            app.PartnerNbfcName,
            app.RequestedAmount,
            app.SanctionedAmount,
            app.TenureMonths,
            app.AnnualInterestRate,
            app.ProcessingFeePercent,
            app.GstAmount,
            app.AnnualPercentageRate,
            app.MonthlyEmi,
            app.CoolingOffDays,
            app.IsESigned,
            app.ESignTransactionId,
            app.ESignedAt,
            app.Status,
            app.DisbursementUtr,
            app.DisbursedAt,
            app.CreatedAt,
            repaymentSchedule,
            paidInstallments
        });
    }

    [HttpPost("payments/razorpay/order")]
    public async Task<IActionResult> CreateRazorpayOrder(
        [FromBody] RazorpayOrderRequest req,
        CancellationToken cancellationToken)
    {
        if (!TryGetRazorpayCredentials(out var keyId, out var keySecret))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Razorpay is not configured. Set Razorpay:KeyId and Razorpay:KeySecret on the API."
            });

        var application = await _db.LoanApplications.FindAsync(req.ApplicationId);
        if (application == null)
            return NotFound(new { message = "Loan application not found." });

        if (!application.IsESigned || application.DisbursedAt == null)
            return BadRequest(new { message = "EMI payments are available only after loan disbursal." });

        var schedule = BuildRepaymentSchedule(
            application.SanctionedAmount ?? 0m,
            application.MonthlyEmi,
            application.AnnualInterestRate,
            application.TenureMonths,
            application.DisbursedAt.Value);
        if (req.InstallmentNumber < 1 || req.InstallmentNumber > schedule.Count)
            return BadRequest(new { message = "Invalid installment number." });

        var paidInstallments = await GetPaidInstallmentsAsync(application.ApplicationId, cancellationToken);
        var nextInstallmentNumber = Enumerable.Range(1, schedule.Count)
            .FirstOrDefault(installmentNumber => !paidInstallments.Contains(installmentNumber));
        if (req.InstallmentNumber != nextInstallmentNumber)
            return Conflict(new { message = "Only the next unpaid installment can be paid." });

        var installment = schedule[req.InstallmentNumber - 1];
        var amountInPaise = decimal.ToInt64(installment.Emi * 100m);
        var client = CreateRazorpayClient(keyId, keySecret);
        var requestBody = JsonSerializer.Serialize(new
        {
            amount = amountInPaise,
            currency = "INR",
            receipt = $"emi{Guid.NewGuid():N}",
            notes = new
            {
                applicationId = application.ApplicationId.ToString(),
                applicationNumber = application.ApplicationNumber,
                installmentNumber = req.InstallmentNumber.ToString()
            }
        });

        try
        {
            using var response = await client.PostAsync(
                "orders",
                new StringContent(requestBody, Encoding.UTF8, "application/json"),
                cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Razorpay order creation failed with status {StatusCode}.", response.StatusCode);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Unable to create a Razorpay payment order. Please try again."
                });
            }

            using var orderDocument = JsonDocument.Parse(responseContent);
            var order = orderDocument.RootElement;
            var orderId = ReadJsonString(order, "id");
            if (string.IsNullOrWhiteSpace(orderId))
            {
                _logger.LogError("Razorpay returned an order response without an order ID.");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Razorpay returned an invalid order response."
                });
            }

            return Ok(new
            {
                keyId,
                orderId,
                amount = amountInPaise,
                currency = "INR"
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Unable to connect to Razorpay while creating an order.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Unable to connect to Razorpay. Please try again."
            });
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Razorpay returned an invalid order response.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Razorpay returned an invalid order response."
            });
        }
    }

    [HttpPost("payments/razorpay/verify")]
    public async Task<IActionResult> VerifyRazorpayPayment(
        [FromBody] RazorpayPaymentVerificationRequest req,
        CancellationToken cancellationToken)
    {
        if (!TryGetRazorpayCredentials(out var keyId, out var keySecret))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Razorpay is not configured on the API."
            });

        if (!IsValidRazorpayId(req.RazorpayOrderId, "order_") ||
            !IsValidRazorpayId(req.RazorpayPaymentId, "pay_") ||
            string.IsNullOrWhiteSpace(req.RazorpaySignature))
            return BadRequest(new { message = "Invalid Razorpay payment details." });

        var application = await _db.LoanApplications.FindAsync(req.ApplicationId);
        if (application == null)
            return NotFound(new { message = "Loan application not found." });

        if (!application.IsESigned || application.DisbursedAt == null)
            return BadRequest(new { message = "EMI payments are available only after loan disbursal." });

        var schedule = BuildRepaymentSchedule(
            application.SanctionedAmount ?? 0m,
            application.MonthlyEmi,
            application.AnnualInterestRate,
            application.TenureMonths,
            application.DisbursedAt.Value);
        if (req.InstallmentNumber < 1 || req.InstallmentNumber > schedule.Count)
            return BadRequest(new { message = "Invalid installment number." });

        byte[] suppliedSignature;
        try
        {
            suppliedSignature = Convert.FromHexString(req.RazorpaySignature);
        }
        catch (FormatException)
        {
            return BadRequest(new { message = "Invalid Razorpay payment signature." });
        }

        var signedPayload = Encoding.UTF8.GetBytes($"{req.RazorpayOrderId}|{req.RazorpayPaymentId}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keySecret));
        var expectedSignature = hmac.ComputeHash(signedPayload);
        if (suppliedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
            return BadRequest(new { message = "Razorpay payment signature verification failed." });

        var existingPayment = await _db.LoanPayments
            .AsNoTracking()
            .FirstOrDefaultAsync(payment => payment.RazorpayPaymentId == req.RazorpayPaymentId, cancellationToken);
        if (existingPayment != null)
        {
            if (existingPayment.ApplicationId != application.ApplicationId ||
                existingPayment.InstallmentNumber != req.InstallmentNumber ||
                existingPayment.RazorpayOrderId != req.RazorpayOrderId)
                return Conflict(new { message = "This payment is already recorded for a different installment." });

            var recordedInstallments = await GetPaidInstallmentsAsync(application.ApplicationId, cancellationToken);
            return Ok(new
            {
                verified = true,
                paymentId = existingPayment.RazorpayPaymentId,
                installmentNumber = existingPayment.InstallmentNumber,
                amount = existingPayment.Amount,
                paidInstallments = recordedInstallments
            });
        }

        var paidInstallments = await GetPaidInstallmentsAsync(application.ApplicationId, cancellationToken);
        if (paidInstallments.Contains(req.InstallmentNumber))
            return Conflict(new { message = "This installment has already been paid." });

        var nextInstallmentNumber = Enumerable.Range(1, schedule.Count)
            .FirstOrDefault(installmentNumber => !paidInstallments.Contains(installmentNumber));
        if (req.InstallmentNumber != nextInstallmentNumber)
            return Conflict(new { message = "Only the next unpaid installment can be verified." });

        var expectedAmount = decimal.ToInt64(schedule[req.InstallmentNumber - 1].Emi * 100m);
        var client = CreateRazorpayClient(keyId, keySecret);
        try
        {
            using var orderResponse = await client.GetAsync(
                $"orders/{req.RazorpayOrderId}",
                cancellationToken);
            if (!orderResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Razorpay order lookup failed with status {StatusCode}.", orderResponse.StatusCode);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Unable to verify the Razorpay order. Please try again."
                });
            }

            using var orderDocument = JsonDocument.Parse(
                await orderResponse.Content.ReadAsStringAsync(cancellationToken));
            var order = orderDocument.RootElement;
            var notes = order.TryGetProperty("notes", out var notesElement) ? notesElement : default;
            if (ReadJsonString(order, "id") != req.RazorpayOrderId ||
                ReadJsonInt64(order, "amount") != expectedAmount ||
                ReadJsonString(order, "currency") != "INR" ||
                notes.ValueKind != JsonValueKind.Object ||
                ReadJsonString(notes, "applicationId") != application.ApplicationId.ToString() ||
                ReadJsonString(notes, "installmentNumber") != req.InstallmentNumber.ToString())
                return BadRequest(new { message = "Razorpay order does not match this EMI installment." });

            using var paymentResponse = await client.GetAsync(
                $"payments/{req.RazorpayPaymentId}",
                cancellationToken);
            if (!paymentResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Razorpay payment lookup failed with status {StatusCode}.", paymentResponse.StatusCode);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Unable to verify the Razorpay payment. Please try again."
                });
            }

            using var paymentDocument = JsonDocument.Parse(
                await paymentResponse.Content.ReadAsStringAsync(cancellationToken));
            var payment = paymentDocument.RootElement;
            if (ReadJsonString(payment, "id") != req.RazorpayPaymentId ||
                ReadJsonString(payment, "order_id") != req.RazorpayOrderId ||
                ReadJsonInt64(payment, "amount") != expectedAmount ||
                ReadJsonString(payment, "currency") != "INR")
                return BadRequest(new { message = "Razorpay payment does not match this EMI installment." });

            if (ReadJsonString(payment, "status") != "captured")
                return Conflict(new { message = "Payment has not been captured yet. Please check again shortly." });

            _db.LoanPayments.Add(new LoanPayment
            {
                ApplicationId = application.ApplicationId,
                InstallmentNumber = req.InstallmentNumber,
                RazorpayOrderId = req.RazorpayOrderId,
                RazorpayPaymentId = req.RazorpayPaymentId,
                Amount = schedule[req.InstallmentNumber - 1].Emi,
                Currency = "INR",
                PaidAt = DateTime.UtcNow
            });

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                var concurrentPayment = await _db.LoanPayments
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        record => record.RazorpayPaymentId == req.RazorpayPaymentId,
                        cancellationToken);
                if (concurrentPayment != null &&
                    concurrentPayment.ApplicationId == application.ApplicationId &&
                    concurrentPayment.InstallmentNumber == req.InstallmentNumber)
                {
                    var recordedInstallments = await GetPaidInstallmentsAsync(
                        application.ApplicationId,
                        cancellationToken);
                    return Ok(new
                    {
                        verified = true,
                        paymentId = concurrentPayment.RazorpayPaymentId,
                        installmentNumber = concurrentPayment.InstallmentNumber,
                        amount = concurrentPayment.Amount,
                        paidInstallments = recordedInstallments
                    });
                }

                if (concurrentPayment != null ||
                    await _db.LoanPayments.AnyAsync(
                        record => record.ApplicationId == application.ApplicationId &&
                                  record.InstallmentNumber == req.InstallmentNumber,
                        cancellationToken))
                    return Conflict(new { message = "This installment has already been paid." });

                _logger.LogError(
                    exception,
                    "Razorpay payment {PaymentId} was captured but could not be recorded.",
                    req.RazorpayPaymentId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Payment was captured but could not be recorded. Contact support with the payment ID."
                });
            }

            paidInstallments = await GetPaidInstallmentsAsync(application.ApplicationId, cancellationToken);
            return Ok(new
            {
                verified = true,
                paymentId = req.RazorpayPaymentId,
                installmentNumber = req.InstallmentNumber,
                amount = schedule[req.InstallmentNumber - 1].Emi,
                paidInstallments
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Unable to connect to Razorpay while verifying a payment.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Unable to connect to Razorpay. Please try again."
            });
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Razorpay returned an invalid payment verification response.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Razorpay returned an invalid verification response."
            });
        }
    }

    private bool TryGetRazorpayCredentials(out string keyId, out string keySecret)
    {
        keyId = _configuration["Razorpay:KeyId"] ?? string.Empty;
        keySecret = _configuration["Razorpay:KeySecret"] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(keyId) && !string.IsNullOrWhiteSpace(keySecret);
    }

    private Task<List<int>> GetPaidInstallmentsAsync(Guid applicationId, CancellationToken cancellationToken) =>
        _db.LoanPayments
            .AsNoTracking()
            .Where(payment => payment.ApplicationId == applicationId)
            .OrderBy(payment => payment.InstallmentNumber)
            .Select(payment => payment.InstallmentNumber)
            .ToListAsync(cancellationToken);

    private async Task SendSanctionEmailAsync(LoanApplication application)
    {
        if (!_configuration.GetValue<bool>("SendGrid:Enabled"))
        {
            _logger.LogInformation("SendGrid sanction emails are disabled by configuration.");
            return;
        }

        var apiKey = _configuration["SendGrid:ApiKey"];
        var fromEmail = _configuration["SendGrid:FromEmail"];
        var fromName = _configuration["SendGrid:FromName"] ?? "K$DPaise Digital Lending";

        if (string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(fromEmail))
        {
            _logger.LogWarning(
                "SendGrid is not configured. Set SendGrid:ApiKey and SendGrid:FromEmail to send sanction letters.");
            return;
        }

        var borrower = await _db.Borrowers.FindAsync(application.BorrowerId);
        if (borrower == null || string.IsNullOrWhiteSpace(borrower.Email))
        {
            _logger.LogWarning(
                "Sanction email was not sent because borrower {BorrowerId} has no email address.",
                application.BorrowerId);
            return;
        }

        var from = new EmailAddress(fromEmail, fromName);
        var to = new EmailAddress(borrower.Email, borrower.FullName);
        var subject = $"Loan sanctioned - {application.ApplicationNumber}";
        var sanctionedAmount = (application.SanctionedAmount ?? 0m).ToString("N2");
        var monthlyEmi = application.MonthlyEmi.ToString("N2");
        var htmlContent = $"""
            <p>Dear {System.Net.WebUtility.HtmlEncode(borrower.FullName ?? "Customer")},</p>
            <p>Your loan application has been successfully sanctioned and disbursed.</p>
            <table cellpadding="6" cellspacing="0" border="1" style="border-collapse:collapse">
              <tr><td><strong>Application Number</strong></td><td>{System.Net.WebUtility.HtmlEncode(application.ApplicationNumber)}</td></tr>
              <tr><td><strong>Sanctioned Amount</strong></td><td>Rs. {sanctionedAmount}</td></tr>
              <tr><td><strong>Tenure</strong></td><td>{application.TenureMonths} months</td></tr>
              <tr><td><strong>Monthly EMI</strong></td><td>Rs. {monthlyEmi}</td></tr>
              <tr><td><strong>Disbursement UTR</strong></td><td>{System.Net.WebUtility.HtmlEncode(application.DisbursementUtr)}</td></tr>
              <tr><td><strong>e-Sign Transaction</strong></td><td>{System.Net.WebUtility.HtmlEncode(application.ESignTransactionId)}</td></tr>
            </table>
            <p>Regards,<br>{System.Net.WebUtility.HtmlEncode(fromName)}</p>
            """;

        var message = MailHelper.CreateSingleEmail(from, to, subject, null, htmlContent);

        try
        {
            var client = new SendGridClient(apiKey);
            var response = await client.SendEmailAsync(message);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Body.ReadAsStringAsync();
                _logger.LogError(
                    "SendGrid rejected sanction email for application {ApplicationNumber}. Status: {StatusCode}. Response: {Response}",
                    application.ApplicationNumber,
                    response.StatusCode,
                    responseBody);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Unable to send sanction email for application {ApplicationNumber}.",
                application.ApplicationNumber);
        }
    }

    private HttpClient CreateRazorpayClient(string keyId, string keySecret)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.razorpay.com/v1/");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keyId}:{keySecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return client;
    }

    private static bool IsValidRazorpayId(string? value, string prefix) =>
        value != null &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length > prefix.Length &&
        value[prefix.Length..].All(char.IsLetterOrDigit);

    private static string? ReadJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadJsonInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    private static List<RepaymentScheduleItem> BuildRepaymentSchedule(
        decimal sanctionedAmount,
        decimal emi,
        decimal annualInterestRate,
        int tenureMonths,
        DateTime disbursedAt)
    {
        var schedule = new List<RepaymentScheduleItem>();
        if (sanctionedAmount <= 0m || emi <= 0m || tenureMonths <= 0)
            return schedule;

        decimal openingBalance = sanctionedAmount;
        decimal monthlyRate = annualInterestRate / 12m / 100m;

        for (int installmentNumber = 1; installmentNumber <= tenureMonths; installmentNumber++)
        {
            bool isFinalInstallment = installmentNumber == tenureMonths;
            decimal interestAmount = Math.Round(openingBalance * monthlyRate, 2, MidpointRounding.AwayFromZero);
            decimal principalAmount = isFinalInstallment
                ? openingBalance
                : Math.Round(emi - interestAmount, 2, MidpointRounding.AwayFromZero);
            decimal installmentAmount = isFinalInstallment
                ? Math.Round(principalAmount + interestAmount, 2, MidpointRounding.AwayFromZero)
                : emi;
            decimal closingBalance = Math.Round(openingBalance - principalAmount, 2, MidpointRounding.AwayFromZero);

            if (closingBalance < 0m)
                closingBalance = 0m;

            schedule.Add(new RepaymentScheduleItem(
                installmentNumber,
                disbursedAt.AddMonths(installmentNumber).ToUniversalTime().ToString("yyyy-MM-dd"),
                openingBalance,
                principalAmount,
                interestAmount,
                installmentAmount,
                closingBalance));

            openingBalance = closingBalance;
        }

        return schedule;
    }

    private sealed record RepaymentScheduleItem(
        int InstallmentNumber,
        string DueDate,
        decimal OpeningBalance,
        decimal PrincipalAmount,
        decimal InterestAmount,
        decimal Emi,
        decimal ClosingBalance);
}