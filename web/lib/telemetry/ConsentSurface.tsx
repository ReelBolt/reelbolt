'use client';

import { useCallback, useState, useSyncExternalStore } from 'react';
import { Button, Dialog, Group, Text } from '@mantine/core';
import { useReducedMotion } from '@mantine/hooks';
import { usePathname } from 'next/navigation';
import { telemetryEnabled } from './config';
import {
  readTelemetryConsent,
  subscribeTelemetryConsent,
  writeTelemetryConsent,
  type TelemetryConsentValue,
} from './consent';
import { previewTelemetryPayload } from './preview';

/**
 * The app-side consent surface (alpha-launch T-018). `/app` had none: `CookieConsentProvider` covers
 * the marketing site only, so client-side telemetry on the device (the ePrivacy art. 5(3) case) had
 * nothing to ask with. This is that surface.
 *
 * It is a **non-modal popup**, not an in-flow banner. It used to be an `<Alert>` drawn in normal flow
 * before the shell, which put it under the fixed navbars — the user was asked by something they could
 * not properly see. It is a Mantine `Dialog`: rendered through a portal at `position: fixed`, above
 * the chrome, so where it is mounted no longer decides where it appears.
 *
 * **Non-modal, deliberately.** Consent here is opt-in for low-stakes, non-essential telemetry;
 * declining must cost the user nothing, so the popup must not block the app. It takes focus when it
 * appears (so a keyboard user can answer it) but traps nothing and gates nothing: every product
 * action works exactly as it did while the question is on screen. `Modal` would have made the
 * refusal costlier than the acceptance, which is the opposite of what a consent prompt is for.
 *
 * ESC and the close button dismiss it for the current page. The state holds the page most recently
 * dismissed: that page does not ask again until the browser reloads (a later dismissal on another
 * page replaces it, so an earlier dismissed page can ask again), and every other page asks. Neither
 * dismissal writes a choice — only `Allow` and `Not now` call `writeTelemetryConsent`. Renders
 * nothing at all when the build has not enabled telemetry, or a choice already exists, so a
 * self-host deployment is unchanged.
 *
 * The wording below is handed to T-021 (the privacy policy / alpha notice); do not copy it into a
 * policy without the same hand-off list. The categories it names are the ones the policy's Section 3
 * describes (four funnel steps; the kind of error and the page it happened on; never files, prompts
 * or names), and "we record your choice either way" is the policy's Section 4, "Records of your
 * telemetry choice" — which says the record is tied to the account and is NOT anonymous. Do not
 * shorten that clause away: PR #437 added it because the denial signal is only acceptable when the
 * person is told the refusal itself is recorded.
 */
export const TELEMETRY_CONSENT_TITLE = 'Help us improve the ReelBolt alpha';
export const TELEMETRY_CONSENT_WORDING =
  'With your permission, we collect usage data to see how the alpha is going: which of four steps ' +
  'you reach, and, when something breaks, the kind of error and the page it happened on. It is off ' +
  'unless you allow it, we record your choice either way, and we never collect your files, prompts ' +
  'or names.';
export const TELEMETRY_CONSENT_ALLOW = 'Allow';
export const TELEMETRY_CONSENT_DENY = 'Not now';

/** Above the shell's chrome: the header is 100, and 201 while the assistant drawer is open. */
const CONSENT_Z_INDEX = 300;
const TITLE_ID = 'telemetry-consent-title';

function getServerSnapshot(): TelemetryConsentValue {
  // The server render and the first client render must match; the real value is read on mount.
  return null;
}

/**
 * One payload preview. A `pre` does not wrap by default, and inside the popup's ~340 px that clipped
 * the longer payloads at the right edge at *every* width — content lost, not merely cut, which is
 * WCAG 1.4.10 at 320 px. Wrapping them and bounding each to its own scrollable box keeps "exactly
 * what we would send" readable on a phone without making the popup tall.
 */
