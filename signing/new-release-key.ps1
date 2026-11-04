<#
Erzeugt den Release-Signaturschlüssel von KeeLiveSync (RSA 3072) – einmalig.
- Privater Schlüssel: außerhalb des Repos (Standard %USERPROFILE%\.keelivesync\release-signing-key.xml),
  nur für den aktuellen Benutzer lesbar. Zusätzlich in KeePass sichern; nie auf GitHub hochladen.
- Öffentlicher Schlüssel: src/KeeLiveSync/ReleaseKey.cs (wird ins Plugin eingebaut).
KeePass akzeptiert die Versionsdatei nur, wenn sie mit diesem Schlüssel signiert ist (signing/sign-version.ps1);
Releases werden mit signing/sign-release.ps1 signiert.
#>
param(
    [string]$KeyPath = (Join-Path $env:USERPROFILE ".keelivesync\release-signing-key.xml")
)
$ErrorActionPreference = "Stop"

if (Test-Path $KeyPath) { throw "Schlüssel existiert bereits: $KeyPath (wird nicht überschrieben)" }

$rsa = New-Object System.Security.Cryptography.RSACng 3072
$private = $rsa.ToXmlString($true)
$public = $rsa.ToXmlString($false)
$rsa.Dispose()

$dir = Split-Path $KeyPath
New-Item -ItemType Directory -Force $dir | Out-Null
[IO.File]::WriteAllText($KeyPath, $private, (New-Object Text.UTF8Encoding($false)))
# Nur der aktuelle Benutzer darf die Datei lesen
icacls $KeyPath /inheritance:r /grant:r "$($env:USERDOMAIN)\$($env:USERNAME):F" | Out-Null

$cs = Join-Path $PSScriptRoot "..\src\KeeLiveSync\ReleaseKey.cs"
$code = @"
namespace KeeLiveSync
{
    /// <summary>Öffentlicher Teil des Release-Signaturschlüssels (erzeugt von signing/new-release-key.ps1)</summary>
    internal static class ReleaseKey
    {
        internal const string PublicKeyXml = "$public";
    }
}
"@
[IO.File]::WriteAllText($cs, $code.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object Text.UTF8Encoding($true)))

$fingerprint = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes($public))) -Algorithm SHA256).Hash.Substring(0, 16)
""
"Privater Schlüssel: $KeyPath"
"Öffentlicher Schlüssel eingetragen in src/KeeLiveSync/ReleaseKey.cs"
"Fingerabdruck (öffentlich): $fingerprint"
""
"Wichtig: Die Datei mit dem privaten Schlüssel zusätzlich als Anhang in KeePass sichern."
"Geht sie verloren, kann keine neue Version mehr signiert werden, die ältere Plugins als gültig erkennen."
