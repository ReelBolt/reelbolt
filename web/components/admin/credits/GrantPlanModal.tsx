'use client';

import { useState } from 'react';
import { Alert, Button, Group, Modal, NumberInput, Select, Stack, Text, Textarea } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import {
  grantOrgPlan,
  promotionAdminMessage,
  type AdminOrg,
  type AdminPlan,
  type AdminPlanGrant,
} from '@/lib/api/promotions';

/** Longest period the API accepts, in days (three years). */
export const MAX_GRANT_DAYS = 1095;

export interface GrantPlanValues {
  planKey: string | null;
  durationDays: number | '';
  reason: string;
}

/** Field → message, empty when the grant can be sent. Mirrors the API's bounds. */
export function validatePlanGrant(values: GrantPlanValues): Partial<Record<keyof GrantPlanValues, string>> {
  const errors: Partial<Record<keyof GrantPlanValues, string>> = {};
  if (!values.planKey) errors.planKey = 'Pick a plan.';
  if (
    typeof values.durationDays !== 'number'
    || !Number.isInteger(values.durationDays)
    || values.durationDays < 1
    || values.durationDays > MAX_GRANT_DAYS
  ) {
    errors.durationDays = `Between 1 and ${MAX_GRANT_DAYS} days.`;
  }
  const reason = values.reason.trim();
  if (!reason) errors.reason = 'Say why. The reason is kept with the grant.';
  else if (reason.length > 500) errors.reason = 'At most 500 characters.';
  return errors;
}

function periodEndLabel(days: number | ''): string {
  if (typeof days !== 'number' || days < 1) return '';
  const end = new Date(Date.now() + days * 24 * 60 * 60 * 1000);
  return end.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/**
 * Gifts a plan to one organization without a payment provider. The API writes a real subscription with
 * `provider = NULL`, so the product's plan gates apply at once; the period lapsing returns the org
 * to whatever it was on. `requestId` is minted by the caller once per opening, so a double submit
 * grants once.
 */
export function GrantPlanModal({
  org,
  plans,
  requestId,
  onClose,
  onGranted,
}: {
  org: AdminOrg | null;
  plans: AdminPlan[];
  requestId: string;
  onClose: () => void;
  onGranted: (grant: AdminPlanGrant) => void;
}) {
  const [values, setValues] = useState<GrantPlanValues>({ planKey: null, durationDays: 30, reason: '' });
  const [errors, setErrors] = useState<Partial<Record<keyof GrantPlanValues, string>>>({});
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function submit() {
    if (!org) return;
    const found = validatePlanGrant(values);
    setErrors(found);
    setSubmitError(null);
    if (Object.keys(found).length > 0) return;
    setSaving(true);
    try {
      const grant = await grantOrgPlan(org.id, {
        requestId,
        planKey: values.planKey as string,
        durationDays: values.durationDays as number,
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
    <Modal opened={org !== null} onClose={onClose} title={org ? `Grant a plan to ${org.name}` : 'Grant a plan'}>
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <Stack gap="sm">
          <Select
            label="Plan"
            required
            placeholder="Choose a plan"
            data={plans.map((p) => ({ value: p.key, label: p.priceMonthlyUsd ? `${p.name} — $${p.priceMonthlyUsd}/mo` : p.name }))}
            value={values.planKey}
            onChange={(planKey) => setValues((p) => ({ ...p, planKey }))}
            error={errors.planKey}
            data-testid="grant-plan-select"
          />
          <NumberInput
            label="Duration (days)"
            description={periodEndLabel(values.durationDays) ? `Runs until ${periodEndLabel(values.durationDays)}.` : undefined}
            required
            min={1}
            max={MAX_GRANT_DAYS}
            allowDecimal={false}
            value={values.durationDays}
            onChange={(v) => setValues((p) => ({ ...p, durationDays: typeof v === 'number' ? v : v.trim() === '' ? '' : Number(v) }))}
            error={errors.durationDays}
            data-testid="grant-plan-duration"
          />
          <Textarea
            label="Reason"
            description="Kept with the grant for audit."
            required
            rows={3}
            maxLength={500}
            value={values.reason}
            onChange={(e) => setValues((p) => ({ ...p, reason: e.currentTarget.value }))}
            error={errors.reason}
            data-testid="grant-plan-reason"
          />
          <Text size="xs" c="dimmed">
            This writes a subscription with no payment provider: the plan applies immediately and lapses
            at the end of the period. It is not counted as revenue.
          </Text>
          {submitError && (
            <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" role="alert">
              {submitError}
            </Alert>
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={saving}>Cancel</Button>
            <Button type="submit" loading={saving} data-testid="grant-plan-submit">Grant plan</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
