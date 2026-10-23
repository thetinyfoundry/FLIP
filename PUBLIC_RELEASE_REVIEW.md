# Pre-publication review — 22 October 2026

Current candidate: **0.4.0-beta.3**. Read [current shortcuts and validation](docs/CUSTOM-SHORTCUTS.md), [the initial 23 October audit](docs/AUDIT-2026-10-23.md), [Windows installation](docs/WINDOWS-INSTALL.md) and [controlled publication plan](docs/PUBLICATION-PLAN.md) first. Ctrl activation and SoundPlayer described in this historical beta.1 review have been replaced. Native drag/audio and clean-machine release gates remain open; no publication occurred.

Candidate: **Flip 0.4.0-beta.1**. This review covers the source project, local Windows application, build inputs and publication contents. No GitHub repository, push or public release was created.

## Decision

**Do not publish an executable release yet.** The source can be reviewed as an experimental beta candidate, but post-restart validation, broader native acceptance and bundle review are still required. Calling it beta does not remove those limitations.

## Results

| Area | Evidence | Status |
|---|---|---|
| Build | Release build/publish, zero compiler errors or warnings | PASS |
| Conversion correctness | 99 checks / 57 conversion cases, zero failed with LibreOffice 26.8.1 | PASS for tested fixtures, before requested restart |
| Input, layout, audio, bounds | 45 checks, zero failed | PASS for tested logic |
| Native user interface | TXT to PDF/DOCX/HTML/JPG from the real multi-output panel; four outputs, zero errors; source hash unchanged | PASS for this flow |
| Core without Pandoc | Isolated local runtime converted TXT to PDF/JPG and PDF to TXT | PASS |
| Dynamic wheel | All twelve TXT outputs visible in WPF render; geometry tested at 1/2/3/6/8/12 sectors | PASS for render/geometry |
| Full physical drag/display matrix | Prior user drag success; mixed-DPI monitors and new wheel interaction not fully qualified | REVIEW |
| Audio | Original 40 ms tic, PCM/content checks; earlier intermittent silence was not reproduced conclusively | REVIEW on output devices |
| Startup | Owned per-user registration/readback and quiet launch; actual next sign-in/reboot not observed | REVIEW |
| Dependencies | Locked restore with NuGet audit enabled succeeded; no NuGet advisories reported for the resolved graph | PASS for that feed/check |
| LibreOffice | Authorized upgrade to 26.8.1.1; official checksum and Authenticode signature verified; installer exit 3010 requests restart | REVIEW after Windows restart |
| Source/privacy | Tracked-file and historical pattern scan, ignored private material, original assets; no configured sensitive-pattern matches | PASS for scan scope |
| Native notices/signing | Source licenses documented; full executable bundle notices/source obligations and signed distribution remain unfinished | BLOCK for executable release |

## Findings corrected

- Fixed eight-slot wheel hid supported destinations. It now exposes every available output, including RTF/ODT and newly tested FB2/RST.
- TXT native PDF/image options and PDF native TXT extraction no longer require Pandoc.
- Published output no longer silently discovers a developer's downloaded engine in an ancestor folder; development fallback is scoped to a source project.
- FB2's handling of plain/code text initially lost content during readback. A controlled paragraph-normalizing filter fixed it; readback now passes. The failed runs remain private evidence.
- External-tool timeout is reported separately from user cancellation.
- A post-upgrade run found five LibreOffice failures with a long temporary-profile path. Profiles now use a short isolated Windows temporary directory with cleanup; all 99 checks, including deep-folder conversion, pass. External-tool errors include exit codes.
- Compressed DOCX/ODT/EPUB inputs have expanded-size and entry-count checks before conversion.
- Empty/oversized CLI batches and absent output formats are rejected.
- The panel cannot replace sources or re-enable conversion during a running job.
- Hidden global Ctrl+Alt registrations were removed; Ctrl observation and tray actions are the supported interface.
- Diagnostics moved outside the application folder into the user's local data, with bounded rotation.
- Debug source paths are mapped to a neutral build prefix. Raw logs, caches, prototypes, backups and runtime packages are excluded from source exports.
- Source formatting and dependency lock handling were normalized.

## Remaining risks

[LibreOffice's security advisories](https://www.libreoffice.org/security/) recommend updated versions, including fixes relevant to document import and PDF export. With explicit authorization, the old installation was updated to 26.8.1.1 after backing up the personal profile privately and verifying the official installer checksum/signature. No documents were open at the preflight check. The installer requested a restart (3010); no automatic restart occurred. Tests involving the new engine used synthetic documents authored by the test suite; post-restart qualification remains open.
The native parsers/processes are not a complete security sandbox. NuGet auditing does not cover every native upstream issue, LibreOffice, the OS or the installed .NET runtime.
Use maintained runtime/engine versions before qualifying a distributed build.

Physical multi-monitor/DPI, Desktop-origin drag, Ctrl orderings across hardware, cancellation during OLE and subjective audio still need a complete acceptance run. The automation can operate the optional panel; its window inventory does not expose every non-taskbar wheel, and it cannot hold a modifier inside its drag action. Off-screen tests were not promoted to physical proof.

## Publication procedure

1. Resolve the engine and native-acceptance gates above.
2. Review README, licenses, file list and commit metadata. Use a GitHub noreply address.
3. Audit both current source and history. The supplied scan is pattern based; no dedicated secret-scanner guarantee is claimed.
4. Export only reviewed Git files. Do not ZIP the entire working directory or app folder.
5. Prefer a clean initial publication repository from the reviewed export, retaining development history privately.
6. Publish source and an explicit beta status first; distribute executables only after bundle/license and clean-machine checks.

GitHub exposes the account, commit author/email and timestamps. There is no honest guarantee of “no traces.” No personal document or raw diagnostic log belongs in the publication.
