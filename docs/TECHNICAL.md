# KeeLiveSync – technical details

Back to the [README](../README.md).

## How it works

```
 KeePass ── opens ──▶ local cache (%APPDATA%\KeeLiveSync\cache)
    ▲                      │  save → upload with If-Match (cTag)
    │ merge (MergeIn)       ▼
    └──── download ◀── OneDrive file ◀── other PCs / phones
               ▲
               └── push notification (Microsoft Graph, socket.io)
```

1. **Local copy first.** On first use the file is downloaded once into `%APPDATA%\KeeLiveSync\cache`.
   KeePass always works with this copy, so the database opens instantly and also works offline.
2. **Version tags instead of whole files.** OneDrive assigns a tag (cTag) to every content version.
   KeeLiveSync remembers the last version it merged and downloads only when the tag in the cloud differs.
3. **Push instead of polling.** KeeLiveSync keeps a live connection (socket.io) to Microsoft Graph. When
   something changes in OneDrive, Microsoft reports it after about 30 seconds; KeeLiveSync then checks the
   version tag (a few hundred bytes) and downloads only on a real change. There is no timer that
   downloads periodically. The subscription is renewed automatically.
4. **Merging with KeePass' own synchronization.** The cloud version is merged into the open database with
   `PwDatabase.MergeIn` (mode "Synchronize") – the same mechanism as File → Synchronize. New, changed,
   moved and deleted entries are taken over; if both sides changed the same entry, the newer one wins and
   the older one goes into the entry's history. Deletions stay deleted, because KeePass records them with
   a timestamp.
5. **Uploads with a version check.** Every upload carries the version it is based on (`If-Match`). If
   someone else saved in the meantime, OneDrive rejects the upload (412); KeeLiveSync fetches the new
   version, merges and uploads again. An upload never overwrites a version it has not seen.
6. **Version history against overwrites.** Apps without this check can still overwrite the file. If the
   version tag jumps, e.g. from 15 to 17, KeeLiveSync also fetches version 16 from the OneDrive version
   history and merges it (up to 10 skipped versions). If something is then missing in the cloud,
   KeeLiveSync uploads the complete state again.
7. **Quiet UI updates.** Single rows of the entry list are inserted, removed or refreshed, and the save
   runs without locking the UI. If a future KeePass version changes the internal functions used for this,
   KeeLiveSync falls back to a full list refresh.

### Data and sign-in

- Data folder: `%APPDATA%\KeeLiveSync` (accounts, linked databases, cache, logs).
- Sign-in: OAuth 2.0 authorization code flow with PKCE via the system browser (public client, no
  secret). The refresh token is stored encrypted with Windows DPAPI for the current user.
- Permissions requested: `Files.ReadWrite.All` (read and write your own and shared files),
  `User.Read` (name and e-mail for the account list), `offline_access` (stay signed in).
- The push subscription covers the whole OneDrive drive (this is how Graph offers socket.io
  notifications). A notification only triggers a lightweight version check; the file is downloaded
  only when it has actually changed.
- Logs never contain passwords, tokens or database contents. A detailed log can be switched on in the
  options and switches itself off after 24 hours.

### KeePass internals

Most of KeeLiveSync uses the official KeePass plugin API and KeePassLib. Three things have no public
API and are accessed via reflection, each with a safe fallback:

| What | Fallback if a future KeePass version changes it |
| --- | --- |
| Inserting/removing single rows in the entry list | full list refresh (correct, just less smooth) |
| Raising `FileSaving`/`FileSaved` for other plugins | other plugins are not notified of the quiet save |
| Looking up the trigger event IDs | triggers are not raised for the quiet save |

## What has been tested

Between PCs running KeeLiveSync, no change is lost by overwriting: every upload is bound to the known
version, and everything is merged. Tested: simultaneous saves (412 conflict), deleting, editing during a
sync and closing KeePass in the middle of an upload.

With KeePassium, this case was tested: KeePassium overwrites an entry that had just arrived from another
device, and KeeLiveSync restores it automatically.

## Verifying a download

Releases are built by GitHub Actions and published only after `SHA256SUMS.txt` has been signed offline
with the KeeLiveSync release key (`SHA256SUMS.txt.sig`, RSA/SHA-256). The public key is in
[`src/KeeLiveSync/ReleaseKey.cs`](../src/KeeLiveSync/ReleaseKey.cs) (fingerprint `6A35F060C245973F`).
The update information `version.txt` is signed with the same key, and KeeLiveSync tells KeePass to
reject it if the signature does not match. KeeLiveSync never downloads code itself.

## Building

```
dotnet build src\KeeLiveSync -c Release -p:KeePassDir="C:\Program Files\KeePass Password Safe 2"
```

`KeePassDir` must contain `KeePass.exe`. If it has a `Plugins` folder, the built DLL is copied there.
The `poc` folder contains the command-line tools used to verify the Graph behavior (If-Match, push,
version history) and to create and modify test databases (password `test`, no real data).
