'use client';

import { useCallback, useMemo, useState } from 'react';
import {
  Alert,
  Button,
  Checkbox,
  Code,
  Group,
  Modal,
  SegmentedControl,
  Stack,
  Text,
  Textarea
} from '@mantine/core';
import { collectShellDiagnostics } from '@/lib/diagnostics/shell-diagnostics';
import { submitFeedback } from '@/lib/api/feedback';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import {
  assembleFeedbackPayload,
  defaultSelection,
  MAX_MESSAGE_LENGTH,
  presentFields,
  previewOf,
  type FeedbackCandidates,
  type FeedbackFieldKey,
  type FeedbackKind,
  type FeedbackSelection
} from '@/lib/feedback/context';

/** Above this the diagnostics are dropped and the feedback is sent anyway. */
const MAX_BODY_BYTES = 120 * 1024;

export interface FeedbackDialogProps {
  /** Everything this page could attach. Only these fields are ever offered, and only these can be sent. */
  candidates: FeedbackCandidates;
  onClose: () => void;
}

/**
 * "Flag a bug / Send feedback" (T-019). Mounted only while open, so its state is initialised from
 * `candidates` on each open and no effect has to reset it.
 *
 * The dialog shows the user <b>exactly</b> what will be attached — every candidate as a removable
 * checkbox, and the request body verbatim — and the only thing it ever sends is the object
 * `assembleFeedbackPayload` returns, which is the same object rendered in the preview.
 */
export function FeedbackDialog({ candidates, onClose }: FeedbackDialogProps) {
  const t = useT('feedback.dialog');
  const tAll = useT();
  const [kind, setKind] = useState<FeedbackKind>('bug');
  const [message, setMessage] = useState('');
  const [selection, setSelection] = useState<FeedbackSelection>(() => defaultSelection(candidates));
  const [sending, setSending] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fieldLabels: Record<FeedbackFieldKey, string> = {
    page: t('field.page'),
    projectId: t('field.projectId'),
    executionId: t('field.executionId'),
    assistantTurns: t('field.assistantTurns'),
  };
  const fields = presentFields(candidates);
  const [diagnostics, setDiagnostics] = useState<Record<string, unknown> | undefined>(undefined);
  const payload = useMemo(
    () => assembleFeedbackPayload(kind, message, candidates, selection, diagnostics),
    [kind, message, candidates, selection, diagnostics]
  );
  // Diagnostics must never make the feedback itself fail: a collector error just means none are attached.
  const collectNow = useCallback(async () => {
    try {
      setDiagnostics(
        await collectShellDiagnostics({ win: window, doc: document, buildId: process.env.NEXT_PUBLIC_BUILD_ID })
      );
    } catch {
      setDiagnostics(undefined);
    }
  }, []);
  const attached = fields.filter((field) => selection[field]);

  const toggle = (key: FeedbackFieldKey) =>
    setSelection((current) => ({ ...current, [key]: !current[key] }));

  const send = async () => {
    setSending(true);
    setError(null);
    try {
      // The exact object shown in the preview is sent. If the body would be too large, send without diagnostics.
      const full = new TextEncoder().encode(JSON.stringify(payload)).length;
      await submitFeedback(
        full > MAX_BODY_BYTES
          ? assembleFeedbackPayload(kind, message, candidates, selection)
          : payload
      );
      setSent(true);
    } catch (cause) {
      setError(describeError(tAll, cause, t('sendFailed')));
    } finally {
      setSending(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title')} size="lg" data-testid="feedback-dialog">
      {sent ? (
        <Stack gap="md">
          <Alert color="green" role="status">
            {t('thanks')}
          </Alert>
          <Group justify="flex-end">
            <Button onClick={onClose}>{t('close')}</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          <SegmentedControl
            aria-label={t('kindLabel')}
            value={kind}
            onChange={(value) => setKind(value as FeedbackKind)}
            data={[
              { value: 'bug', label: t('kindBug') },
              { value: 'feedback', label: t('kindFeedback') }
            ]}
          />
          <Textarea
            label={t('whatHappened')}
            description={t('maxChars', { max: MAX_MESSAGE_LENGTH })}
            value={message}
            onChange={(event) => setMessage(event.currentTarget.value)}
            maxLength={MAX_MESSAGE_LENGTH}
            minRows={4}
            required
            data-testid="feedback-message"
          />
          <div>
            <Text fw={600} size="sm">
              {t('attachedTitle')}
            </Text>
            <Text size="xs" c="dimmed">
              {t('attachedHint')}
            </Text>
            {fields.length === 0 ? (
              <Text size="sm" mt="xs" data-testid="feedback-no-context">
                {t('nothingAttached')}
              </Text>
            ) : (
              <Stack gap={4} mt="xs">
                {fields.map((field) => (
                  <Checkbox
                    key={field}
                    checked={selection[field]}
                    onChange={() => toggle(field)}
                    label={fieldLabels[field]}
                    description={previewOf(candidates, field)}
                  />
                ))}
              </Stack>
            )}
          </div>
          <Checkbox
            checked={selection.diagnostics === true}
            onChange={(event) => {
              const checked = event.currentTarget.checked;
              setSelection((current) => ({ ...current, diagnostics: checked }));
              if (checked) void collectNow();
              else setDiagnostics(undefined);
            }}
            label={t('shareDiagnostics')}
            description={t('shareDiagnosticsHint')}
            data-testid="feedback-share-diagnostics"
          />
          <div>
            <Text fw={600} size="sm">
              {t('sendTitle')}
            </Text>
            <div data-testid="feedback-payload">
              <Code block>{JSON.stringify(payload, null, 2)}</Code>
            </div>
          </div>
          {attached.length === 0 && fields.length > 0 ? (
            <Text size="xs" c="dimmed" data-testid="feedback-context-empty">
              {t('allRemoved')}
            </Text>
          ) : null}
          {error ? (
            <Alert color="red" role="alert">
              {error}
            </Alert>
          ) : null}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={sending}>
              {t('cancel')}
            </Button>
            <Button onClick={send} loading={sending} disabled={!message.trim()}>
              {t('send')}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
