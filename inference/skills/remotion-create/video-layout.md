# Layout for video

Video is not a web page: there is no scrolling, no viewport that resizes, and viewers cannot zoom.
Design for one fixed frame.

## Frame sizes

| Format | Size | Use |
|---|---|---|
| Landscape 16:9 | 1920 x 1080 | default, web and YouTube |
| Vertical 9:16 | 1080 x 1920 | Shorts, Reels, TikTok |
| Square 1:1 | 1080 x 1080 | feeds |
| Portrait 4:5 | 1080 x 1350 | feeds |

Read `width` and `height` from `useVideoConfig()` and derive sizes from them instead of hard-coding
pixel values in a scene, so the same scene can be rendered in another format.

## Safe area and sizes

- Keep text and logos at least 5% of the short side away from every edge. Phone UI covers more of
  the vertical formats: in 9:16 keep important content out of the top and bottom ~12%.
- Body text on a 1080p frame should be 40 px or larger; titles 90 px or larger. Anything smaller is
  unreadable on a phone.
- Keep contrast high. Light text on a dark or heavily tinted background beats a thin font on a photo.
- One idea per scene. Two or three short lines of text at most.

## Structure

- One component per scene, one file per scene. A parent composition places scenes in time with
  `<Series>` or `<Sequence>` (see `remotion-markup`, `sequencing.md`).
- Each scene is positioned in its own time: inside a `<Sequence>`, `useCurrentFrame()` starts at 0,
  so a scene never needs to know where it sits in the whole video.
- Put shared colours, font names and spacing in one `theme.ts` and import it everywhere. Use the
  user's own brand colours and fonts when the project provides them.
- Give every scene an enter and an exit (a fade or move over 6 to 15 frames) so cuts feel designed.

## Styling

Inline `style` objects and plain CSS both work. Flexbox and CSS grid are fine; they lay out
identically on every frame. Avoid `vh`/`vw` units (use the composition's pixel size), `position:
fixed`, hover states and anything that depends on the pointer.

Tailwind is not pre-installed in the sandbox. If you want it you must add and configure it for
Remotion; for generated promo videos, inline styles are simpler and safer.
