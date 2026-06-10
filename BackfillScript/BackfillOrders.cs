// ============================================================
// Backfill Script: 2026-04-29 → 2026-05-05
// Processes missed orders day by day and inserts into Oracle
// Run with: dotnet script BackfillOrders.cs  (or dotnet run)
// ============================================================

using MongoDB.Bson;
using MongoDB.Driver;
using Oracle.ManagedDataAccess.Client;
using System.Data;

// ─── CONFIG ─────────────────────────────────────────────────
const string mongoConnectionString =
    "mongodb://netways:P%40ssw0rd@172.16.17.11:27017,172.16.17.12:27017,172.16.17.13:27017?authSource=admin&replicaSet=ethprodrs0";

const string oracleConnectionString =
    "User Id=ETraining_Services;Password=etraining8642;Data Source=(DESCRIPTION =(ADDRESS = (PROTOCOL = TCP)(HOST = exa1-scan)(PORT = 1521))(CONNECT_DATA = (SERVER = DEDICATED)(SERVICE_NAME = IPS)))";

// ─── DATE RANGE ─────────────────────────────────────────────
var startRange = new DateTime(2026, 4, 29);
var endRange   = new DateTime(2026, 5, 5);   // inclusive — will process up to end of this day
// ────────────────────────────────────────────────────────────

var mongoClient   = new MongoClient(mongoConnectionString);
var profileDb     = mongoClient.GetDatabase("profiledb");
var coursesDb     = mongoClient.GetDatabase("coursesdb");
var userDataDb    = mongoClient.GetDatabase("userdatadb");

var profileCol    = profileDb.GetCollection<BsonDocument>("profile");
var courseCol     = coursesDb.GetCollection<BsonDocument>("course");
var orderCol      = userDataDb.GetCollection<BsonDocument>("Order");
var couponCol     = userDataDb.GetCollection<BsonDocument>("Coupon");

Console.WriteLine("=================================================");
Console.WriteLine($"Backfill started at {DateTime.Now}");
Console.WriteLine($"Date range: {startRange:yyyy-MM-dd} → {endRange:yyyy-MM-dd}");
Console.WriteLine("=================================================");

int grandTotal = 0;

