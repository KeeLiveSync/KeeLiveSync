# KeeLiveSync

**Live synchronization of KeePass 2 databases in Microsoft OneDrive.**

KeeLiveSync is a plugin for [KeePass Password Safe 2](https://keepass.info)
([listed on keepass.info](https://keepass.info/plugins.html#keelivesync)). It opens a database stored in
OneDrive instantly from a local copy, keeps it in sync in the background and merges changes made by other
people or devices – while the database stays open, without reloading and without losing your place in the
entry list.

It is made for databases shared by several people – a household or a small team – each with their own
PC and a phone app such as KeePassium.

> 🇩🇪 Eine deutsche Kurzbeschreibung steht [weiter unten](#deutsch).

## Screenshots

| Open from OneDrive | Open a shared database |
| --- | --- |
| ![Open from OneDrive](docs/screenshots/open-onedrive.png) | ![Open a shared database](docs/screenshots/open-shared-link.png) |

| Changes from others | Status bar | Options |
| --- | --- | --- |
| ![Notice](docs/screenshots/notification.png) | ![Status bar](docs/screenshots/status-bar.png) | ![Options](docs/screenshots/options.png) |

<sub>Example data. Screenshots are generated with `tools/screenshots.ps1`.</sub>

## Features

- **Opens instantly** from a local copy; the cloud is checked afterwards.
- **Live updates.** OneDrive reports changes via push (about 30 seconds); no periodic downloads.
- **Real merging** with KeePass' own synchronization – new, changed, moved and deleted entries.
- **No lost changes.** If two devices save at the same time, KeeLiveSync merges instead of overwriting.
  If a phone app overwrote the file, the lost changes are restored from the OneDrive version history.
- **Quiet.** Only the affected rows of the entry list change; scroll position and selection are kept.
  While you edit an entry, incoming changes wait until the dialog is closed.
- **Saves and uploads immediately** after you press OK (optional, on by default).
- **Know who changed what.** A notice lists the changed entries and the account that saved them;
  "Show" lists exactly these entries, "Recent changes" shows the last 20 syncs again.
- **Shared databases** via a OneDrive sharing link, several databases and accounts at the same time.
- **Status at a glance** in the KeePass status bar, with a reminder when the Microsoft sign-in has
  expired – until then you keep working with the local copy.
- **KeePass triggers and other plugins keep working**: the quiet save raises the same events as a
  normal save.
- **Seven languages**, following the KeePass language.

## Installation

1. Download `KeeLiveSync.dll` from the [latest release](https://github.com/KeeLiveSync/KeeLiveSync/releases/latest).
2. Copy it into the `Plugins` folder of KeePass (e.g. `C:\Program Files\KeePass Password Safe 2\Plugins`).
3. Restart KeePass.

Requirements: KeePass 2.x on Windows (tested with 2.61.1 and 2.62), .NET Framework 4.8 and a Microsoft account
with OneDrive – personal or Microsoft 365 work account (an administrator may have to approve the app
once). KeePass checks for new versions of KeeLiveSync together with its own update check. Releases are
signed, see [Verifying a download](docs/TECHNICAL.md#verifying-a-download).

## Usage

1. **File → Open → Open from the cloud (KeeLiveSync) …**
2. **Sign in with Microsoft** (once per account, in the browser).
3. Pick the database in **My OneDrive**, or paste a sharing link in the **Shared link** tab.
4. Enter the master key as usual.

After that, open it again via KeePass' recent files list. Options are under
**Tools → KeeLiveSync → Options** or by clicking the status indicator.

**Phone apps** write directly to OneDrive. If an app asks what to do because the file has changed,
choose **Merge** where offered. If it overwrites anyway, KeeLiveSync restores the lost changes the next
time a PC with the plugin is online (up to 10 skipped versions).

**Personal and shared database together:** open both with KeeLiveSync, each in its own tab. To unlock
only once, use [KeeAutoExec](https://keepass.info/plugins.html#keeautoexec): in your personal database,
create a group `AutoOpen` with an entry for the shared one – **URL:** its local copy (full path under
File → Open Recent), **Password:** its master password, **User name:** the path of its key file (if any).

## Security and privacy

- KeeLiveSync talks only to Microsoft (sign-in and Microsoft Graph). No own server, no telemetry.
- The database leaves your PC only encrypted, exactly as KeePass saves it; the master key is never
  transmitted. The sign-in token is stored encrypted with Windows DPAPI.
- Logs never contain passwords, tokens or database contents.

**Recommendation:** anyone who gets access to your Microsoft account or the shared folder can copy a cloud
database and attack it offline. Protect it with a **key file** (kept only locally on each device and
handed over separately, e.g. on a USB stick) or a **hardware key**, plus a long, unique master password
and two-factor sign-in for the Microsoft account.

## Limits

- KeeLiveSync cannot control phone apps. What they overwrite is restored from the version history – as
  long as OneDrive still keeps that version, at most 10 versions were skipped and a PC with KeeLiveSync
  is running.
- With "Save changes immediately" switched off, unsaved changes exist only in the open KeePass.
- KeeLiveSync does not replace a backup.

How it works in detail, what has been tested and how to build it: [technical details](docs/TECHNICAL.md).

## Translations

English and German are written in the code; all other languages are JSON files in
[`src/KeeLiveSync/Languages`](src/KeeLiveSync/Languages). French, Spanish, Italian, Dutch and Polish are
machine translations – **corrections are very welcome** as a pull request that changes only the JSON file.

- New language: create `<ISO 639-1 code>.json` (e.g. `pt.json`) with `{"_language": "Português"}`, run
  `python tools/i18n.py update` to add all texts, then translate.
- `{0}`, `{1}` … are placeholders and must stay. Polish uses an extra key `"<English plural>#few"` for
  2–4, 22–24 …
- `python tools/i18n.py check` reports missing texts and wrong placeholders.

## License

[GNU General Public License v3.0](LICENSE) © 2026 Michael Kaufmann

KeePass is © Dominik Reichl and licensed under the GPL. Microsoft and OneDrive are trademarks of the
Microsoft group of companies. KeeLiveSync is an independent project and not affiliated with or endorsed
by Microsoft or the KeePass project.

---

## Deutsch

KeeLiveSync ist ein Plugin für KeePass 2, das eine Datenbank in OneDrive live synchron hält. Gedacht ist
es für Datenbanken, die mehrere Personen gemeinsam nutzen – im Haushalt oder in einem kleinen Team –, jede
am eigenen PC und dazu am Handy, etwa mit KeePassium.

**Was es kann**

- Die Datenbank öffnet sofort aus einer lokalen Kopie und lässt sich auch offline nutzen.
- Änderungen anderer erscheinen nach etwa 30 Sekunden in der offenen Liste – ohne Neuladen; Scrollposition
  und markierter Eintrag bleiben.
- Speichern zwei Geräte gleichzeitig, wird zusammengeführt statt überschrieben. Hat eine Handy-App die Datei
  überschrieben, holt KeeLiveSync die verlorenen Einträge aus dem OneDrive-Versionsverlauf zurück.
- Etwa eine Sekunde nach „OK“ ist eine Änderung gespeichert und hochgeladen (abschaltbar).
- Eine Einblendung zeigt, wer welche Einträge geändert hat.
- Geteilte Datenbanken öffnest du direkt über den OneDrive-Freigabelink.

**Installation und Nutzung**

1. `KeeLiveSync.dll` aus dem [neuesten Release](https://github.com/KeeLiveSync/KeeLiveSync/releases/latest)
   in den `Plugins`-Ordner von KeePass kopieren und KeePass neu starten.
2. **Datei → Öffnen → Aus der Cloud öffnen (KeeLiveSync) …**, mit Microsoft anmelden, Datenbank wählen
   (oder Freigabelink einfügen), Hauptschlüssel eingeben.
3. Danach öffnest du sie wie gewohnt über „Zuletzt geöffnet“. Einstellungen: **Extras → KeeLiveSync →
   Optionen** oder Klick auf die Statusanzeige.

Fragt eine Handy-App beim Speichern, ob überschrieben oder zusammengeführt werden soll, wähle
**„Zusammenführen“**.

**Sicherheit:** KeeLiveSync spricht nur mit Microsoft, ohne eigenen Server und ohne Telemetrie; die
Datenbank verlässt den PC nur verschlüsselt. Schütze eine Cloud-Datenbank zusätzlich mit einer
**Schlüsseldatei**, die nur lokal auf den Geräten liegt, oder einem **Hardware-Schlüssel**. KeeLiveSync
ersetzt kein Backup.
