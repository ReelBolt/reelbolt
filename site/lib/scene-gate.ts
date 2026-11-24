// Gate for the homepage's remaining WebGL scene (the viewfinder). A scene mounts only on a wide
// desktop viewport, on a client with enough hardware threads to render it without starving the main
// thread, and never for a Save-Data visitor. Every other client keeps the static fallback and never
// requests the three.js chunk. Reduced motion is a separate check in the mount component and always
// wins.

export interface SceneEnv {
  wide: boolean;
  cores: number;
  saveData: boolean;
}

export const MIN_SCENE_CORES = 4;

export function shouldMountScene(env: SceneEnv): boolean {
  return env.wide && env.cores >= MIN_SCENE_CORES && !env.saveData;
}

// Browser-only: call from an effect, never during render or SSR.
export function readSceneEnv(): SceneEnv {
  const nav = navigator as Navigator & { connection?: { saveData?: boolean } };
  return {
    wide: window.matchMedia('(min-width: 1024px)').matches,
    cores: nav.hardwareConcurrency ?? 0,
    saveData: nav.connection?.saveData === true,
  };
}

// Runs `callback` once the browser is idle (3s timeout), with a plain timer where
// requestIdleCallback is missing (Safari). Returns a cancel function for effect cleanup.
export function whenIdle(callback: () => void): () => void {
  if (typeof window.requestIdleCallback === 'function') {
    const id = window.requestIdleCallback(callback, { timeout: 3000 });
    return () => window.cancelIdleCallback(id);
  }
  const id = window.setTimeout(callback, 2000);
  return () => window.clearTimeout(id);
}

const INTERACTION_EVENTS = ['pointermove', 'pointerdown', 'keydown', 'touchstart', 'wheel', 'scroll'];

// Runs `callback` once, on the first pointer, key, touch, wheel or scroll event on window. Used
// instead of whenIdle so the scene's cost is paid only by visitors who are actually using the page.
// Returns a cleanup that removes every listener (safe to call after the callback has already run).
export function whenFirstInteraction(callback: () => void): () => void {
  // Capture the target now so cleanup removes listeners from the same object they were added to.
  const target = window;
  const options: AddEventListenerOptions = { passive: true };
  function stop(): void {
    for (const type of INTERACTION_EVENTS) target.removeEventListener(type, fire, options);
  }
  function fire(): void {
    stop();
    callback();
  }
  for (const type of INTERACTION_EVENTS) target.addEventListener(type, fire, options);
  return stop;
}
