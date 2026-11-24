import type { Msg } from '@/i18n/msg';
import { sentenceMsg, type ReasonEntry } from './sentence';
import { REASON_TABLES } from './reason-tables';

/**
 * i18n: maps the English sentences the editing libs return as `{ reason }` / status text to catalogue
 * messages. These tables hold English on purpose (they MATCH the lib output, byte for byte); the
 * `reason-msg.test.ts` round trip fails if a lib sentence changes and the table does not.
 */

/** For `addTransitionOp` / `makeRoomOps` results `{ reason }`: `transitionReasonMsg(result.reason)`. */
export const transitionReasonMsg = (reason: string): Msg => sentenceMsg(REASON_TABLES.transitions, reason);

/**
 * Any lib sentence that has a table in `reason-tables.ts`: `reasonMsg('trim', result.reason)`. Text that is
 * not in the table (a server message, a name) passes through as `editor-lib.common.raw`.
 */
export const reasonMsg = (table: keyof typeof REASON_TABLES, text: string): Msg => sentenceMsg(REASON_TABLES[table] as readonly ReasonEntry[], text);
