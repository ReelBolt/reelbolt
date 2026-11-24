import { TURN_CONCURRENCY_LIMIT_CODE, type AssistantCreditsCode } from './AssistantCreditsNotice';

/**
 * A chat turn the server refused before streaming anything: today only the 429
 * `{ code: 'TURN_CONCURRENCY_LIMIT', message }` a workspace gets when too many assistant turns are
 * already in flight. Thrown from the runtime's `onResponse`, so the reply ends in its error state
 * carrying the server's own sentence rather than the transport's `Status 429: {...}`.
 *
 * It carries `code` and `message` as plain strings, which is the shape assistant-ui keeps as the
 * message's error as-is (an `AssistantError`), so the chat can tell this refusal from a broken stream.
 */
export class AssistantTurnRefusal extends Error {
  readonly code: AssistantCreditsCode;

  constructor(code: AssistantCreditsCode, message: string) {
    super(message);
    this.name = 'AssistantTurnRefusal';
    this.code = code;
  }
}

/**
 * The refusal a chat response carries, or null when the response is anything else. Reads a clone,
 * so a response this does not claim reaches the stream decoder (or its generic error) untouched.
 */
export async function turnRefusalOf(response: Response): Promise<AssistantTurnRefusal | null> {
  if (response.status !== 429) return null;
  let body: unknown;
  try {
    body = await response.clone().json();
  } catch {
    return null;
  }
  if (typeof body !== 'object' || body === null) return null;
  const { code, message } = body as { code?: unknown; message?: unknown };
  if (code !== TURN_CONCURRENCY_LIMIT_CODE || typeof message !== 'string' || message.trim() === '') return null;
  return new AssistantTurnRefusal(TURN_CONCURRENCY_LIMIT_CODE, message);
}

/** `onResponse` for the chat runtime: throws the refusal a response carries, else lets it through. */
export async function rejectTurnRefusal(response: Response): Promise<void> {
  const refusal = await turnRefusalOf(response);
  if (refusal) throw refusal;
}

/** The refusal code of a message's error (as the runtime stores it), or null for any other error. */
export function turnRefusalCodeOf(error: unknown): AssistantCreditsCode | null {
  if (typeof error !== 'object' || error === null) return null;
  const { code } = error as { code?: unknown };
  return code === TURN_CONCURRENCY_LIMIT_CODE ? TURN_CONCURRENCY_LIMIT_CODE : null;
}
