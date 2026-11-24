# Showing captions

## 1. Group words into pages

A page is a few words shown together. Group by a time window and break on pauses:

```ts
type Page = {startMs: number; endMs: number; words: Caption[]};

export function toPages(captions: Caption[], opts = {windowMs: 1200, silenceMs: 400, maxChars: 32}): Page[] {
  const pages: Page[] = [];
  let current: Page | null = null;
  for (const c of captions) {
    const gap = current ? c.startMs - current.words[current.words.length - 1].endMs : 0;
    const chars = current ? current.words.map((w) => w.text).join('').length + c.text.length : 0;
    const tooLong = current && (c.endMs - current.startMs > opts.windowMs || chars > opts.maxChars);
    if (!current || gap > opts.silenceMs || tooLong) {
      current = {startMs: c.startMs, endMs: c.endMs, words: []};
      pages.push(current);
    }
    current.words.push(c);
    current.endMs = c.endMs;
  }
  return pages;
}
```

If `@remotion/captions` is installed, `createTikTokStyleCaptions({captions, combineTokensWithinMilliseconds: 1200})`
returns `{pages}` where each page has `text`, `startMs`, `durationMs` and `tokens` (`text`, `fromMs`,
`toMs`). It does the same job.

## 2. Show the active page

Convert the frame to a time and render the page whose window contains it. Use one `Sequence` per
page so each page animates in from its own frame 0:

```tsx
const {fps} = useVideoConfig();
const pages = useMemo(() => toPages(captions), [captions]);

return (
  <AbsoluteFill>
    {pages.map((page, i) => {
      const from = Math.round((page.startMs / 1000) * fps);
      const next = pages[i + 1]?.startMs ?? page.endMs + 300;
      const duration = Math.max(1, Math.round(((next - page.startMs) / 1000) * fps));
      return (
        <Sequence key={i} from={from} durationInFrames={duration} layout="none">
          <CaptionPage page={page} startMs={page.startMs} />
        </Sequence>
      );
    })}
  </AbsoluteFill>
);
```

Pages end when the next one starts, so two pages are never visible at once.

## 3. Highlight the spoken word

Inside a page, compare the current time with each word's range. Remember that inside the
`Sequence` the frame restarts at 0, so add the page's start back:

```tsx
const CaptionPage: React.FC<{page: Page; startMs: number}> = ({page, startMs}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const nowMs = startMs + (frame / fps) * 1000;
  const enter = spring({frame, fps, config: {damping: 200}, durationInFrames: 6});

  return (
    <AbsoluteFill style={{justifyContent: 'flex-end', alignItems: 'center', paddingBottom: 140}}>
      <div style={{
        maxWidth: '80%', textAlign: 'center', fontSize: 64, fontWeight: 800, lineHeight: 1.2,
        color: 'white', textShadow: '0 3px 12px rgba(0,0,0,0.8)',
        opacity: enter, transform: `translateY(${(1 - enter) * 16}px)`, whiteSpace: 'pre-wrap',
      }}>
        {page.words.map((w, i) => {
          const active = nowMs >= w.startMs && nowMs < w.endMs;
          return <span key={i} style={{color: active ? '#facc15' : 'white'}}>{w.text}</span>;
        })}
      </div>
    </AbsoluteFill>
  );
};
```

Use `whiteSpace: 'pre-wrap'` so the leading spaces in word text are kept.

## Styles

| Style | How |
|---|---|
| Subtitle | whole page in white, small box behind it, no per-word effect |
| Karaoke | all words visible, the active word recolours (above) |
| Punch | one to three big words at a time, scale-in on each page (use `maxChars: 14`) |

## Checks

- Show the first and last frame of three different pages in stills and read them aloud against the
  audio.
- The caption layer must not extend the composition: its sequences all end by the composition's
  `durationInFrames`.
- Characters outside the installed fonts (rare scripts, emoji) fall back to another font; check
  them in a still.
