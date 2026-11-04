<#
Erzeugt Screenshots der KeeLiveSync-Dialoge mit Beispieldaten (keine echten Konten oder Datenbanken).
Die Dialoge werden außerhalb des Bildschirms geöffnet, mit Beispielinhalten gefüllt und als PNG gespeichert.
Aufruf (STA nötig): powershell -STA -File tools\screenshots.ps1 [-Culture en-US] [-Out docs\screenshots]
#>
param(
    [string]$Culture = "en-US",
    [string]$Out = (Join-Path $PSScriptRoot "..\docs\screenshots"),
    [string]$KeePassDir = (Join-Path $env:USERPROFILE "KeeLiveSyncDEV"),
    [string]$Dll = (Join-Path $PSScriptRoot "..\src\KeeLiveSync\bin\Release\net48\KeeLiveSync.dll")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$null = [Reflection.Assembly]::LoadFrom((Join-Path $KeePassDir "KeePass.exe"))
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Dll))
[Threading.Thread]::CurrentThread.CurrentUICulture = $Culture
[Threading.Thread]::CurrentThread.CurrentCulture = $Culture
New-Item -ItemType Directory -Force $Out | Out-Null
$F = [Reflection.BindingFlags]"Instance,Static,NonPublic,Public"

function Get-Field($obj, $name) { $obj.GetType().GetField($name, $F).GetValue($obj) }
function Set-Field($obj, $name, $value) { $obj.GetType().GetField($name, $F).SetValue($obj, $value) }
function Pump($ms = 300) { $end = [DateTime]::Now.AddMilliseconds($ms); while ([DateTime]::Now -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 } }
function Show-Offscreen($form) {
    $form.StartPosition = "Manual"; $form.Location = New-Object Drawing.Point -6000, -6000; $form.ShowInTaskbar = $false
    $form.Show(); Pump 400
}
function Save-Form($form, $name) {
    Pump 200
    $bmp = New-Object Drawing.Bitmap $form.Width, $form.Height
    $form.DrawToBitmap($bmp, (New-Object Drawing.Rectangle 0, 0, $form.Width, $form.Height))
    $file = Join-Path $Out $name
    $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "gespeichert: $file"
}
function New-Item-Drive($name, $folder, $size, $minutesAgo, $by, $owner, $children) {
    $t = $asm.GetType("KeeLiveSync.DriveItem")
    $d = [Activator]::CreateInstance($t, $true)
    $t.GetField("Id").SetValue($d, [guid]::NewGuid().ToString())
    $t.GetField("Name").SetValue($d, $name)
    $t.GetField("IsFolder").SetValue($d, $folder)
    $t.GetField("Size").SetValue($d, [long]$size)
    $t.GetField("ChildCount").SetValue($d, [int]$children)
    $t.GetField("Modified").SetValue($d, [DateTime]::Now.AddMinutes(-$minutesAgo))
    $t.GetField("ModifiedBy").SetValue($d, $by)
    $t.GetField("OwnerName").SetValue($d, $owner)
    $d
}
$Text = $asm.GetType("KeeLiveSync.Text")
function T($de, $en) { $Text.GetMethods($F) | Where-Object { $_.Name -eq "T" -and $_.GetParameters().Count -eq 2 } | ForEach-Object { $_.Invoke($null, @($de, $en)) } }
function Ago($dt) { $Text.GetMethod("Ago", $F).Invoke($null, @($dt)) }
function Plural($n, $d1, $dn, $e1, $en) { $Text.GetMethod("Plural", $F).Invoke($null, @([long]$n, $d1, $dn, $e1, $en)) }
$Theme = $asm.GetType("KeeLiveSync.UI.Theme")
function Avatar($initials, $size) { $Theme.GetMethod("Avatar", $F).Invoke($null, @($initials, [int]$size)) }

