# Capture the Nihon Kohden central station (QP918P) commanding the live BSM-3562.
# Run this in an ELEVATED PowerShell (Run as administrator). It records every packet to/from
# the monitor while you press "NIBP Start" in the station, then writes a .pcapng we can parse.
param(
    [string]$MonitorIp = '10.10.46.48',
    [int]$Seconds = 90,
    [string]$OutDir = "$PSScriptRoot"
)

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "NOT ELEVATED - right-click PowerShell > Run as administrator, then run this again." -ForegroundColor Red; exit 1 }

$stamp = Get-Date -Format 'HHmmss'
$etl = Join-Path $OutDir "nibp-$stamp.etl"
$pcap = Join-Path $OutDir "nibp-$stamp.pcapng"

Write-Host "Resetting pktmon filters ..." -ForegroundColor Cyan
pktmon filter remove | Out-Null
pktmon filter add NibpMon -i $MonitorIp | Out-Null
pktmon filter list

Write-Host "`nStarting capture -> $etl" -ForegroundColor Cyan
pktmon start --capture --pkt-size 0 --file-name $etl | Out-Null

Write-Host "`n==================== DO THIS NOW ====================" -ForegroundColor Yellow
Write-Host " 1) Launch the station:  C:\NIHON KOHDEN\QP918P\QP918P.exe"
Write-Host " 2) Make sure bed ICU-001 (10.10.46.48) is shown/admitted"
Write-Host " 3) Select that bed and press  NIBP Start  (confirm the dialog)"
Write-Host " 4) Wait for the reading, then you can press NIBP Stop"
Write-Host "=====================================================" -ForegroundColor Yellow

for ($i = $Seconds; $i -gt 0; $i--) { Write-Host -NoNewline ("`r  capturing... {0,3}s left " -f $i); Start-Sleep 1 }
Write-Host "`nStopping capture ..." -ForegroundColor Cyan
pktmon stop | Out-Null
pktmon filter remove | Out-Null

Write-Host "Converting to pcapng ..." -ForegroundColor Cyan
pktmon pcapng $etl -o $pcap | Out-Null

if (Test-Path $pcap) {
    Write-Host "`nDONE. Capture written to:" -ForegroundColor Green
    Write-Host "  $pcap"
    Write-Host "Tell Claude the path and it will extract the NIBP Start command."
} else {
    Write-Host "pcapng conversion failed; the .etl is at $etl" -ForegroundColor Red
}
