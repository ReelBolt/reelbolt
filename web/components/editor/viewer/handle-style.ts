/** The colour of each picture tool's handles (the picture is the same in either theme). */
export const COLORS = { mask: '#ffd43b', window: '#da77f2', reframe: '#69db7c', pin: '#ffa94d' } as const;

/** The light core and dark halo every handle carries (tokens in `global.css`), so it reads on any picture. */
export const HANDLE_CORE = 'var(--rb-ed-handle-core)';
export const HANDLE_HALO = 'var(--rb-ed-handle-halo)';

/** Handle hit areas in CSS px: a fine pointer, then a coarse one (the 44 px touch target). */
export const HANDLE_HIT_FINE = 24;
export const HANDLE_HIT_COARSE = 44;
