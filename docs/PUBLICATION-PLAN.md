# Controlled GitHub publication

Status: local preparation, 23 October 2026. No repository remote is configured and no upload has been performed in this audit.

1. Review the diff and run `scripts/Audit-Repository.ps1`. It checks tracked text, new unignored files, historical paths, historical text and commit metadata using explicit patterns. Review images/audio and commit identities separately. A regex scan cannot certify absence of all secrets or personal data. Ignored local files must never be uploaded by zipping the workspace.
2. Run locked restore, build, input tests and conversion tests. Record the exact revision and results. Dependency vulnerability lookup must succeed online; a network error is not a clean result.
3. Verify physical Explorer and Desktop drag with the configured key: drag first, key held first, releasing the key, dropping outside, Escape, normal click selection, multiple files, monitor edges, mixed DPI and swapped mouse buttons. Test settings capture/save/cancel/restart persistence and installer default/opt-out integrations on a clean machine. Count source files and compare original hashes before/after. Listen on the actual output device for both the wheel tick and absence of Explorer error tones.
4. Commit only reviewed source and public assets. Use the GitHub noreply identity intentionally. Keep local audit evidence, release strategy for Alexandra, recordings under review, logs and installer backups ignored.
5. Create a private GitHub repository first. Push reviewed commits, run Windows CI and enable secret scanning/push protection where available, dependency alerts and appropriate branch protection. Do not put write tokens in workflows; CI only needs read access for the current build. Review all workflow changes before running contributions.
6. Have a second human review the private repository, metadata and downloadable artifacts. Then deliberately change visibility to public. Source publication and executable publication are separate decisions.
7. Publish the binary as an explicitly labelled prerelease only after its gates pass. Include supported Windows/architecture, runtime requirements, known limitations, SHA256 checksums, notices and any corresponding-source obligations for bundled engines. Do not claim the entire bundle is MIT-only or signed if it is not.
8. Upload the approved short MP4 through GitHub's README editor to obtain GitHub's actual attachment URL; put that returned URL beneath the introductory paragraph. Keep a poster PNG in `docs/assets`. Never fabricate an attachment URL or insert a link to a nonexistent demo. Preserve the original recording and its privacy-reviewed cut locally.

Do not silently replace the beta executable with a different build under the same release asset name. Version the application and keep a changelog, checksums and rollback instructions together. Release publication is a final deliberate action after reviewing this candidate.

## Reference products checked on 23 October 2026

- [Tangerine for Mac](https://www.tangerineformac.com/press): drag-to-convert radial interaction. Credit the inspiration; no affiliation or feature-parity claim.
- [Tangerine for Windows](https://github.com/CristianO2601/tangerine-windows): an existing drag-to-convert Windows project. FLIP must not be advertised as the first Windows implementation.
- [File Converter](https://file-converter.io/): established open-source Explorer context-menu conversion. Different interaction, overlapping practical need.

Position FLIP as a small local Windows utility with a radial selector and transparent format limits. Its value must be demonstrated by a real conversion, not inferred from uniqueness.
