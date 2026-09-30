param(
    [string]$OutDir,
    [string[]]$Files
)
$key = [byte[]](0xC0, 0xDE, 0xFA, 0xCE, 0xBA, 0xAD, 0xF0, 0x0D, 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xD0, 0x0D)
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
foreach ($f in $Files) {
    $name = [System.IO.Path]::GetFileName($f)
    $dest = Join-Path $OutDir $name
    if (-not (Test-Path $f)) { Write-Host "SKIP $name (not found)"; continue }
    $data = [System.IO.File]::ReadAllBytes($f)
    for ($i = 0; $i -lt $data.Length; $i++) { $data[$i] = $data[$i] -bxor $key[$i % $key.Length] }
    [System.IO.File]::WriteAllBytes($dest, $data)
    Write-Host "Encrypted $name -> $dest"
}
