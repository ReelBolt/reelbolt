/**
 * Which edits a running job holds. An edit that changes WHICH part of the video a clip plays, or whether it exists,
 * always matters (the source keys below). On top of that each job kind lists the clip properties it reads or writes:
 * changing one while the job runs would make its result wrong or be overwritten by it.
 *
 * | job kind        | also held                                         | why                                                       |
 * |-----------------|---------------------------------------------------|-----------------------------------------------------------|
 * | objectEffect    | stabilize                                         | a clip cannot carry region effects and be stabilised      |
 * | audioCleanup    | (source only)                                     | reads the clip's sound, nothing else                       |
 * | proposal        | (source only)                                     | its ops are anchored to the clips as they were            |
 * | follow          | crop, reframe, stabilize, transform               | the result is only valid for that framing and it writes   |
 * |                 |                                                   | the transform                                             |
 * | stabilize       | stabilize (itself); trims are fine                | the proxy is cut from the whole source; turning the       |
 * |                 |                                                   | switch off or swapping the source makes it useless        |
 * | enhance (L4)    | (source only)                                     | renders new footage from the source                       |
 * | anything else   | (source only)                                     | the safe default                                          |
 *
 * Grade and effects are not held for any kind: no job reads them.
 */

/** The clip properties that change what footage a clip plays. */
export const SOURCE_KEYS: ReadonlySet<string> = new Set(['playbackRate', 'sourceIn', 'reverse', 'hold', 'assetRef']);

/** The extra properties each job kind holds (see the table above). Add a row when a new kind of job arrives. */
export const JOB_READS: Readonly<Record<string, readonly string[]>> = {
  objectEffect: ['stabilize'],
  audioCleanup: [],
  proposal: [],
  follow: ['crop', 'reframe', 'stabilize', 'transform'],
  // The proxy is cut from the WHOLE source, so only a source swap and the switch itself matter; trims, moves, speed, deletes are free.
  stabilize: ['assetRef', 'stabilize'],
  enhance: [],
};

/** Job kinds that hold nothing but the properties listed for them (no trim, move, split, delete, speed hold). */
const PROPERTY_ONLY: ReadonlySet<string> = new Set(['stabilize']);

/** True when structural edits (delete, trim, move, split, slip, replace) on the job's clips are held. */
export const holdsStructuralEdits = (jobKind: string): boolean => !PROPERTY_ONLY.has(jobKind);

const cache = new Map<string, ReadonlySet<string>>();

/** Every `setProperty` key a job of this kind holds. */
export function heldKeysFor(jobKind: string): ReadonlySet<string> {
  let keys = cache.get(jobKind);
  if (!keys) {
    keys = new Set([...(PROPERTY_ONLY.has(jobKind) ? [] : SOURCE_KEYS), ...(Object.prototype.hasOwnProperty.call(JOB_READS, jobKind) ? JOB_READS[jobKind]! : [])]);
    cache.set(jobKind, keys);
  }
  return keys;
}
