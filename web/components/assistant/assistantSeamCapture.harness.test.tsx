// @vitest-environment jsdom

import { describe, it, expect } from 'vitest';
import { writeFileSync } from 'node:fs';
import { captureAssistantSeamBody, CAPTURE_THREAD_ID, CAPTURE_TURN_TEXT } from './assistantSeamCapture';

/**
 * Runs the real-component capture (`assistantSeamCapture.tsx`) and hands the result to whatever
 * asked for it.
 *
 * This file is an ENTRY POINT, not a guard. The guard that consumes its output is the client half of
 * `e2e/tests/assistant-seam.contract.spec.ts`, which runs this file as a subprocess:
 *
 *   cd web && ASSISTANT_SEAM_CAPTURE_OUT=<path> \
 *     env -u NODE_ENV NODE_ENV=test npx vitest run \
 *     components/assistant/assistantSeamCapture.harness.test.tsx
 *
 * The body is written to `ASSISTANT_SEAM_CAPTURE_OUT` when that variable is set (the e2e path),
 * and printed to stdout either way so a human running the ordinary web suite can see it. What it
 * must NOT do is compare itself to the fixture: a capture that also decides whether it matches
 * cannot go red when the client changes, which is the whole point of moving this out of a
 * hand-written `adapter.run({...})` call.
 *
 * The scenario it drives is a BOUND turn — the client binds a thread first, because the shipped UI
 * no longer lets a turn out before one is bound. See `assistantSeamCapture.tsx`, and
 * `AssistantThreadIdentity.test.tsx` for the guard on that window itself.
 */
describe('assistant seam capture harness', () => {
  it(
    'captures the body the real drawer stack posts once a server thread is bound',
    async () => {
      const capture = await captureAssistantSeamBody();

      // Non-vacuity: the scenario must really have created a thread and then sent a turn, or the
      // consumer would be comparing a fixture against nothing. Both ends matter — a capture that
      // never bound a thread is no longer even reachable through the UI (the composer is inert
      // until one is bound), so a missing `/assistant/threads` request here means the capture
      // stopped exercising the bind, not that the client skipped it.
      expect(capture.requestUrls.some((url) => url.includes('/assistant/threads'))).toBe(true);
      expect(capture.requestUrls.some((url) => url.includes('/assistant/chat'))).toBe(true);
      expect(JSON.stringify(capture.boundTurn)).toContain(CAPTURE_TURN_TEXT);

      // The bound thread travels in the body. A turn that omitted it would be allocated a SECOND
      // thread by the server, orphaning the turn — the defect the local-identity guard covers.
      expect(capture.boundTurn.threadId).toBe(CAPTURE_THREAD_ID);

      const serialized = `${JSON.stringify(capture.boundTurnNormalized, null, 2)}\n`;
      console.log(`ASSISTANT_SEAM_CAPTURE ${JSON.stringify(capture.boundTurnNormalized)}`);

      const out = process.env.ASSISTANT_SEAM_CAPTURE_OUT;
      if (out) writeFileSync(out, serialized, 'utf8');
    },
    20000,
  );
});