for (var date = startRange; date <= endRange; date = date.AddDays(1))
{
    var dayStart = date;
    var dayEnd   = date.AddDays(1);

    Console.WriteLine();
    Console.WriteLine($"─── Processing {date:yyyy-MM-dd} ───────────────────────────");

    // Query orders for this day
    var filterBuilder = Builders<BsonDocument>.Filter;
    var filter = filterBuilder.Gte("Created", dayStart)
               & filterBuilder.Lte("Created", dayEnd)
               & filterBuilder.Eq("Invoice.Status", "PmtCompleted");

    var orders = await orderCol.Find(filter).ToListAsync();
    Console.WriteLine($"  Found {orders.Count} orders");

    int daySaved = 0;

    foreach (var item in orders)
    {
        var userProfileId = item.Contains("UserProfileId") ? item["UserProfileId"].ToString() : null;
        if (string.IsNullOrWhiteSpace(userProfileId)) continue;

        // ── Get trainee code from profile ──────────────────
        var profileFilter = Builders<BsonDocument>.Filter.Eq("_id", userProfileId.Trim());
        var profileDoc    = await profileCol.Find(profileFilter).FirstOrDefaultAsync();
        if (profileDoc == null) continue;

        string? traineeCode = profileDoc.Contains("NationalId")
            ? profileDoc["NationalId"].ToString()
            : profileDoc.GetValue("_id").ToString();

        if (string.IsNullOrWhiteSpace(traineeCode)) continue;

        if (!item.Contains("Details") || !item["Details"].IsBsonArray) continue;

        var detailsArray = item["Details"].AsBsonArray;
        if (detailsArray.Count == 0) continue;

        double totalInvoiceAmount = detailsArray
            .Where(d => d.AsBsonDocument.Contains("Amount"))
            .Sum(d => d.AsBsonDocument["Amount"].ToDouble());

        double vatPercentage = item.Contains("VatPercentage") ? item["VatPercentage"].ToDouble() : 0;

        // ── MADA_TRACK_ID ──────────────────────────────────
        string? madaTrackId = null;
        if (item.Contains("Invoice") && !item["Invoice"].IsBsonNull)
        {
            var inv = item["Invoice"].AsBsonDocument;
            if (inv.Contains("MadaInfo") && !inv["MadaInfo"].IsBsonNull)
            {
                var madaInfo = inv["MadaInfo"].AsBsonDocument;
                madaTrackId = madaInfo.Contains("TrackId") ? madaInfo["TrackId"].ToString() : null;
            }
        }

        // ── DISCOUNT_CODE ──────────────────────────────────
        string? discountCode = item.Contains("CouponName") && !item["CouponName"].IsBsonNull
            ? item["CouponName"].ToString()
            : null;

        // ── DISCOUNT_PERC ──────────────────────────────────
        double? discountPerc = null;
        if (!string.IsNullOrWhiteSpace(discountCode))
        {
            var couponFilter = Builders<BsonDocument>.Filter.Eq("Name", discountCode);
            var coupon = await couponCol.Find(couponFilter).FirstOrDefaultAsync();
            if (coupon != null && coupon.Contains("Value") && coupon["Value"].AsBsonDocument.Contains("PercValue"))
                discountPerc = coupon["Value"].AsBsonDocument["PercValue"].ToDouble();
        }

        foreach (var detailItem in detailsArray)
        {
            var detail = detailItem.AsBsonDocument;

            // ── Amounts ────────────────────────────────────
            double detailAmount     = detail.Contains("Amount")         ? detail["Amount"].ToDouble()         : 0;
            double amountWithoutVat = detail.Contains("AmountWithoutVat") ? detail["AmountWithoutVat"].ToDouble() : 0;

            // ── Course info ────────────────────────────────
            string? programName = null;
            int     programId   = 0;
            var productId = detail.Contains("ProductId") ? detail["ProductId"].ToString() : null;

            if (!string.IsNullOrEmpty(productId))
            {
                var courseFilter = Builders<BsonDocument>.Filter.Eq("_id", productId);
                var courseDoc    = await courseCol.Find(courseFilter).FirstOrDefaultAsync();
                if (courseDoc != null)
                {
                    programName = courseDoc.Contains("NameAr") ? courseDoc["NameAr"].ToString() : productId;
                    programId   = courseDoc.Contains("OracleId") ? courseDoc["OracleId"].ToInt32() : 0;
                }
                else
                {
                    programName = detail.Contains("ProductName") ? detail["ProductName"].ToString() : null;
                    programId   = 0;
                }
            }

            // ── Invoice fields ─────────────────────────────
            string  invoiceNo      = string.Empty;
            string  invoiceDate    = DateTime.Now.ToString("yyyy/MM/dd");
            string  paymentType    = string.Empty;
            string? sadadBillNo    = null;
            string  detailId       = detail.Contains("_id") ? detail["_id"].ToString()! : "NO_ID";
            string  invoiceNote    = detailId;

            if (item.Contains("Invoice") && !item["Invoice"].IsBsonNull)
            {
                var invoice = item["Invoice"].AsBsonDocument;
                invoiceNo   = invoice.Contains("PaymentNumber") ? invoice["PaymentNumber"].ToString()! : string.Empty;
                invoiceDate = invoice.Contains("Created")
                    ? invoice["Created"].ToUniversalTime().ToString("yyyy/MM/dd")
                    : DateTime.Now.ToString("yyyy/MM/dd");
                paymentType = invoice.Contains("PaymentMethod") ? GetPaymentType(invoice["PaymentMethod"].ToInt32()) : string.Empty;
                sadadBillNo = invoice.Contains("SadadId") ? invoice["SadadId"].ToString() : null;
            }

            // ── Skip if already exists in Oracle ───────────
            if (await InvoiceDetailExistsAsync(oracleConnectionString, invoiceNo, invoiceNote))
            {
                Console.WriteLine($"  [SKIP] Already exists: {invoiceNo} | detail: {detailId}");
                continue;
            }

            // ── Save to Oracle ─────────────────────────────
            bool saved = await SaveToOracleAsync(oracleConnectionString, new InvoiceRecord(
                InvoiceNo:          invoiceNo,
                InvoiceDate:        invoiceDate,
                TraineeCode:        traineeCode,
                PaymentTypeDesc:    paymentType,
                SadadBillNo:        sadadBillNo ?? string.Empty,
                InvoiceNote:        invoiceNote,
                ProgramId:          programId,
                ProgramName:        programName ?? string.Empty,
                TotalTaxableAmount: Math.Round(amountWithoutVat, 2, MidpointRounding.AwayFromZero),
                TotalVat:           Math.Round(amountWithoutVat * (vatPercentage / 100), 2, MidpointRounding.AwayFromZero),
                InvoiceTotalAmount: Math.Round(totalInvoiceAmount, 2, MidpointRounding.AwayFromZero),
                NetAmount:          Math.Round(detailAmount, 2, MidpointRounding.AwayFromZero),
                MadaTrackId:        madaTrackId,
                DiscountCode:       discountCode,
                DiscountPerc:       discountPerc
            ));

            if (saved)
            {
                daySaved++;
                Console.WriteLine($"  [OK]   {invoiceNo} | {programName} | NET: {Math.Round(detailAmount,2)} | MADA: {madaTrackId ?? "N/A"} | Coupon: {discountCode ?? "N/A"} ({discountPerc?.ToString() ?? "N/A"}%)");
            }
            else
            {
                Console.WriteLine($"  [FAIL] {invoiceNo} | {programName} | trainee: {traineeCode}");
            }
        }
    }

    Console.WriteLine($"  → Day total saved: {daySaved}");
    grandTotal += daySaved;
}

