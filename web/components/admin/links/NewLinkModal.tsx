'use client';

import { useMemo, useState } from 'react';
import { Alert, Anchor, Autocomplete, Button, Group, Modal, NativeSelect, Stack, Text, TextInput } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconAlertCircle } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { createMarketingLink, marketingLinkErrorCode } from '@/lib/api/marketing-links';
import {
  LINK_CHANNELS,
  LINK_CODE_MAX_LENGTH,
  LINK_MEDIA,
  UTM_VALUE_MAX_LENGTH,
  type MarketingLink,
} from '@/lib/types/marketing-links';
import { LandingPersonaCostFields, PlacementField } from './LinkFormFields';
import { LinkUrls } from './LinkUrls';
import {
  CHANNEL_LABELS,
  DEFAULT_MEDIUM,
  MEDIUM_LABELS,
  campaignSuggestions,
  createRequestOf,
  destinationOf,
  fieldOfErrorCode,
  isLinkChannel,
  messageOfErrorCode,
  personaAfterLandingChange,
  startingCurrency,
  suggestLinkCode,
  validateNewLink,
  type LinkFormErrors,
  type LinkFormField,
  type NewLinkValues,
} from './link-model';

/** The form field each value belongs to, for "has the admin touched this field yet". */
const FIELD_OF_VALUE: Record<keyof NewLinkValues, LinkFormField> = {
  placement: 'placement',
  channel: 'channel',
  medium: 'medium',
  campaign: 'campaign',
  landing: 'destination',
  customPath: 'destination',
  persona: 'persona',
  cost: 'cost',
  currency: 'cost',
  code: 'code',
};

interface NewLinkModalProps {
  /** Every loaded link, archived ones included: their codes are taken and their campaigns are suggested. */
  links: readonly MarketingLink[];
  onClose: () => void;
  /** Called once the API has created the link, while the modal still shows its two addresses. */
  onCreated: (link: MarketingLink) => void;
}

/**
 * Creates a tracked link, then shows its short and long address with a copy button each.
 *
 * The medium follows the channel and the code follows channel + campaign + placement until the admin
 * edits either by hand. Mount it only while it is open, so every opening starts from an empty form.
 */
