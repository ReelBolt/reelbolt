import { OUTPUT_CHECK_INTERVAL_MS, probeOutput, type OutputProbe } from '@/lib/outputs/output-check';
import { decodeEnvelope } from './sync';

/** How many times a still-being-prepared source is asked about before the decode error is surfaced. */
const MAX_WAITS = 12;

/**
 * Decodes a clip's loudness envelope. Only if that fails is the URL asked about once; while the server says the
 * source is still being prepared (output or file route alike) it waits quietly, every few seconds, and decodes
 * again, so a new clip does not surface an error for what is a short wait. Anything else rethrows the first error.
 */
export async function decodeWhenReady(
  url: string,
  deps: { probe?: (url: string) => Promise<OutputProbe>; wait?: (ms: number) => Promise<void>; decode?: (url: string) => Promise<Float32Array> } = {},
): Promise<Float32Array> {
  const probe = deps.probe ?? probeOutput;
  const wait = deps.wait ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  const decode = deps.decode ?? ((u: string) => decodeEnvelope(u));
  try {
    return await decode(url);
  } catch (first) {
    for (let i = 0; i < MAX_WAITS; i += 1) {
      if ((await probe(url)).kind !== 'pending') throw first;
      await wait(OUTPUT_CHECK_INTERVAL_MS);
      try {
        return await decode(url);
      } catch {
        // Still not decodable: ask again.
      }
    }
    throw first;
  }
}
