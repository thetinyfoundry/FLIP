# Changelog

## 0.4.0-beta.3

- Add tray Settings with arrow presets and custom single-key capture; default to Down Arrow on new installations.
- Reserve the chosen key during Explorer/Desktop gestures so it does not also reach Explorer; preserve normal keyboard use and pair captured key-up/repeat events.
- Support dragging before the key press and holding the key before starting the drag; cancel stale ordering state and keep the wheel open after key release.
- Keep the original wheel tick and its mute control. Do not change Windows system sounds.
- Create Desktop/Start shortcuts and enable per-user startup by default in the installer, with `-NoShortcuts` and `-NoStartup` opt-outs.
## 0.4.0-beta.2

- Replace Ctrl drag activation with `<>`; migrate old saved choices to `<>`.
- Play the original tick directly from pinned memory; remove the hover debounce and per-sector disk logging.
- Clean LibreOffice read-only lock files only inside validated owned job directories.
- Validate missing CLI option values and stop file lists before following options.
- Add a per-user installer with backup/checksum checks and an explicit-region screen recorder.
- Expand repository audit to historical filenames, new source files and commit metadata; document installation, release gates and reference products.

## 0.4.0-beta.1
- Show every compatible format in a dynamic wheel, including RTF/ODT; add tested FB2/RST document conversions.
- Restore core TXT-to-PDF/images and PDF-to-TXT without Pandoc.
- Slightly more translucent backing, larger outer margin and an original 40 ms tic.
- Harden container/batch bounds, timeout reporting, busy UI and private log rotation.
- Keep isolated LibreOffice profiles short and clean them on exit; add deep-folder conversion coverage and external-tool exit codes after validating LibreOffice 26.8.1.
- Scope development-engine fallback, normalize source paths and remove hidden global shortcuts.
- Prepare clean publication helpers, Italian README and release review; public binary release remains blocked.

## 0.3.0
- Add a tray sound-test command and bounded audio-request diagnostics; retain the existing sound player after intermittent user reports.
- Remove the extra-formats button from the wheel and center the filename/status area.
- Optional startup at Windows sign-in, controllable from the tray menu.
- Desktop and Start-menu shortcut installation/removal commands.
- Quiet background startup without opening a panel or spawning duplicate hosts.
- Original 120 ms wheel sound with mute and persistent preferences.
- Denser charcoal wheel to reduce visual interference from the folder underneath.
- Prevent child-control drag-leave transitions from repeatedly clearing the selected sector.
- Source publication preparation: ignore private files, document dependencies and add build CI.

## 0.2.0
- Real image, PDF and document conversions with collision-safe output beside the source.
- Batch selection and multiple output formats.
- Right/left Ctrl gesture and swapped mouse button support.
- Explanation for incompatible batches.
