# Descarrega a PawnIOLib.dll e o módulo assinado SmbusI801.bin (PawnIO, LGPL-2.1)
# tal como o OpenRGB 1.1 os distribui, e confirma o SHA-256. Ficam em
# third_party\pawnio e o build copia-os para junto do executável.
$ErrorActionPreference = 'Stop'
$base = 'https://gitlab.com/CalcProgrammer1/OpenRGB/-/raw/release_1.1/dependencies/PawnIO'
$files = @(
    @{ Name = 'PawnIOLib.dll'; Url = "$base/PawnIOLib.dll";         Sha = 'c6e7c2f54643ef9cb51b9a3ec7b0efc9e204803c206f33014e39f281546ea9b4' },
    @{ Name = 'SmbusI801.bin'; Url = "$base/modules/SmbusI801.bin"; Sha = 'a0f7d066e7efda28c0e754c1f52dbd8dc280d388ba8575b514a80cb67490530c' }
)
$dir = Join-Path $PSScriptRoot '..\third_party\pawnio'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
foreach ($f in $files) {
    $dest = Join-Path $dir $f.Name
    if (-not (Test-Path $dest) -or (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -ne $f.Sha) {
        Write-Host "  downloading $($f.Name)"
        Invoke-WebRequest -Uri $f.Url -OutFile $dest -UseBasicParsing
    }
    $hash = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $f.Sha) {
        Remove-Item $dest -Force
        throw "$($f.Name): SHA-256 mismatch ($hash)"
    }
    Write-Host "  [OK] $($f.Name)"
}
