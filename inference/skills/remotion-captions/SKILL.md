---
name: remotion-captions
description: Timed captions: grouping words into pages, showing and highlighting them, importing SRT
version: 4.0.535
---

Captions are text that follows the speech. In Remotion you draw them yourself from timed data, so
the work is: (1) get timed words, (2) group them into short on-screen pages, (3) show the page that
is active on the current frame.

Topic files (read with `ReadSkillResource`):

- `display-captions.md`: grouping words into pages, showing and highlighting them.
- `import-srt-captions.md`: reading `.srt` subtitle files into the same data shape.
- `timing-from-voiceover.md`: building captions from the per-scene voiceover timing the platform
  provides, when there is no transcript file.

## Data shape

Keep captions as JSON, in milliseconds, using the `Caption` type from `@remotion/captions`:

```ts
type Caption = {
  text: string;            // usually one word, with its leading space ("  hello")
  startMs: number;
  endMs: number;
  timestampMs: number | null;
  confidence: number | null;
};
```

`@remotion/captions` (parseSrt, serializeSrt, createTikTokStyleCaptions) is not pre-installed in the
sandbox. If it cannot be installed at exactly the project's Remotion version, define the type
yourself and use the small helpers in the topic files; nothing here requires the package.

## Where timing comes from

The sandbox cannot transcribe audio (no network, no speech model). Use whichever exists, in this
order:

1. A transcript or `.srt` file in the project (`import-srt-captions.md`).
2. The platform's voiceover timing for each scene (`timing-from-voiceover.md`).
3. The script text split evenly over the scene's duration: a last resort, and only for short
   lines. Never present guessed timing as exact.

## Design rules

- At most two lines, about 28 to 36 characters per line on a 16:9 frame, fewer on 9:16.
- Font 56 px or larger on 1080p (72+ on vertical). High contrast: white text with a dark outline or
  a semi-opaque dark box.
- Place in the lower third inside the safe area; on 9:16 keep above the bottom ~15%.
- A page should stay on screen at least 1 s (long enough to read) and switch on a natural pause.
- Never cover a lower-third graphic or the speaker's face; move captions up when other text is in
  the same area.
- Use the speaker's words verbatim. Do not paraphrase, "fix" or translate unless asked.
- Captions are an overlay layer in the final composition: draw them above the picture, below any
  logo watermark.