# SyncEngine-Platzhalter (die Dialoge lesen nur die Liste offener Sitzungen)
$engType = $asm.GetType("KeeLiveSync.SyncEngine")
$engine = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($engType)
foreach ($fld in $engType.GetFields($F)) {
    if ($fld.FieldType.IsGenericType -and $fld.GetValue($engine) -eq $null) { try { $fld.SetValue($engine, [Activator]::CreateInstance($fld.FieldType)) } catch {} }
}

function Set-DemoAccount($form) {
    Set-Field $form "_loadToken" ((Get-Field $form "_loadToken") + 1000)
    (Get-Field $form "_accountName").Text = "Alex Morgan"
    (Get-Field $form "_accountMail").Text = "alex.morgan@example.com"
    $av = Get-Field $form "_avatar"; $av.Image = Avatar "AM" $av.Width
    (Get-Field $form "_busy").Visible = $false
}

# 1) Öffnen-Dialog: Mein OneDrive
$open = $asm.GetType("KeeLiveSync.UI.OpenForm").GetConstructors($F)[0].Invoke(@($engine))
Show-Offscreen $open
Set-DemoAccount $open
(Get-Field $open "_crumb").Text = "OneDrive  ›  Family"
$list = Get-Field $open "_list"
$list.Items.Clear()
$demo = @(
    (New-Item-Drive "Archive" $true 0 4000 "" "" 3),
    (New-Item-Drive "Shared with Sam" $true 0 900 "" "" 2),
    (New-Item-Drive "Family.kdbx" $false 48211 5 "Sam Morgan" "" 0),
    (New-Item-Drive "Home office.kdbx" $false 23552 1440 "Alex Morgan" "" 0)
)
foreach ($i in $demo) {
    $lvi = New-Object Windows.Forms.ListViewItem($i.Name, $(if ($i.IsFolder) { "folder" } else { "kdbx" }))
    $lvi.Tag = $i
    $when = Ago $i.Modified
    if (-not $i.IsFolder) { $when += " · " + $i.ModifiedBy.Split(' ')[0] }
    [void]$lvi.SubItems.Add($when)
    [void]$lvi.SubItems.Add($(if ($i.IsFolder) { Plural $i.ChildCount "{0} Element" "{0} Elemente" "{0} item" "{0} items" } else { "{0:0.#} KB" -f ($i.Size / 1024) }))
    if (-not $i.IsFolder) { $lvi.Font = $Theme.GetMethod("SemiboldFont", $F).Invoke($null, @([single]9.75)) }
    [void]$list.Items.Add($lvi)
}
(Get-Field $open "_empty").Visible = $false
$list.Items[2].Selected = $true
(Get-Field $open "_open").Enabled = $true
(Get-Field $open "_status").Text = Plural 2 "{0} Datenbank in diesem Ordner" "{0} Datenbanken in diesem Ordner" "{0} database in this folder" "{0} databases in this folder"
Save-Form $open "open-onedrive.png"

# 2) Öffnen-Dialog: Freigabelink (Texte mit „“ in einfachen Anführungszeichen – PowerShell wertet „ und “ als Anführungszeichen)
$open.GetType().GetMethod("ShowPage", $F).Invoke($open, @($false))
$shared = New-Item-Drive "Family.kdbx" $false 48211 5 "Sam Morgan" "Sam Morgan" 0
$open.GetType().GetMethod("ShowLinkChip", $F).Invoke($open, @("https://1drv.ms/u/c/0a1b2c3d4e5f6a7b/EXAMPLEexampleEXAMPLE", $shared))
(Get-Field $open "_linkCardTitle").Text = "Family.kdbx"
(Get-Field $open "_linkCardInfo").Text = (T "geteilt von Sam Morgan" "shared by Sam Morgan") + "  ·  " + (T "geändert " "modified ") + (Ago $shared.Modified) + "  ·  47.1 KB"
(Get-Field $open "_linkCard").Visible = $true
(Get-Field $open "_open").Enabled = $true
(Get-Field $open "_status").Text = T 'Gefunden – mit „Öffnen“ verknüpfen.' 'Found – press "Open" to link it.'
Save-Form $open "open-shared-link.png"
$open.Close()

