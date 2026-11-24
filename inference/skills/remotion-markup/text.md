# On-screen text

## Rules

- Text is plain HTML/CSS inside `AbsoluteFill`s. Use real elements (`h1`, `p`, `span`), not images
  of text, so it can be measured, translated and checked.
- Use the exact wording the brief or script gives you. Do not invent claims, prices, statistics or
  quotes.
- Titles: 90 px or larger on a 1080p frame; supporting lines 48 to 64 px; never below 40 px.
- Maximum two or three short lines per scene. Break long sentences into separate beats.
- Set `lineHeight` (1.1 to 1.2 for titles, 1.3 to 1.4 for body) and `letterSpacing` (slightly negative
  for large titles, e.g. `-0.02em`).
- Never let text depend on the browser to wrap unpredictably. Give text blocks an explicit
  `maxWidth` (in pixels derived from `width`), `textWrap: 'balance'` for titles, and check the
  result with the layout check tool when one is available.
- Keep text inside the safe area (see `remotion-create`, `video-layout.md`).

## Fade and rise

```tsx
const enter = spring({frame, fps, config: {damping: 200}});
<h1 style={{opacity: enter, transform: `translateY(${(1 - enter) * 24}px)`}}>{title}</h1>
```

## Word-by-word reveal

```tsx
const words = text.split(' ');
<p>
  {words.map((w, i) => {
    const s = spring({frame, fps, delay: i * 3, config: {damping: 200}});
    return (
      <span key={i} style={{display: 'inline-block', opacity: s, transform: `translateY(${(1 - s) * 16}px)`}}>
        {w}&nbsp;
      </span>
    );
  })}
</p>
```

## Typewriter

Slice the string by frame, keep the layout stable by reserving the space of the full text:

```tsx
const chars = Math.floor(interpolate(frame, [0, 45], [0, text.length], {extrapolateRight: 'clamp'}));
<span style={{position: 'relative'}}>
  <span style={{visibility: 'hidden'}}>{text}</span>
  <span style={{position: 'absolute', left: 0, top: 0}}>{text.slice(0, chars)}</span>
</span>
```

## Number counter

```tsx
const value = Math.round(interpolate(frame, [0, 45], [0, 12500], {easing: Easing.out(Easing.cubic), extrapolateRight: 'clamp'}));
<span style={{fontVariantNumeric: 'tabular-nums'}}>{value.toLocaleString('en-US')}</span>
```

`tabular-nums` keeps digits from jittering as the number changes.

## Highlight and underline

Animate a background bar behind the words with a width driven by a spring:

```tsx
<span style={{position: 'relative', display: 'inline-block'}}>
  <span style={{position: 'absolute', left: 0, bottom: 4, height: '0.4em', width: `${enter * 100}%`,
    background: 'rgba(124,58,237,0.45)', zIndex: 0}} />
  <span style={{position: 'relative', zIndex: 1}}>important words</span>
</span>
```

## Lower-third / callout

A small card anchored to a corner: 5% margin, a solid or lightly blurred background for contrast,
title 48 to 56 px, subtitle 32 to 36 px, slide in over 12 frames and out over 9. For an overlay
that must sit on top of footage, render it on a transparent background (see `remotion-render`,
`transparent-videos.md`).

## Measuring text

If a box must fit its text, do not guess: size the font from the available width, using a
conservative character-width factor (about 0.55 of the font size per character for typical sans
fonts), or measure with `@remotion/layout-utils` (`measureText`, `fitText`) when it is installed at
the project's Remotion version.
