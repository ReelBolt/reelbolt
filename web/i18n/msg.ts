/**
 * Non-component code never holds English text: it returns a `Msg` (a catalogue key plus values) and
 * the component that shows it resolves it with `useT().msg(m)` (or `getT().msg(m)` on the server).
 * A Msg is plain data, so it survives JSON, SWR caches and Server Component props.
 */
export type MsgValue = string | number | boolean | Date | null | undefined | Msg;

export interface Msg {
  readonly $msg: string;
  readonly values?: Readonly<Record<string, MsgValue>>;
}

/** `msg('files.manager.uploadButton')`, `msg('files.manager.count', { count: 3 })`. Key = `<namespace file>.<path>`. */
export function msg(key: string, values?: Record<string, MsgValue>): Msg {
  return values ? { $msg: key, values } : { $msg: key };
}

export function isMsg(value: unknown): value is Msg {
  return typeof value === 'object' && value !== null && typeof (value as Msg).$msg === 'string';
}
