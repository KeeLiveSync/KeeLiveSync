<#
Signiert ein von der GitHub-Action angelegtes Release (Entwurf) und veröffentlicht es.
1. Lädt alle Dateien des Releases und prüft sie gegen SHA256SUMS.txt (jede Datei gelistet, Prüfsummen stimmen,
   KeeLiveSync-<Version>.zip vorhanden, Version = Tag).
2. Signiert SHA256SUMS.txt mit dem privaten Release-Schlüssel und prüft die Signatur gegen ReleaseKey.cs.
3. Lädt SHA256SUMS.txt.sig hoch und veröffentlicht den Entwurf als neuestes Release.
Aufruf: .\signing\sign-release.ps1 v0.1.1
        .\signing\sign-release.ps1 v0.1.1 -DryRun   (nur prüfen)
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$KeyPath = (Join-Path $env:USERPROFILE ".keelivesync\release-signing-key.xml"),
    [string]$Repo = "blacksun80/KeeLiveSync",
    # Nur prüfen und signieren, nichts hochladen oder veröffentlichen
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $KeyPath)) { throw "Privater Schlüssel fehlt: $KeyPath" }
$version = $Tag.TrimStart("v")

$tmp = Join-Path $env:TEMP ("keelivesync-sign-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $tmp | Out-Null
try {
    gh release download $Tag --repo $Repo --dir $tmp
    if ($LASTEXITCODE) { throw "Download des Releases $Tag fehlgeschlagen" }

    $sumsPath = Join-Path $tmp "SHA256SUMS.txt"
    if (-not (Test-Path $sumsPath)) { throw "SHA256SUMS.txt fehlt im Release" }
    $listed = @{}
    foreach ($line in Get-Content $sumsPath) {
        if (-not $line.Trim()) { continue }
        $hash, $name = $line.Trim() -split "\s+", 2
        $file = Join-Path $tmp $name
        if (-not (Test-Path $file)) { throw "Gelistete Datei fehlt im Release: $name" }
        if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLower() -ne $hash.ToLower()) { throw "Prüfsumme falsch: $name" }
        $listed[$name] = $hash.ToLower()
    }
    Get-ChildItem $tmp -File | Where-Object { $_.Name -notin "SHA256SUMS.txt", "SHA256SUMS.txt.sig" } | ForEach-Object {
        if (-not $listed.ContainsKey($_.Name)) { throw "Datei nicht in SHA256SUMS.txt: $($_.Name)" }
    }
    $zip = "KeeLiveSync-$version.zip"
    if (-not $listed.ContainsKey($zip)) { throw "$zip fehlt (Tag passt nicht zur Version?)" }
    if (-not $listed.ContainsKey("KeeLiveSync.dll")) { throw "KeeLiveSync.dll fehlt" }
    $others = $listed.Keys | Where-Object { $_ -like "KeeLiveSync-*.zip" -and $_ -ne $zip }
    if ($others) { throw "Mehrere versionierte Pakete gelistet: $($others -join ', ')" }
    $dllVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $tmp "KeeLiveSync.dll")).FileVersion
    if (-not ($dllVersion -eq $version -or $dllVersion -eq "$version.0")) { throw "KeeLiveSync.dll hat Version $dllVersion, Tag ist $version" }

    $data = [IO.File]::ReadAllBytes($sumsPath)
    $rsa = New-Object System.Security.Cryptography.RSACng
    $rsa.FromXmlString([IO.File]::ReadAllText($KeyPath))
    $sig = $rsa.SignData($data, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $rsa.Dispose()

    # Gegenprobe mit dem öffentlichen Schlüssel, der ins Plugin eingebaut ist
    $cs = Get-Content (Join-Path $PSScriptRoot "..\src\KeeLiveSync\ReleaseKey.cs") -Raw
    $public = [regex]::Match($cs, '<RSAKeyValue>.*?</RSAKeyValue>').Value
    if (-not $public) { throw "Kein öffentlicher Schlüssel in ReleaseKey.cs" }
    $check = New-Object System.Security.Cryptography.RSACng
    $check.FromXmlString($public)
    $ok = $check.VerifyData($data, $sig, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $check.Dispose()
    if (-not $ok) { throw "Privater Schlüssel passt nicht zu ReleaseKey.cs" }

    if ($DryRun) { "Probelauf: $($listed.Count) Dateien geprüft, Signatur passt zu ReleaseKey.cs. Nichts hochgeladen."; return }
    $sigPath = Join-Path $tmp "SHA256SUMS.txt.sig"
    [IO.File]::WriteAllBytes($sigPath, $sig)
    gh release upload $Tag $sigPath --repo $Repo --clobber
    if ($LASTEXITCODE) { throw "Hochladen der Signatur fehlgeschlagen" }
    gh release edit $Tag --repo $Repo --draft=false --latest
    if ($LASTEXITCODE) { throw "Veröffentlichen fehlgeschlagen" }
    "Release $Tag signiert und veröffentlicht ($($listed.Count) Dateien geprüft)."
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
