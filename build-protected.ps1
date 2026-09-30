$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

$buildOut = "bin\Release\net8.0-windows\win-x64"
$confusedOut = "bin\Release\net8.0-windows\win-x64\bin\Confused"
$publishOut = "bin\Release\net8.0-windows\win-x64\publish"

# --- Step 1: Build ---
Write-Host "`n=== Step 1: Building project ===" -ForegroundColor Cyan
dotnet build "$projectDir\CoreX.Loader.csproj" -c Release -r win-x64
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed!" -ForegroundColor Red; exit 1 }

# --- Step 2: Obfuscate with ConfuserEx ---
$confuserPaths = @(
    "$projectDir\ConfuserEx\Confuser.CLI.exe",
    "$env:USERPROFILE\Desktop\ConfuserEx\Confuser.CLI.exe",
    "$env:ProgramFiles\ConfuserEx\Confuser.CLI.exe",
    "C:\Tools\ConfuserEx\Confuser.CLI.exe"
)
$confuserCli = $null
foreach ($p in $confuserPaths) {
    if (Test-Path $p) { $confuserCli = $p; break }
}

if ($confuserCli) {
    Write-Host "`n=== Step 2: Obfuscating with ConfuserEx ===" -ForegroundColor Cyan
    & $confuserCli "$projectDir\CoreX.Loader.crproj"
    if ($LASTEXITCODE -ne 0) { Write-Host "ConfuserEx failed! Continuing without obfuscation..." -ForegroundColor Yellow }
    else {
        Write-Host "Copying obfuscated DLL back to build output..." -ForegroundColor Green
        Copy-Item "$confusedOut\CoreX.Loader.dll" "$buildOut\CoreX.Loader.dll" -Force
        Write-Host "Obfuscation applied successfully." -ForegroundColor Green
    }
} else {
    Write-Host "`n=== Step 2: SKIPPED (ConfuserEx not found) ===" -ForegroundColor Yellow
    Write-Host "Download ConfuserEx from: https://github.com/mkaring/ConfuserEx/releases"
    Write-Host "Extract to one of these locations:"
    foreach ($p in $confuserPaths) { Write-Host "  - $p" }
}

# --- Step 3: Publish single-file ---
Write-Host "`n=== Step 3: Publishing single-file .exe ===" -ForegroundColor Cyan
dotnet publish "$projectDir\CoreX.Loader.csproj" -c Release -r win-x64 --self-contained --no-build
if ($LASTEXITCODE -ne 0) { Write-Host "Publish failed!" -ForegroundColor Red; exit 1 }

$exe = Join-Path $publishOut "CoreX.Loader.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "`n=== BUILD COMPLETE ===" -ForegroundColor Green
    Write-Host "Output: $exe" -ForegroundColor Green
    Write-Host "Size: ${size} MB" -ForegroundColor Green

    # Copy to corexfinal
    $dest = "$env:USERPROFILE\Desktop\corexfinal\CoreX.Loader.exe"
    Copy-Item $exe $dest -Force
    Write-Host "Copied to: $dest" -ForegroundColor Green
} else {
    Write-Host "Published .exe not found!" -ForegroundColor Red
}
