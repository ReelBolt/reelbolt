# Reading an .srt file

An SRT file is blocks of: an index, a time range `HH:MM:SS,mmm --> HH:MM:SS,mmm`, then one or more
text lines, separated by blank lines.

```
1
00:00:01,000 --> 00:00:03,500
Welcome to the product tour.

2
00:00:03,500 --> 00:00:06,000
Everything starts with a project.
```

## With the package

If `@remotion/captions` is available:

```ts
import {parseSrt} from '@remotion/captions';
const {captions} = parseSrt({input: srtText});
```

## Without the package

```ts
function toMs(t: string): number {
  const [hms, ms] = t.trim().split(',');
  const [h, m, s] = hms.split(':').map(Number);
  return ((h * 60 + m) * 60 + s) * 1000 + Number(ms);
}

export function parseSrtText(input: string): Caption[] {
  return input
    .replace(/\r\n/g, '\n')
    .trim()
    .split(/\n{2,}/)
    .flatMap((block) => {
      const lines = block.split('\n');
      const timeLine = lines.find((l) => l.includes('-->'));
      if (!timeLine) return [];
      const [a, b] = timeLine.split('-->');
      const text = lines.slice(lines.indexOf(timeLine) + 1).join('\n');
      return [{text, startMs: toMs(a), endMs: toMs(b), timestampMs: null, confidence: null}];
    });
}
```

An SRT cue is a whole phrase, not a word, so each caption is a page already. Skip the grouping
step and render one `Sequence` per cue (`display-captions.md`, section 2). You cannot highlight
single words from an SRT file; fall back to the subtitle style.

## Getting the text into the composition

The sandbox has no network. Read the file from the project, not from a URL:

- Stage the `.srt` into `public/` and read it with `fetch(staticFile('captions.srt'))` inside
  `calculateMetadata` or a `delayRender` handle (release the handle in `finally`); or
- Parse it ahead of time and pass the resulting JSON as a composition prop (`defaultProps`),
  which avoids any async loading. For short videos this is the simplest and most robust.

Validate after parsing: every caption has `endMs > startMs`, the list is sorted by `startMs`, and
the last `endMs` is not past the video's end. Fix or clamp otherwise.
