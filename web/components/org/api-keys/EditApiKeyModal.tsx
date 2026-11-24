'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import { Alert, Button, Checkbox, Group, Modal, NumberInput, Stack, Text, TextInput } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import { updateApiKey } from '@/lib/api/api-keys';
import type { ApiKeyCapsRequest, ApiKeyItem } from '@/lib/types/api-key';
import { useT } from '@/i18n';
import {
  DEFAULT_DAILY_CAP_CREDITS,
  dailyCapNeedsConfirmation,
  isCapConfirmationRequired,
  mapApiKeyError,
} from './daily-cap';

const MAX_CAP_CREDITS = 10_000_000;

function valueOf(value: number | null | undefined): number | '' {
  return typeof value === 'number' ? value : '';
}

function isCap(value: number | ''): boolean {
  return value === '' || (Number.isInteger(value) && value >= 1 && value <= MAX_CAP_CREDITS);
}

interface EditApiKeyModalProps {
  item: ApiKeyItem | null;
  opened: boolean;
  onClose: () => void;
  onSaved: (item: ApiKeyItem) => void | Promise<void>;
}

/** Name and caps only: the server keeps scopes, projects and expiry immutable (make a new key). */
export function EditApiKeyModal({ item, opened, onClose, onSaved }: EditApiKeyModalProps) {
  const t = useT('org.apiKeys');
  const tAll = useT();
  const [name, setName] = useState(item?.name ?? '');
  const [daily, setDaily] = useState<number | ''>(valueOf(item?.caps.dailyCredits));
  const [weekly, setWeekly] = useState<number | ''>(valueOf(item?.caps.weeklyCredits));
  const [monthly, setMonthly] = useState<number | ''>(valueOf(item?.caps.monthlyCredits));
  const [noDailyCap, setNoDailyCap] = useState(item?.dailyCap?.status === 'waived');
  const [capsConfirmed, setCapsConfirmed] = useState(false);
  const [serverWantsConfirm, setServerWantsConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [seededFor, setSeededFor] = useState<string | null>(null);

  // Re-seed the fields when a different key is opened. Deriving during render (the template below
  // is only ever used while `opened`) keeps it a plain state transition, no effect.
  if (opened && item && seededFor !== item.id) {
    setSeededFor(item.id);
    setName(item.name);
    setDaily(valueOf(item.caps.dailyCredits));
    setWeekly(valueOf(item.caps.weeklyCredits));
    setMonthly(valueOf(item.caps.monthlyCredits));
    setNoDailyCap(item.dailyCap?.status === 'waived');
    setCapsConfirmed(false);
    setServerWantsConfirm(false);
    setError(null);
  }

  const status = item?.dailyCap?.status ?? null;
  const defaultCredits = item?.dailyCap?.defaultCredits ?? DEFAULT_DAILY_CAP_CREDITS;
  const needsConfirm =
    serverWantsConfirm ||
    dailyCapNeedsConfirmation(
      { noDailyCap, daily, current: item?.caps.dailyCredits ?? null, currentWaived: status === 'waived' },
      defaultCredits,
    );

  // Also sent when an unchanged above-default or no-limit value is re-saved, so that save never fails.
  const sendConfirm = noDailyCap || (daily !== '' && daily > defaultCredits);

  function fail(err: unknown) {
    if (isCapConfirmationRequired(err)) setServerWantsConfirm(true);
    setError(mapApiKeyError(err, t) ?? describeError(tAll, err, t('notifications.updateFailed')));
  }

  /** One click, explicit: sets the daily limit to the default on a key that had none. Never automatic. */
  async function setDefaultLimit() {
    if (!item) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await updateApiKey(item.id, { caps: { dailyCredits: defaultCredits } });
      await onSaved(updated);
      close();
    } catch (err: unknown) {
      fail(err);
    } finally {
      setSaving(false);
    }
  }

  function close() {
    setSeededFor(null);
    onClose();
  }

  async function save() {
    if (!item) return;
    const trimmed = name.trim();
    if (!trimmed) return setError(t('create.nameRequired'));
    if (trimmed.length > 80) return setError(t('create.nameTooLong'));
    if (!isCap(daily) || !isCap(weekly) || !isCap(monthly)) return setError(t('edit.capRange'));

    if (!noDailyCap && daily === '' && status !== 'legacy_uncapped') return setError(t('capGuard.dailyRequired'));
    if (needsConfirm && !capsConfirmed) return setError(t('capGuard.confirmNeeded'));

    const caps: ApiKeyCapsRequest = {
      weeklyCredits: weekly === '' ? null : weekly,
      monthlyCredits: monthly === '' ? null : monthly,
    };
    // No daily key at all means "leave the daily limit as it is" (a legacy key stays flagged until set).
    if (noDailyCap) caps.noDailyCap = true;
    else if (daily !== '') caps.dailyCredits = daily;

    setSaving(true);
    setError(null);
    try {
      const updated = await updateApiKey(item.id, {
        name: trimmed,
        caps,
        ...(needsConfirm || sendConfirm ? { confirmCaps: true } : {}),
      });
      await onSaved(updated);
      close();
    } catch (err: unknown) {
      fail(err);
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal opened={opened} onClose={close} title={t('edit.title')} centered>
      <Stack gap="md">
        <TextInput
          label={t('edit.nameLabel')}
          value={name}
          maxLength={80}
          required
          onChange={(event) => setName(event.currentTarget.value)}
        />

        <Text size="sm" fw={600}>
          {t('edit.capsLabel')}
        </Text>
        <Text size="sm" c="dimmed">
          {t('edit.capsDescription')}
        </Text>
        {status === 'legacy_uncapped' && (
          <Alert icon={<IconAlertTriangle size={16} />} color="yellow" variant="light" data-testid="api-key-legacy-warning">
            <Stack gap="xs">
              <Text size="sm">{t('capGuard.legacyWarning')}</Text>
              <Button size="compact-sm" variant="light" onClick={() => void setDefaultLimit()} loading={saving}>
                {t('capGuard.setDefault', { default: defaultCredits })}
              </Button>
            </Stack>
          </Alert>
        )}
        {status === 'waived' && (
          <Text size="sm" c="dimmed" data-testid="api-key-waived-note">
            {t('capGuard.waivedNote')}
          </Text>
        )}
        <Group grow align="flex-start">
          <NumberInput
            label={t('edit.dailyCap')}
            description={t('capGuard.dailyHelp', { default: defaultCredits })}
            placeholder={status === 'legacy_uncapped' ? t('capGuard.legacyPlaceholder') : undefined}
            min={1}
            max={MAX_CAP_CREDITS}
            value={noDailyCap ? '' : daily}
            disabled={noDailyCap}
            onChange={(value) => {
              setDaily(typeof value === 'number' ? value : '');
              setCapsConfirmed(false);
            }}
          />
          <NumberInput
            label={t('edit.weeklyCap')}
            placeholder={t('edit.capPlaceholder')}
            min={1}
            max={MAX_CAP_CREDITS}
            value={weekly}
            onChange={(value) => setWeekly(typeof value === 'number' ? value : '')}
          />
          <NumberInput
            label={t('edit.monthlyCap')}
            placeholder={t('edit.capPlaceholder')}
            min={1}
            max={MAX_CAP_CREDITS}
            value={monthly}
            onChange={(value) => setMonthly(typeof value === 'number' ? value : '')}
          />
        </Group>
        <Checkbox
          checked={noDailyCap}
          label={t('capGuard.noDailyLimit')}
          onChange={(event) => {
            setNoDailyCap(event.currentTarget.checked);
            setCapsConfirmed(false);
          }}
        />
        {needsConfirm && (
          <Alert icon={<IconAlertTriangle size={16} />} color="yellow" variant="light" data-testid="api-key-cap-confirm">
            <Stack gap="xs">
              <Text size="sm">{noDailyCap ? t('capGuard.removeConsequence') : t('capGuard.raiseConsequence')}</Text>
              <Checkbox
                checked={capsConfirmed}
                label={t('capGuard.confirmLabel')}
                onChange={(event) => setCapsConfirmed(event.currentTarget.checked)}
              />
            </Stack>
          </Alert>
        )}

        {error && (
          <Alert color="red" variant="light">
            {error}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={close}>
            {t('edit.cancel')}
          </Button>
          <Button onClick={() => void save()} loading={saving}>
            {t('edit.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
