'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  Loader,
  Select,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { IconAlertTriangle, IconShieldLock } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { usePairingRequest } from '@/lib/hooks/use-runners';
import { useOrgs } from '@/lib/hooks/use-orgs';
import { useAuth } from '@/lib/hooks/use-auth';
import {
  approvePairing,
  denyPairing,
  describePairingError,
  formatUserCode,
  normalizeUserCode,
} from '@/lib/api/runners';
import { orgDisplayLabel } from '@/lib/org-label';
import { formatRelative, runnerPlatformLabel } from '@/lib/utils/runners';
import type { Org } from '@/lib/api/orgs';
import { useT, useFormat } from '@/i18n';

export default function PairRunnerPage() {
  // useSearchParams needs a Suspense boundary above it in the App Router, so the page is a thin
  // shell and everything with state lives in the inner component.
  return (
    <Suspense fallback={<Center h={300}><Loader /></Center>}>
      <PairRunnerView />
    </Suspense>
  );
}

/**
 * The approval screen (E7-UI).
 *
 * This is the browser half of the RFC 8628 device flow: a machine that wants to join the workspace
 * shows the user a short code and sends them here with it in the query string (?code=XXXX-XXXX,
 * which is exactly the verificationUri the API returned from /runners/pair/start).
 *
 * TWO THINGS THIS SCREEN EXISTS TO GET RIGHT:
 *
 *  1. It is the anti-phishing control's human end. The classic abuse of a device flow is talking a
 *     victim into approving the ATTACKER's machine, which then receives the victim's media. So the
 *     warning is not decoration — it says, in the strongest terms the UI has, to approve only a
 *     code shown on a device the user can see, and it says that approving emails every Owner of
 *     the workspace, which is the control that catches a mistake.
 *  2. It reports the server's answers without improving on them. An unknown, expired, used or
 *     denied code, and a workspace the caller is not a member of, are all ONE answer each — the
 *     API refuses to distinguish them (D5), and this screen refuses too. In particular it never
 *     renders 'you do not have permission' where the server said 404.
 *
 * What it will NOT claim: a location. The API returns the IP the request came from and nothing
 * else — coarse geolocation was not implemented — so the address is shown raw, labelled as an
 * address.
 */
function PairRunnerView() {
  const t = useT('runners.pair');
  const search = useSearchParams();
  const router = useRouter();
  const codeParam = search.get('code') ?? '';
  const normalized = useMemo(() => normalizeUserCode(codeParam), [codeParam]);

  const [typed, setTyped] = useState('');
  const [typedError, setTypedError] = useState<string | null>(null);

  if (!normalized) {
    return (
      <CodeEntry
        typed={typed}
        setTyped={setTyped}
        typedError={typedError}
        onSubmit={() => {
          const code = normalizeUserCode(typed);
          if (!code) {
            setTypedError(t('code.invalid'));
            return;
          }
          setTypedError(null);
          router.replace('/runners/pair?code=' + encodeURIComponent(code));
        }}
      />
    );
  }

  return <ApprovalView userCode={normalized} />;
}

/** Where a user who followed a bare link (or typed the URL) lands: one field, and the next step. */
function CodeEntry({
  typed,
  setTyped,
  typedError,
  onSubmit,
}: {
  typed: string;
  setTyped: (value: string) => void;
  typedError: string | null;
  onSubmit: () => void;
}) {
  const t = useT('runners.pair');
  return (
    <>
      <PageHeader title={t('title')} />
      <Card withBorder maw={560}>
        <Stack gap="sm">
          <Title order={4}>{t('code.heading')}</Title>
          <Text size="sm" c="dimmed">
            {t('code.intro')}
          </Text>
          <TextInput
            label={t('code.label')}
            placeholder={t('code.placeholder')}
            value={typed}
            onChange={(e) => setTyped(e.currentTarget.value)}
            error={typedError}
            onKeyDown={(e) => {
              if (e.key === 'Enter') onSubmit();
            }}
            styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', letterSpacing: '0.1em' } }}
            aria-label={t('code.label')}
          />
          <Group justify="flex-end">
            <Button onClick={onSubmit}>{t('code.continue')}</Button>
          </Group>
        </Stack>
      </Card>
    </>
  );
}

/** A workspace a device may be approved into, by the server's own rule. */
function mayApproveInto(org: Org): boolean {
  // Mirrors api/handlers/runners/runners.go: anyone in their own Personal workspace, and an Owner
  // or Admin of a Team. The members list endpoint is not involved; the role comes from GET /orgs.
  return org.kind === 'Personal' || org.role === 'Owner' || org.role === 'Admin';
}

