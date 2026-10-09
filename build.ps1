<#
.SYNOPSIS
    Membangun kedua flavor (Server dan Client) sekaligus.

.EXAMPLE
    .\build.ps1                    # Debug, Server lalu Client
    .\build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$failed = @()
foreach ($flavor in 'Server', 'Client') {
    Write-Host "=== Build $flavor ($Configuration) ===" -ForegroundColor Cyan
    dotnet build TLIGDashboard.csproj -c $Configuration -p:Flavor=$flavor -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { $failed += $flavor }
}

if ($failed.Count -gt 0) {
    Write-Host "Gagal: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "Server dan Client berhasil dibangun." -ForegroundColor Green
