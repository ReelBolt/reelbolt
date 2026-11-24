import { useEffect, useRef, type RefObject } from 'react';

/**
 * The latest value of `value`, readable from effects and event callbacks without making them
 * re-run when it changes. Assigned in an effect (never during render), so read `.current` only
 * inside effect or event callbacks. Declare it before the effects that read it.
 */
export function useLatest<V>(value: V): RefObject<V> {
  const ref = useRef(value);
  useEffect(() => {
    ref.current = value;
  });
  return ref;
}