# 3) Optionen
$opt = $asm.GetType("KeeLiveSync.UI.OptionsForm").GetConstructors($F)[0].Invoke(@($engine))
Show-Offscreen $opt
$acc = Get-Field $opt "_accounts"
$acc.Items.Clear(); $acc.SmallImageList.Images.Clear()
$acc.SmallImageList.Images.Add("demo", (Avatar "AM" $acc.SmallImageList.ImageSize.Width))
[void]$acc.Items.Add((New-Object Windows.Forms.ListViewItem("Alex Morgan  ·  alex.morgan@example.com", "demo")))
$links = Get-Field $opt "_links"
$links.Items.Clear()
foreach ($row in @(@("Family.kdbx", 1, (T "geöffnet" "open")), @("Home office.kdbx", 180, ""))) {
    $lvi = New-Object Windows.Forms.ListViewItem($row[0])
    [void]$lvi.SubItems.Add("alex.morgan@example.com")
    [void]$lvi.SubItems.Add((Ago ([DateTime]::Now.AddMinutes(-$row[1]))))
    [void]$lvi.SubItems.Add($row[2])
    [void]$links.Items.Add($lvi)
}
Save-Form $opt "options.png"
$opt.Close()

# 4) Einblendung bei übernommenen Änderungen + Statusanzeige
$host1 = New-Object Windows.Forms.Form
$host1.Size = New-Object Drawing.Size 560, 220; $host1.BackColor = [Drawing.Color]::FromArgb(240, 240, 240)
Show-Offscreen $host1
$summary = (Plural 1 "{0} neuer Eintrag" "{0} neue Einträge" "{0} new entry" "{0} new entries") + ", " + (Plural 2 "{0} geändert" "{0} geändert" "{0} changed" "{0} changed")
$toast = ($asm.GetType("KeeLiveSync.UI.Toast").GetMethods($F) | Where-Object { $_.Name -eq "Show" -and $_.IsStatic -and $_.GetParameters().Count -eq 5 }).Invoke($null, [object[]]@($host1.PSObject.BaseObject, (T "Änderungen übernommen" "Changes applied"), ("Sam Morgan: " + $summary + "  ·  Family.kdbx"), $null, $null))
Pump 300
Save-Form $toast "notification.png"
$toast.Close(); $host1.Close()

$host2 = New-Object Windows.Forms.Form
$host2.Size = New-Object Drawing.Size 520, 80
$strip = New-Object Windows.Forms.StatusStrip
$strip.Items.Add((New-Object Windows.Forms.ToolStripStatusLabel((T "1 von 84 ausgewählt" "1 of 84 selected")))) | Out-Null
$ready = New-Object Windows.Forms.ToolStripStatusLabel((T "Bereit." "Ready.")); $ready.Spring = $true; $ready.TextAlign = "MiddleLeft"
$strip.Items.Add($ready) | Out-Null
$state = [Enum]::Parse($asm.GetType("KeeLiveSync.SyncState"), "Synced")
$color = $Theme.GetMethod("StateColor", $F).Invoke($null, @($state, $true))
$icon = $Theme.GetMethod("StatusIcon", $F).Invoke($null, @([int]16, $color, $false))
$badge = New-Object Windows.Forms.ToolStripStatusLabel((T "Live · synchron" "Live · in sync"), $icon)
$strip.Items.Add($badge) | Out-Null
$host2.Controls.Add($strip)
Show-Offscreen $host2
$bmp = New-Object Drawing.Bitmap $strip.Width, $strip.Height
$strip.DrawToBitmap($bmp, (New-Object Drawing.Rectangle 0, 0, $strip.Width, $strip.Height))
$bmp.Save((Join-Path $Out "status-bar.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"gespeichert: " + (Join-Path $Out "status-bar.png")
$host2.Close()
