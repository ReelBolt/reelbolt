'use client';

import { useState } from 'react';
import { Alert, Button, Group, Modal, NumberInput, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import {
  grantOrgCredits,
  promotionAdminMessage,
  type AdminCreditGrant,
  type AdminOrg,
} from '@/lib/api/promotions';

const DAY_MS = 24 * 60 * 60 * 1000;

function pad(n: number): string {
  return String(n).padStart(2, '0');
}

/** A `date` input value (local calendar day), `days` from now. */
export function dateInputValue(days: number, from: Date = new Date()): string {
  const d = new Date(from.getTime() + days * DAY_MS);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** The instant a chosen expiry day means: the end of that day, local time. */
export function expiryFromDateInput(value: string): Date | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const d = new Date(`${value}T23:59:59`);
  return Number.isNaN(d.getTime()) ? null : d;
}

export interface GrantCreditsValues {
  credits: number | '';
  expiresOn: string;
  reason: string;
}

/** Field → message, empty when the grant can be sent. Mirrors the API's bounds. */
export function validateGrant(values: GrantCreditsValues, now: Date = new Date()): Partial<Record<keyof GrantCreditsValues, string>> {
  const errors: Partial<Record<keyof GrantCreditsValues, string>> = {};
  if (typeof values.credits !== 'number' || !Number.isInteger(values.credits) || values.credits < 1 || values.credits > 1_000_000) {
    errors.credits = 'Between 1 and 1,000,000 credits.';
  }
  const expiry = expiryFromDateInput(values.expiresOn);
  const latest = new Date(now);
  latest.setFullYear(latest.getFullYear() + 3);
  if (!expiry) errors.expiresOn = 'Pick an expiry date.';
  else if (expiry.getTime() - now.getTime() < DAY_MS) errors.expiresOn = 'At least one day from now.';
  else if (expiry.getTime() > latest.getTime()) errors.expiresOn = 'At most three years from now.';
  const reason = values.reason.trim();
  if (!reason) errors.reason = 'Say why. The reason is kept with the grant.';
  else if (reason.length > 500) errors.reason = 'At most 500 characters.';
  return errors;
}

/**
 * Grants promo credits to one organization. `requestId` is minted by the caller once per opening,
 * so a double submit (or a retry after a lost response) grants once: the API answers a replayed id
 * with the row it already wrote.
 */
export function GrantCreditsModal({
  org,
  requestId,
  onClose,
  onGranted,
}: {
  org: AdminOrg | null;
  requestId: string;
  onClose: () => void;
  onGranted: (grant: AdminCreditGrant) => void;
}) {
  const [values, setValues] = useState<GrantCreditsValues>(() => ({
    credits: '',
    expiresOn: dateInputValue(90),
    reason: '',
  }));
  const [errors, setErrors] = useState<Partial<Record<keyof GrantCreditsValues, string>>>({});
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function submit() {
    if (!org) return;
    const found = validateGrant(values);
    setErrors(found);
    setSubmitError(null);
    if (Object.keys(found).length > 0) return;
    const expiry = expiryFromDateInput(values.expiresOn) as Date;
    setSaving(true);
    try {
      const grant = await grantOrgCredits(org.id, {
        requestId,
        credits: values.credits as number,
        expiresAt: expiry.toISOString(),
        reason: values.reason.trim(),
      });
      onGranted(grant);
      onClose();
    } catch (error) {
      setSubmitError(promotionAdminMessage(error));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal opened={org !== null} onClose={onClose} title={org ? `Grant credits to ${org.name}` : 'Grant credits'}>
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <Stack gap="sm">
          <NumberInput
            label="Credits"
            required
            min={1}
            max={1_000_000}
            allowDecimal={false}
            thousandSeparator=","
            value={values.credits}
            onChange={(v) => setValues((p) => ({ ...p, credits: typeof v === 'number' ? v : v.trim() === '' ? '' : Number(v) }))}
            error={errors.credits}
            data-testid="grant-credits-amount"
          />
          <TextInput
            type="date"
            label="Expires on"
            description="Between tomorrow and three years from now. Unused credits lapse at the end of that day."
            required
            value={values.expiresOn}
            onChange={(e) => {
              const expiresOn = e.currentTarget.value;
              setValues((p) => ({ ...p, expiresOn }));
            }}
            error={errors.expiresOn}
            data-testid="grant-credits-expires"
          />
          <Textarea
            label="Reason"
            description="Kept with the grant for audit."
            required
            rows={3}
            maxLength={500}
            value={values.reason}
            onChange={(e) => {
              const reason = e.currentTarget.value;
              setValues((p) => ({ ...p, reason }));
            }}
            error={errors.reason}
            data-testid="grant-credits-reason"
          />
          <Text size="xs" c="dimmed">
            Granted as promo credits, which have no cash value and cannot be transferred.
          </Text>
          {submitError && (
            <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" role="alert">
              {submitError}
            </Alert>
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={saving}>Cancel</Button>
            <Button type="submit" loading={saving} data-testid="grant-credits-submit">Grant credits</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
