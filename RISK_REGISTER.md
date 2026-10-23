# Risk register — beta candidate
| Risk | Handling / gate |
|---|---|
| LibreOffice lifecycle | Authorized upgrade to 26.8.1.1 verified and tested; installer requested restart. Post-restart check still open; maintained external engines remain required |
| Deep Office profile paths | Post-upgrade crash reproduced with long staging paths; short isolated temporary profiles plus deep-folder regression now pass. Very long input/output paths still need broader coverage |
| Native document/image parsers | Pinned packages, limits, staging, no elevation; NuGet feed audit is not complete upstream/native coverage or a full sandbox |
| Windows drag modifiers | Configurable single non-modifier key; Down Arrow default. Ctrl activation removed. Outside-wheel Windows drag/drop still applies; Escape cancels before release |
| False drag candidates | OLE payload validates before conversion; input state alone is not a file drag |
| Display/input hardware | Swapped mouse mapping tested; mixed-DPI/monitor edges and complete physical matrix still REVIEW |
| Audio devices | Original tic and mute/test command; intermittent prior reports not conclusively attributed to a backend defect |
| Document fidelity | No OCR/layout reconstruction/browser screenshot; FB2 plain-text loss found by tests and fixed |
| Incompatible formats | Shared output intersection; Pages and audio/video conversion not available |
| Resource footprint | About 102 MiB measured idle; local runtime includes large engines/debug symbols. No ultra-lightweight distribution claim |
| Hard shutdown | Normal cleanup passes; hard termination can leave private staging |
| Private diagnostics | Per-user rotated logs may contain paths. Never include them in repository/release |
| Git history/identity | Pattern scan and reviewed export; use noreply. GitHub account/commit metadata stays public |
| Helper execution | Scripts respect the host policy; manual build path documented; remote CI not yet run |
| Redistribution | Source license reviewed; executable notices/source obligations/signing/clean-machine tests still block public binaries |

Numeric limits and measured results are in TECHNICAL_DECISIONS.md and TEST_RESULTS.md. No audit proves that arbitrary untrusted input is safe.
