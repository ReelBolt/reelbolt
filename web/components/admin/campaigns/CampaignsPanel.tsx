'use client';

import { Fragment, useState } from 'react';
import useSWR from 'swr';
import { Alert, Badge, Button, Card, Code, Group, Modal, Stack, Table, Text, TextInput, Textarea, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import {
  ACTIVE_STATUSES,
  CAMPAIGNS_URL,
  CAMPAIGN_SETTINGS_URL,
  MAX_SUBJECT_LENGTH,
  STATUS_LABELS,
  STAT_COLUMNS,
  codeMessage,
  duplicateCampaign,
  resolveCampaign,
  campaignMessage,
  createCampaign,
  dryRunCampaign,
  getCampaign,
  getCampaignSettings,
  listCampaigns,
  refreshCampaignResults,
  sendBlockReason,
  sendCampaign,
  statValue,
  testSendCampaign,
  updateCampaign,
  type Campaign,
  type CampaignDryRun,
} from '@/lib/api/marketingCampaigns';

const POLL_MS = 5000;
const fmt = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : 'n/a');

const hasActions = (c: Campaign): boolean => c.status === 'draft' || c.status === 'sending' || c.status === 'sent' || c.status === 'failed';

export function CampaignsPanel() {
  const list = useSWR(CAMPAIGNS_URL, listCampaigns, {
    refreshInterval: (rows) => (rows?.some((c) => ACTIVE_STATUSES.includes(c.status)) ? POLL_MS : 0),
  });
  const settings = useSWR(CAMPAIGN_SETTINGS_URL, getCampaignSettings);

  const [current, setCurrent] = useState<Campaign | null>(null);
  const [subject, setSubject] = useState('');
  const [html, setHtml] = useState('');
  const [dryRun, setDryRun] = useState<{ result: CampaignDryRun; updatedAt: string } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [typed, setTyped] = useState('');
  const [resolving, setResolving] = useState<{ id: string; outcome: 'sent' | 'notSent' } | null>(null);

  // Follow the saved row as polling refreshes it (list rows carry no HTML, so keep the loaded one).
  const row = current ? list.data?.find((c) => c.id === current.id) : undefined;
  const saved = current ? (row ? { ...row, html: current.html, previewHtml: current.previewHtml, previewError: current.previewError } : current) : null;

  const listFailed = list.error instanceof ApiError ? list.error.status !== 404 : !!list.error;
  if (list.error instanceof ApiError && list.error.status === 404) {
    return (
      <Alert color="gray" variant="light" title="Campaigns are not enabled" data-testid="campaigns-disabled">
        Marketing campaigns are switched off on this deployment.
      </Alert>
    );
  }

  const cfg = settings.data;
  const dirty = !saved || subject.trim() !== saved.subject.trim() || html !== (saved.html ?? '');
  const dryRunFresh = !!saved && !dirty && dryRun !== null && dryRun.updatedAt === saved.updatedAt && dryRun.result.errors.length === 0;
  const reason = saved
    ? sendBlockReason({ listSendEnabled: !!cfg?.listSendEnabled, testCurrent: saved.testCurrent, dryRunFresh, dirty })
    : 'Save a draft first.';
  const editable = !saved || saved.status === 'draft';

  async function run<T>(name: string, fn: () => Promise<T>, after?: (v: T) => void) {
    setBusy(name);
    try {
      const value = await fn();
      after?.(value);
      await list.mutate();
    } catch (error) {
      notifications.show({ title: 'Not done', message: campaignMessage(error), color: 'red' });
    } finally {
      setBusy(null);
    }
  }

  const save = () =>
    run('save', () => (saved ? updateCampaign(saved.id, subject, html) : createCampaign(subject, html)), (row) => {
      setCurrent(row);
      setDryRun(null);
      notifications.show({ title: 'Draft saved', message: 'The draft was saved.', color: 'green' });
    });
  const edit = (c: Campaign) => {
    setCurrent(c);
    setSubject(c.subject);
    setHtml(c.html ?? '');
    setDryRun(null);
  };
  const open = (c: Campaign) =>
    run('open', () => getCampaign(c.id), edit);

  const previewDoc = !dirty && saved?.previewHtml ? saved.previewHtml : html;
  const count = dryRun?.result.recipientCount ?? 0;

  return (
    <Stack gap="lg" data-testid="campaigns-panel">
      <Card withBorder>
        <Group justify="space-between" mb="sm">
          <Title order={4}>History</Title>
          <Button variant="default" onClick={() => { setCurrent(null); setSubject(''); setHtml(''); setDryRun(null); }}>
            New campaign
          </Button>
        </Group>
        {listFailed && !list.data ? (
          <Alert color="red" variant="light" title="The campaigns could not be loaded" data-testid="campaigns-list-error">
            <Group justify="space-between">
              <Text size="sm">{campaignMessage(list.error)}</Text>
              <Button size="compact-sm" variant="default" onClick={() => void list.mutate()}>Retry</Button>
            </Group>
          </Alert>
        ) : (list.data ?? []).length === 0 ? (
          <Text c="dimmed">No campaigns yet.</Text>
        ) : (
          <Table.ScrollContainer minWidth={640}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Subject</Table.Th>
                  <Table.Th>Status</Table.Th>
                  <Table.Th>Recipients</Table.Th>
                  {STAT_COLUMNS.map((s) => <Table.Th key={s.key}>{s.label}</Table.Th>)}
                  <Table.Th>When</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {list.data?.map((c) => (
                  <Fragment key={c.id}>
                  <Table.Tr style={hasActions(c) ? { borderBottom: 'none' } : undefined}>
                    <Table.Td miw={180}>{c.subject}</Table.Td>
                    <Table.Td>
                      <Badge variant="light" color={c.status === 'failed' || c.status === 'sendUnknown' ? 'red' : 'gray'}>{STATUS_LABELS[c.status] ?? 'Unknown'}</Badge>
                      {c.status === 'failed' && c.failureCode && (
                        <Text size="xs" c="red" mt={4} data-testid="failure-reason">{codeMessage(c.failureCode)}</Text>
                      )}
                    </Table.Td>
                    <Table.Td>{c.finalRecipientCount ?? c.confirmedRecipientCount ?? 'n/a'}</Table.Td>
                    {STAT_COLUMNS.map((s) => <Table.Td key={s.key}>{statValue(c.stats, s.keys) ?? 'n/a'}</Table.Td>)}
                    <Table.Td>{fmt(c.dispatchStartedAt ?? c.createdAt)}</Table.Td>
                  </Table.Tr>
                  {hasActions(c) && (
                    <Table.Tr data-testid="campaign-actions-row">
                      <Table.Td colSpan={4 + STAT_COLUMNS.length} pt={0}>
                        <Group gap="xs" wrap="wrap" maw={300} style={{ position: 'sticky', left: 0 }}>
                        {c.status === 'draft' && <Button size="compact-sm" variant="default" onClick={() => void open(c)}>Edit</Button>}
                        {(c.status === 'sending' || c.status === 'sent') && (
                          <Button
                            size="compact-sm"
                            variant="default"
                            loading={busy === `refresh-${c.id}`}
                            onClick={() => void run(`refresh-${c.id}`, () => refreshCampaignResults(c.id))}
                          >
                            Refresh results
                          </Button>
                        )}
                        {(c.status === 'failed' || c.status === 'sent') && (
                          <Button
                            size="compact-sm"
                            variant="default"
                            loading={busy === `dup-${c.id}`}
                            onClick={() => void run(`dup-${c.id}`, () => duplicateCampaign(c.id), (copy) => {
                              edit({ ...copy, html: copy.html ?? '' });
                              notifications.show({ title: 'Draft created', message: 'A copy was saved as a new draft.', color: 'green' });
                            })}
                          >
                            Duplicate as draft
                          </Button>
                        )}
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  )}
                  </Fragment>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
        {(list.data ?? []).filter((c) => c.status === 'sendUnknown').map((c) => (
          <Alert key={c.id} color="red" variant="light" mt="sm" title={`Send unknown: ${c.subject}`} data-testid="send-unknown-warning">
            <Stack gap="xs">
              <Text size="sm">
                This send may or may not have reached Brevo. Check Brevo before anything else. It is never resent from here,
                and another send is refused until this one is resolved.
              </Text>
              <Group gap="xs">
                <Button size="compact-sm" variant="default" loading={busy === `refresh-${c.id}`}
                  onClick={() => void run(`refresh-${c.id}`, () => refreshCampaignResults(c.id))}>
                  Refresh results
                </Button>
                <Button size="compact-sm" variant="default" onClick={() => setResolving({ id: c.id, outcome: 'sent' })}>
                  I checked Brevo: it was sent
                </Button>
                <Button size="compact-sm" variant="default" onClick={() => setResolving({ id: c.id, outcome: 'notSent' })}>
                  I checked Brevo: it was not sent
                </Button>
              </Group>
            </Stack>
          </Alert>
        ))}
      </Card>

      <Card withBorder>
        <Title order={4} mb="sm">{saved ? 'Edit draft' : 'Compose'}</Title>
        <Stack>
          <TextInput
            label="Subject"
            value={subject}
            maxLength={MAX_SUBJECT_LENGTH}
            disabled={!editable}
            onChange={(e) => setSubject(e.currentTarget.value)}
            description={`${subject.length}/${MAX_SUBJECT_LENGTH}`}
          />
          <Textarea label="HTML" value={html} minRows={8} disabled={!editable} onChange={(e) => setHtml(e.currentTarget.value)} />
          <Text size="sm" c="dimmed">The footer and the unsubscribe link are added automatically when the email is sent.</Text>
          <div>
            <Text size="sm" fw={500} mb={4}>Preview</Text>
            {dirty ? (
              <Text size="sm" c="dimmed" mb={4}>Showing your unsaved HTML. The footer is added when you save.</Text>
            ) : saved?.previewError ? (
              <Alert color="red" variant="light" mb={4} data-testid="preview-error">{codeMessage(saved.previewError)}</Alert>
            ) : null}
            <iframe
              title="Email preview"
              sandbox=""
              srcDoc={previewDoc}
              data-testid="campaign-preview"
              style={{ width: '100%', height: 360, border: '1px solid var(--mantine-color-default-border)', background: '#fff' }}
            />
          </div>
          <Group>
            <Button onClick={() => void save()} loading={busy === 'save'} disabled={!editable || !subject.trim() || !html.trim() || !dirty}>
              Save draft
            </Button>
          </Group>
        </Stack>
      </Card>

      {saved && saved.status === 'draft' && (
        <Card withBorder>
          <Title order={4} mb="sm">Check and send</Title>
          <Stack>
            <Group align="flex-start">
              <Button variant="default" loading={busy === 'dry'} disabled={dirty}
                onClick={() => void run('dry', () => dryRunCampaign(saved.id), (r) => setDryRun({ result: r, updatedAt: saved.updatedAt }))}>
                Dry run
              </Button>
              <Button variant="default" loading={busy === 'test'} disabled={dirty}
                onClick={() => void run('test', () => testSendCampaign(saved.id), () => notifications.show({ title: 'Test sent', message: 'The test email was sent.', color: 'green' }))}>
                Send test
              </Button>
            </Group>
            <Text size="sm" data-testid="test-recipient">
              Test emails go to <Code>{cfg?.testRecipientMasked ?? 'no test recipient configured'}</Code>. The recipient is set on the server and cannot be changed here.
            </Text>
            {dryRun && dryRun.updatedAt === saved.updatedAt && (
              <Stack gap={4} data-testid="dry-run-result">
                <Text>Recipients: <b>{dryRun.result.recipientCount}</b>. Suppressed: <b>{dryRun.result.suppressedCount}</b>.</Text>
                {dryRun.result.sampleMasked.length > 0 && <Text size="sm" c="dimmed">Sample: {dryRun.result.sampleMasked.join(', ')}</Text>}
                {dryRun.result.errors.map((e) => <Alert key={e} color="red" variant="light">{codeMessage(e)}</Alert>)}
              </Stack>
            )}
            {reason && <Text size="sm" c="dimmed" data-testid="send-reason">{reason}</Text>}
            <div>
              <Button color="red" disabled={reason !== null} onClick={() => { setTyped(''); setConfirmOpen(true); }} data-testid="send-button">
                Send to the list
              </Button>
            </div>
          </Stack>
        </Card>
      )}

      <Modal
        opened={resolving !== null}
        onClose={() => setResolving(null)}
        title="Confirm what happened"
        styles={{ inner: { paddingTop: 80 } }}
      >
        <Stack>
          <Text>
            {resolving?.outcome === 'sent'
              ? 'Mark this campaign as sent. Only do this if you checked Brevo and the email went out.'
              : 'Mark this campaign as not sent. Only do this if you checked Brevo and nothing went out. It is not resent automatically.'}
          </Text>
          <Button
            color="red"
            loading={busy === 'resolve'}
            onClick={() => resolving && void run('resolve', () => resolveCampaign(resolving.id, resolving.outcome), () => setResolving(null))}
          >
            {resolving?.outcome === 'sent' ? 'Yes, it was sent' : 'Yes, it was not sent'}
          </Button>
        </Stack>
      </Modal>

      <Modal opened={confirmOpen} onClose={() => setConfirmOpen(false)} title="Send to the list" styles={{ inner: { paddingTop: 80 } }}>
        <Stack>
          <Text>This sends the campaign to <b>{count}</b> recipients and cannot be undone. Type {count} to confirm.</Text>
          <TextInput aria-label="Recipient count" value={typed} onChange={(e) => setTyped(e.currentTarget.value)} />
          <Button
            color="red"
            disabled={typed.trim() !== String(count)}
            loading={busy === 'send'}
            onClick={() => saved && void run('send', () => sendCampaign(saved.id, count), () => { setConfirmOpen(false); setCurrent(null); setSubject(''); setHtml(''); setDryRun(null); })}
          >
            Send {count} emails
          </Button>
        </Stack>
      </Modal>
    </Stack>
  );
}
