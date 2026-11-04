# Changelog

All notable changes to KeeLiveSync are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0] – 2026-11-04

### Added
- Notice when a database cannot be synced – also for databases in a background tab – with a clear cause
  instead of technical error messages, and a button to go to the database.
- When the master key was changed on another device: notice "New master key" with "Reopen" – closes the
  database, refreshes the local copy from the cloud and asks for the new key. Changes that could not be
  uploaded are kept as a backup file.
- New entries from others are highlighted for a few seconds; the notice offers "Show" to jump to them.
- README: recommendation to protect cloud databases with a key file or hardware key.

### Changed
- The log states why the entry list was not updated in place (database in a background tab, search view).

### Tested
- OneDrive for Business (Microsoft 365 account), two databases from two accounts at the same time, and
  KeeAutoExec (Auto-Open) with password and key file.

## [0.2.1] – 2026-11-04

### Fixed
- Sync failed with "You cannot get the content of the current version" (400) when OneDrive's version IDs
  did not match the cTag counter. Skipped versions are now selected from the version history by their
  modification time, and a problem with this extra step no longer stops the normal sync.

## [0.2.0] – 2026-11-04

### Added
- User interface in French, Spanish, Italian, Dutch and Polish (machine translation by the authors –
  corrections welcome). The language follows KeePass; missing texts fall back to English.
- Language files are embedded in the DLL (`src/KeeLiveSync/Languages/*.json`); `tools/i18n.py` adds new
  texts and checks every language file (missing texts, placeholders) – also in the release build.

### Changed
- Dates and times follow the display language.
- Buttons in the options dialog grow with longer translations.

## [0.1.2] – 2026-11-04

### Changed
- The project moved to its own GitHub organization: https://github.com/KeeLiveSync/KeeLiveSync
- The update information is now `version.txt` (signed as before); KeePass checks the new address.

## [0.1.1] – 2026-11-04

### Security
- The update information (`keepass.version`) is now signed with the KeeLiveSync release key; KeePass
  rejects unsigned or modified files instead of announcing a wrong version.
- Releases are built by GitHub Actions and published only after an offline signature of
  `SHA256SUMS.txt` (`SHA256SUMS.txt.sig`, RSA/SHA-256).

### Added
- Release package `KeeLiveSync-<version>.zip` with license, readme and changelog.

## [0.1.0] – 2026-11-04

First public version.

### Added
- Open databases from Microsoft OneDrive via **File → Open → Open from the cloud (KeeLiveSync)**:
  browse your OneDrive (folders and `.kdbx` only, scroll position kept when going back) or paste a
  sharing link to a file or folder.
- Local cache: databases open instantly; the cloud version is checked afterwards.
- Live updates through Microsoft Graph push notifications (socket.io); the subscription is renewed
  automatically.
- Merging with KeePass' own synchronization; the entry list is updated row by row without greying out
  and keeps scroll position and selection.
- Uploads with `If-Match`; on 412 the newer cloud version is merged and the upload repeated.
- Restore of changes overwritten by other apps by merging skipped versions from the OneDrive version
  history (up to 10).
- Option "Save and upload changes immediately" (on by default), including saving before KeePass would
  ask on close.
- Pending uploads are finished in the background when a database or KeePass is closed.
- Incoming changes wait while a dialog is open and are merged afterwards.
- Quiet save raises KeePass triggers and `FileSaving`/`FileSaved` for other plugins.
- Several databases and Microsoft accounts at the same time.
- Status indicator in the KeePass status bar, notices for applied changes and expired sign-ins.
- Options dialog: accounts, linked databases, sync and notification settings, detailed log (24 h).
- Update check through KeePass (`keepass.version`).
- German and English UI.

[Unreleased]: https://github.com/KeeLiveSync/KeeLiveSync/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/KeeLiveSync/KeeLiveSync/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/KeeLiveSync/KeeLiveSync/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/KeeLiveSync/KeeLiveSync/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/KeeLiveSync/KeeLiveSync/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/KeeLiveSync/KeeLiveSync/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/KeeLiveSync/KeeLiveSync/releases/tag/v0.1.0
