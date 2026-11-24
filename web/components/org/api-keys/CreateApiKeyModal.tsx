'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import Link from 'next/link';
import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Code,
  CopyButton,
  Divider,
  Group,
  Modal,
  MultiSelect,
  NumberInput,
  SegmentedControl,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { IconAlertTriangle, IconCheck, IconCopy } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { createApiKey } from '@/lib/api/api-keys';
import { useProjects } from '@/lib/hooks/use-projects';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { TRIAL_PLAN_KEY } from '@/components/billing/subscription';
import { API_KEY_SCOPES, type ApiKeyCapsRequest, type ApiKeyScope } from '@/lib/types/api-key';
import {
  DEFAULT_DAILY_CAP_CREDITS,
  dailyCapNeedsConfirmation,
  isCapConfirmationRequired,
  mapApiKeyError,
} from './daily-cap';
import { useT } from '@/i18n';

const DAY_MS = 24 * 60 * 60 * 1000;
const MINUTE_MS = 60 * 1000;
const MAX_CAP_CREDITS = 10_000_000;
const MAX_LIFETIME_DAYS = 365;
const EXPIRY_MARGIN_MS = 5 * MINUTE_MS;
const PROJECTS_MODE_LABEL_ID = 'api-key-projects-mode';
const EXPIRY_MODE_LABEL_ID = 'api-key-expiry-mode';

/**
 * The furthest expiry the server accepts: 365 days from now, minus five of the ten minutes of
 * tolerance it allows, so a date the form offers is never refused for being a few seconds long.
 */
export function maxExpiryInstant(now: number = Date.now()): Date {
  return new Date(now + MAX_LIFETIME_DAYS * DAY_MS - EXPIRY_MARGIN_MS);
}

/** The earliest expiry the server accepts is one hour out; five minutes of margin keep an offered date from being refused. */
export function minExpiryInstant(now: number = Date.now()): Date {
  return new Date(now + 60 * MINUTE_MS + EXPIRY_MARGIN_MS);
}

function dateInputValue(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

type ExpiryMode = '30' | '90' | '365' | 'custom' | 'none';
type ProjectMode = 'all' | 'selected';

/** `instant`, or the furthest accepted expiry when `instant` is later than that. */
export function clampExpiry(instant: Date, now: number = Date.now()): Date {
  const max = maxExpiryInstant(now);
  return instant.getTime() > max.getTime() ? max : instant;
}

/** The instant a `date` input value means: the end of that day, local time. */
export function expiryInstantFromDate(value: string): Date | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const date = new Date(`${value}T23:59:59`);
  return Number.isNaN(date.getTime()) ? null : date;
}

function isCap(value: number | ''): boolean {
  return value === '' || (Number.isInteger(value) && value >= 1 && value <= MAX_CAP_CREDITS);
}

interface CreateApiKeyModalProps {
  opened: boolean;
  onClose: () => void;
  /** Called after a successful create, before the secret step is shown. */
  onCreated: () => void | Promise<unknown>;
  /** The server's default daily limit (from the list); 200 when it did not say. */
  defaultDailyCap?: number | null;
}

/**
 * Create a workspace API key, ending on a secret-shown-once step modelled on
 * `components/admin/TempPasswordModal.tsx`.
 *
 * The secret lives in this component's state only: it is never put in SWR, localStorage or a log,
 * and `reset()` clears it on close so reopening the modal shows a blank form, not the old secret.
 */
