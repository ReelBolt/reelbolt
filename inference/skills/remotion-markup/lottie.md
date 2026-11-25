# Lottie animations

Use `@remotion/lottie` to play a Lottie (After Effects export) as part of the timeline.
Remotion drives the animation frame by frame, so it renders deterministically.

```tsx
import {Lottie, LottieAnimationData} from '@remotion/lottie';
import {useEffect, useState} from 'react';
import {cancelRender, continueRender, delayRender, staticFile} from 'remotion';

export const Animation: React.FC = () => {
  const [data, setData] = useState<LottieAnimationData | null>(null);
  const [handle] = useState(() => delayRender('Loading Lottie'));

  useEffect(() => {
    fetch(staticFile('animation.json'))
      .then((res) => res.json())
      .then((json) => {
        setData(json);
        continueRender(handle);
      })
      .catch((err) => cancelRender(err));
  }, [handle]);

  return data ? <Lottie animationData={data} style={{width: 400, height: 400}} /> : null;
};
```

- Keep the JSON in `public/` and load it with `staticFile()`; no remote URLs in the sandbox.
- Always pair the load with `delayRender` and `continueRender` so the frame waits for the data.
