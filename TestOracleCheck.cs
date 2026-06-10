using Oracle.ManagedDataAccess.Client;

var connectionString = "User Id=ETraining_Services;Password=etraining8642;Data Source=(DESCRIPTION =(ADDRESS = (PROTOCOL = TCP)(HOST = exa1-scan)(PORT = 1521))(CONNECT_DATA = (SERVER = DEDICATED)(SERVICE_NAME = IPS)))";

var invoiceNo = "700202600653462294";
var invoiceNote = "1c448c3a-1a36-40b6-a2a3-0b7295da2f44";

try
{
    using var connection = new OracleConnection(connectionString);
    await connection.OpenAsync();
    Console.WriteLine("✅ Connected to Oracle successfully");

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM eth_trne_invoices WHERE invoice_no = :invoice_no AND invoice_note = :invoice_note";
    command.Parameters.Add("invoice_no", OracleDbType.Varchar2).Value = invoiceNo;
    command.Parameters.Add("invoice_note", OracleDbType.Varchar2).Value = invoiceNote;

    var count = Convert.ToInt32(await command.ExecuteScalarAsync());
    
    Console.WriteLine($"Invoice: {invoiceNo}");
    Console.WriteLine($"Note: {invoiceNote}");
    Console.WriteLine($"Count in Oracle: {count}");
    Console.WriteLine($"Exists: {count > 0}");
    
    if (count == 0)
    {
        Console.WriteLine("\n✅ CONFIRMED: Record does NOT exist in Oracle");
    }
    else
    {
        Console.WriteLine($"\n❌ WARNING: {count} record(s) found in Oracle");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();
