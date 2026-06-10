# Quick script to check if invoices exist in Oracle
$invoices = @(
    "700202600637265320",
    "700202600658866322",
    "700202600644689059",
    "700202600653462294"
)

Write-Host "Checking Oracle DB for invoices..." -ForegroundColor Cyan
Write-Host ""

foreach ($inv in $invoices) {
    $query = "SELECT COUNT(*) as CNT FROM eth_trne_invoices WHERE invoice_no = '$inv'"
    Write-Host "Invoice: $inv" -ForegroundColor Yellow
    
    # You can manually run this in SQL Developer or Oracle client
    Write-Host "  SQL: $query"
    Write-Host ""
}

Write-Host "`nTo delete these invoices from Oracle, run:" -ForegroundColor Green
foreach ($inv in $invoices) {
    Write-Host "DELETE FROM eth_trne_invoices WHERE invoice_no = '$inv';" -ForegroundColor White
}
