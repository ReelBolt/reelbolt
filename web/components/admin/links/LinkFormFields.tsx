'use client';

import { useId } from 'react';
import { Group, NativeSelect, NumberInput, Stack, Text, TextInput } from '@mantine/core';
import { useT } from '@/i18n';
import { LINK_DESTINATION_MAX_LENGTH, LINK_PLACEMENT_MAX_LENGTH } from '@/lib/types/marketing-links';
import {
  CUSTOM_LANDING,
  LANDING_OPTIONS,
  isAppLandingPath,
  PERSONA_LABELS,
  isLinkPersona,
  isPersona,
  minorUnitDigits,
  personaChoices,
  type LinkDetailsValues,
} from './link-model';

/** One field's error sentence, already resolved, or undefined. */
export type FieldErrors = Partial<Record<'placement' | 'destination' | 'persona' | 'cost', string>>;

interface LinkDetailsFieldsProps {
  values: LinkDetailsValues;
  errors: FieldErrors;
  onChange: (patch: Partial<LinkDetailsValues>) => void;
  disabled?: boolean;
}

/** Where the link is posted: the operator's own note. */
export function PlacementField({ values, errors, onChange, disabled }: LinkDetailsFieldsProps) {
  const t = useT('adminLinks.form');
  return (
    <TextInput
      label={t('placement')}
      description={t('placementHint')}
      value={values.placement}
      maxLength={LINK_PLACEMENT_MAX_LENGTH}
      error={errors.placement}
      disabled={disabled}
      required
      onChange={(event) => onChange({ placement: event.currentTarget.value })}
      data-testid="link-placement"
    />
  );
}

/** The landing page (a listed one or an own-site path), the audience it carries and an optional cost. */
export function LandingPersonaCostFields({ values, errors, onChange, disabled }: LinkDetailsFieldsProps) {
  const t = useT('adminLinks.form');
  const tr = useT();
  const costHintId = useId();
  const digits = minorUnitDigits(values.currency.trim().toUpperCase());
  const custom = values.landing === CUSTOM_LANDING;
  // The audiences a link may be given, plus the one this link already holds when the form does not
  // offer it: `other` on a link saved earlier (shown by name, marked as no longer offered), or a
  // token a newer server wrote (shown as it is). It stays selectable rather than vanishing.
  const personaLabelOf = (persona: string): string => {
    if (isLinkPersona(persona)) return tr.msg(PERSONA_LABELS[persona]);
    return isPersona(persona) ? t('personaNoLongerOffered', { name: tr.msg(PERSONA_LABELS[persona]) }) : persona;
  };

  return (
    <>
      <Stack gap="xs">
        <NativeSelect
          label={t('landing')}
          value={values.landing}
          error={custom ? undefined : errors.destination}
          disabled={disabled}
          required
          data={[
            ...LANDING_OPTIONS.map((option) => ({
              value: option.path,
              label: t('landingOption', { name: tr.msg(option.label), path: option.path }),
            })),
            { value: CUSTOM_LANDING, label: t('landingCustom') },
          ]}
          onChange={(event) => onChange({ landing: event.currentTarget.value })}
          data-testid="link-landing"
        />
        {custom ? (
          <TextInput
            label={t('customPath')}
            description={t('customPathHint')}
            value={values.customPath}
            maxLength={LINK_DESTINATION_MAX_LENGTH}
            error={errors.destination}
            disabled={disabled}
            required
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            onChange={(event) => onChange({ customPath: event.currentTarget.value })}
            data-testid="link-custom-path"
          />
        ) : null}
        {custom && isAppLandingPath(values.customPath) ? (
          <Text size="sm" c="orange" role="note" data-testid="link-app-landing-note">
            {t('appLandingNote')}
          </Text>
        ) : null}
      </Stack>
      <NativeSelect
        label={t('persona')}
        description={t('personaHint')}
        value={values.persona}
        error={errors.persona}
        disabled={disabled}
        data={[
          { value: '', label: t('personaNone') },
          ...personaChoices(values.persona).map((persona) => ({ value: persona, label: personaLabelOf(persona) })),
        ]}
        onChange={(event) => onChange({ persona: event.currentTarget.value })}
        data-testid="link-persona"
      />
      <Stack gap={4}>
        <Group align="flex-start" wrap="nowrap">
          <NumberInput
            label={t('cost')}
            aria-describedby={costHintId}
            value={values.cost}
            min={0}
            allowNegative={false}
            decimalScale={digits}
            hideControls
            error={errors.cost}
            disabled={disabled}
            inputMode="decimal"
            onChange={(value) => onChange({ cost: typeof value === 'number' ? value : value === '' ? '' : Number(value) })}
            data-testid="link-cost"
            style={{ flex: 1, minWidth: 0 }}
          />
          <TextInput
            label={t('currency')}
            value={values.currency}
            maxLength={3}
            disabled={disabled}
            autoCapitalize="characters"
            autoCorrect="off"
            spellCheck={false}
            onChange={(event) => onChange({ currency: event.currentTarget.value.toUpperCase() })}
            data-testid="link-currency"
            w={110}
          />
        </Group>
        <Text size="xs" c="dimmed" id={costHintId}>
          {t('costHint')}
        </Text>
      </Stack>
    </>
  );
}
