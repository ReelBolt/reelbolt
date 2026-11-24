'use client';

import { useCallback, useMemo, useState, type ReactNode } from 'react';
import Link from 'next/link';
import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import {
  ESTIMATE_GATE_BODY,
  ESTIMATE_GATE_CANCEL_LABEL,
  ESTIMATE_GATE_CLOSE_LABEL,
  ESTIMATE_GATE_FREE_PATH,
  ESTIMATE_GATE_OVER_CAP_TITLE,
  ESTIMATE_GATE_PROFILE_LINK_LABEL,
  ESTIMATE_GATE_TITLE,
  ESTIMATE_GATE_UNAVAILABLE,
  ESTIMATE_GATE_UNAVAILABLE_TITLE,
  CONFIRM_BUTTON_LABEL,
  estimateGateOverCapText,
  estimateGateRange,
  estimateGateUpTo,
} from './copy';
import { useT } from '@/i18n';

/** What a refused start (409 `ESTIMATE_NOT_ACCEPTED` / `ESTIMATE_OVER_CAP`, 503 `ESTIMATE_UNAVAILABLE`) says. */
export interface EstimateGate {
  kind: 'not-accepted' | 'over-cap' | 'unavailable';
  low: number | null;
  high: number | null;
  cap: number | null;
}

function num(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/** The gate an error carries, or null for any other failure (which stays the caller's own). */
export function estimateGateOf(err: unknown): EstimateGate | null {
  if (!(err instanceof ApiError)) return null;
  const kind =
    err.status === 409 && err.code === 'ESTIMATE_NOT_ACCEPTED'
      ? 'not-accepted'
      : err.status === 409 && err.code === 'ESTIMATE_OVER_CAP'
        ? 'over-cap'
        : err.status === 503 && err.code === 'ESTIMATE_UNAVAILABLE'
          ? 'unavailable'
          : null;
  if (kind === null) return null;
  const body = typeof err.body === 'object' && err.body !== null ? (err.body as Record<string, unknown>) : {};
  const estimate =
    typeof body.estimate === 'object' && body.estimate !== null ? (body.estimate as Record<string, unknown>) : {};
  const gate: EstimateGate = { kind, low: num(estimate.low), high: num(estimate.high), cap: num(body.capCredits) };
  // Nothing honest to accept without a high figure: fall back to the unavailable wording.
  return kind === 'not-accepted' && gate.high === null ? { ...gate, kind: 'unavailable' } : gate;
}

interface EstimateGateModalProps {
  projectId: string;
  gate: EstimateGate | null;
  onConfirm: (acceptEstimateCredits: number) => void;
  onClose: () => void;
}

export function EstimateGateModal({ projectId, gate, onConfirm, onClose }: EstimateGateModalProps) {
  const tr = useT();
  const title =
    gate?.kind === 'over-cap'
      ? tr.msg(ESTIMATE_GATE_OVER_CAP_TITLE)
      : gate?.kind === 'unavailable'
        ? tr.msg(ESTIMATE_GATE_UNAVAILABLE_TITLE)
        : tr.msg(ESTIMATE_GATE_TITLE);
  return (
    <Modal opened={gate !== null} onClose={onClose} title={title} centered>
      {gate?.kind === 'not-accepted' && gate.high !== null && (
        <Stack gap="sm" data-testid="estimate-gate">
          <Text size="sm">{tr.msg(ESTIMATE_GATE_BODY)}</Text>
          {gate.low !== null && (
            <Text size="sm" fw={600} data-testid="estimate-gate-range">
              {tr.msg(estimateGateRange(gate.low, gate.high))}
            </Text>
          )}
          <Text size="sm">{tr.msg(estimateGateUpTo(gate.high))}</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              {tr.msg(ESTIMATE_GATE_CANCEL_LABEL)}
            </Button>
            <Button onClick={() => onConfirm(gate.high as number)}>{tr.msg(CONFIRM_BUTTON_LABEL)}</Button>
          </Group>
        </Stack>
      )}
      {gate?.kind === 'over-cap' && (
        <Stack gap="sm">
          <Alert color="yellow">
            <Stack gap="xs">
              <Text size="sm" data-testid="estimate-gate-over-cap">
                {tr.msg(estimateGateOverCapText(gate.low, gate.high, gate.cap))}
              </Text>
              <Text size="sm">{tr.msg(ESTIMATE_GATE_FREE_PATH)}</Text>
            </Stack>
          </Alert>
          <Group justify="flex-end">
            <Button component={Link} href={`/projects/${projectId}?tab=profile`} variant="light">
              {tr.msg(ESTIMATE_GATE_PROFILE_LINK_LABEL)}
            </Button>
            <Button variant="default" onClick={onClose}>
              {tr.msg(ESTIMATE_GATE_CLOSE_LABEL)}
            </Button>
          </Group>
        </Stack>
      )}
      {gate?.kind === 'unavailable' && (
        <Stack gap="sm">
          <Text size="sm">{tr.msg(ESTIMATE_GATE_UNAVAILABLE)}</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              {tr.msg(ESTIMATE_GATE_CLOSE_LABEL)}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

/**
 * The page-side wiring. In a catch: `if (gate.intercept(err, (accepted) => send(accepted))) return;`
 * — it opens the matching modal and, on Confirm, calls `resend` with the high figure so the SAME
 * request goes out again carrying it. Returns false for any error that is not an estimate refusal.
 */
export function useEstimateGate(projectId: string): {
  intercept: (err: unknown, resend: (acceptEstimateCredits: number) => unknown) => boolean;
  modal: ReactNode;
} {
  const [pending, setPending] = useState<{
    gate: EstimateGate;
    resend: (acceptEstimateCredits: number) => unknown;
  } | null>(null);

  const intercept = useCallback((err: unknown, resend: (acceptEstimateCredits: number) => unknown) => {
    const gate = estimateGateOf(err);
    if (gate === null) return false;
    setPending({ gate, resend });
    return true;
  }, []);

  const close = useCallback(() => setPending(null), []);
  const confirm = useCallback(
    (accepted: number) => {
      const resend = pending?.resend;
      setPending(null);
      void resend?.(accepted);
    },
    [pending],
  );

  const modal = useMemo(
    () => <EstimateGateModal projectId={projectId} gate={pending?.gate ?? null} onConfirm={confirm} onClose={close} />,
    [projectId, pending, confirm, close],
  );
  return { intercept, modal };
}