export function NewLinkModal({ links, onClose, onCreated }: NewLinkModalProps) {
  const t = useT('adminLinks.form');
  const tr = useT();
  const isPhone = useMediaQuery('(max-width: 48em)');
  const takenCodes = useMemo(() => links.map((link) => link.code), [links]);
  const campaigns = useMemo(() => campaignSuggestions(links), [links]);

  const [values, setValues] = useState<NewLinkValues>(() => ({
    placement: '',
    channel: '',
    medium: '',
    campaign: '',
    landing: '/',
    customPath: '',
    persona: '',
    cost: '',
    currency: startingCurrency(links),
    code: '',
  }));
  const [mediumEdited, setMediumEdited] = useState(false);
  const [codeEdited, setCodeEdited] = useState(false);
  const [touched, setTouched] = useState<ReadonlySet<LinkFormField>>(new Set());
  const [submitted, setSubmitted] = useState(false);
  const [saving, setSaving] = useState(false);
  const [serverErrors, setServerErrors] = useState<LinkFormErrors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [created, setCreated] = useState<MarketingLink | null>(null);

  const suggestion = suggestLinkCode(values, takenCodes);

  const change = (patch: Partial<NewLinkValues>, edited: { medium?: boolean; code?: boolean } = {}) => {
    const fields = (Object.keys(patch) as (keyof NewLinkValues)[]).map((key) => FIELD_OF_VALUE[key]);
    const followMedium = !(edited.medium ?? mediumEdited);
    const followCode = !(edited.code ?? codeEdited);
    setValues((previous) => {
      const next = { ...previous, ...patch };
      if ('channel' in patch && followMedium && isLinkChannel(next.channel)) next.medium = DEFAULT_MEDIUM[next.channel];
      if ('landing' in patch || 'customPath' in patch) {
        next.persona = personaAfterLandingChange(destinationOf(previous), destinationOf(next), previous.persona);
      }
      if (followCode && !('code' in patch)) next.code = suggestLinkCode(next, takenCodes);
      return next;
    });
    setTouched((previous) => new Set([...previous, ...fields]));
    // A refusal from the server is about the value that was sent: editing the field retires it.
    setServerErrors((previous) => {
      const next = { ...previous };
      for (const field of fields) delete next[field];
      return next;
    });
  };

  const errors = validateNewLink(values, takenCodes);
  /** Shown as soon as a typed value is wrong; a missing value only once Create was pressed. */
  const errorOf = (field: LinkFormField): string | undefined => {
    const server = serverErrors[field];
    if (server) return tr.msg(server);
    const local = errors[field];
    if (!local) return undefined;
    const live = touched.has(field) && !local.$msg.endsWith('Required');
    return submitted || live ? tr.msg(local) : undefined;
  };

  const submit = async () => {
    setSubmitted(true);
    setFailure(null);
    if (Object.keys(errors).length > 0) return;
    setSaving(true);
    try {
      const link = await createMarketingLink(createRequestOf(values));
      setCreated(link);
      onCreated(link);
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

  if (created) {
    return (
      <Modal opened onClose={onClose} title={t('createdTitle')} centered size="lg" fullScreen={!!isPhone}>
        <Stack gap="lg" data-testid="link-created">
          <Text size="sm">{t('createdBody')}</Text>
          <LinkUrls link={created} />
          <Group justify="flex-end">
            <Button onClick={onClose} data-testid="link-created-done">
              {tr('adminLinks.urls.done')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    );
  }

  const detailErrors = {
    placement: errorOf('placement'),
    destination: errorOf('destination'),
    persona: errorOf('persona'),
    cost: errorOf('cost'),
  };

  return (
    <Modal
      opened
      onClose={() => {
        if (!saving) onClose();
      }}
      title={t('newTitle')}
      centered
      size="lg"
      fullScreen={!!isPhone}
    >
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
        data-testid="new-link-form"
      >
        <Stack gap="md">
          <PlacementField values={values} errors={detailErrors} onChange={change} disabled={saving} />
          <NativeSelect
            label={t('channel')}
            value={values.channel}
            error={errorOf('channel')}
            disabled={saving}
            required
            data={[
              { value: '', label: t('channelPlaceholder'), disabled: true },
              ...LINK_CHANNELS.map((channel) => ({ value: channel, label: tr.msg(CHANNEL_LABELS[channel]) })),
            ]}
            onChange={(event) => change({ channel: event.currentTarget.value })}
            data-testid="link-channel"
          />
          <NativeSelect
            label={t('medium')}
            description={t('mediumHint')}
            value={values.medium}
            error={errorOf('medium')}
            disabled={saving || !values.channel}
            required
            data={[
              ...(values.medium ? [] : [{ value: '', label: '', disabled: true }]),
              ...LINK_MEDIA.map((medium) => ({ value: medium, label: tr.msg(MEDIUM_LABELS[medium]) })),
            ]}
            onChange={(event) => {
              setMediumEdited(true);
              change({ medium: event.currentTarget.value }, { medium: true });
            }}
            data-testid="link-medium"
          />
          <Autocomplete
            label={t('campaign')}
            description={t('campaignHint')}
            value={values.campaign}
            data={campaigns}
            maxLength={UTM_VALUE_MAX_LENGTH}
            error={errorOf('campaign')}
            disabled={saving}
            required
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            // Lowercased and hyphenated as it is typed, so what is on screen is what is stored.
            onChange={(value) => change({ campaign: value.toLowerCase().replace(/\s+/g, '-') })}
            data-testid="link-campaign"
          />
          <LandingPersonaCostFields values={values} errors={detailErrors} onChange={change} disabled={saving} />
          <Stack gap={4}>
            <TextInput
              label={t('code')}
              description={t('codeHint', { path: `/l/${values.code || suggestion || '…'}` })}
              value={values.code}
              maxLength={LINK_CODE_MAX_LENGTH}
              error={errorOf('code')}
              disabled={saving}
              required
              autoCapitalize="none"
              autoCorrect="off"
              spellCheck={false}
              onChange={(event) => {
                setCodeEdited(true);
                change({ code: event.currentTarget.value.toLowerCase().replace(/\s+/g, '-') }, { code: true });
              }}
              data-testid="link-code"
            />
            {codeEdited && suggestion && suggestion !== values.code ? (
              <Anchor
                component="button"
                type="button"
                size="sm"
                ta="left"
                onClick={() => {
                  setCodeEdited(false);
                  change({ code: suggestion }, { code: false });
                }}
                data-testid="link-code-suggestion"
              >
                {t('useSuggestion')}
              </Anchor>
            ) : null}
          </Stack>
          <Text size="sm" c="dimmed">
            {t('immutable')}
          </Text>
          {failure ? (
            <Alert color="red" variant="light" icon={<IconAlertCircle size={16} />} role="alert" data-testid="new-link-error">
              {failure}
            </Alert>
          ) : null}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={saving}>
              {t('cancel')}
            </Button>
            <Button type="submit" loading={saving} data-testid="new-link-submit">
              {t('create')}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