function ApprovalView({ userCode }: { userCode: string }) {
  const t = useT('runners.pair');
  const tr = useT();
  const f = useFormat();
  const { activeOrgId } = useAuth();
  const { data: pairing, isLoading, error } = usePairingRequest(userCode);
  const { data: orgs } = useOrgs();

  const eligible = useMemo(() => (orgs ?? []).filter(mayApproveInto), [orgs]);
  const [chosenOrgId, setChosenOrgId] = useState<string | null>(null);
  const defaultOrgId = useMemo(() => {
    if (eligible.some((org) => org.id === activeOrgId)) return activeOrgId ?? null;
    return eligible[0]?.id ?? null;
  }, [eligible, activeOrgId]);
  const orgId = chosenOrgId ?? defaultOrgId;

  const [working, setWorking] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<'approved' | 'denied' | null>(null);

  // A coarse clock, so the expiry countdown stays roughly true without a per-second re-render.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(timer);
  }, []);

  async function handleApprove() {
    if (!orgId) return;
    setWorking(true);
    setFailure(null);
    try {
      await approvePairing(userCode, orgId);
      setOutcome('approved');
    } catch (err: unknown) {
      setFailure(describePairingError(err));
    } finally {
      setWorking(false);
    }
  }

  async function handleDeny() {
    setWorking(true);
    setFailure(null);
    try {
      await denyPairing(userCode);
      setOutcome('denied');
    } catch (err: unknown) {
      setFailure(describePairingError(err));
    } finally {
      setWorking(false);
    }
  }

  if (outcome === 'approved') {
    return (
      <OutcomeCard
        title={t('approved.title')}
        color="green"
        body={t('approved.body')}
      />
    );
  }
  if (outcome === 'denied') {
    return (
      <OutcomeCard
        title={t('denied.title')}
        color="gray"
        body={t('denied.body')}
      />
    );
  }

  if (isLoading) {
    return <Center h={300}><Loader /></Center>;
  }

  if (error || !pairing) {
    return (
      <>
        <PageHeader title={t('title')} />
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />} title={t('notFound.title')}>
          {describePairingError(error)}
        </Alert>
        <Group mt="md">
          <Button component={Link} href="/runners/pair" variant="default">{t('notFound.another')}</Button>
          <Button component={Link} href="/runners" variant="subtle">{t('notFound.back')}</Button>
        </Group>
      </>
    );
  }

  const expired = Date.parse(pairing.expiresAt) <= now;

  return (
    <>
      <PageHeader
        title={t('approve.title')}
        breadcrumbs={[{ label: t('approve.breadcrumbRunners'), href: '/runners' }, { label: t('approve.breadcrumbPair') }]}
      />

      <Stack maw={640} gap="md">
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />} title={t('approve.warnTitle')}>
          <Text size="sm">
            {t('approve.warnBody1')}
          </Text>
          <Text size="sm" mt="xs">
            {t('approve.warnBody2')}
          </Text>
        </Alert>

        <Card withBorder>
          <Stack gap="sm">
            <Group justify="space-between">
              <Title order={4}>{pairing.name}</Title>
              <Badge
                variant="light"
                color="gray"
                styles={{ label: { fontFamily: 'var(--mantine-font-family-monospace)', letterSpacing: '0.1em' } }}
              >
                {pairing.userCode || formatUserCode(userCode)}
              </Badge>
            </Group>

            <Fact label={t('approve.platform')} value={runnerPlatformLabel(pairing.os, pairing.arch, tr)} />
            <Fact label={t('approve.version')} value={pairing.version || t('approve.notReported')} />
            <Fact
              label={t('approve.requested')}
              value={t('approve.withRelative', {
                date: new Date(pairing.requestedAt).toLocaleString(f.locale),
                when: formatRelative(pairing.requestedAt, now, tr),
              })}
            />
            <Fact
              label={t('approve.expires')}
              value={
                expired
                  ? t('approve.expiredValue', { when: formatRelative(pairing.expiresAt, now, tr) })
                  : t('approve.withRelative', {
                      date: new Date(pairing.expiresAt).toLocaleString(f.locale),
                      when: formatRelative(pairing.expiresAt, now, tr),
                    })
              }
            />
            {/*
              The IP and nothing else. There is no geolocation in this deployment, so the screen
              shows the address raw and calls it an address: 'near London' would be invented.
            */}
            <Fact
              label={t('approve.requestedFrom')}
              value={pairing.requestedFromIp || t('approve.notRecorded')}
              hint={t('approve.ipHint')}
            />
          </Stack>
        </Card>

        <Card withBorder>
          <Stack gap="sm">
            <Title order={4}>{t('workspace.heading')}</Title>
            <Text size="sm" c="dimmed">
              {t('workspace.intro')}
            </Text>

            {eligible.length === 0 ? (
              <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
                {t('workspace.none')}
              </Alert>
            ) : (
              <Select
                label={t('workspace.label')}
                value={orgId}
                onChange={setChosenOrgId}
                placeholder={t('workspace.placeholder')}
                data={eligible.map((org) => ({ value: org.id, label: orgDisplayLabel(org, tr) }))}
                allowDeselect={false}
              />
            )}
          </Stack>
        </Card>

        {failure && (
          <Alert color="red" icon={<IconAlertTriangle size={16} />} title={t('failedTitle')}>
            {failure}
          </Alert>
        )}

        <Group>
          <Button
            onClick={handleApprove}
            loading={working}
            disabled={!orgId || expired}
            leftSection={<IconShieldLock size={16} />}
          >
            {t('approveButton')}
          </Button>
          <Button variant="default" color="red" onClick={handleDeny} loading={working} disabled={expired}>
            {t('deny')}
          </Button>
          <Button component={Link} href="/runners" variant="subtle">{t('cancel')}</Button>
        </Group>
      </Stack>
    </>
  );
}

function Fact({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Group justify="space-between" align="flex-start" wrap="nowrap">
      <Text size="sm" c="dimmed" style={{ flex: '0 0 auto' }}>{label}</Text>
      <Stack gap={0} align="flex-end" style={{ minWidth: 0 }}>
        <Text size="sm" ta="right" style={{ wordBreak: 'break-word' }}>{value}</Text>
        {hint && (
          <Text size="xs" c="dimmed" ta="right">{hint}</Text>
        )}
      </Stack>
    </Group>
  );
}

function OutcomeCard({ title, body, color }: { title: string; body: string; color: string }) {
  const t = useT('runners.pair');
  return (
    <>
      <PageHeader title={title} />
      <Alert color={color} title={title}>{body}</Alert>
      <Group mt="md">
        <Button component={Link} href="/runners">{t('goToRunners')}</Button>
      </Group>
    </>
  );
}
