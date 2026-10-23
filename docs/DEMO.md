# README demonstration

The repository includes the supplied recording at [docs/assets/flip-demo.mp4](assets/flip-demo.mp4). It is a 38-second H.264/AAC vertical capture at 428×838, suitable as an early product demonstration. Before using it as the final promotional video, review the whole clip and confirm that no personal paths, notifications or unrelated windows are visible. The existing wheel PNG is a rendered UI preview, not evidence of physical drag/drop.

Use an otherwise empty Explorer folder with a synthetic `sample.png`. Show file extensions. Keep names and paths anonymous in the final crop; hide notifications. Confirm the selected shortcut in Settings before capture; use Down Arrow for the default demonstration.

Record: source file (2 s), start dragging and tap Down Arrow (3 s), move across two sectors (3 s), drop on JPG (2 s), show `sample.converted.jpg` next to the original and open the result (6 s). Finish with “Local conversion · original preserved · Windows beta”. Separately record key-held-first ordering, cancellation and outside-wheel behavior for acceptance, without presenting them as promotional footage.

`scripts/Record-Demo.ps1` records an explicitly selected screen rectangle using an already installed FFmpeg. It records the mouse but no microphone/system audio. X/Y are physical desktop coordinates; choose a crop that includes the actual wheel and synthetic Explorer folder. Example for a safe region starting at the desktop origin:

```powershell
.\scripts\Record-Demo.ps1 -X 0 -Y 0 -Width 1280 -Height 720 -Seconds 25
```

There is a 5-second countdown. Review the entire MP4, verify conversion output and unchanged source, then use GitHub's README attachment upload to obtain a real attachment URL. Follow `PUBLICATION-PLAN.md`. Keep raw takes under ignored `_local/demo`; add only the approved public poster/asset.

The README demo must use an actual conversion, not an animation that implies native testing. A silent video does not validate sound. Test audio separately using the tray's “Prova il suono” and rapid real sector changes.
