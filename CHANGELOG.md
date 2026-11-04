# Changelog

All notable changes to KeeLiveSync are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/blacksun80/KeeLiveSync/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/blacksun80/KeeLiveSync/releases/tag/v0.1.0
