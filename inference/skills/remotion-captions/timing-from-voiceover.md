# Captions from voiceover timing

When the narration is generated per scene, you usually have, for each scene, the audio file and
its duration (the staging tool returns a `timing` list), plus the script text. That is enough for
accurate page-level captions even without word timestamps.

## Page-level captions

For each scene:

1. Take the scene's start frame and the audio's duration.
2. Split the scene's script into phrases at sentence or comma boundaries.
3. Give each phrase a share of the duration proportional to its character count.
4. Each phrase is a page (`startMs`, `endMs`); render with `Sequence` per page.

```ts
export function phrasesToCaptions(text: string, sceneStartMs: number, sceneDurationMs: number): Caption[] {
  const phrases = text.split(/(?<=[.!?,;:])\s+/).filter(Boolean);
  const total = phrases.reduce((n, p) => n + p.length, 0) || 1;
  let cursor = sceneStartMs;
  return phrases.map((p) => {
    const dur = (p.length / total) * sceneDurationMs;
    const cap = {text: p, startMs: Math.round(cursor), endMs: Math.round(cursor + dur), timestampMs: null, confidence: null};
    cursor += dur;
    return cap;
  });
}
```

Pad the end by 0 and do not let the last phrase run past the scene's audio.

## Be honest about precision

Proportional timing is an estimate. It is good enough for 2 to 3 second phrases and not good enough
for karaoke-style word highlighting. If word-level timing is required and the platform has
supplied it (a transcript with word times), use that instead and the full approach in
`display-captions.md`. Do not simulate word timing to fake a karaoke effect.

## Audio-first

Because scene length comes from the audio, compute the caption times from the same numbers that
size the scenes (`remotion-markup`, `sequencing.md`) so captions and picture cannot drift apart.
