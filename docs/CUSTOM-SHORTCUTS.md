# Custom shortcuts and installation defaults

Current candidate: 0.4.0-beta.3, 23 October 2026. This update supersedes the fixed ISO-key choice in the earlier audit.

Right-click Flip's notification-area icon → **Impostazioni…**. Choose an arrow, Space, the ISO `<>` key, or use **Scegli un altro tasto…** to record a supported single key. Save applies it immediately and persists it atomically. Cancel leaves the previous choice. Down Arrow is the default for new profiles; existing supported choices are preserved. Modifiers alone, Fn, Windows keys, Escape, Delete and other reserved system keys are rejected. Users can change or mute the wheel tick separately in the tray menu; the original sound was preserved.

Both orders are supported:

- Start a file drag from Explorer/Desktop, then press the chosen key.
- Hold the chosen key first, then start dragging. Small click movements do not open the wheel. Release the key after the wheel appears.

A key tapped and released before the mouse gesture does not arm a future unrelated drag. Outside a gesture, keys retain their normal action; pressing a navigation key first may therefore navigate normally before the drag begins. Once the gesture begins, repeats are reserved for Flip. If the initial down was passed to Windows, its up is passed too; if Flip consumed the down during a drag, it consumes the matching up even after mouse release or focus changes. This avoids half-delivered presses and prevents Explorer from also handling captured shortcut events. Ctrl/Shift/native drag effects outside the wheel remain Windows behavior. Escape cancels; leaving the wheel is not a guarantee of drag cancellation. Integration with third-party file managers is not claimed.

The keyboard callback filters only the selected key, performs no file I/O and posts state changes to the Dispatcher. It never injects keys or logs unrelated typing. Raw Input tracks mouse movement, swapped primary buttons, button release and Escape. The [Microsoft low-level keyboard hook contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) supports consuming a handled event before the target receives it. A runtime registration event is not proof of audible results or a complete physical drag.

`Install.ps1` now creates Desktop/Start shortcuts and enables quiet, per-user Windows startup by default. `-NoShortcuts` and `-NoStartup` skip those integration steps. The tray startup control can disable it later; Windows can independently disable startup applications. No global Windows sound settings are changed. Running a built executable directly does not install shortcuts/startup.

## Evidence

- Input/policy/PCM/geometry/shortcut tests: **94 passed, 0 failed**, including both orders, movement threshold, stale mouse-down cancellation, normal typing, repeat handling and paired release.
- Conversion suite on this update's unchanged conversion engine: **99 passed, 0 failed**. An older trigger-order test was replaced by current press-pair checks.
- Installer copied to an isolated local destination with `-NoShortcuts -NoStartup`; checksums passed. The new default integration path was not tested on a clean Windows installation. Existing Desktop/Start links and startup on the development machine were read and already pointed to the active application.
- The settings window was opened in the real Windows session, inspected through accessibility and shown on screen. Custom-key UI input automation did not complete because of user activity and a subsequent foreground-window error; no full settings UI acceptance is claimed. Persisting Down Arrow through the same application settings handler and runtime hook registration were checked separately.
- Build/publish completed. The existing NU1900 warning reflects failed/cached NuGet network access; it is not a compiler error. No dependency versions were changed by this update.
- Physical Explorer/Desktop drag, audio error absence, startup after reboot, keyboard-layout coverage and a clean-machine install remain acceptance work. Nothing was published online.
