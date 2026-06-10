-- Delete all invoices for today (2026/01/06)
DELETE FROM eth_trne_invoices WHERE invoice_date = '2026/01/06';
COMMIT;

-- Verify deletion
SELECT COUNT(*) as "Remaining Records for Today" FROM eth_trne_invoices WHERE invoice_date = '2026/01/06';