export function CreateApiKeyModal({ opened, onClose, onCreated, defaultDailyCap }: CreateApiKeyModalProps) {
  const defaultCredits = defaultDailyCap ?? DEFAULT_DAILY_CAP_CREDITS;
  const t = useT('org.apiKeys');
  const tAll = useT();
  const { data: projects } = useProjects();
  const { data: billing } = useBillingSummary(opened);
  const onTrial = billing?.subscription?.planKey === TRIAL_PLAN_KEY;

  const [step, setStep] = useState<'form' | 'secret'>('form');
  const [name, setName] = useState('');
  const [scopes, setScopes] = useState<ApiKeyScope[]>([]);
  const [projectMode, setProjectMode] = useState<ProjectMode>('all');
  const [projectIds, setProjectIds] = useState<string[]>([]);
  const [expiryMode, setExpiryMode] = useState<ExpiryMode>('90');
  const [customDate, setCustomDate] = useState('');
  const [daily, setDaily] = useState<number | '' | null>(null);
  const [noDailyCap, setNoDailyCap] = useState(false);
  const [capsConfirmed, setCapsConfirmed] = useState(false);
  const [serverWantsConfirm, setServerWantsConfirm] = useState(false);
  const [weekly, setWeekly] = useState<number | ''>('');
  const [monthly, setMonthly] = useState<number | ''>('');
  const [secret, setSecret] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [planBlocked, setPlanBlocked] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  function reset() {
    setStep('form');
    setName('');
    setScopes([]);
    setProjectMode('all');
    setProjectIds([]);
    setExpiryMode('90');
    setCustomDate('');
    setDaily(null);
    setNoDailyCap(false);
    setCapsConfirmed(false);
    setServerWantsConfirm(false);
    setWeekly('');
    setMonthly('');
    setSecret('');
    setError(null);
    setPlanBlocked(false);
    setSubmitting(false);
  }

  function handleClose() {
    reset();
    onClose();
  }

  const scopeGroups: { label: string; scopes: { value: ApiKeyScope; label: string; description: string }[] }[] = [
    {
      label: t('scope.projectsGroup'),
      scopes: [
        { value: 'projects.read', label: t('scope.projectsRead'), description: t('scope.projectsReadDescription') },
        { value: 'projects.write', label: t('scope.projectsWrite'), description: t('scope.projectsWriteDescription') },
      ],
    },
    {
      label: t('scope.filesGroup'),
      scopes: [
        { value: 'files.read', label: t('scope.filesRead'), description: t('scope.filesReadDescription') },
        { value: 'files.write', label: t('scope.filesWrite'), description: t('scope.filesWriteDescription') },
      ],
    },
    {
      label: t('scope.workflowsGroup'),
      scopes: [
        {
          value: 'workflows.read',
          label: t('scope.workflowsRead'),
          description: t('scope.workflowsReadDescription'),
        },
        {
          value: 'workflows.write',
          label: t('scope.workflowsWrite'),
          description: t('scope.workflowsWriteDescription'),
        },
      ],
    },
    {
      label: t('scope.runsGroup'),
      scopes: [
        { value: 'runs.read', label: t('scope.runsRead'), description: t('scope.runsReadDescription') },
        { value: 'runs.execute', label: t('scope.runsExecute'), description: t('scope.runsExecuteDescription') },
      ],
    },
    {
      label: t('scope.outputsGroup'),
      scopes: [
        { value: 'outputs.read', label: t('scope.outputsRead'), description: t('scope.outputsReadDescription') },
      ],
    },
    {
      label: t('scope.webhooksGroup'),
      scopes: [
        {
          value: 'webhooks.manage',
          label: t('scope.webhooksManage'),
          description: t('scope.webhooksManageDescription'),
        },
      ],
    },
    {
      // Off unless ticked: lets a run started with this key allow sending media (inputs.allowMediaEgress).
      label: t('scope.mediaGroup'),
      scopes: [
        {
          value: 'media.egress',
          label: t('scope.mediaEgress'),
          description: t('scope.mediaEgressDescription'),
        },
      ],
    },
  ];

  const toggleScope = (value: ApiKeyScope, checked: boolean) =>
    setScopes((prev) => (checked ? [...new Set([...prev, value])] : prev.filter((scope) => scope !== value)));

  // "Select all" never ticks the permission to send media: that one is chosen on its own, on purpose.
  const selectAllRead = () => setScopes(API_KEY_SCOPES.filter((scope) => scope.endsWith('.read')));

  const effectiveDaily = daily ?? defaultCredits;
  const needsConfirm =
    serverWantsConfirm || dailyCapNeedsConfirmation({ noDailyCap, daily: effectiveDaily }, defaultCredits);

  async function submit() {
    setError(null);
    setPlanBlocked(false);
    const trimmed = name.trim();
    if (!trimmed) return setError(t('create.nameRequired'));
    if (trimmed.length > 80) return setError(t('create.nameTooLong'));
    if (scopes.length === 0) return setError(t('create.scopesRequired'));
    if (projectMode === 'selected' && projectIds.length === 0) return setError(t('create.projectsRequired'));
    if (!isCap(effectiveDaily) || !isCap(weekly) || !isCap(monthly)) return setError(t('create.capRange'));

    let expiresAt: string | undefined;
    if (expiryMode === 'custom') {
      const instant = expiryInstantFromDate(customDate);
      if (!instant) return setError(t('create.expiryDateRequired'));
      if (instant.getTime() <= minExpiryInstant().getTime()) return setError(t('create.expiryTooSoon'));
      if (customDate > dateInputValue(maxExpiryInstant())) return setError(t('create.expiryTooFar'));
      expiresAt = clampExpiry(instant).toISOString();
    } else if (expiryMode !== 'none') {
      const days = expiryMode === '30' ? 30 : expiryMode === '90' ? 90 : MAX_LIFETIME_DAYS;
      const instant = days === MAX_LIFETIME_DAYS ? maxExpiryInstant() : new Date(Date.now() + days * DAY_MS);
      expiresAt = instant.toISOString();
    }

    if (needsConfirm && !capsConfirmed) return setError(t('capGuard.confirmNeeded'));

    const caps: ApiKeyCapsRequest = {};
    if (noDailyCap) caps.noDailyCap = true;
    else caps.dailyCredits = effectiveDaily === '' ? defaultCredits : effectiveDaily;
    if (weekly !== '') caps.weeklyCredits = weekly;
    if (monthly !== '') caps.monthlyCredits = monthly;

    setSubmitting(true);
    try {
      const created = await createApiKey({
        name: trimmed,
        scopes,
        ...(projectMode === 'selected' ? { projectIds } : {}),
        ...(expiresAt ? { expiresAt } : {}),
        caps,
        ...(needsConfirm ? { confirmCaps: true } : {}),
      });
      setSecret(created.secret);
      setStep('secret');
      await onCreated();
    } catch (err: unknown) {
      if (isCapConfirmationRequired(err)) {
        setServerWantsConfirm(true);
        setError(mapApiKeyError(err, t));
      } else if (err instanceof ApiError && err.status === 402 && err.code !== 'API_PLAN_READ_ONLY') setPlanBlocked(true);
      else if (mapApiKeyError(err, t)) setError(mapApiKeyError(err, t));
      else if (err instanceof ApiError && err.code === 'api_key_limit') setError(t('create.limit'));
      else setError(describeError(tAll, err, t('create.createFailed')));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={step === 'secret' ? t('secret.title') : t('create.title')}
      centered
      size="lg"
      closeOnClickOutside={step === 'form'}
      closeOnEscape={step === 'form'}
      withCloseButton={step === 'form'}
    >
      {step === 'secret' ? (
        <Stack gap="md" data-testid="api-key-secret-step">
          <Text size="sm">{t('secret.body')}</Text>
          <Stack gap={4}>
            <Text size="sm" fw={600}>
              {t('secret.label')}
            </Text>
            <Code block p="md" data-testid="api-key-secret">
              {secret}
            </Code>
          </Stack>
          <Alert icon={<IconAlertTriangle size={16} />} color="yellow" variant="light">
            {t('secret.warning')}
          </Alert>
          <CopyButton value={secret}>
            {({ copied, copy }) => (
              <Button
                leftSection={copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
                color={copied ? 'green' : undefined}
                onClick={copy}
                fullWidth
              >
                {copied ? t('secret.copied') : t('secret.copy')}
              </Button>
            )}
          </CopyButton>
          <Group justify="flex-end">
            <Button onClick={handleClose}>{t('secret.done')}</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            {t('description')}
          </Text>
          {onTrial && (
            <Text size="sm" c="dimmed" data-testid="api-key-plan-hint">
              {t('create.planHint')}
            </Text>
          )}

          <TextInput
            label={t('create.nameLabel')}
            placeholder={t('create.namePlaceholder')}
            value={name}
            maxLength={80}
            required
            onChange={(event) => setName(event.currentTarget.value)}
          />

          <Divider label={t('create.scopesLabel')} labelPosition="left" />
          <Group gap="xs">
            <Button size="compact-sm" variant="light" onClick={selectAllRead}>
              {t('create.selectAllRead')}
            </Button>
            <Button size="compact-sm" variant="subtle" onClick={() => setScopes(API_KEY_SCOPES.filter((scope) => scope !== 'media.egress'))}>
              {t('create.selectAll')}
            </Button>
          </Group>
          <Stack gap="sm">
            {scopeGroups.map((group) => (
              <Stack key={group.label} gap={4}>
                <Text size="sm" fw={600}>
                  {group.label}
                </Text>
                {group.scopes.map((scope) => (
                  <Checkbox
                    key={scope.value}
                    checked={scopes.includes(scope.value)}
                    label={scope.label}
                    description={scope.description}
                    onChange={(event) => toggleScope(scope.value, event.currentTarget.checked)}
                  />
                ))}
              </Stack>
            ))}
          </Stack>

          <Divider
            label={<span id={PROJECTS_MODE_LABEL_ID}>{t('create.projectsLabel')}</span>}
            labelPosition="left"
          />
          <SegmentedControl
            aria-labelledby={PROJECTS_MODE_LABEL_ID}
            value={projectMode}
            onChange={(value) => setProjectMode(value as ProjectMode)}
            data={[
              { value: 'all', label: t('create.projectsAll') },
              { value: 'selected', label: t('create.projectsSelected') },
            ]}
          />
          {projectMode === 'selected' && (
            <MultiSelect
              label={t('create.projectsLabel')}
              placeholder={t('create.projectsPlaceholder')}
              data={(projects ?? []).map((project) => ({ value: project.id, label: project.name }))}
              value={projectIds}
              searchable
              onChange={setProjectIds}
            />
          )}

          <Divider
            label={<span id={EXPIRY_MODE_LABEL_ID}>{t('create.expiryLabel')}</span>}
            labelPosition="left"
          />
          <SegmentedControl
            aria-labelledby={EXPIRY_MODE_LABEL_ID}
            value={expiryMode}
            onChange={(value) => setExpiryMode(value as ExpiryMode)}
            data={[
              { value: '30', label: t('create.expiry30') },
              { value: '90', label: t('create.expiry90') },
              { value: '365', label: t('create.expiry365') },
              { value: 'custom', label: t('create.expiryCustom') },
              { value: 'none', label: t('create.expiryNone') },
            ]}
          />
          {expiryMode === 'custom' && (
            <TextInput
              type="date"
              label={t('create.expiryDateLabel')}
              min={dateInputValue(minExpiryInstant())}
              max={dateInputValue(maxExpiryInstant())}
              value={customDate}
              onChange={(event) => setCustomDate(event.currentTarget.value)}
            />
          )}
          {expiryMode === 'none' && (
            <Alert icon={<IconAlertTriangle size={16} />} color="yellow" variant="light">
              {t('create.noExpiryWarning')}
            </Alert>
          )}

          <Divider label={t('create.capsLabel')} labelPosition="left" />
          <Text size="sm" c="dimmed">
            {t('create.capsDescription')}
          </Text>
          <Group grow align="flex-start">
            <NumberInput
              label={t('create.dailyCap')}
              description={t('capGuard.dailyHelp', { default: defaultCredits })}
              min={1}
              max={MAX_CAP_CREDITS}
              value={noDailyCap ? '' : effectiveDaily}
              disabled={noDailyCap}
              onChange={(value) => {
                setDaily(typeof value === 'number' ? value : '');
                setCapsConfirmed(false);
              }}
            />
            <NumberInput
              label={t('create.weeklyCap')}
              placeholder={t('create.capPlaceholder')}
              min={1}
              max={MAX_CAP_CREDITS}
              value={weekly}
              onChange={(value) => setWeekly(typeof value === 'number' ? value : '')}
            />
            <NumberInput
              label={t('create.monthlyCap')}
              placeholder={t('create.capPlaceholder')}
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

          <Text size="sm">{t('create.footer')}</Text>

          {planBlocked && (
            <Alert color="yellow" variant="light" title={t('create.planTitle')} data-testid="api-key-plan-blocked">
              <Stack gap="xs">
                <Text size="sm">{t('create.planBody')}</Text>
                <Text size="sm">
                  <Link href="/settings/billing">{t('create.upgrade')}</Link>
                </Text>
              </Stack>
            </Alert>
          )}
          {error && !planBlocked && (
            <Alert color="red" variant="light">
              {error}
            </Alert>
          )}

          <Group justify="flex-end">
            <Button variant="default" onClick={handleClose}>
              {t('create.cancel')}
            </Button>
            <Button onClick={() => void submit()} loading={submitting}>
              {t('create.submit')}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}

/** A small badge list shared with the table: the first few scopes, then a +N overflow. */
export function ScopeBadges({ scopes }: { scopes: string[] }) {
  const t = useT('org.apiKeys');
  const shown = scopes.slice(0, 3);
  const rest = scopes.length - shown.length;
  return (
    <Group gap={4}>
      {shown.map((scope) => (
        <Badge key={scope} size="xs" fz="xs" variant="light" color="gray">
          {scope}
        </Badge>
      ))}
      {rest > 0 && (
        <Badge size="xs" fz="xs" variant="outline" color="gray">
          {t('values.moreScopes', { count: rest })}
        </Badge>
      )}
    </Group>
  );
}
