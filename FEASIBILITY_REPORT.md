# Interaction feasibility
Flip independently implements a Windows radial drop target using C#/.NET 10/WPF, Raw Input and OLE IDataObject. It does not inject code into Explorer, install hooks, or change Windows components.

A wheel opening before OLE DragEnter is only a candidate: mouse/keyboard activity does not prove that a file drag exists. The payload is accepted and validated by the drop target later. Only Copy is returned; Move-only payloads are rejected.

Observed during development: real Explorer PNG-to-JPG/WebP and HTML-to-Markdown drops, with converted copies beside the source and user reports of successful conversions. Initial Shift activation failed in user testing. A free-letter D trigger leaked into Explorer's type-ahead; it was removed. Right Ctrl now works before or during dragging in user testing, with Windows-swapped mouse buttons.

Ctrl-before-drag retains Windows behavior: Ctrl+click can accumulate selections. A group containing unsupported video has no common conversion target; Flip now explains this. It does not silently discard members or intercept Ctrl globally.

Alternatives available: select files in the app and click an output, or opt into the classic per-user Explorer context-menu verb. No shortcut combination is mandatory. The static context-menu verb is single-file and may appear under Show more options on Windows 11.

The wheel is non-taskbar, topmost, animated and mostly opaque. Escape, button-release cleanup and a watchdog close it. Window positioning uses the destination monitor and HWND DPI. Physical mixed-DPI multi-monitor and display-edge validation remains incomplete; geometry checks do not qualify those native scenarios.

Recommendation: publish as an experimental source project, retain the picker alternative, and complete the native input/display matrix before claiming general reliability.
