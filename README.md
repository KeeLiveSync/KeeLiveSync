# KeeLiveSync

**Live synchronization of KeePass 2 databases – works with Microsoft OneDrive.**

KeeLiveSync is a plugin for [KeePass Password Safe 2](https://keepass.info). It opens a database
stored in OneDrive instantly from a local copy, keeps it in sync in the background and merges changes
made by other people or devices – while the database stays open, without reloading and without losing
your place in the entry list.

It was built for a database that two people share (a household or a small team), each with their own
PC and a phone app such as KeePassium.

> 🇩🇪 Eine deutsche Kurzbeschreibung steht [weiter unten](#deutsch).

## Features

- **Opens instantly.** The database is opened from a local cache; the cloud is checked afterwards.
- **Push instead of polling.** Changes in OneDrive are reported by Microsoft Graph via a live
  connection (socket.io). Typical delay: about 30 seconds. No timers, no periodic downloads.
- **Real merging.** Remote changes are merged with KeePass' own synchronization
  (`PwDatabase.MergeIn`) – new, changed, moved and deleted entries, including history.
- **Quiet UI updates.** Only the affected rows of the entry list are inserted, removed or refreshed.
  The window does not grey out, the scroll position and selection are kept.
- **Safe uploads.** Every upload carries the version it is based on (`If-Match`). If someone else saved
  in the meantime, OneDrive rejects the upload (412), KeeLiveSync merges and uploads again – no
  conflict copies, no lost changes.
- **Heals overwrites.** If another app overwrote the file without merging (e.g. "Overwrite" in a mobile
  app), KeeLiveSync also merges the skipped versions from the OneDrive version history and restores
  what was lost.
- **Save and upload immediately** (optional, on by default): changes are saved and uploaded about one
  second after you press OK – also when you close KeePass right away.
- **Waits for open dialogs.** While you edit an entry, incoming changes are held back and merged as
  soon as the dialog is closed.
- **Shared databases.** Open a database someone shared with you by pasting the OneDrive sharing link
  (file or folder). No need to add it to "My files".
- **Several databases and accounts** at the same time, each in its own KeePass tab.
- **Status at a glance.** A small indicator in the status bar ("Live · in sync") with details in its
  tooltip, and an unobtrusive notice when changes from others were applied.
- **Sign-in reminder.** If the Microsoft sign-in has expired, a notice offers to sign in again; until
  then you keep working with the local copy.
- **Clean diagnostics.** Errors and events are always logged; a detailed log can be switched on in the
  options and switches itself off after 24 hours. Logs never contain passwords, tokens or database
  contents.
- **KeePass triggers and other plugins keep working.** The quiet save raises the same
  "saving/saved database file" triggers and plugin events as a normal save.
- German and English UI, following the KeePass language.

## Requirements

- KeePass 2.x on Windows (developed and tested with **KeePass 2.61.1**), .NET Framework 4.8
- A Microsoft account with OneDrive
  - Tested with **personal** Microsoft accounts.
  - Work or school accounts (OneDrive for Business) are not tested yet; your organization may have to
    approve the app first.

## Installation

1. Download `KeeLiveSync.dll` from the [latest release](https://github.com/KeeLiveSync/KeeLiveSync/releases/latest).
2. Copy it into the `Plugins` folder of your KeePass installation
   (e.g. `C:\Program Files\KeePass Password Safe 2\Plugins`).
3. Restart KeePass.

KeePass checks for new versions of KeeLiveSync together with its own update check.

### Verifying a download

Releases are built by GitHub Actions and published only after `SHA256SUMS.txt` has been signed offline
with the KeeLiveSync release key (`SHA256SUMS.txt.sig`, RSA/SHA-256). The public key is in
[`src/KeeLiveSync/ReleaseKey.cs`](src/KeeLiveSync/ReleaseKey.cs) (fingerprint `6A35F060C245973F`).
The update information `version.txt` is signed with the same key, and KeeLiveSync tells KeePass to
reject it if the signature does not match.

## Usage

1. **File → Open → Open from the cloud (KeeLiveSync) …**
2. **Sign in with Microsoft** (a browser window opens; you only do this once per account).
3. Pick the database in **My OneDrive**, or paste a sharing link in the **Shared link** tab.
4. Enter the master key as usual.

From then on the database is linked: open it again via KeePass' recent files list or the same dialog.
Options are under **Tools → KeeLiveSync → Options** or by clicking the status indicator.

### Tips for mobile apps

Phone apps write the file directly to OneDrive. If an app asks what to do because the file has
changed, choose **Merge** where offered. If it overwrites anyway, KeeLiveSync restores the lost changes
from the version history the next time a PC with the plugin is online (up to 10 skipped versions).

## How it works

```
 KeePass ── opens ──▶ local cache (%APPDATA%\KeeLiveSync\cache)
    ▲                      │  save → upload with If-Match (cTag)
    │ merge (MergeIn)       ▼
    └──── download ◀── OneDrive file ◀── other PCs / phones
               ▲
               └── push notification (Microsoft Graph, socket.io)
```

- Data folder: `%APPDATA%\KeeLiveSync` (accounts, linked databases, cache, logs).
- Sign-in: OAuth 2.0 authorization code flow with PKCE via the system browser (public client, no
  secret). The refresh token is stored encrypted with Windows DPAPI for the current user.
- Permissions requested: `Files.ReadWrite.All` (read and write your own and shared files),
  `User.Read` (name and e-mail for the account list), `offline_access` (stay signed in).
- The push subscription covers the whole OneDrive drive (this is how Graph offers socket.io
  notifications). A notification only triggers a lightweight version check; the file is downloaded
  only when it has actually changed.

### About KeePass internals

Most of KeeLiveSync uses the official KeePass plugin API and KeePassLib. Three things have no public
API and are accessed via reflection, each with a safe fallback:

| What | Fallback if a future KeePass version changes it |
| --- | --- |
| Inserting/removing single rows in the entry list | full list refresh (correct, just less smooth) |
| Raising `FileSaving`/`FileSaved` for other plugins | other plugins are not notified of the quiet save |
| Looking up the trigger event IDs | triggers are not raised for the quiet save |

## Building

```
dotnet build src\KeeLiveSync -c Release -p:KeePassDir="C:\Program Files\KeePass Password Safe 2"
```

`KeePassDir` must contain `KeePass.exe`. If it has a `Plugins` folder, the built DLL is copied there.
The `poc` folder contains the command-line tools used to verify the Graph behavior (If-Match, push,
version history) and to create and modify test databases (password `test`, no real data).

## Privacy

KeeLiveSync talks only to Microsoft (sign-in and Microsoft Graph). It has no server of its own, sends
no telemetry and never transmits your master key – the database is encrypted before it leaves your PC,
exactly as KeePass saves it.

## License

[GNU General Public License v3.0](LICENSE) © 2026 Michael Kaufmann

KeePass is © Dominik Reichl and licensed under the GPL. Microsoft and OneDrive are trademarks of the
Microsoft group of companies. KeeLiveSync is an independent project and not affiliated with or endorsed
by Microsoft or the KeePass project.

---

## Deutsch

KeeLiveSync ist ein Plugin für KeePass 2, das eine Datenbank in OneDrive **sofort aus einer lokalen
Kopie öffnet** und sie **live abgleicht**. Änderungen anderer Personen oder Geräte meldet Microsoft per
Push, und KeeLiveSync führt sie im laufenden Betrieb mit KeePass' eigener Synchronisation zusammen. In
der Liste erscheinen oder verschwinden nur die betroffenen Einträge; nichts wird grau, die
Scrollposition bleibt.

Hochgeladen wird mit Versionsprüfung, so dass gleichzeitige Änderungen zusammengeführt statt
überschrieben werden. Hat eine Handy-App die Datei trotzdem überschrieben, holt KeeLiveSync die
übersprungenen Versionen aus dem OneDrive-Versionsverlauf und stellt verlorene Einträge wieder her.

**Installation:** `KeeLiveSync.dll` aus dem neuesten Release in den `Plugins`-Ordner von KeePass
kopieren und KeePass neu starten. Danach: **Datei → Öffnen → Aus der Cloud öffnen (KeeLiveSync) …**
