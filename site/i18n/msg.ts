// Non-component code never holds English text: it returns a Msg that a component resolves with
// `useT().msg(m)`. A lookup keyed by a dynamic value must be declared in `dynamic-keys.ts`.
export type MsgValue = string | number | boolean | Date;
export interface Msg {
  /** `<namespace>.<path>`, e.g. `layout.footer.copyright`. */
  key: string;
  values?: Record<string, MsgValue>;
}

export function msg(key: string, values?: Record<string, MsgValue>): Msg {
  return values ? { key, values } : { key };
}
