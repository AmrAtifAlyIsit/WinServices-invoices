// ============================================================
// Reprocess Script — CSV-driven Oracle delete + re-insert
//
// Usage:
//   1. Place a CSV file with payment numbers in the same folder
//      (or pass the full path as the first argument).
//      The CSV may have a header row; any row whose first column
//      looks like a payment number (numeric / non-empty) is used.
//      One payment number per row is sufficient, e.g.:
//
//          PaymentNumber
//          700202616049466849
//          700202616172702337
//
//   2. Run:  dotnet run -- payments.csv
//      (defaults to "payments.csv" in the current directory)
//
// What the script does for every payment number in the CSV:
//   a. Deletes ALL existing rows from Oracle eth_trne_invoices
//      where invoice_no = <payment number>
//   b. Looks up the matching Order in MongoDB by Invoice.PaymentNumber
//   c. Re-inserts each order detail with the updated logic:
//        - NETAMOUNT taken from the flat top-level Amount field
//        - Orders with Amount == 0 are skipped
//        - COMMIT issued after every successful SP call
//        - ROLLBACK issued when the SP call fails, so the
//          Oracle connection pool is always returned clean
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
// ────────────────────────────────────────────────────────────

// ─── CSV PATH ────────────────────────────────────────────────
string csvPath = args.Length > 0 ? args[0] : "payments.csv";
if (!File.Exists(csvPath))
{
    Console.Error.WriteLine($"[ERROR] CSV file not found: {Path.GetFullPath(csvPath)}");
    return 1;
}
// ────────────────────────────────────────────────────────────

// ─── READ PAYMENT NUMBERS FROM CSV ──────────────────────────
var paymentNumbers = new List<string>();
bool isFirstLine = true;

foreach (var rawLine in File.ReadLines(csvPath))
{
    var line = rawLine.Trim();
    if (string.IsNullOrWhiteSpace(line)) continue;

    // First non-empty line: skip if it looks like a header
    // (contains letters rather than being purely numeric / GUID-like)
    if (isFirstLine)
    {
        isFirstLine = false;
        var firstCell = line.Split(',')[0].Trim().Trim('"');
        if (!IsPaymentNumber(firstCell))
        {
            Console.WriteLine($"[INFO] Skipping header row: {firstCell}");
            continue;
        }
    }

    var cell = line.Split(',')[0].Trim().Trim('"');
    if (!string.IsNullOrWhiteSpace(cell))
        paymentNumbers.Add(cell);
}

if (paymentNumbers.Count == 0)
{
    Console.Error.WriteLine("[ERROR] No payment numbers found in the CSV file.");
    return 1;
}

Console.WriteLine("=================================================");
Console.WriteLine($"Reprocess started at {DateTime.Now}");
Console.WriteLine($"CSV file : {Path.GetFullPath(csvPath)}");
Console.WriteLine($"Payment numbers to reprocess: {paymentNumbers.Count}");
Console.WriteLine("=================================================");

// ─── MONGO SETUP ────────────────────────────────────────────
var mongoClient  = new MongoClient(mongoConnectionString);
var profileDb    = mongoClient.GetDatabase("profiledb");
var coursesDb    = mongoClient.GetDatabase("coursesdb");
var userDataDb   = mongoClient.GetDatabase("userdatadb");

var profileCol   = profileDb.GetCollection<BsonDocument>("profile");
var courseCol    = coursesDb.GetCollection<BsonDocument>("course");
var orderCol     = userDataDb.GetCollection<BsonDocument>("Order");
var couponCol    = userDataDb.GetCollection<BsonDocument>("Coupon");
// ────────────────────────────────────────────────────────────

int grandDeleted = 0;
int grandInserted = 0;
int grandSkipped  = 0;
int grandFailed   = 0;

