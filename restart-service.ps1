# Restart Ethrai Order Fix Service
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
    Write-Host "Restarting service..." -ForegroundColor Yellow
    Restart-Service -Name $serviceName -Force
    
    Start-Sleep -Seconds 2
    
    # Display service status
    Get-Service -Name $serviceName | Format-Table -AutoSize
    
    Write-Host "Service restarted successfully!" -ForegroundColor Green
} else {
    Write-Host "Service does not exist. Please install it first using install-service.ps1" -ForegroundColor Red
}
