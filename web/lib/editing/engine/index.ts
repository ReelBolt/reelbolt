/**
 * Public surface of the pure EditTimeline v2 edit engine.
 *
 * `ops` declares the closed op catalogue (and why an inverse is a program rather than a single
 * op); `apply` executes it. Consumers — the editor store, the undo stack, the optimistic
 * concurrency path — import from here and nowhere else.
 */

export * from './ops';
export * from './apply';
