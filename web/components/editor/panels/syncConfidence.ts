import type { SyncResult } from '@/lib/editing/audio/sync';
import { isConfidentSync } from '@/lib/editing/audio/sync';

/** A plain word for how sure the audio sync is, so the panel never shows a correlation coefficient. */
export function syncConfidenceWord(result: SyncResult): 'good' | 'fair' | 'low' {
  if (isConfidentSync(result)) return 'good';
  return result.correlation >= 0.3 ? 'fair' : 'low';
}
