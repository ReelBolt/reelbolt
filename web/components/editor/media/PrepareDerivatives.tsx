'use client';

/**
 * Derivative production — the ONE place in the media workspace that asks the backend to DO work.
 *
 * ## Why this is a component and not an effect somewhere convenient
 *
 * Triggering a write from a hover is a contract decision, and the ruling is explicit: derivative
 * production is requested on **explicit user action** — opening/selecting an asset in the media
 * workspace's detail pane — and **never on hover, never on list render**. Making that a component
 * mounted by the detail pane means the rule is not a convention a future edit can quietly break:
 * the component exists only while a clip is open, and it is the only caller of
 * `requestMediaDerivatives` in the feature.
 *
 * The auto-request on mount IS the sanctioned explicit action. Selecting a clip mounts this pane
 * (the page keys it by `mediaRef`), and mounting it is exactly "the user opened this asset" — issue
 * #123 §3.6's lazy derivation on first open. A pointer crossing a grid mounts nothing.
 *
 * ## One request per opened clip, proved by a call count
 *
 * The "already asked for this mediaRef?" guard is a **ref**, not state: it must be decided
 * synchronously, before any await, because a re-render arriving while the first request is still in
 * flight would otherwise queue a second round of encoding work for the same clip. A re-render of the
 * same open clip therefore issues nothing further, while opening a DIFFERENT clip legitimately asks
 * for that clip's derivatives.
 *
 * ## What it tells the truth about
 *
 * A `202 Accepted` means the request was **published**, nothing more: this service has no ffmpeg, so
 * no derivative row exists because of this call and the UI must never say the proxy is ready. The
 * copy says "requested", and the derivatives list is re-read rather than assumed — which is why
 * "Check again" exists. A failed request is reported as a failed REQUEST, never as "derivatives
 * unavailable" or "generation failed": the contract cannot distinguish those, so claiming them would
 * invent a fact.
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Group, Stack, Text } from '@mantine/core';
import { requestMediaDerivatives } from '@/lib/editing/api/media';
import type { MediaPoolItem, RequestMediaDerivativesResponse } from '@/lib/editing/media/types';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';

/** The value the server substitutes when a caller names no reason. Kept in lockstep with it. */
export const REQUESTED_BY_EDITOR_OPEN = 'editor-open';

type RequestEntry =
  | { kind: 'sending' }
  | { kind: 'accepted'; response: RequestMediaDerivativesResponse }
  | { kind: 'failed'; message: string };

/**
 * The last request outcome, TAGGED with the clip it belongs to.
 *
 * The tag is what stops a response for the previously open clip rendering under the newly open one
 * — the same reason `SourceViewer` tags its derivatives entry, and the reason no reset effect (and
 * therefore no setState inside an effect) is needed when the selection changes.
 */
interface RequestRecord {
  mediaRef: string;
  entry: RequestEntry;
}

export interface PrepareDerivativesProps {
  /** The owning project — needed only to build the API path. */
  projectId: string;
  /** The clip the user opened, or `null` when the selection is empty. */
  item: MediaPoolItem | null;
  /**
   * Called after a request is accepted, so the caller can re-read the derivatives list.
   *
   * The point is that nothing is assumed: the panel re-reads the real rows instead of predicting
   * that a proxy now exists.
   */
  onCheckAgain?: () => void;
}

export function PrepareDerivatives({ projectId, item, onCheckAgain }: PrepareDerivativesProps) {
  const t = useT('editor-media.prepare');
  const [record, setRecord] = useState<RequestRecord | null>(null);
  const mediaRef = item ? item.mediaRef : null;

  const current = record && record.mediaRef === mediaRef ? record.entry : null;

  // The synchronous "have we asked for this clip already?" guard. A ref, because state would not
  // have committed by the time a second render in the same tick read it.
  const autoRequestedFor = useRef<string | null>(null);

  /**
   * Sends one request and records its outcome.
   *
   * It deliberately does **not** set a "sending" state: it is called from the open effect as well as
   * from the button, and a synchronous state write from an effect is exactly the cascading-render
   * pattern this feature avoids. The button sets its own "sending" state before calling in, which is
   * an event handler and therefore fine; the automatic request simply reports nothing until it has
   * something real to report.
   */
  const requestAndRecord = useCallback(
    (ref: string) =>
      requestMediaDerivatives(projectId, {
        mediaRef: ref,
        requestedBy: REQUESTED_BY_EDITOR_OPEN,
      })
        .then((response) => {
          setRecord({ mediaRef: ref, entry: { kind: 'accepted', response } });
        })
        .catch((error: unknown) => {
          setRecord({ mediaRef: ref, entry: { kind: 'failed', message: humanizeErrorT(t, error, t('requestFailed')) } });
        }),
    [projectId, t],
  );

  // ONE automatic request per opened clip. No synchronous setState happens here: the guard is a ref,
  // and every state write this effect can cause arrives in the request's own callbacks.
  useEffect(() => {
    if (!mediaRef) return undefined;
    if (autoRequestedFor.current === mediaRef) return undefined;
    autoRequestedFor.current = mediaRef;
    void requestAndRecord(mediaRef);
    return undefined;
  }, [mediaRef, requestAndRecord]);

  /** The explicit affordance: a press is as sanctioned a trigger as opening the clip. */
  const handlePress = useCallback(() => {
    if (!mediaRef) return;
    setRecord({ mediaRef, entry: { kind: 'sending' } });
    void requestAndRecord(mediaRef);
  }, [mediaRef, requestAndRecord]);

  if (!item) {
    return null;
  }

  const accepted = current?.kind === 'accepted' ? current.response : null;

  return (
    <Stack gap={6} data-testid="prepare-derivatives" data-state={current?.kind ?? 'idle'}>
      <Group gap="xs" align="center">
        <Button
          size="compact-xs" fz="xs"
          variant="light"
          loading={current?.kind === 'sending'}
          onClick={handlePress}
          data-testid="prepare-derivatives-button"
        >
          {accepted ? t('requestAgain') : t('prepareProxy')}
        </Button>
        {onCheckAgain ? (
          <Button
            size="compact-xs" fz="xs"
            variant="default"
            onClick={onCheckAgain}
            data-testid="prepare-derivatives-check"
          >
            {t('checkAgain')}
          </Button>
        ) : null}
      </Group>

      <Text size="xs" c="dimmed" data-testid="prepare-derivatives-note">
        {t('note')}
      </Text>

      {current?.kind === 'sending' ? (
        <Text size="xs" c="dimmed" data-testid="prepare-derivatives-sending">
          {t('sending')}
        </Text>
      ) : current?.kind === 'failed' ? (
        <Alert color="red" variant="light" p="xs" data-testid="prepare-derivatives-failed">
          <Text size="xs" fw={600}>
            {t('failedTitle')}
          </Text>
          <Text size="xs" c="dimmed">
            {current.message}
          </Text>
        </Alert>
      ) : accepted ? (
        <Text size="xs" c="dimmed" data-testid="prepare-derivatives-accepted">
          {t('accepted', { count: accepted.kinds.length, by: accepted.requestedBy })}
        </Text>
      ) : null}
    </Stack>
  );
}
