# Roadmap
## Before public executable release
- Recheck after the installer-requested Windows restart. Authorized LibreOffice 26.8.1 upgrade and the corrected pre-restart conversion suite passed.
- Complete physical Explorer/Desktop drag with configurable keys in both orders, OLE Escape/cancellation and mixed-DPI monitor/edge checks.
- Validate the new tic on actual output devices and the revised transparency over busy folders.
- Confirm startup after actual sign-in/reboot, not just registration/manual quiet launch.
- Validate an isolated clean build/runtime and finish exact bundle notices/source obligations, packaging and signing.

## Source publication
Review PUBLIC_RELEASE_REVIEW.md, close the chosen publication gates, run repository privacy checks, and use a clean source export/initial repository.
Keep development evidence privately in _local. Do not publish the whole workspace or app directory.
No remote publication is automatic.

## Later
Improve accidental multi-selection handling and the optional batch panel.
Reduce distribution/debug-symbol size and profile cold-start and active conversion cost.
Evaluate audio/video, archives, OCR and office spreadsheet/presentation categories as separate modules with tested source/destination matrices.
Do not promise arbitrary conversion of every file type or unsupported Pages output.
