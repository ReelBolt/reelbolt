'use client';

import { ActionIcon, NumberInput } from '@mantine/core';
import { IconMinus, IconPlus } from '@tabler/icons-react';
import { useT } from '@/i18n';

/** Tap target for the stepper buttons: the touch minimum, on every device. */
const BUTTON_SIZE = 44;

/**
 * A seat count with its own minus and plus buttons. Mantine's built-in spinner is 17px wide, too
 * small for a thumb and unnamed for a screen reader, so it is hidden and replaced by two 44px
 * buttons that say what they do. The helper text is 12px, the smallest text this app uses.
 */
export function SeatStepper({
  label,
  description,
  value,
  min,
  onChange,
  planName,
}: {
  label: string;
  description: string;
  value: number;
  min: number;
  onChange: (seats: number) => void;
  /** Named in the accessible names ("Fewer seats on the Team plan"); omitted for the current plan. */
  planName?: string;
}) {
  const t = useT('billing.seatStepper');
  return (
    <NumberInput
      label={label}
      description={description}
      value={value}
      onChange={(next) => onChange(Math.max(min, typeof next === 'number' ? next : Number(next) || min))}
      min={min}
      step={1}
      allowDecimal={false}
      hideControls
      aria-label={planName ? t('onPlan', { plan: planName }) : t('current')}
      leftSectionWidth={BUTTON_SIZE}
      rightSectionWidth={BUTTON_SIZE}
      leftSection={
        <ActionIcon
          variant="subtle"
          color="gray"
          size={BUTTON_SIZE}
          aria-label={planName ? t('fewerOn', { plan: planName }) : t('fewer')}
          disabled={value <= min}
          onClick={() => onChange(Math.max(min, value - 1))}
        >
          <IconMinus size={16} />
        </ActionIcon>
      }
      rightSection={
        <ActionIcon
          variant="subtle"
          color="gray"
          size={BUTTON_SIZE}
          aria-label={planName ? t('moreOn', { plan: planName }) : t('more')}
          onClick={() => onChange(value + 1)}
        >
          <IconPlus size={16} />
        </ActionIcon>
      }
      styles={{
        description: { fontSize: 12 },
        input: { textAlign: 'center', minHeight: BUTTON_SIZE, fontSize: 16 },
      }}
    />
  );
}
