<#
Signiert version.txt für die Update-Prüfung von KeePass (RSA/SHA-512 mit dem Release-Schlüssel).
Format laut KeePass: erste Zeile = Trennzeichen ":" + Base64-Signatur; signiert werden alle getrimmten,
nicht leeren Zeilen zwischen Kopf- und Fußzeile, jeweils mit "\n" abgeschlossen, UTF-8.
Das Plugin registriert den öffentlichen Schlüssel (ReleaseKey.cs) per UpdateCheckEx.SetFileSigKey.
Aufruf (vor dem Commit der neuen Version): .\signing\sign-version.ps1
#>
param(
    [string]$KeyPath = (Join-Path $env:USERPROFILE ".keelivesync\release-signing-key.xml"),
    [string]$VersionFile = (Join-Path $PSScriptRoot "..\version.txt")
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $KeyPath)) { throw "Privater Schlüssel fehlt: $KeyPath" }

$lines = [IO.File]::ReadAllText((Resolve-Path $VersionFile)).Replace("`r`n", "`n").Split("`n")
$body = @($lines | Select-Object -Skip 1 | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($body.Count -lt 2 -or $body[-1] -ne ":") { throw "version.txt hat nicht das erwartete Format (Kopfzeile, Einträge, Fußzeile ':')" }
$entries = $body[0..($body.Count - 2)]
$data = [Text.Encoding]::UTF8.GetBytes((($entries -join "`n") + "`n"))

$rsa = New-Object System.Security.Cryptography.RSACng
$rsa.FromXmlString([IO.File]::ReadAllText($KeyPath))
$sig = $rsa.SignData($data, [Security.Cryptography.HashAlgorithmName]::SHA512, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$rsa.Dispose()

# Gegenprobe mit dem öffentlichen Schlüssel im Plugin
$cs = Get-Content (Join-Path $PSScriptRoot "..\src\KeeLiveSync\ReleaseKey.cs") -Raw
$public = [regex]::Match($cs, '<RSAKeyValue>.*?</RSAKeyValue>').Value
$check = New-Object System.Security.Cryptography.RSACng
$check.FromXmlString($public)
$ok = $check.VerifyData($data, $sig, [Security.Cryptography.HashAlgorithmName]::SHA512, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$check.Dispose()
if (-not $ok) { throw "Privater Schlüssel passt nicht zu ReleaseKey.cs" }

$text = ":" + [Convert]::ToBase64String($sig) + "`n" + ($entries -join "`n") + "`n:`n"
[IO.File]::WriteAllText((Resolve-Path $VersionFile), $text, (New-Object Text.UTF8Encoding($false)))
"version.txt signiert: " + ($entries -join ", ")
