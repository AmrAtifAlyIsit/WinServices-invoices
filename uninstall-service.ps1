# Uninstall Ethrai Order Fix Service
# Run this script as Administrator

$serviceName = "EthraiOrderFixService"

# Check if running as Administrator
if (-NOT ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
    Write-Warning "Please run this script as Administrator!"
    exit
}

# Check if service exists
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host "Stopping service..." -ForegroundColor Yellow
    Stop-Service -Name $serviceName -Force
    
    Write-Host "Removing service..." -ForegroundColor Yellow
    sc.exe delete $serviceName
    
    Write-Host "Service removed successfully!" -ForegroundColor Green
} else {
    Write-Host "Service does not exist." -ForegroundColor Red
}
