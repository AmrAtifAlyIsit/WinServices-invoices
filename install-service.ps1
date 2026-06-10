# Install Ethrai Order Fix Service as Windows Service
# Run this script as Administrator

$serviceName = "EthraiOrderFixService"
$displayName = "Ethrai Order Fix Service"
$description = "Background service to sync invoice orders from MongoDB to Oracle DB"
$exePath = "E:\supportScript\EthraiOrderInvoiceService\EthraiOrderFixService\publish\EthraiOrderFixService.exe"

# Check if running as Administrator
if (-NOT ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Warning "Please run this script as Administrator!"
    exit
}

# Check if service already exists
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host "Service already exists. Stopping and removing..." -ForegroundColor Yellow
    Stop-Service -Name $serviceName -Force
    sc.exe delete $serviceName
    Start-Sleep -Seconds 2
}

# Create the service
Write-Host "Creating Windows Service..." -ForegroundColor Green
sc.exe create $serviceName binPath= $exePath start= auto DisplayName= $displayName

# Set description
sc.exe description $serviceName $description

# Start the service
Write-Host "Starting service..." -ForegroundColor Green
Start-Service -Name $serviceName

# Display service status
Get-Service -Name $serviceName | Format-Table -AutoSize

Write-Host "`nService installed successfully!" -ForegroundColor Green
Write-Host "Service Name: $serviceName" -ForegroundColor Cyan
Write-Host "Status: " -NoNewline
Get-Service -Name $serviceName | Select-Object -ExpandProperty Status