function PayloadPreview({ testId, kind }: { testId: string; kind: 'funnel' | 'error' | 'consent' }) {
  return (
    <pre
      data-testid={testId}
      style={{
        whiteSpace: 'pre-wrap',
        overflowWrap: 'anywhere',
        margin: '4px 0 0',
        fontSize: 12,
        lineHeight: 1.45,
        maxHeight: 140,
        overflowY: 'auto',
      }}
    >
      {previewTelemetryPayload(kind)}
    </pre>
  );
}

export function TelemetryConsentSurface() {
  const consent = useSyncExternalStore(subscribeTelemetryConsent, readTelemetryConsent, getServerSnapshot);
  const pathname = usePathname();
  const reduceMotion = useReducedMotion();
  // The page a dismissal was most recently made on — one slot, so a later dismissal elsewhere
  // replaces it. A dismissal is not a choice, so nothing is written to storage.
  const [dismissedOn, setDismissedOn] = useState<string | null>(null);

  const open = telemetryEnabled() && consent === null && dismissedOn !== pathname;

  // The dialog is the thing that has the question, so it takes focus when it appears — that is what
  // makes ESC reach it without a document-level listener that would also close it when the user
  // pressed ESC at some other dialog.
  //
  // It is focused from a callback ref, not from an effect on `open`. `Dialog` renders through
  // Mantine's `Portal`, which creates its target node in its own effect and returns null until then:
  // the dialog element does not exist when an `[open]` effect first runs, so such an effect would
  // focus nothing (verified against the rendered DOM). The callback fires the moment React attaches
  // the portaled node, which is the only point at which the element can be focused.
  const focusDialog = useCallback((node: HTMLDivElement | null) => {
    node?.focus();
  }, []);

  if (!open) return null;

  const dismiss = () => setDismissedOn(pathname);

  return (
    <Dialog
      ref={focusDialog}
      opened
      zIndex={CONSENT_Z_INDEX}
      role="dialog"
      aria-labelledby={TITLE_ID}
      tabIndex={-1}
      withCloseButton
      // `Dialog` has no `closeButtonLabel` (that is an `Alert` prop); its `attributes` slot is the
      // supported way to name the close button it renders.
      attributes={{ closeButton: { 'aria-label': 'Dismiss for now' } }}
      onClose={dismiss}
      onKeyDown={(event) => {
        if (event.key === 'Escape') dismiss();
      }}
      transitionProps={reduceMotion ? { transition: 'fade', duration: 0 } : undefined}
    >
      {/* The Dialog is anchored to the bottom edge, so an expanded disclosure grew it upwards past
          the top of a short viewport (a phone in landscape put the title 337 px off-screen and left
          it unreachable). Bounding the content here and letting it scroll keeps the question — and
          the close button, which `Dialog` renders beside this element — on screen at any height. */}
      <div style={{ maxHeight: 'calc(100dvh - 76px)', overflowY: 'auto' }}>
        <Text id={TITLE_ID} fw={600} size="sm">{TELEMETRY_CONSENT_TITLE}</Text>
        <Text size="sm" mt="xs">{TELEMETRY_CONSENT_WORDING}</Text>
        <details>
          <summary>See exactly what we would send</summary>
          <PayloadPreview testId="telemetry-payload-preview" kind="funnel" />
          <PayloadPreview testId="telemetry-error-preview" kind="error" />
          {/* The decision itself is the one thing a declining browser still sends, so it stays in
              the disclosure (PR #437). Collapsed with the rest — minimal, not hidden. */}
          <PayloadPreview testId="telemetry-consent-preview" kind="consent" />
        </details>
        <Group mt="sm" gap="xs">
          {/* Both buttons carry equal weight, and both are a full phone touch target: 30 px was
              under the 44 px guideline the rest of the dashboard already follows. */}
          <Button size="xs" variant="default" style={{ minHeight: 44 }} onClick={() => writeTelemetryConsent('granted')}>
            {TELEMETRY_CONSENT_ALLOW}
          </Button>
          <Button size="xs" variant="default" style={{ minHeight: 44 }} onClick={() => writeTelemetryConsent('denied')}>
            {TELEMETRY_CONSENT_DENY}
          </Button>
        </Group>
      </div>
    </Dialog>
  );
}
