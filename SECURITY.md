# Security

## Supported version

Only the latest release receives fixes.

## Reporting a vulnerability

Use [private vulnerability reporting](https://github.com/thetinyfoundry/FLIP/security/advisories/new) if it is available. Otherwise open an issue that asks for a private contact, without exploit details, personal files or logs.

## Scope

FLIP processes local files with managed and native libraries and optional external programs. Process separation, size limits and path checks reduce the risk, but they are not a complete sandbox: treat untrusted documents with care. FLIP does not run macros or formulas in spreadsheets, and does not need administrator rights.

FLIP has no account, telemetry or background network service. It contacts GitHub when you check for updates and downloads optional engines when you ask. See [PRIVACY.md](PRIVACY.md).

The Microsoft Store package is signed by Microsoft. The GitHub ZIP is not signed: check it with the published SHA256 file.
