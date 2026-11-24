// What the client-rendered recipe page does with a /api/recipes/<id> response: a 404 (or an id that is not
// one) is the friendly "gone" page; anything else that is not a readable recipe is a failure worth retrying.
export type LoadOutcome = 'ready' | 'gone' | 'failed';

export function outcomeFor(status: number, parsedOk: boolean): LoadOutcome {
  if (status === 404) return 'gone';
  if (status >= 200 && status < 300) return parsedOk ? 'ready' : 'gone';
  return 'failed';
}
