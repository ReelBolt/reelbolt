'use client';

import React, { useCallback, useEffect, useState } from 'react';
import { Button, Group, Stack, Text } from '@mantine/core';
import { IconPlayerPlay } from '@tabler/icons-react';
import { preview3sApi, previewContentUrl } from '@/lib/editing/api/preview3s';
import { ApiError } from '@/lib/api/client';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { priceFromEstimate, PREVIEW_SECONDS, type PriceState } from '@/lib/editing/preview/preview3s';
import { preview3sStore, usePreview3sState } from '@/lib/editing/preview/preview3s-store';
import { preview3sButtonMsg, preview3sIncludedMsg, preview3sMessageMsg } from '@/lib/editing/preview/labels-msg';
import { rawMsg } from '@/lib/editing/labels-msg';
import { useT } from '@/i18n';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import { InlineJobStatus } from '@/components/activity/InlineJobStatus';
import { localJob } from '@/lib/editing/background/activity-job';

export interface Preview3sButtonProps {
  projectId: string;
  timelineId: string;
  /** The frame the three seconds start at (the playhead). */
  atFrame: number;
  readOnly?: boolean;
  /** Called just before the render starts, so the host can pause playback. */
  onBeforeStart?: () => void;
  size?: 'compact-xs' | 'xs' | 'sm' | 'md';
}

/** Price first (from the estimate), then one click renders the real result. */
export function Preview3sButton({ projectId, timelineId, atFrame, readOnly = false, onBeforeStart, size = 'compact-xs' }: Preview3sButtonProps) {
  const t = useT('editor.viewer.preview3s');
  const state = usePreview3sState();
  const [price, setPrice] = useState<PriceState>({ kind: 'unknown' });
  // Bumped to ask the price again (a timeline with a clip still being checked is refused for a moment).
  const [attempt, setAttempt] = useState(0);
  const [checking, setChecking] = useState(false);
  const busy = state.phase === 'starting' || state.phase === 'rendering';

  useEffect(() => {
    if (readOnly) return;
    let live = true;
    const timer = window.setTimeout(() => {
      preview3sApi
        .estimate(projectId, timelineId, { atFrame: Math.max(0, Math.round(atFrame)), seconds: PREVIEW_SECONDS })
        .then((estimate) => { if (live) { setChecking(false); setPrice(priceFromEstimate(estimate)); } })
        .catch((error: unknown) => {
          if (!live) return;
          setChecking(error instanceof ApiError && error.code === 'CONTENT_MODERATION_PENDING');
          setPrice({ kind: 'refused', reason: humanizeErrorT(t, error, t('thePriceCouldNot')) });
        });
    }, 400);
    return () => {
      live = false;
      window.clearTimeout(timer);
    };
  }, [projectId, timelineId, atFrame, readOnly, t, attempt]);

  const click = useCallback(() => {
    if (checking) {
      setAttempt((n) => n + 1);
      return;
    }
    onBeforeStart?.();
    void preview3sStore.start(projectId, timelineId, atFrame);
  }, [onBeforeStart, projectId, timelineId, atFrame, checking]);

  const refused = price.kind === 'refused';
  return (
    <Stack gap={2} data-testid="preview3s">
      <Button
        size={size}
        fz="xs"
        variant="light"
        leftSection={<IconPlayerPlay size={12} />}
        disabled={readOnly || busy || (refused && !checking)}
        loading={state.phase === 'starting'}
        onClick={click}
        style={{ minHeight: 44 }}
        data-testid="preview3s-button"
      >
        {t.msg(preview3sButtonMsg(price.kind === 'ready' ? (price.label === 'Included' ? preview3sIncludedMsg() : rawMsg(`${price.label.charAt(0).toLowerCase()}${price.label.slice(1)}`)) : null))}
      </Button>
      {refused && (
        <Text size="xs" c="red" role="status" data-testid="preview3s-refused">
          {t.msg(preview3sMessageMsg(price.reason))}
        </Text>
      )}
    </Stack>
  );
}

/** The small player over the viewer: progress, the result, or a plain reason. Never writes the document. */
export function Preview3sOverlay() {
  const t = useT('editor.viewer.preview3s');
  const state = usePreview3sState();
  // Leaving the editor stops the polling.
  useEffect(() => () => preview3sStore.close(), []);
  // Declared before the predicate reads it: the activity feed calls the predicate as soon as it holds
  // any job, and reading a `const` declared below would throw (temporal dead zone) and take the page down.
  const runningId = state.phase === 'rendering' ? state.jobId : null;
  const fed = useJobFor((j) => !!runningId && j.executionId === runningId);
  if (state.phase === 'idle') return null;
  const session = preview3sStore.getSession();
  const close = () => preview3sStore.close();

  return (
    <div
      role="dialog"
      aria-label={t('preview3S')}
      data-testid="preview3s-overlay"
      style={{ position: 'absolute', inset: 0, zIndex: 20, background: 'rgba(0,0,0,0.88)', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 12, pointerEvents: 'auto' }}
    >
      <Stack gap="sm" align="center" style={{ maxWidth: '100%', maxHeight: '100%', minHeight: 0 }}>
        {(state.phase === 'starting' || state.phase === 'rendering') && (
          <>
            <InlineJobStatus compact job={fed ?? localJob({ state: 'running', type: 'preview', executionId: runningId })} />
            <Text size="sm" c="gray.0" role="status" data-testid="preview3s-progress">
              {state.phase === 'starting' ? t('startingYourPreview') : t.msg(preview3sMessageMsg(state.statusWord))}
            </Text>
            <Button size="md" variant="default" onClick={close} style={{ minHeight: 44 }} data-testid="preview3s-cancel">
              {t('cancel')}
            </Button>
          </>
        )}
        {state.phase === 'ready' && session && (
          <>
            <video
              src={previewContentUrl(session.projectId, session.timelineId, state.jobId)}
              controls
              loop
              autoPlay
              playsInline
              data-testid="preview3s-video"
              style={{ maxWidth: '100%', maxHeight: 'calc(100cqh - 90px)', background: '#000' }}
            />
            <Text size="xs" c="gray.4">
              {t('theRealRenderOf')}
            </Text>
            <Group gap="md">
              <Button size="md" variant="default" onClick={close} style={{ minHeight: 44 }} data-testid="preview3s-close">
                {t('closePreview')}
              </Button>
              <Button size="md" onClick={close} style={{ minHeight: 44 }} data-testid="preview3s-back">
                {t('backToEditing')}
              </Button>
            </Group>
          </>
        )}
        {(state.phase === 'failed' || state.phase === 'refused' || state.phase === 'limited') && (
          <>
            <Text size="sm" c="gray.0" role="alert" data-testid="preview3s-problem">
              {t.msg(preview3sMessageMsg(state.message))}
            </Text>
            <Button size="md" variant="default" onClick={close} style={{ minHeight: 44 }} data-testid="preview3s-close">
              {t('close')}
            </Button>
          </>
        )}
      </Stack>
    </div>
  );
}