Console.WriteLine();
Console.WriteLine("=================================================");
Console.WriteLine($"Backfill COMPLETED at {DateTime.Now}");
Console.WriteLine($"Grand total inserted: {grandTotal}");
Console.WriteLine("=================================================");

// ─── HELPERS ────────────────────────────────────────────────

static string GetPaymentType(int method) => method switch
{
    0 => "SADAD",
    1 => "MADA",
    3 => "MADA",
    _ => "MADA"
};

static async Task<bool> InvoiceDetailExistsAsync(string connStr, string invoiceNo, string invoiceNote)
{
    try
    {
        using var conn = new OracleConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM eth_trne_invoices WHERE invoice_no = :invoice_no AND invoice_note = :invoice_note";
        cmd.Parameters.Add("invoice_no",   OracleDbType.Varchar2).Value = invoiceNo;
        cmd.Parameters.Add("invoice_note", OracleDbType.Varchar2).Value = invoiceNote;
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [WARN] Existence check failed: {ex.Message}");
        return false;
    }
}

static async Task<bool> SaveToOracleAsync(string connStr, InvoiceRecord r)
{
    try
    {
        using var conn = new OracleConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "ETRAINING_SERVICES.ins_eth_trne_invoices";

        cmd.Parameters.Add("p_invoice_no",           OracleDbType.Varchar2).Value = r.InvoiceNo;
        cmd.Parameters.Add("p_invoice_date",          OracleDbType.Varchar2).Value = r.InvoiceDate;
        cmd.Parameters.Add("p_trainee_code",          OracleDbType.Varchar2).Value = r.TraineeCode;
        cmd.Parameters.Add("p_payment_type_desc",     OracleDbType.Varchar2).Value = r.PaymentTypeDesc;
        cmd.Parameters.Add("p_sadad_bill_no",         OracleDbType.Varchar2).Value = r.SadadBillNo;
        cmd.Parameters.Add("p_invoice_note",          OracleDbType.Varchar2).Value = r.InvoiceNote;
        cmd.Parameters.Add("p_program_id",            OracleDbType.Int32   ).Value = r.ProgramId;
        cmd.Parameters.Add("p_program_name",          OracleDbType.Varchar2).Value = r.ProgramName;
        cmd.Parameters.Add("p_total_taxable_amount",  OracleDbType.Decimal ).Value = r.TotalTaxableAmount;
        cmd.Parameters.Add("p_total_vat",             OracleDbType.Decimal ).Value = r.TotalVat;
        cmd.Parameters.Add("p_invoice_total_amount",  OracleDbType.Decimal ).Value = r.InvoiceTotalAmount;
        cmd.Parameters.Add("p_NET_AMOUNT",            OracleDbType.Decimal ).Value = r.NetAmount;

        // TODO: Uncomment when DB team adds these to the stored procedure
        // cmd.Parameters.Add("p_mada_track_id",  OracleDbType.Varchar2).Value = r.MadaTrackId  ?? string.Empty;
        // cmd.Parameters.Add("p_discount_code",  OracleDbType.Varchar2).Value = r.DiscountCode ?? string.Empty;
        // cmd.Parameters.Add("p_discount_perc",  OracleDbType.Decimal ).Value = r.DiscountPerc ?? 0;

        var outputParam = new OracleParameter("p_error_msg", OracleDbType.Varchar2, 4000)
        {
            Direction = ParameterDirection.Output
        };
        cmd.Parameters.Add(outputParam);

        await cmd.ExecuteNonQueryAsync();

        string? result = outputParam.Value?.ToString()?.Trim();
        return result == "0";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [ERROR] Oracle insert failed: {ex.Message}");
        return false;
    }
}

// ─── RECORD ─────────────────────────────────────────────────
record InvoiceRecord(
    string   InvoiceNo,
    string   InvoiceDate,
    string   TraineeCode,
    string   PaymentTypeDesc,
    string   SadadBillNo,
    string   InvoiceNote,
    int      ProgramId,
    string   ProgramName,
    double   TotalTaxableAmount,
    double   TotalVat,
    double   InvoiceTotalAmount,
    double   NetAmount,
    string?  MadaTrackId,
    string?  DiscountCode,
    double?  DiscountPerc
);
