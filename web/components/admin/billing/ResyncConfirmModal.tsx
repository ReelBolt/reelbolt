'use client';

import { useRef, useState } from 'react';
import { Alert, Button, Group, Modal, Stack, Text, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconRefresh } from '@tabler/icons-react';
import { resyncBillingOrganization } from '@/lib/api/billing-admin';
import { useMerchantLabel } from '@/lib/hooks/use-billing';
import { describeResyncError, describeResyncResult, outcomeColor, outcomeLabel } from './reconcile-copy';

export const RESYNC_REASON_MAX = 500;

export interface ResyncTarget {
  organizationId: string;
  /** Null when the admin typed an ID that is not on the attention list. */
  organizationName: string | null;
}

/**
 * The one confirm step both entry points (a row's button, the by-ID input) go through. Mount it with
 * `key={target.organizationId}` so the reason and any error start empty for each workspace.
 *
 * A failed request keeps the modal open with the error under the reason, so a retry does not lose
 * what was typed; success closes it and reports the outcome as a notification. `onSettled` fires
 * after every answer, because the server writes an audit row and updates the last check either way.
 */
export function ResyncConfirmModal({
  target,
  onClose,
  onSettled,
}: {
  target: ResyncTarget | null;
  onClose: () => void;
  onSettled: () => void;
}) {
  const provider = useMerchantLabel();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  // State alone lets a fast double click through before the re-render that disables the button.
  const inFlight = useRef(false);

  const workspace = target?.organizationName ?? target?.organizationId ?? '';
  const tooLong = reason.length > RESYNC_REASON_MAX;

  const handleConfirm = async () => {
    if (!target || inFlight.current || tooLong) return;
    inFlight.current = true;
    setSubmitting(true);
    setError(null);
    try {
      const result = await resyncBillingOrganization(target.organizationId, { reason });
      notifications.show({
        title: outcomeLabel(result.outcome, provider),
        message: describeResyncResult(result, workspace, provider),
        color: outcomeColor(result.outcome),
      });
      onClose();
    } catch (err: unknown) {
      setError(describeResyncError(err, provider));
    } finally {
      inFlight.current = false;
      setSubmitting(false);
      onSettled();
    }
  };

  return (
    <Modal
      opened={target !== null}
      onClose={onClose}
      title={`Resync from ${provider}`}
      centered
      size="md"
      closeOnClickOutside={!submitting}
      closeOnEscape={!submitting}
      withCloseButton={!submitting}
    >
      <Stack gap="md">
        <Stack gap={4}>
          <Text size="sm">
            Reads <b>{workspace}</b>&apos;s current subscription from {provider} and applies it here, exactly
            as a delayed webhook would have.
          </Text>
          {target?.organizationName ? (
            <Text size="xs" c="dimmed" ff="monospace">
              {target.organizationId}
            </Text>
          ) : null}
          <Text size="sm" c="dimmed">
            It never adds credits by itself. Credits change only if the subscription {provider} reports
            grants them under the normal plan rules.
          </Text>
        </Stack>

        <Textarea
          label="Reason (optional)"
          description={`Saved with the audit entry. ${reason.length}/${RESYNC_REASON_MAX} characters.`}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          error={tooLong ? `Keep the reason to ${RESYNC_REASON_MAX} characters or fewer.` : undefined}
          maxLength={RESYNC_REASON_MAX}
          rows={3}
          disabled={submitting}
        />

        {error ? (
          <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} title="Resync did not run">
            {error}
          </Alert>
        ) : null}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={submitting}>
            Cancel
          </Button>
          <Button
            leftSection={<IconRefresh size={16} />}
            onClick={handleConfirm}
            loading={submitting}
            disabled={tooLong}
          >
            Resync from {provider}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
