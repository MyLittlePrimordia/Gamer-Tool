# Assets & Media Provenance (I-002)

All files under `app/Assets/` are embedded into `GamerTool.exe`.
Total ~18.6 MB — the bulk of the binary. Before publishing, confirm each
file is either original or redistributable, and record source + license here.

| File | Size | Purpose | Source | License |
|------|------|---------|--------|---------|
| `day.jpg` | ~1.0 MB | Display preview scene | **TODO owner: confirm origin** | **TODO** |
| `night.jpg` | ~0.9 MB | Display preview scene | **TODO owner: confirm origin** | **TODO** |
| `animated.gif` | ~8.6 MB | Preview loop | **TODO owner: confirm origin** | **TODO — shrink dimensions/frames if kept** |
| `game.mp3` | ~1.5 MB | Audio preview | **TODO owner: confirm origin** | **TODO — re-encode ~96kbps if kept** |
| `music.mp3` | ~5.4 MB | Audio preview | **TODO owner: confirm origin** | **TODO — re-encode ~96kbps if kept** |
| `footsteps.mp3` | ~2.5 MB | Audio preview | **TODO owner: confirm origin** | **TODO — re-encode ~96kbps if kept** |
| `icon.png` / `GamerTool.ico` | ~20/62 KB | App icon | Original | MIT (this repo) |
| `fac-template.fac` | ~1 KB | FxSound preset template | Original | MIT (this repo) |
| `emoji/*.png` | small | Colour emoji bitmaps (WPF cannot draw COLR/CPAL) | Rendered via browser text stack | Confirm font license allows bitmap redistribution |
| `crosshairs/*.png` | ~0.8 MB (200 files) | Crosshair pack, embedded and unpacked to `%AppData%\GamerTool\crosshair-pack` on first launch | **TODO owner: confirm origin** | **TODO** |

## Rules for replacement

- If any asset is not clearly redistributable, replace it with an original
  or a CC0 / MIT-licensed file and update this table.
- Keep preview media small: mp3 ~96 kbps mono is enough for a preview loop;
  gif cannot become webm/mp4 in WPF without extra deps, so shrink
  dimensions/frames instead.
- Code license: MIT — see `LICENSE` at repo root.
