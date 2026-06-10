using EthraiOrderFixService.Services;
using Microsoft.Extensions.Options;
namespace EthraiOrderFixService.Workers;

public class OrderFixWorker : BackgroundService
{
    private readonly ILogger<OrderFixWorker> _logger;
    private readonly OrderService _orderService;
    private readonly ProfileService _profileService;
    private readonly CourseService _courseService;
    private readonly CouponService _couponService;
    private readonly TransactionService _transactionService;
    private readonly WorkerOptions _options;

    public OrderFixWorker(
        ILogger<OrderFixWorker> logger,
        OrderService orderService,
        ProfileService profileService,
        CourseService courseService,
        CouponService couponService,
        TransactionService transactionService,
        IOptions<WorkerOptions> options)
    {
        _logger = logger;
        _orderService = orderService;
        _profileService = profileService;
        _courseService = courseService;
        _couponService = couponService;
        _transactionService = transactionService;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=================================================");
        _logger.LogInformation("OrderFixWorker STARTED at {StartTime}", DateTime.Now);
        _logger.LogInformation("Configuration - Recurring: {recurring}, Interval: {interval} minutes", _options.RunRecurring, _options.IntervalMinutes);
        _logger.LogInformation("=================================================");

        // Start from yesterday to catch any missed orders, then stay on today
        DateTime processingDate = DateTime.Now.Date.AddDays(-1);

        do
        {
            try
            {
                DateTime today      = DateTime.Now.Date;
                DateTime startDate  = processingDate;
                DateTime endDate    = processingDate.AddDays(1);

                _logger.LogInformation("--- New execution cycle started at {Time} ---", DateTime.Now);
                _logger.LogInformation("Querying orders from {StartDate} to {EndDate}", startDate, endDate);
                
                var orders = await _orderService.GetOrdersAsync(startDate, endDate, stoppingToken);
                _logger.LogInformation("Found {OrderCount} orders to process", orders.Count);
                
                int totalSaved = 0;

                foreach (var item in orders)
                {
                    var userProfileId = item.Contains("UserProfileId") ? item["UserProfileId"].ToString() : null;
                    if (string.IsNullOrEmpty(userProfileId))
                        continue;

                    var profile = await _profileService.GetProfileIdentificationByIdAsync(userProfileId.Trim(), stoppingToken);
                    if (profile == null)
                        continue;

                    if (item.Contains("Details") && item["Details"].IsBsonArray && item["Details"].AsBsonArray.Count > 0)
                    {
                        var detailsArray = item["Details"].AsBsonArray;

                        double totalInvoiceAmount = detailsArray
                            .Where(d => d.AsBsonDocument.Contains("Amount"))
                            .Sum(d => d.AsBsonDocument["Amount"].ToDouble());

                        double vatPercentage = item.Contains("VatPercentage") ? item["VatPercentage"].ToDouble() : 0;

                        // --- Prepare new fields (order-level, shared across all details) ---
                        // MADA_TRACK_ID: from Invoice.MadaInfo.TrackId
                        string? madaTrackId = null;
                        if (item.Contains("Invoice"))
                        {
                            var inv = item["Invoice"].AsBsonDocument;
                            if (inv.Contains("MadaInfo") && !inv["MadaInfo"].IsBsonNull)
                            {
                                var madaInfo = inv["MadaInfo"].AsBsonDocument;
                                madaTrackId = madaInfo.Contains("TrackId") ? madaInfo["TrackId"].ToString() : null;
                            }
                        }

                        // DISCOUNT_CODE: from Order.CouponName
                        string? discountCode = item.Contains("CouponName") && !item["CouponName"].IsBsonNull
                            ? item["CouponName"].ToString()
                            : null;

                        // DISCOUNT_PERC: from Coupon collection, looked up by CouponName

                        string? CouponId = item.Contains("CouponId") && !item["CouponId"].IsBsonNull
                            ? item["CouponId"].ToString()
                            : null;
                        double? discountPerc = null;
                        
                        if (!string.IsNullOrWhiteSpace(CouponId))
                        {
                            discountPerc = await _couponService.GetDiscountPercByCouponNameAsync(CouponId, stoppingToken);
                        }
                        // ---------------------------------------------------------------

                        foreach (var detailItem in detailsArray)
                        {
                            var detail = detailItem.AsBsonDocument;
                            var transactionInvoice = new TransactionInvoice();

                            transactionInvoice.TRAINEE_CODE = profile;

                            double detailAmount = detail.Contains("Amount") ? detail["Amount"].ToDouble() : 0;
                            transactionInvoice.NETAMOUNT = Math.Round(detailAmount, 2, MidpointRounding.AwayFromZero);

                            double amountWithoutVat = detail.Contains("AmountWithoutVat") ? detail["AmountWithoutVat"].ToDouble() : 0;
                            transactionInvoice.TOTAL_TAXABLE_AMOUNT = Math.Round(amountWithoutVat, 2, MidpointRounding.AwayFromZero);
                            transactionInvoice.TOTAL_VAT = Math.Round(amountWithoutVat * (vatPercentage / 100), 2, MidpointRounding.AwayFromZero);
                            transactionInvoice.INVOICE_TOTAL_AMOUNT = Math.Round(totalInvoiceAmount, 2, MidpointRounding.AwayFromZero);

                            var productId = detail.Contains("ProductId") ? detail["ProductId"].ToString() : null;
                            var productNameFromDetail = detail.Contains("ProductName") ? detail["ProductName"].ToString() : null;

                            if (!string.IsNullOrEmpty(productId))
                            {
                                var info = await _courseService.GetCourseInfoByIdAsync(productId, stoppingToken);
                                if (info != null)
                                {
                                    transactionInvoice.PROGRAM_NAME = info.CourseName;
                                    transactionInvoice.PROGRAM_ID = info.OracleId;
                                }
                                else if (!string.IsNullOrEmpty(productNameFromDetail))
                                {
                                    // Fallback to ProductName from detail if course not found
                                    transactionInvoice.PROGRAM_NAME = productNameFromDetail;
                                    transactionInvoice.PROGRAM_ID = 0;
                                }
                            }

                            // Get order detail ID for invoice note - check both Id and _id fields
                            var detailId = detail.Contains("Id") ? detail["Id"].ToString() :
                                          detail.Contains("_id") ? detail["_id"].ToString() :
                                          "NO_ID";

                            if (item.Contains("Invoice"))
                            {
                                var invoice = item["Invoice"].AsBsonDocument;
                                transactionInvoice.INVOICE_NO = invoice.Contains("PaymentNumber") ? invoice["PaymentNumber"].ToString() : string.Empty;
                                transactionInvoice.INVOICE_DATE = invoice.Contains("Created") ? invoice["Created"].ToUniversalTime().ToString("yyyy/MM/dd") : DateTime.Now.ToString("yyyy/MM/dd");
                                transactionInvoice.PAYMENT_TYPE_DESC = invoice.Contains("PaymentMethod") ? GetPaymentType(invoice["PaymentMethod"].ToInt32()) : string.Empty;
                                transactionInvoice.SADAD_BILL_NO = invoice.Contains("SadadId") ? invoice["SadadId"].ToString() : null;
                                // Store MongoDB detail ID in INVOICE_NOTE for tracking
                                transactionInvoice.INVOICE_NOTE = $"{detailId}";
                            }

                            // Assign new fields (pending DB team SP update)
                            transactionInvoice.MADA_TRACK_ID = madaTrackId;
                            transactionInvoice.DISCOUNT_CODE = discountCode;
                            transactionInvoice.DISCOUNT_PERC = discountPerc;

                            // Check if this specific detail (invoice + detail ID) already exists
                            if (await _transactionService.InvoiceDetailExistsByNote(transactionInvoice.INVOICE_NO ?? string.Empty, transactionInvoice.INVOICE_NOTE ?? string.Empty, stoppingToken))
                            {
                                _logger.LogInformation("Skipping already existing invoice detail: {inv} - Detail ID: {detailId}", 
                                    transactionInvoice.INVOICE_NO, 
                                    detailId);
                                continue;
                            }

                            var saveSuccess = await _transactionService.SaveTransactionInvoiceToOracle(transactionInvoice, stoppingToken);
                            if (saveSuccess)
                            {
                                totalSaved++;
                                _logger.LogInformation("Saved invoice {inv} | Program: {prog} | NET: {net} | TOTAL: {total} | MadaTrackId: {mada} | DiscountCode: {code} | DiscountPerc: {perc}", 
                                    transactionInvoice.INVOICE_NO, 
                                    transactionInvoice.PROGRAM_NAME,
                                    transactionInvoice.NETAMOUNT, 
                                    transactionInvoice.INVOICE_TOTAL_AMOUNT,
                                    transactionInvoice.MADA_TRACK_ID ?? "N/A",
                                    transactionInvoice.DISCOUNT_CODE ?? "N/A",
                                    transactionInvoice.DISCOUNT_PERC?.ToString() ?? "N/A");
                            }
                            else
                            {
                                _logger.LogWarning("Failed to save invoice {inv} for trainee {trainee} - Program: {prog}", 
                                    transactionInvoice.INVOICE_NO, 
                                    transactionInvoice.TRAINEE_CODE,
                                    transactionInvoice.PROGRAM_NAME);
                            }
                        }
                    }
                }

                _logger.LogInformation("=== Execution Summary ===");
                _logger.LogInformation("Total invoices successfully saved: {count}", totalSaved);
                _logger.LogInformation("Cycle completed at {Time}", DateTime.Now);
                _logger.LogInformation("========================");

                // If still catching up on past days — advance and loop immediately (no delay)
                if (processingDate < DateTime.Now.Date)
                {
                    _logger.LogInformation("⏩ Catch-up: moving from {current} to {next}",
                        processingDate.ToString("yyyy-MM-dd"),
                        processingDate.AddDays(1).ToString("yyyy-MM-dd"));
                    processingDate = processingDate.AddDays(1);
                    continue;
                }

                // Already on today — stay on today for next cycle
                processingDate = DateTime.Now.Date;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing orders at {Time}", DateTime.Now);
            }

            if (!_options.RunRecurring)
            {
                _logger.LogInformation("RunRecurring is FALSE. Worker will stop after this execution.");
                break;
            }

            _logger.LogInformation("Waiting {Interval} minutes until next execution...", _options.IntervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);

        } while (!stoppingToken.IsCancellationRequested);
        
        _logger.LogInformation("OrderFixWorker STOPPED at {StopTime}", DateTime.Now);
    }

    private static string GetPaymentType(int method) => method switch
    {
        0 => "SADAD",
        1 => "MADA",
        3 => "MADA",
        _ => "MADA"
    };
}
