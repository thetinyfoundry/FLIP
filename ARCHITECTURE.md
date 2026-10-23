# Architecture
Flip is a Windows x64 .NET 10 WPF tray application. src/Flip is the source; app is local published output; _local contains private evidence/history. packages and tools are ignored development inputs.

- Program/Host owns the hidden host, tray, single-instance mutex, CurrentUserOnly IPC, job lifetime and shutdown. Background duplicate launches exit quietly.
- WheelShortcut parses persisted single-key choices, with Down Arrow as the default. ShortcutSettingsWindow provides preset and custom-key selection. ShortcutHook consumes the chosen key's down/repeat/up during an eligible Explorer/Desktop mouse gesture; unrelated typing passes through. ShortcutOrderGate supports drag-first and held-key-first ordering. If the initial key-down was normal input, gesture repeats can be reserved while its key-up is still passed. The low-level callback only posts state to the Dispatcher, without file I/O or input injection. Native maps swapped mouse buttons and monitor/DPI placement. Raw Input observes Escape, drag movement and primary-button release; retired TriggerGate logic was removed.
- Wheel renders a dynamic list of compatible formats. Sector geometry is shared by rendering and hit testing; keyboard and pointer selection use the same ordered format set.
- WheelSound generates the original tic and uses asynchronous WinMM playback from pinned memory. Sector changes restart the short tick without a dispatch queue or 55 ms suppression. Mute/preferences and API diagnostics are separate from acoustic acceptance.
- AppPreferences stores per-user settings atomically. StartupIntegration owns a per-user HKCU Run value and Desktop/Start shortcuts. Install.ps1 requests both by default, with explicit opt-out flags; tray settings can disable startup later.
- SelectionWindow provides the optional batch/multi-output flow and prevents source replacement while busy.
- Policy computes capabilities per engine, validates local inputs and intersects batch output sets.
- ConversionEngine serializes jobs, stages local copies, validates output and commits without overwrite.
- DocumentTools invokes optional Pandoc and LibreOffice with structured argument lists, timeout, cancellation and a dedicated Office profile.
- ConversionTests/InputTests produce repeatable private evidence. Native and user acceptance remain separate.

Core image/PDF/text paths use SkiaSharp, PDFtoImage/PDFium and PdfPig. Pandoc adds document conversions; maintained LibreOffice adds document PDF/image rendering.
The FB2 output path normalizes plain/code text into paragraphs with a controlled local filter so text survives readback.

Jobs use .flip-job-GUID beside the source. Source files are never opened for writing. A failed multi-page commit rolls back that format's new outputs; prior completed conversions remain.
LibreOffice profiles use short, isolated Windows temporary directories, with macro security configured before launch and cleanup in a finally block. Keeping these profiles independent of source-folder depth avoids the reproduced post-upgrade crash. Office inputs/outputs remain in the source-adjacent job folder.
DOCX/EPUB/ODT preflight bounds entry count and expanded size. Native parsers are not a full sandbox.

Default diagnostic logs live in the user's local app data, rotate at approximately 2 MiB plus one predecessor, and may contain paths. Explicit --evidence directories are for tests. Neither belongs in source publication.
