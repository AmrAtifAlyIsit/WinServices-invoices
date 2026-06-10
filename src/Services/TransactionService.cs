using MongoDB.Bson;
using Oracle.ManagedDataAccess.Client;
using System.Data;

namespace EthraiOrderFixService.Services;

public class TransactionInvoice
{
    public string? TRAINEE_CODE { get; set; }
    public double NETAMOUNT { get; set; }
    public double TOTAL_TAXABLE_AMOUNT { get; set; }
    public double TOTAL_VAT { get; set; }
    public double INVOICE_TOTAL_AMOUNT { get; set; }
    public string? PROGRAM_NAME { get; set; }
    public int PROGRAM_ID { get; set; }
    public string? INVOICE_NO { get; set; }
    public string? INVOICE_DATE { get; set; }
    public string? PAYMENT_TYPE_DESC { get; set; }
    public string? SADAD_BILL_NO { get; set; }
    public string INVOICE_NOTE { get; set; } = "";

    // New fields - pending DB team to update the stored procedure
    public string? MADA_TRACK_ID { get; set; }   // From Order.Invoice.MadaInfo.TrackId
    public string? DISCOUNT_CODE { get; set; }    // From Order.CouponName
    public double? DISCOUNT_PERC { get; set; }    // From Coupon.Value.PercValue (looked up by CouponName)

}

public class TransactionService
{
    private readonly ILogger<TransactionService> _logger;
    string oracleConnectionString = "User Id=ETraining_Services;Password=etraining8642;Data Source=(DESCRIPTION =(ADDRESS = (PROTOCOL = TCP)(HOST = exa1-scan)(PORT = 1521))(CONNECT_DATA = (SERVER = DEDICATED)(SERVICE_NAME = IPS)))";

    public TransactionService(ILogger<TransactionService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> SaveTransactionInvoiceToOracle(TransactionInvoice invoice, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new OracleConnection(oracleConnectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "ETRAINING_SERVICES.ins_eth_trne_invoices";

            // Add input parameters
            command.Parameters.Add("p_invoice_no", OracleDbType.Varchar2).Value = invoice.INVOICE_NO ?? string.Empty;
            command.Parameters.Add("p_invoice_date", OracleDbType.Varchar2).Value = invoice.INVOICE_DATE?.Trim() ?? string.Empty;
            command.Parameters.Add("p_trainee_code", OracleDbType.Varchar2).Value = invoice.TRAINEE_CODE?.Trim() ?? string.Empty;
            command.Parameters.Add("p_payment_type_desc", OracleDbType.Varchar2).Value = invoice.PAYMENT_TYPE_DESC?.Trim() ?? string.Empty;
            command.Parameters.Add("p_sadad_bill_no", OracleDbType.Varchar2).Value = invoice.SADAD_BILL_NO?.Trim() ?? string.Empty;
            command.Parameters.Add("p_invoice_note", OracleDbType.Varchar2).Value = invoice.INVOICE_NOTE?.Trim() ?? string.Empty;
            command.Parameters.Add("p_program_id", OracleDbType.Int32).Value = invoice.PROGRAM_ID;
            command.Parameters.Add("p_program_name", OracleDbType.Varchar2).Value = invoice.PROGRAM_NAME?.Trim() ?? string.Empty;
            command.Parameters.Add("p_total_taxable_amount", OracleDbType.Decimal).Value = invoice.TOTAL_TAXABLE_AMOUNT;
            command.Parameters.Add("p_total_vat", OracleDbType.Decimal).Value = invoice.TOTAL_VAT;
            command.Parameters.Add("p_invoice_total_amount", OracleDbType.Decimal).Value = invoice.INVOICE_TOTAL_AMOUNT;
            command.Parameters.Add("p_NET_AMOUNT", OracleDbType.Decimal).Value = invoice.NETAMOUNT;

            // TODO: Uncomment when DB team adds these parameters to the stored procedure
            command.Parameters.Add("p_MADA_TRACK_ID", OracleDbType.Varchar2).Value = invoice.MADA_TRACK_ID?.Trim() ?? string.Empty;
            command.Parameters.Add("p_DISCOUNT_CODE", OracleDbType.Varchar2).Value = invoice.DISCOUNT_CODE?.Trim() ?? string.Empty;
            command.Parameters.Add("p_DISCOUNT_PERC", OracleDbType.Decimal).Value = invoice.DISCOUNT_PERC ?? 0;

            // Add output parameter
            var outputParam = new OracleParameter("p_error_msg", OracleDbType.Varchar2, 4000)
            {
                Direction = ParameterDirection.Output
            };
            command.Parameters.Add(outputParam);

            await command.ExecuteNonQueryAsync();

            // Check result
            string? result = outputParam.Value?.ToString()?.Trim();
            return result == "0";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inserting invoice: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> InvoiceExists(string invoiceNo, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new OracleConnection(oracleConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM eth_trne_invoices WHERE invoice_no = :invoice_no";
            command.Parameters.Add("invoice_no", OracleDbType.Varchar2).Value = invoiceNo;

            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            return count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking invoice existence: {ex.Message}");
            return false; // treat errors as "not exists" or handle differently
        }
    }

    public async Task<bool> InvoiceDetailExists(string invoiceNo, int programId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new OracleConnection(oracleConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM eth_trne_invoices WHERE invoice_no = :invoice_no AND program_id = :program_id";
            command.Parameters.Add("invoice_no", OracleDbType.Varchar2).Value = invoiceNo;
            command.Parameters.Add("program_id", OracleDbType.Int32).Value = programId;

            var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            return count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking invoice detail existence: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> InvoiceDetailExistsByNote(string invoiceNo, string invoiceNote, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new OracleConnection(oracleConnectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM eth_trne_invoices WHERE invoice_no = :invoice_no AND invoice_note = :invoice_note";
            command.Parameters.Add("invoice_no", OracleDbType.Varchar2).Value = invoiceNo;
            command.Parameters.Add("invoice_note", OracleDbType.Varchar2).Value = invoiceNote;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var count = Convert.ToInt32(result);
            
            _logger.LogInformation("[ORACLE CHECK] Invoice: {inv} | Detail ID: {detailId} | Count in DB: {count} | Will Skip: {exists}", 
                invoiceNo, invoiceNote, count, count > 0);
            
            return count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking invoice detail - Invoice: {inv}, Detail: {note}", invoiceNo, invoiceNote);
            // If error occurs, assume it doesn't exist to allow insertion attempt
            return false;
        }
    }
}