foreach (var paymentNo in paymentNumbers)
{
    Console.WriteLine();
    Console.WriteLine($"─── Payment: {paymentNo} ─────────────────────────────────");

    // ── Step 1: Delete existing Oracle rows ───────────────
    int deleted = await DeleteFromOracleAsync(oracleConnectionString, paymentNo);
    grandDeleted += deleted;
    Console.WriteLine($"  [DELETE] Removed {deleted} existing row(s) from Oracle");

    // ── Step 2: Find the order in MongoDB ─────────────────
    var orderFilter = Builders<BsonDocument>.Filter.Eq("Invoice.PaymentNumber", paymentNo);
    var orders = await orderCol.Find(orderFilter).ToListAsync();

    if (orders.Count == 0)
    {
        Console.WriteLine($"  [WARN]   No matching order found in MongoDB — skipping");
        continue;
    }

    foreach (var item in orders)
    {
        // ── Trainee profile ───────────────────────────────
        var userProfileId = item.Contains("UserProfileId") ? item["UserProfileId"].ToString() : null;
        if (string.IsNullOrWhiteSpace(userProfileId))
        {
            Console.WriteLine("  [SKIP] Missing UserProfileId");
            grandSkipped++;
            continue;
        }

        var profileFilter = Builders<BsonDocument>.Filter.Eq("_id", userProfileId.Trim());
        var profileDoc    = await profileCol.Find(profileFilter).FirstOrDefaultAsync();
        if (profileDoc == null)
        {
            Console.WriteLine($"  [SKIP] Profile not found for UserProfileId: {userProfileId}");
            grandSkipped++;
            continue;
        }

        string? traineeCode = profileDoc.Contains("NationalId")
            ? profileDoc["NationalId"].ToString()
            : profileDoc.GetValue("_id").ToString();

        if (string.IsNullOrWhiteSpace(traineeCode))
        {
            Console.WriteLine($"  [SKIP] Empty trainee code for UserProfileId: {userProfileId}");
            grandSkipped++;
            continue;
        }

        if (!item.Contains("Details") || !item["Details"].IsBsonArray) continue;

        var detailsArray = item["Details"].AsBsonArray;
        if (detailsArray.Count == 0) continue;

        // ── Flat order-level Amount (used as NETAMOUNT) ───
        double orderAmount = item.Contains("Amount") ? item["Amount"].ToDouble() : 0;
        if (orderAmount == 0)
        {
            Console.WriteLine($"  [SKIP] Zero top-level Amount for payment {paymentNo}");
            grandSkipped++;
            continue;
        }

        double vatPercentage = item.Contains("VatPercentage") ? item["VatPercentage"].ToDouble() : 0;

        double totalInvoiceAmount = detailsArray
            .Where(d => d.AsBsonDocument.Contains("Amount"))
            .Sum(d => d.AsBsonDocument["Amount"].ToDouble());

        // ── MADA_TRACK_ID ─────────────────────────────────
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

        // ── DISCOUNT_CODE ─────────────────────────────────
        string? discountCode = item.Contains("CouponName") && !item["CouponName"].IsBsonNull
            ? item["CouponName"].ToString()
            : null;

        // ── DISCOUNT_PERC (lookup by CouponId) ────────────
        double? discountPerc = null;
        string? couponId = item.Contains("CouponId") && !item["CouponId"].IsBsonNull
            ? item["CouponId"].ToString()
            : null;

        if (!string.IsNullOrWhiteSpace(couponId))
        {
            var couponFilter = Builders<BsonDocument>.Filter.Eq("_id", couponId);
            var coupon = await couponCol.Find(couponFilter).FirstOrDefaultAsync();
            if (coupon != null && coupon.Contains("Value") && coupon["Value"].AsBsonDocument.Contains("PercValue"))
                discountPerc = coupon["Value"].AsBsonDocument["PercValue"].ToDouble();
        }

        // ── Invoice-level fields ──────────────────────────
        string invoiceNo   = string.Empty;
        string invoiceDate = DateTime.Now.ToString("yyyy/MM/dd");
        string paymentType = string.Empty;
        string? sadadBillNo = null;

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

        // ── Process each detail line ──────────────────────
        foreach (var detailItem in detailsArray)
        {
            var detail = detailItem.AsBsonDocument;

            double amountWithoutVat = detail.Contains("AmountWithoutVat") ? detail["AmountWithoutVat"].ToDouble() : 0;

            // ── Course info ───────────────────────────────
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

            // ── Detail ID → invoice_note ──────────────────
            string detailId = detail.Contains("Id")  ? detail["Id"].ToString()!  :
                              detail.Contains("_id") ? detail["_id"].ToString()! :
                              "NO_ID";

            var record = new InvoiceRecord(
                InvoiceNo:          invoiceNo,
                InvoiceDate:        invoiceDate,
                TraineeCode:        traineeCode,
                PaymentTypeDesc:    paymentType,
                SadadBillNo:        sadadBillNo ?? string.Empty,
                InvoiceNote:        detailId,
                ProgramId:          programId,
                ProgramName:        programName ?? string.Empty,
                TotalTaxableAmount: Math.Round(amountWithoutVat, 2, MidpointRounding.AwayFromZero),
                TotalVat:           Math.Round(amountWithoutVat * (vatPercentage / 100), 2, MidpointRounding.AwayFromZero),
                InvoiceTotalAmount: Math.Round(totalInvoiceAmount, 2, MidpointRounding.AwayFromZero),
                NetAmount:          Math.Round(orderAmount, 2, MidpointRounding.AwayFromZero),
                MadaTrackId:        madaTrackId,
                DiscountCode:       discountCode,
                DiscountPerc:       discountPerc
            );

            bool saved = await SaveToOracleAsync(oracleConnectionString, record);

            if (saved)
            {
                grandInserted++;
                Console.WriteLine($"  [OK]   {invoiceNo} | {programName} | NET: {record.NetAmount} | detail: {detailId}");
            }
            else
            {
                grandFailed++;
                Console.WriteLine($"  [FAIL] {invoiceNo} | {programName} | trainee: {traineeCode} | detail: {detailId}");
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine("=================================================");
Console.WriteLine($"Reprocess COMPLETED at {DateTime.Now}");
Console.WriteLine($"  Oracle rows deleted : {grandDeleted}");
Console.WriteLine($"  Oracle rows inserted: {grandInserted}");
Console.WriteLine($"  Skipped (no data)   : {grandSkipped}");
Console.WriteLine($"  Failed inserts      : {grandFailed}");
Console.WriteLine("=================================================");

return grandFailed > 0 ? 2 : 0;

// ─── HELPERS ────────────────────────────────────────────────

static bool IsPaymentNumber(string value)
{
    // A header cell typically contains letters; payment numbers are digits
    return !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsDigit(c));
}

static string GetPaymentType(int method) => method switch
{
    0 => "SADAD",
    1 => "MADA",
    3 => "MADA",
    _ => "MADA"
};

static async Task<int> DeleteFromOracleAsync(string connStr, string paymentNo)
{
    try
    {
        using var conn = new OracleConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM eth_trne_invoices WHERE invoice_no = :invoice_no";
        cmd.BindByName = true;
        cmd.Parameters.Add("invoice_no", OracleDbType.Varchar2).Value = paymentNo;

        int rows = await cmd.ExecuteNonQueryAsync();

        using var commitCmd = conn.CreateCommand();
        commitCmd.CommandText = "COMMIT";
        await commitCmd.ExecuteNonQueryAsync();

        return rows;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [ERROR] Delete failed for {paymentNo}: {ex.Message}");
        return 0;
    }
}

static async Task<bool> SaveToOracleAsync(string connStr, InvoiceRecord r)
{
    using var conn = new OracleConnection(connStr);
    try
    {
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText  = "ETRAINING_SERVICES.ins_eth_trne_invoices";
        cmd.BindByName   = true;

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
        cmd.Parameters.Add("p_MADA_TRACK_ID",         OracleDbType.Varchar2).Value = r.MadaTrackId  ?? string.Empty;
        cmd.Parameters.Add("p_DISCOUNT_CODE",         OracleDbType.Varchar2).Value = r.DiscountCode ?? string.Empty;
        cmd.Parameters.Add("p_DISCOUNT_PERC",         OracleDbType.Decimal ).Value = r.DiscountPerc ?? 0;

        var outputParam = new OracleParameter("p_error_msg", OracleDbType.Varchar2, 4000)
        {
            Direction = ParameterDirection.Output
        };
        cmd.Parameters.Add(outputParam);

        await cmd.ExecuteNonQueryAsync();

        string? result = outputParam.Value?.ToString()?.Trim();
        Console.WriteLine($"  [SP]   p_error_msg = '{result ?? "NULL"}'");

        if (result == "0")
        {
            // Commit so the row is visible to every other session immediately
            using var commitCmd = conn.CreateCommand();
            commitCmd.CommandText = "COMMIT";
            await commitCmd.ExecuteNonQueryAsync();
            return true;
        }
        else
        {
            // Roll back so the pooled connection is returned in a clean state
            using var rollbackCmd = conn.CreateCommand();
            rollbackCmd.CommandText = "ROLLBACK";
            await rollbackCmd.ExecuteNonQueryAsync();
            Console.WriteLine($"  [WARN] SP returned non-zero result, rolled back.");
            return false;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [ERROR] Oracle insert failed: {ex.Message}");
        try
        {
            // Best-effort rollback to clean the pooled connection
            using var rollbackCmd = conn.CreateCommand();
            rollbackCmd.CommandText = "ROLLBACK";
            await rollbackCmd.ExecuteNonQueryAsync();
        }
        catch { /* ignore secondary failure */ }
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
