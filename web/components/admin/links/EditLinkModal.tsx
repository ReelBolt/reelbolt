'use client';

import { useState } from 'react';
import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { marketingLinkErrorCode, patchMarketingLink } from '@/lib/api/marketing-links';
import type { MarketingLink } from '@/lib/types/marketing-links';
import { LandingPersonaCostFields, PlacementField } from './LinkFormFields';
import {
  channelLabel,
  destinationOf,
  detailsOf,
  fieldOfErrorCode,
  mediumLabel,
  messageOfErrorCode,
  patchRequestOf,
  personaAfterLandingChange,
  validateEditLink,
  type LinkDetailsValues,
  type LinkFormErrors,
  type LinkFormField,
} from './link-model';

const FIELD_OF_VALUE: Record<keyof LinkDetailsValues, LinkFormField> = {
  placement: 'placement',
  landing: 'destination',
  customPath: 'destination',
  persona: 'persona',
  cost: 'cost',
  currency: 'cost',
};

interface EditLinkModalProps {
  link: MarketingLink;
  /** The currency a cost starts in when this link has none yet. */
  fallbackCurrency: string;
  onClose: () => void;
  onSaved: (link: MarketingLink) => void;
}

/**
 * Edits what a posted link may change: the placement note, the landing page, the audience and the
 * cost. The code, channel, medium and campaign are shown and fixed. Only the fields that differ are
 * sent. Mount it per link (`key={link.id}`), so it starts from that link's values.
 */
export function EditLinkModal({ link, fallbackCurrency, onClose, onSaved }: EditLinkModalProps) {
  const t = useT('adminLinks.form');
  const tr = useT();
  const isPhone = useMediaQuery('(max-width: 48em)');
  const [values, setValues] = useState<LinkDetailsValues>(() => detailsOf(link, fallbackCurrency));
  const [saving, setSaving] = useState(false);
  const [serverErrors, setServerErrors] = useState<LinkFormErrors>({});
  const [failure, setFailure] = useState<string | null>(null);

  const change = (patch: Partial<LinkDetailsValues>) => {
    setValues((previous) => {
      const next = { ...previous, ...patch };
      if ('landing' in patch || 'customPath' in patch) {
        next.persona = personaAfterLandingChange(destinationOf(previous), destinationOf(next), previous.persona);
      }
      return next;
    });
    setServerErrors((previous) => {
      const next = { ...previous };
      for (const key of Object.keys(patch) as (keyof LinkDetailsValues)[]) delete next[FIELD_OF_VALUE[key]];
      return next;
    });
  };

  const errors = validateEditLink(values);
  const valid = Object.keys(errors).length === 0;
  const patch = valid ? patchRequestOf(link, values) : {};
  const changed = Object.keys(patch).length > 0;
  // Every field starts filled in from a stored link, so a wrong value is shown at once.
  const errorOf = (field: LinkFormField): string | undefined => {
    const message = serverErrors[field] ?? errors[field];
    return message ? tr.msg(message) : undefined;
  };

  const save = async () => {
    if (!valid || !changed) return;
    setSaving(true);
    setFailure(null);
    try {
      const saved = await patchMarketingLink(link.id, patch);
      notifications.show({ message: t('saved'), color: 'green' });
      onSaved(saved);
      onClose();
    } catch (error) {
      const code = marketingLinkErrorCode(error);
      const field = fieldOfErrorCode(code);
      const message = messageOfErrorCode(code);
      if (field && message) setServerErrors({ [field]: message });
      else setFailure(t('error'));
    } finally {
      setSaving(false);
    }
  };

  const fieldErrors = {
    placement: errorOf('placement'),
    destination: errorOf('destination'),
    persona: errorOf('persona'),
    cost: errorOf('cost'),
  };
  const channel = channelLabel(link.channel);
  const medium = mediumLabel(link.medium);

  return (
    <Modal
      opened
      onClose={() => {
        if (!saving) onClose();
      }}
      title={t('editTitle', { code: link.code })}
      centered
      size="lg"
      fullScreen={!!isPhone}
    >
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
        data-testid="edit-link-form"
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed" data-testid="edit-link-fixed">
            {t('fixed', {
              channel: channel ? tr.msg(channel) : link.channel,
              medium: medium ? tr.msg(medium) : link.medium,
              campaign: link.campaign,
            })}
          </Text>
          <PlacementField values={values} errors={fieldErrors} onChange={change} disabled={saving} />
          <LandingPersonaCostFields values={values} errors={fieldErrors} onChange={change} disabled={saving} />
          {failure ? (
            <Alert color="red" variant="light" icon={<IconAlertCircle size={16} />} role="alert" data-testid="edit-link-error">
              {failure}
            </Alert>
          ) : null}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={saving}>
              {t('cancel')}
            </Button>
            <Button type="submit" loading={saving} disabled={!valid || !changed} data-testid="edit-link-save">
              {t('save')}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
