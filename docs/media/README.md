# Demo assets for README

`demo.gif` is the README hero demo. It must always show a **real film
produced by this pipeline** — placeholder or synthetic clips undersell the
project and must not be used.

Current `demo.gif`: three segments from a real CAM DOWN! PV take captured in
Unity (cameraman close-up → monster reveal → the standoff), 12 s, 720p,
10 fps, ~10 MB. Regenerate from the source take if a better cut ships.

## Recommended source

The CAM DOWN! PV delivery (`final.mp4`, 48 s, 1080p24 — see
`samples/timelines/camdown-pv/README.md`) or 《别停机 / KEEP ROLLING》.

## Produce the GIF (ffmpeg)

Cut a 8–12 s representative segment, 960 px wide, 12 fps:

```bash
ffmpeg -ss 00:00:10 -t 10 -i final.mp4 \
  -vf "fps=12,scale=960:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128[p];[s1][p]paletteuse=dither=bayer" \
  -loop 0 docs/media/demo.gif
```

Keep the file under ~10 MB so the README loads fast; shorten the segment or
drop to 10 fps / 720 px if needed. Then uncomment the image line in
`README.md`.
