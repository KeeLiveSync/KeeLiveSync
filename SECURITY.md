# Security policy

## Reporting a vulnerability

Please do not report security problems in public issues.

Use GitHub's private reporting instead: open the [Security tab](https://github.com/KeeLiveSync/KeeLiveSync/security) and choose **Report a vulnerability**. Only you and the maintainers can see the report.

Helpful details are the affected version, the KeePass version, the steps to reproduce and what an attacker could achieve.

KeeLiveSync is maintained in spare time, so there is no guaranteed response time. We will answer as soon as we can and tell you when a fix is released.

## Supported versions

Only the latest release receives security fixes.

## Scope

In scope are the KeeLiveSync plugin, its Microsoft sign-in and token storage, the synchronization with OneDrive and the signed release and update information.

An attacker who already runs code under your Windows account can also read keystrokes and data protected for that account, including the stored sign-in token. KeeLiveSync cannot fully protect against that, but reports that make such an attack easier are welcome.
