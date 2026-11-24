'use client';

import { useState } from 'react';
import {
  Alert,
  Button,
  Group,
  Modal,
  MultiSelect,
  NumberInput,
  SegmentedControl,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import {
  PROMO_CODE_PATTERN,
  createPromotion,
  fetchFounderPricePreset,
  normalizePromoCode,
  promotionAdminMessage,
  type CreatePromotionRequest,
  type Promotion,
  type PromotionDiscountType,
  type PromotionDuration,
  type PromotionKind,
} from '@/lib/api/promotions';
import { parseDateTimeLocalValue, toDateTimeLocalValue } from '@/lib/utils/billing';

export const PROMOTION_KIND_LABELS: Record<PromotionKind, string> = {
  creditCode: 'Credit code',
  planDiscount: 'Plan discount',
  packDiscount: 'Pack discount',
  packBonus: 'Pack bonus',
};

const KIND_HELP: Record<PromotionKind, string> = {
  creditCode: 'A code a workspace redeems for free credits that expire. Needs a code.',
  planDiscount:
    'A payment-provider discount on plan subscriptions. With no code it applies automatically to every matching checkout.',
  packDiscount: 'A payment-provider discount on credit packs, once per purchase. With no code it applies automatically.',
  packBonus: 'Extra credits on top of a purchased pack. With no code it applies automatically.',
};

/** Every field the form holds, as text where the user types it, so validation sees what was typed. */
export interface PromotionFormValues {
  kind: PromotionKind;
  name: string;
  publicHeadline: string;
  code: string;
  startsAt: string;
  endsAt: string;
  maxRedemptions: number | '';
  maxRedemptionsPerOrg: number | '';
  credits: number | '';
  creditExpiryDays: number | '';
  discountType: PromotionDiscountType;
  discountAmount: string;
  duration: PromotionDuration;
  durationMonths: number | '';
  planKeys: string[];
  packCredits: string[];
  bonusPercent: number | '';
}

export function emptyPromotionForm(kind: PromotionKind = 'creditCode'): PromotionFormValues {
  return {
    kind,
    name: '',
    publicHeadline: '',
    code: '',
    startsAt: '',
    endsAt: '',
    maxRedemptions: '',
    maxRedemptionsPerOrg: 1,
    credits: '',
    creditExpiryDays: 90,
    discountType: 'percentage',
    discountAmount: '',
    duration: 'once',
    durationMonths: '',
    planKeys: [],
    packCredits: [],
    bonusPercent: '',
  };
}

const isDiscount = (kind: PromotionKind) => kind === 'planDiscount' || kind === 'packDiscount';
const hasPacks = (kind: PromotionKind) => kind === 'packDiscount' || kind === 'packBonus';

function intIn(value: number | '', min: number, max: number): boolean {
  return typeof value === 'number' && Number.isInteger(value) && value >= min && value <= max;
}

/** A percentage the API accepts: "0.01" to "100", at most two decimals. */
export function isValidPercentage(amount: string): boolean {
  const trimmed = amount.trim();
  if (!/^\d{1,3}(\.\d{1,2})?$/.test(trimmed)) return false;
  const value = Number(trimmed);
  return value >= 0.01 && value <= 100;
}

/** A flat amount: whole US cents, at least one. */
export function isValidFlatAmount(amount: string): boolean {
  const trimmed = amount.trim();
  return /^\d{1,9}$/.test(trimmed) && Number(trimmed) >= 1;
}

/** Field → message, empty when the form can be sent. Mirrors the API's own checks. */
export function validatePromotionForm(values: PromotionFormValues): Partial<Record<keyof PromotionFormValues, string>> {
  const errors: Partial<Record<keyof PromotionFormValues, string>> = {};
  const name = values.name.trim();
  if (!name) errors.name = 'Give the promotion a name.';
  else if (name.length > 120) errors.name = 'At most 120 characters.';
  if (values.publicHeadline.trim().length > 120) errors.publicHeadline = 'At most 120 characters.';

  const code = normalizePromoCode(values.code);
  if (values.kind === 'creditCode' && !code) errors.code = 'A credit code needs a code.';
  else if (code && !PROMO_CODE_PATTERN.test(code)) errors.code = '4 to 32 letters and digits.';

  const starts = parseDateTimeLocalValue(values.startsAt);
  const ends = parseDateTimeLocalValue(values.endsAt);
  if (starts && ends && ends <= starts) errors.endsAt = 'The end must be after the start.';

  if (values.maxRedemptions !== '' && !intIn(values.maxRedemptions, 1, 10_000_000)) {
    errors.maxRedemptions = 'A whole number, at least 1.';
  }
  if (values.maxRedemptionsPerOrg !== '' && !intIn(values.maxRedemptionsPerOrg, 1, 1000)) {
    errors.maxRedemptionsPerOrg = 'A whole number, at least 1.';
  }

  if (values.kind === 'creditCode') {
    if (!intIn(values.credits, 1, 100_000)) errors.credits = 'Between 1 and 100,000 credits.';
    if (!intIn(values.creditExpiryDays, 1, 730)) errors.creditExpiryDays = 'Between 1 and 730 days.';
  }

  if (isDiscount(values.kind)) {
    if (values.discountType === 'percentage' && !isValidPercentage(values.discountAmount)) {
      errors.discountAmount = 'A percentage between 0.01 and 100.';
    }
    if (values.discountType === 'flat' && !isValidFlatAmount(values.discountAmount)) {
      errors.discountAmount = 'Whole US cents, at least 1.';
    }
  }

  if (values.kind === 'planDiscount' && values.duration === 'months' && !intIn(values.durationMonths, 1, 999)) {
    errors.durationMonths = 'Between 1 and 999 months.';
  }

  if (values.kind === 'packBonus' && !intIn(values.bonusPercent, 1, 100)) {
    errors.bonusPercent = 'Between 1 and 100 percent.';
  }

  return errors;
}

/** The `CreatePromotionDto` for valid values. Only the fields of the chosen kind are sent. */
export function buildCreatePromotionRequest(values: PromotionFormValues): CreatePromotionRequest {
  const code = normalizePromoCode(values.code);
  const headline = values.publicHeadline.trim();
  const starts = parseDateTimeLocalValue(values.startsAt);
  const ends = parseDateTimeLocalValue(values.endsAt);
  const req: CreatePromotionRequest = {
    kind: values.kind,
    name: values.name.trim(),
    ...(headline ? { publicHeadline: headline } : {}),
    ...(code ? { code } : {}),
    ...(starts ? { startsAt: starts } : {}),
    ...(ends ? { endsAt: ends } : {}),
    ...(values.maxRedemptions !== '' ? { maxRedemptions: values.maxRedemptions } : {}),
    ...(values.maxRedemptionsPerOrg !== '' ? { maxRedemptionsPerOrg: values.maxRedemptionsPerOrg } : {}),
  };

  const packCredits = values.packCredits.map(Number).filter((n) => Number.isFinite(n));

  switch (values.kind) {
    case 'creditCode':
      return {
        ...req,
        credits: values.credits === '' ? undefined : values.credits,
        creditExpiryDays: values.creditExpiryDays === '' ? undefined : values.creditExpiryDays,
      };
    case 'planDiscount':
      return {
        ...req,
        discountType: values.discountType,
        discountAmount: values.discountAmount.trim(),
        ...(values.discountType === 'flat' ? { currencyCode: 'USD' } : {}),
        duration: values.duration,
        ...(values.duration === 'months' && values.durationMonths !== '' ? { durationMonths: values.durationMonths } : {}),
        ...(values.planKeys.length > 0 ? { planKeys: values.planKeys } : {}),
      };
    case 'packDiscount':
      return {
        ...req,
        discountType: values.discountType,
        discountAmount: values.discountAmount.trim(),
        ...(values.discountType === 'flat' ? { currencyCode: 'USD' } : {}),
        ...(packCredits.length > 0 ? { packCredits } : {}),
      };
    case 'packBonus':
      return {
        ...req,
        bonusPercent: values.bonusPercent === '' ? undefined : values.bonusPercent,
        ...(packCredits.length > 0 ? { packCredits } : {}),
      };
  }
}

const numberOrEmpty = (value: string | number): number | '' =>
  typeof value === 'number' ? value : value.trim() === '' ? '' : Number(value);

/**
 * Creates a promotion. Value fields cannot be changed afterwards, which the modal says up front.
 */
export function CreatePromotionModal({
  opened,
  onClose,
  onCreated,
  planOptions,
  packOptions,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (promotion: Promotion) => void;
  /** The plans on sale, for targeting. Empty targets mean every plan. */
  planOptions: { value: string; label: string }[];
  /** The pack sizes on sale, in credits. Empty targets mean every pack. */
  packOptions: number[];
}) {
  const [values, setValues] = useState<PromotionFormValues>(() => emptyPromotionForm());
  const [errors, setErrors] = useState<Partial<Record<keyof PromotionFormValues, string>>>({});
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const [presetLoading, setPresetLoading] = useState(false);

  async function applyFounderPreset() {
    setPresetLoading(true);
    setSubmitError(null);
    try {
      const preset = await fetchFounderPricePreset();
      const ends = preset.endsAt ? new Date(preset.endsAt) : null;
      setValues({
        ...emptyPromotionForm(preset.kind),
        name: preset.name,
        publicHeadline: preset.publicHeadline ?? '',
        code: preset.code ?? '',
        discountType: preset.discountType ?? 'percentage',
        discountAmount: preset.discountAmount ?? '',
        duration: preset.duration ?? 'once',
        planKeys: preset.planKeys ?? [],
        endsAt: ends && !Number.isNaN(ends.getTime()) ? toDateTimeLocalValue(ends) : '',
      });
      setErrors({});
    } catch (error) {
      setSubmitError(promotionAdminMessage(error));
    } finally {
      setPresetLoading(false);
    }
  }

  const set = <K extends keyof PromotionFormValues>(key: K, value: PromotionFormValues[K]) =>
    setValues((prev) => ({ ...prev, [key]: value }));

  function close() {
    setValues(emptyPromotionForm());
    setErrors({});
    setSubmitError(null);
    onClose();
  }

  async function submit() {
    const found = validatePromotionForm(values);
    setErrors(found);
    setSubmitError(null);
    if (Object.keys(found).length > 0) return;
    setSaving(true);
    try {
      const created = await createPromotion(buildCreatePromotionRequest(values));
      onCreated(created);
      close();
    } catch (error) {
      setSubmitError(promotionAdminMessage(error));
    } finally {
      setSaving(false);
    }
  }

  const { kind } = values;

  return (
    <Modal opened={opened} onClose={close} title="New promotion" size="lg">
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <Stack gap="sm">
          <Button variant="light" onClick={() => void applyFounderPreset()} loading={presetLoading} data-testid="founder-price-preset">
            Founder price
          </Button>
          <SegmentedControl
            fullWidth
            value={kind}
            onChange={(value) => {
              setValues((prev) => ({ ...prev, kind: value as PromotionKind }));
              setErrors({});
            }}
            data={(Object.keys(PROMOTION_KIND_LABELS) as PromotionKind[]).map((k) => ({
              value: k,
              label: PROMOTION_KIND_LABELS[k],
            }))}
            aria-label="Promotion kind"
          />
          <Text size="xs" c="dimmed">{KIND_HELP[kind]}</Text>

          <TextInput
            label="Name"
            description="Admin only."
            required
            value={values.name}
            onChange={(e) => set('name', e.currentTarget.value)}
            error={errors.name}
            maxLength={120}
          />
          <TextInput
            label="Public headline"
            description="Shown to customers, e.g. “Spring sale: 20% off”."
            value={values.publicHeadline}
            onChange={(e) => set('publicHeadline', e.currentTarget.value)}
            error={errors.publicHeadline}
            maxLength={120}
          />
          <TextInput
            label="Code"
            description={kind === 'creditCode' ? 'Required. Letters and digits, 4 to 32.' : 'Leave empty to apply automatically.'}
            required={kind === 'creditCode'}
            value={values.code}
            onChange={(e) => set('code', e.currentTarget.value.toUpperCase())}
            error={errors.code}
            maxLength={32}
          />

          {kind === 'creditCode' && (
            <Group grow align="flex-start">
              <NumberInput
                label="Credits"
                required
                min={1}
                max={100_000}
                allowDecimal={false}
                value={values.credits}
                onChange={(v) => set('credits', numberOrEmpty(v))}
                error={errors.credits}
              />
              <NumberInput
                label="Credits expire after (days)"
                min={1}
                max={730}
                allowDecimal={false}
                value={values.creditExpiryDays}
                onChange={(v) => set('creditExpiryDays', numberOrEmpty(v))}
                error={errors.creditExpiryDays}
              />
            </Group>
          )}

          {(kind === 'planDiscount' || kind === 'packDiscount') && (
            <>
              <SegmentedControl
                value={values.discountType}
                onChange={(v) => set('discountType', v as PromotionDiscountType)}
                data={[
                  { value: 'percentage', label: 'Percentage' },
                  { value: 'flat', label: 'Flat amount (USD)' },
                ]}
                aria-label="Discount type"
              />
              <TextInput
                label={values.discountType === 'percentage' ? 'Percent off' : 'Amount off (US cents)'}
                description={
                  values.discountType === 'percentage'
                    ? 'From 0.01 to 100.'
                    : 'Whole cents, in US dollars only: 500 is five dollars.'
                }
                required
                value={values.discountAmount}
                onChange={(e) => set('discountAmount', e.currentTarget.value)}
                error={errors.discountAmount}
                inputMode="decimal"
              />
            </>
          )}

          {kind === 'planDiscount' && (
            <>
              <SegmentedControl
                value={values.duration}
                onChange={(v) => set('duration', v as PromotionDuration)}
                data={[
                  { value: 'once', label: 'First payment' },
                  { value: 'months', label: 'Several months' },
                  { value: 'forever', label: 'Forever' },
                ]}
                aria-label="Discount duration"
              />
              {values.duration === 'months' && (
                <NumberInput
                  label="Months"
                  required
                  min={1}
                  max={999}
                  allowDecimal={false}
                  value={values.durationMonths}
                  onChange={(v) => set('durationMonths', numberOrEmpty(v))}
                  error={errors.durationMonths}
                />
              )}
              <MultiSelect
                label="Plans"
                description="Leave empty for every plan on sale."
                data={planOptions}
                value={values.planKeys}
                onChange={(v) => set('planKeys', v)}
                clearable
              />
            </>
          )}

          {kind === 'packBonus' && (
            <NumberInput
              label="Bonus (percent of the pack's credits)"
              required
              min={1}
              max={100}
              allowDecimal={false}
              value={values.bonusPercent}
              onChange={(v) => set('bonusPercent', numberOrEmpty(v))}
              error={errors.bonusPercent}
            />
          )}

          {hasPacks(kind) && (
            <MultiSelect
              label="Credit packs"
              description="Leave empty for every pack on sale."
              data={packOptions.map((p) => ({ value: String(p), label: `${p.toLocaleString('en-US')} credits` }))}
              value={values.packCredits}
              onChange={(v) => set('packCredits', v)}
              clearable
            />
          )}

          <Group grow align="flex-start">
            <TextInput
              type="datetime-local"
              label="Starts"
              description="Empty: now."
              value={values.startsAt}
              onChange={(e) => set('startsAt', e.currentTarget.value)}
            />
            <TextInput
              type="datetime-local"
              label="Ends"
              description="Empty: no end."
              value={values.endsAt}
              onChange={(e) => set('endsAt', e.currentTarget.value)}
              error={errors.endsAt}
            />
          </Group>

          <Group grow align="flex-start">
            <NumberInput
              label="Total redemptions"
              description="Empty: unlimited."
              min={1}
              allowDecimal={false}
              value={values.maxRedemptions}
              onChange={(v) => set('maxRedemptions', numberOrEmpty(v))}
              error={errors.maxRedemptions}
            />
            <NumberInput
              label="Per workspace"
              min={1}
              allowDecimal={false}
              value={values.maxRedemptionsPerOrg}
              onChange={(v) => set('maxRedemptionsPerOrg', numberOrEmpty(v))}
              error={errors.maxRedemptionsPerOrg}
            />
          </Group>

          <Text size="xs" c="dimmed">
            The kind, code, amounts, targets and duration cannot be changed after creation. Only the
            name, headline, status and end date can.
          </Text>

          {submitError && (
            <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" role="alert">
              {submitError}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={close} disabled={saving}>Cancel</Button>
            <Button type="submit" loading={saving} data-testid="create-promotion-submit">Create promotion</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
