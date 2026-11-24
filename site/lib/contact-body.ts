// Framework-free body guard for the contact route. It reads the request body and turns a parse
// failure (a malformed / non-JSON body) into the same `{ ok: false, errors: [...] }` 400 the
// validator answers with, so a malformed body and an invalid body are indistinguishable to the
// caller. No React/Next imports: this must run unmodified in a Node route handler and under
// `node --test`, which cannot import a Next route (it pulls `next/server` and the `@/` alias).

import type { ContactInput, ValidationError } from './validation.ts';

/** The body a caller gets for a request whose body is not valid JSON. Its shape matches a
 * validator failure exactly (`ok` + `errors`, each error `field` + `message`). */
export const MALFORMED_CONTACT_BODY: { ok: false; errors: ValidationError[] } = {
  ok: false,
  errors: [{ field: '_form', message: 'Invalid request.' }],
};

export type ContactBody =
  | { ok: true; payload: ContactInput }
  | { ok: false; status: 400; response: { ok: false; errors: ValidationError[] } };

/**
 * Reads a request body. A parse failure is a client error, not a server one: it is answered with
 * `MALFORMED_CONTACT_BODY` and a 400. On success the parsed value is handed back typed as
 * `ContactInput` so the rest of the handler can keep reading fields off it — validation still
 * decides whether it is actually a valid contact.
 */
export async function readContactBody(request: { json(): Promise<unknown> }): Promise<ContactBody> {
  let parsed: unknown;
  try {
    parsed = await request.json();
  } catch {
    return { ok: false, status: 400, response: MALFORMED_CONTACT_BODY };
  }
  // A contact body is a JSON object. A parsed null, array, string, number or boolean is not one,
  // and must not reach the handler (which reads fields off it): answer it like any other malformed
  // body, rather than letting a client error become a server error or a bogus success.
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ok: false, status: 400, response: MALFORMED_CONTACT_BODY };
  }
  return { ok: true, payload: parsed as ContactInput };
}
