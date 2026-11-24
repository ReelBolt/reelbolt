// The autoplay rule for the hero's launch film, as a pure predicate so it can be unit-tested away
// from the DOM. Two conditions are absolute: reduced motion is a hard opt-out (a visitor who asked
// the OS for less motion is never shown an autoplaying 61-second film), and a visitor's own pause is
// never overridden (scrolling the film back into view must not restart what they stopped).

export interface FilmAutoplayState {
  /** `prefers-reduced-motion: reduce` is in effect. */
  reduced: boolean;
  /** The film is currently intersecting the viewport. */
  intersecting: boolean;
  /** The visitor paused the film themselves (as opposed to the observer pausing it off-screen). */
  userPaused: boolean;
}

/** The fraction of the film that must be visible before it may autoplay. */
export const FILM_AUTOPLAY_THRESHOLD = 0.25;

export function shouldAutoplay(state: FilmAutoplayState): boolean {
  return state.intersecting && !state.reduced && !state.userPaused;
}
