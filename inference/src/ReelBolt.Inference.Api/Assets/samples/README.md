# Bundled sample clips

`first-film.mp4` is the clip behind `POST /api/v1/projects/{projectId}/files/sample` (`sampleKey: "first-film"`).
It is embedded in the Inference API assembly (logical name `Samples.first-film.mp4`).

It is entirely first-party and synthetic: no third-party footage, music or text. Regenerate it with
`./generate-first-film.sh` (ffmpeg with libx264 and the `lavfi` sources): 24 s, 640x360, 25 fps, h264 + aac,
about 0.4 MB. Four 6 s scenes with hard cuts (a colour card with the text "Scene 1", the `testsrc2` pattern, a colour
card with "Scene 3", SMPTE bars), over a 330 Hz sine tone that is silent for the last 1.8 s of each scene so the
silence/pause detectors have something to find.
