# Changelog

Notable changes to **FLIP**. Versions before 1.0.0 were distributed only on GitHub.

---

## [1.0.2] - Unreleased

### Added
- **Engine suggestions.** When a missing engine would add formats for the files on the wheel, a notice says so once per engine. Clicking it opens *Engines & GPU*, where each missing engine lists the formats it adds next to its 1-Click button. The wheel itself is unchanged.

Contributed by [@augustodecans-dev](https://github.com/augustodecans-dev) in [#7](https://github.com/thetinyfoundry/FLIP/pull/7). Thank you!
- **Hold-to-open.** Hold the mouse button on a selected file in File Explorer or on the Desktop to open the wheel without pressing a key. It is off by default and adds to the drag gesture, which works as before. The hold time is adjustable from 1 to 8 seconds under *Activation & Input*. It starts only on a file or folder, never on scroll bars or empty space, and FLIP listens to the mouse only while File Explorer or the Desktop is in front. Right-click or Esc closes the wheel.
- **Folders and RAR.** A folder, dragged onto the wheel or opened with hold-to-open, becomes a ZIP, or a RAR when WinRAR is installed, next to the original folder. Empty subfolders are kept. Archiving a folder never sends its files to the Recycle Bin.
- **Unavailable tools.** Tools that cannot run on the selection are dimmed, and clicking one explains why inside the wheel. Waveform and OCR stay visible when FFmpeg or Tesseract is missing. Notices also appear in a dark box on the screen with the cursor.
- **Merge PDF page size.** Press **A** to keep the original page size or choose A4 or A3, portrait or landscape.
- **Scroll without dragging.** The mouse wheel switches between formats and tools on any open wheel. **A** and **Z** also work when the wheel was not opened by dragging.

### Fixed
- **Transcriptions stuck at 96%.** On some PCs Whisper finished writing the transcript but did not close, and the wheel waited at 96%. FLIP now delivers the transcript when the file is complete and Whisper has been idle for 15 seconds.
- **Keys on a wheel opened by holding the mouse button.** Space opens QuickLook, and A, Z and Tab work even while File Explorer keeps the keyboard focus. File Explorer no longer receives those keys.

### Security
- **Imported settings.** Importing a settings file keeps the engine paths, history and network settings of this PC. Imported watch folders start off, and the Hub says so.
- **PDF/A.** Removing metadata also removes the author, title and earlier revisions left inside the original PDF. Very large pages use less memory.
- **Files dragged from the browser.** A public page can no longer make FLIP read services on this PC or the home network. Local pages and intranets still work. Brotli is not used, hostile HTML no longer freezes the wheel, and inline data is always treated as an image.
- **Network paths.** Dropped or copied network paths are recognized before Windows tries to open them.
- **Programs.** WinGet runs from its own folder, and FLIP no longer looks for programs in the working folder.
- **Archives and 3D.** Archives and glTF, GLB or FBX files that declare enormous contents are stopped.
- **Engines.** Whisper and Pandoc downloads without a published SHA256 are refused. LibreOffice always runs with macros off.
- **Watch folders.** Symbolic links are ignored.
- **Installer.** The ZIP installer requires a complete SHA256SUMS.txt, refuses unexpected files and warns when .NET 10 needs a security update.
- **Components.** PDFium 160.0.8165 and SkiaSharp 4.157.0.

## [1.0.1] - 2026-11-25

First public 1.0 release. Version 1.0.0 was a private Microsoft Store test.

From this version the GitHub and Microsoft Store editions share the same number and are released together.

### Added
- **Microsoft Store edition.** An MSIX package with .NET, FFmpeg and Whisper included, Windows startup task, Windows 11 context menu and Store updates. The GitHub edition stays available. See [docs/MICROSOFT-STORE.md](docs/MICROSOFT-STORE.md).
- **Support card.** *Guide & Shortcuts* links to Buy Me a Coffee. Support is voluntary and unlocks nothing.
- **Privacy policy.** [PRIVACY.md](PRIVACY.md) lists what stays on the PC and when FLIP uses the network.

### Fixed
- **Files and settings.** Previewing SVG, DXF or ICO no longer uses the output settings, which could overwrite a PNG. Unreadable settings are backed up and valid values recovered. Undo restores originals moved to the Recycle Bin.
- **Tables and data.** CSV files with `;` or `sep=`, ANSI text, duplicate columns, long codes and decimals such as `1.50` are kept. XML mixed text, JSONL with BOM and INI root keys are kept. XLSX dates stay dates and JSON → XLSX keeps every column. CSV → TOML and INI now work.
- **Documents.** HTML with nested blocks, RTF punctuation, Markdown headings and front matter convert correctly. Documents keep their own title. Generated ODT files open in Pandoc.
- **Excel to PDF.** XLSX, XLSM, XLTX, XLS and ODS become PDF, CSV and other tables without LibreOffice. Every visible sheet is printed as a table, with dates, currencies, colors and merged cells. Tables saved as `.xls` by other programs also open. If LibreOffice fails, FLIP uses this reader. PDF is now on the main ring of the wheel for spreadsheets.
- **PDF to images.** Pages keep their proportions.
- **PDF.** Merge and Strip Metadata copy pages instead of turning them into images. Phone photos keep their orientation.
- **Images.** ICC colors, semitransparent pixels and icon proportions are preserved.
- **Audio and video.** H.264 output plays in common players. Compress to size uses the real duration and reports progress. Long videos no longer stop after 20 minutes. Impossible MIDI and APE outputs are no longer offered.
- **Archives.** GitHub `.tar.gz` files open. Canceling 7z stops the process and removes its temporary files.
- **Watch folders.** Several rules on one folder, read-only files and long copies work. Watchers are not recreated after each conversion, and updates wait for them.
- **Batches.** One failed format no longer cancels the other selected formats.
- **Settings Hub.** The File Explorer menu and Windows startup switches show and change the real state. The unused drag-only switch is removed.
- **Store engines.** A Whisper program chosen by the user is preferred to the bundled one. Whisper, background-removal models and engines downloaded by the GitHub edition are found again, so moving to the Store needs no new downloads.
- **Uninstaller.** `Disinstalla-FLIP.cmd` removes the File Explorer menu entry and keeps Pandoc in `%AppData%\FLIP\Engines`.
- **Memory.** The background-removal model is released after one minute without use.
- **Downloads.** FFmpeg, Whisper and fallback engine downloads are verified with SHA256.

## [0.6.3] - 2026-11-24 ("AURA Edition")

### Added
- **Update from the app.** The new Updates card in *Guide & Shortcuts* checks GitHub, downloads the official package, verifies its SHA256, installs it and restarts FLIP. FLIP contacts GitHub only when you ask. Thanks to [@AlphaGolf-sec](https://github.com/AlphaGolf-sec) for [PR #6](https://github.com/thetinyfoundry/FLIP/pull/6). This works for updates that follow v0.6.3: install v0.6.3 from the ZIP once.

### Changed
- **Safer updates.** FLIP does not update while it runs as administrator, waits for running conversions and reopens if the update fails. If the copy fails, the installer restores the previous files.
- **One backup.** After a verified update the installer keeps only the newest `Flip.backup-*` folder and removes older ones. Thanks to [@AlphaGolf-sec](https://github.com/AlphaGolf-sec) for pointing this out.

## [0.6.2] - 2026-11-24 ("AURA Edition")

### Added
- **Built-in DOCX → PDF and DOCX → RTF with formatting.** When LibreOffice is not installed, FLIP now preserves styles, colors, headings, lists, tables and Unicode accents.

### Fixed
- **PDF/A-2b output.** Files now embed a valid sRGB ICC profile and a correct cross-reference table for ISO 19005-2b. This resolves the Adobe Acrobat/Reader message "A drawing error occurred".
- **Wheel and routing.** PDF is always first on the primary ring for DOCX, DOC, ODT and RTF, next to a separate PDF/A entry. LibreOffice takes priority when installed. When it exports DOCX to PDF, the preset must not request metadata removal. The built-in exporter supports metadata removal.
- **Command-line `--convert` for YAML, XML and CSV.** Conversions from these formats no longer fail with a `JsonElement` error. Thanks to [@giovamolinaro](https://github.com/giovamolinaro) for the original fix.
- **YAML lists.** Nested lists and list items with several keys are no longer dropped. Invalid or unsupported YAML now reports an error instead of producing partial output.
- **Leading zeros.** CSV values such as postal codes (`00123`), phone numbers (`0815551234`) and codes (`007`) stay text, in the wheel and on the command line. Values with a leading `+` (`+39081`) are also kept. `0`, `0.5`, `-0.25`, `42` and `-15` stay numbers. The same rule applies to XML, YAML and INI values.

## [0.6.1] - 2026-11-20 ("AURA Edition")

- **DOCX → PDF.** FLIP usa LibreOffice per conservare l'impaginazione. Installa LibreOffice da **Impostazioni → Motori & GPU** se PDF non compare tra i formati. Questa conversione richiede un preset PDF senza rimozione metadati.
- **PDF → DOC.** I PDF con testo selezionabile si possono convertire in un file Word 97–2003 modificabile con LibreOffice. Il documento riprende il testo. Per impaginazione, immagini e scansioni resta necessario un flusso separato.
- **Secondo avvio di FLIP.** Se Windows nega l'accesso all'istanza già aperta, FLIP spiega come chiuderla e riaprirla normalmente. Grazie ad [AlphaGolf-sec](https://github.com/AlphaGolf-sec) per la segnalazione e la [PR #2](https://github.com/thetinyfoundry/FLIP/pull/2).

## [0.6.0] - 2026-11-18 ("AURA Edition")

### Added
- **FLIP QuickLook (`FlipPreviewWindow`) con Spazio durante il drag**:
  - **Viewport 3D Interattivo** (DirectX / WPF `Viewport3D`) con illuminazione studio a 3 punti, orbita quaternion senza gimbal lock, pan, zoom, overlay Wireframe arancione FLIP, vista **Blueprint a 4 Viste** (`Tab`), **Segnalibro d'Inquadratura `(A)`** (`📌 Memorizza Vista (A)` / `↺ Richiama Vista (A)` con salvataggio di orientamento, pan, zoom e stato auto-giro).
  - **Provino Tipografico `"Aa"`** nella vista Palette `#HEX`: selezione coppia *Testo / Sfondo* con calcolo contrasto WCAG (`AAA`/`AA`), pulsante di scambio `⇄` e copia immediata `#TESTO / #SFONDO`.
  - Anteprima universale per testo, documenti tabellari, immagini SVG/DXF/ICO e riproduzione zero-file con salvataggio esplicito tramite `Ctrl+S`.
- **FLIP Creator Pack (`BuiltInQuickTools` & `EngineBootstrap`)**:
  - `Turntable3DQuickTool` (`360°` GIF 720x720 24 frame con wipe wireframe), `PaletteQuickTool` (Swatch Card PNG con 6 colori dominanti `#HEX`), `WaveformQuickTool` (`1920x480` trasparente `.wav/.audio -> .png`), preset `9:16 Blur` Social in `CropQuickTool`, `PrivacyCleanQuickTool` (zero metadati EXIF/XMP) e combo `+ ARCHIVIO .ZIP` con `Z`.
- **Conversione PDF/A-2b (`PdfEngine`, `FormatRegistry`, `RoutePlanner`, `OutputPathAllocator`)**:
  - Generazione di file **PDF/A-2b (ISO 19005-2)** conformi con XMP `pdfaid:part=2` / `conformance=B`, `OutputIntents` `sRGB IEC61966-2.1` e supporto diretto da PDF (`_pdfa.pdf`), immagini e documenti di testo.
- **Recupero del Batch Misto & Traccia dell'Ultima Scelta**:
  - Pillole per gruppi omogenei (es. `[ 8 PNG ]`, `[ 2 WAV ]`, `[ 1 PDF ]`, `[ Tutti ]`) sia nell'Hub (`SettingsHubWindow`) sia sulla Ruota (`Wheel`) quando vengono selezionati o trascinati file incompatibili.
  - Dizionario in RAM `Wheel.LastSuccessfulTargetBySourceExt` con arco indicatore ambra (`#FFB547`) sullo spicchio corrispondente (e nei `Sub-Rings`).
- **Tutorial Interattivo Passo-Passo (`FirstRunWelcomeWindow`)**:
  - Guida a 4 schede cliccabili (*Trascina & Rilascia* con mini-ruota interattiva e Coach-Mark, *Strumenti (Tab)* con ribaltamento animato, *QuickLook (Spazio)*, *Motori AI & Tobii*) e notifica di benvenuto nella barra di sistema al completamento.
- **Accessibilità Tobii / Senza Mani (`GazeOrbWindow` & `GazeDwell`)**:
  - Bollino flottante `[ F ]` auto-dock sul bordo destro (`Opacity = 0.35`, fade-in `90px` -> `1.0`), trascinabile in verticale, menu contestuale scuro e selezione a sguardo (*Dwell-to-Select*) su Ruota e QuickLook.
- **Aura Luminosa Multi-Monitor (`ScreenEdgeAuraWindow`) & Hub Unificato (`SettingsHubWindow`)**:
  - Aura click-through lungo i bordi del monitor attivo con impulso verde/ambra di completamento.
  - Hub dark-theme borderless a 6 sezioni (`1. Ruota & Spicchi`, `2. Attivazione & Input`, `3. Destinazione File`, `4. Motori & GPU`, `5. Cartelle Automatiche`, `6. Cronologia & Preset`) con verifica reale su disco (`CliResolver.VerifyAndDetectEngines`) e **Installer 1-Click (`EngineInstaller`)** per tutti i 6 motori esterni (`whisper`, `pandoc`, `ffmpeg`, `tesseract`, `calibre`, `libreoffice`).
- **Servizi di Sistema & Installer 1-Clic**:
  - `BrowserDropService` (immagini/URL/Data-URI dal browser), `ClipboardFlipService` (`Ctrl+Shift+F`), `WatchFolderService` anti-loop.
  - `scripts/Installa-FLIP.cmd` e `scripts/Disinstalla-FLIP.cmd` con verifica preventiva integrità `SHA256SUMS.txt` in `Install.ps1`.
- **Motori Nativi & Formato 3D FBX**:
  - 6 motori offline (`ImageEngine`, `PdfEngine`, `OfficeOpenXmlEngine`, `DataSerializationEngine`, `ArchiveEngine`, `Mesh3DVectorGeoEngine`) incluso supporto `.fbx` ASCII e binario (`Kaydara FBX Binary`), `RoutePlanner` con isolamento rigoroso di dominio, `OutputPathAllocator` atomico e `SettingsStore` thread-safe.

### Changed
- **Salvataggio predefinito accanto al file originale (`SameAsSource`)**: i file convertiti vengono creati nella stessa cartella dell'originale; la cartella `Downloads` funge da fallback solo per sorgenti provenienti da cartelle temporanee (`%TEMP%`, browser drop) o senza permessi di scrittura.
- **Ridenominazione UX `"Preset & Macro"`**: aggiornate tutte le etichette UI da *"Ricetta"/"Ricette"* a *"Preset"* / *"Preset & Macro"* (mantenendo la retrocompatibilità JSON di `Recipes`/`FlipRecipe`) e collegato `StripMetadata` lungo tutta la pipeline (`ExecuteRecipeAsync`, `ImageEngine`, `PdfEngine`, `ConversionEngine`, `FfmpegEngine`, `PandocEngine`).
- **Scansione Whisper AI "Zero-Duplicati" (`WhisperLocator`)**: ricerca limitata alle posizioni applicative standard con esclusione generica di wrapper Python (`python_embeded`, `venv`, `conda`, `Scripts\whisper.exe`) e verifica dimensione minima modello (`>= 30 MB`).
- **GNU GPLv3**. Da questa versione, il codice originale e le risorse di FLIP sono distribuiti con licenza `GPL-3.0-only`.

### Fixed
- **Multi-Monitor, DPI misti e Tavoletta Grafica (`Native.cs`, `Wheel.cs`, `Program.cs`, `ScreenEdgeAuraWindow.cs`)**:
  - Riposizionamento fisico sempre forzato sul monitor corrente del cursore (`PlaceNearCursor`, `OnDpiChanged` su `Wheel` con `TransformToDevice`, senza mai saltare `SetWindowPos` a `Width`/`Height` invariate).
  - Sblocco sincrono della vecchia istanza `wheel` (`DismissWheel(false)` / `CloseImmediately`) se orfana o dopo input penna senza `WM_INPUT MouseUp`, con `watchdog` reattivo basato su `!Native.IsPrimaryMousePressed()`.
  - Arresto immediato di `monitorTimer` in `ScreenEdgeAuraWindow.Dismiss()` / `FadeOut()` e applicazione condizionale di `SWP_SHOWWINDOW` solo se `_isActive == true`.
- **Routing di Dominio & Sub-Rings (`RoutePlanner.cs`, `WheelLayout.cs`)**: eliminati i percorsi multi-hop lossy tra famiglie incompatibili (es. audio/testo verso immagini) e rimosse le etichette generiche `"Altri 1..5"` sostituendole con espansioni semantiche chiare.

### Security & Privacy
- **Validazione rigida buffer esterni glTF (`Mesh3DVectorGeoEngine`)**: rifiuto esplicito di URI assoluti (`http://`, `file://`, drive letter), percorsi di rete UNC, path traversal (`..` e `%2e%2e`), symlink / `ReparsePoint` su file o cartelle intermedie e limiti massimi di allocazione (`MaxGltfBufferBytes`).
- **Audit privacy pre-rilascio**: `Evidence.Enabled` disattivato di default fuori dalle suite diagnostiche; mascheramento automatico di `%USERPROFILE%`, `data:` URI ed URL (`[redacted-url]`) con troncamento a 200 caratteri in `Evidence.SanitizeString`; `FlipPreviewWindow` limita l'output visualizzato a `fi.Name`; `SettingsStore.ExportToFile` esclude cronologia e percorsi locali personalizzati.
- **Hardening Packaging (`Package.ps1`)**: rimozione rigorosa di `.pdb`, `.lnk`, `events.jsonl`, `logs/` e `obj/`, scansione automatica anti-leak su `Flip.dll` prima di creare l'archivio `.zip` e verifica degli hash `SHA256SUMS.txt` prima dell'installazione.

---

## 0.5.1

- Fix `Install.ps1` when run directly from the extracted ZIP. After installing the .NET 10 Windows Desktop Runtime x64, extract the entire ZIP, double-click `Install-FLIP.cmd`, then start FLIP from the Desktop or Start menu shortcut.
- Detect FFmpeg placed in `%LOCALAPPDATA%\Flip\tools`. Restart FLIP after copying `ffmpeg.exe` to that folder to enable audio and video conversions.
- Default to A4 when converting DOCX to PDF through Pandoc and LibreOffice.
- Explain how to open the wheel in the multiple-conversion window, using the configured shortcut.

## 0.5.0-beta.1

- Add native structured-data conversions for JSON, YAML, XML and CSV (into JSON, YAML, XML, CSV, TXT, HTML, Markdown and PDF) without external dependencies.
- Add audio conversions (MP3, WAV, FLAC, OGG, M4A, AAC) and video conversions/audio extraction (MP4, MKV, WebM, AVI, MOV, plus MP3 and WAV audio extraction) powered by local FFmpeg.
- Keep the radial wheel open during long conversions with an animated circular progress arc, radial sweep line, live percentage counter and Escape cancellation.
- Add a GitHub release update check in the tray context menu (`Verifica aggiornamenti su GitHub…`).

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
