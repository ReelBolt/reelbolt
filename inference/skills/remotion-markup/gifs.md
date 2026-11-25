# Animated GIFs

Use `@remotion/gif` for animated GIFs. It keeps the GIF in step with the Remotion timeline, so
a render is deterministic. A plain `<img>` plays on the browser clock and renders unpredictably.

```tsx
import {Gif} from '@remotion/gif';
import {staticFile} from 'remotion';

export const Reaction: React.FC = () => (
  <Gif src={staticFile('reaction.gif')} width={480} height={270} fit="contain" />
);
```

- Put the file in `public/` and reference it with `staticFile()`. There is no network in the
  sandbox, so a remote URL fails.
- `playbackRate` speeds the GIF up or down; `loopBehavior` is `loop`, `pause-after-finish` or
  `unmount-after-finish`.
- Wrap the GIF in a `Sequence` to choose when it appears.
- For a long or large animation, prefer a video file with `OffthreadVideo`.
