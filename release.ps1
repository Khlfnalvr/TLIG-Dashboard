# release.ps1 - Build, package, and create installers for TLIG Dashboard
#
# Usage:
#   .\release.ps1                    # full build (Server + Client)
#   .\release.ps1 -Flavor Server     # one flavor only
#   .\release.ps1 -SkipBuild         # re-package from existing publish output
#   .\release.ps1 -SkipInstaller     # skip Inno Setup (ZIP only)
#   .\release.ps1 -SkipZip           # skip update ZIPs (installer only)
#
# Requirements:
#   - .NET 10 SDK   (dotnet)
#   - Inno Setup 6  (ISCC.exe) for installer creation
#   - cloudflared.exe in this folder for the Server build (auto-bundled)

param(
    [ValidateSet('Server', 'Client', 'Both')]
    [string] $Flavor = 'Both',
    [switch] $SkipBuild,
    [switch] $SkipInstaller,
    [switch] $SkipZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Version      = "1.0.0-Hotel"
$Project      = Join-Path $PSScriptRoot "TLIGDashboard.csproj"
$PublishRoot  = Join-Path $PSScriptRoot "publish"
$InnoCompiler = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

$Flavors = if ($Flavor -eq 'Both') { @('Server', 'Client') } else { @($Flavor) }

$InstallerScript = @{
    Server = Join-Path $PSScriptRoot "installer_server.iss"
    Client = Join-Path $PSScriptRoot "installer_client.iss"
}

New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

# --- Build -------------------------------------------------------------------
if (-not $SkipBuild) {
    foreach ($f in $Flavors) {
        Write-Host ""
        Write-Host "=== Building $f (Release) ===" -ForegroundColor Cyan
        $out = Join-Path $PublishRoot $f
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }

        $publishArgs = @(
            'publish', $Project,
            '-c', 'Release',
            "-p:Flavor=$f",
            '-o', $out
        )
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "Build failed for flavor '$f' (exit $LASTEXITCODE)" }
        Write-Host "  Output: $out" -ForegroundColor Green
    }
}

# --- Create update ZIPs ------------------------------------------------------
if (-not $SkipZip) {
    Add-Type -Assembly System.IO.Compression.FileSystem

    foreach ($f in $Flavors) {
        Write-Host ""
        Write-Host "=== Packaging $f update ZIP ===" -ForegroundColor Cyan
        $src = Join-Path $PublishRoot $f
        if (-not (Test-Path $src)) {
            Write-Warning "Publish output not found at '$src'. Run without -SkipBuild first."
            continue
        }
        $zipName = "TLIGDashboard-$f-v$Version-Update.zip"
        $zipPath = Join-Path $PublishRoot $zipName
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($src, $zipPath, 'Optimal', $false)
        $sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
        Write-Host "  Created: $zipPath ($sizeMb MB)" -ForegroundColor Green
    }
}

# --- Create installers -------------------------------------------------------
if (-not $SkipInstaller) {
    if (-not (Test-Path $InnoCompiler)) {
        Write-Warning "Inno Setup 6 not found at: $InnoCompiler"
        Write-Warning "Install from https://jrsoftware.org/isdl.php or skip with -SkipInstaller."
    } else {
        foreach ($f in $Flavors) {
            $iss = $InstallerScript[$f]
            Write-Host ""
            Write-Host "=== Compiling installer for $f ===" -ForegroundColor Cyan
            & $InnoCompiler $iss
            if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for '$iss' (exit $LASTEXITCODE)" }
            Write-Host "  Installer done: $f" -ForegroundColor Green
        }
    }
}

# --- Summary -----------------------------------------------------------------
Write-Host ""
Write-Host "=== Release v$Version complete ===" -ForegroundColor Green
Write-Host "Artifacts in: $PublishRoot"
Get-ChildItem $PublishRoot -File -ErrorAction SilentlyContinue |
    Select-Object Name, @{N='Size (MB)'; E={ [math]::Round($_.Length/1MB,1) }} |
    Format-Table -AutoSize
