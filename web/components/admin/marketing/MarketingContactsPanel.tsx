'use client';

import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Center,
  Group,
  Loader,
  Modal,
  Pagination,
  Paper,
  Select,
  SimpleGrid,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconPencil, IconRefresh, IconSearch } from '@tabler/icons-react';
import { useFormat, useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { ApiError } from '@/lib/api/client';
import {
  contactsUrl,
  requestContactSyncRun,
  updateContactName,
  type ContactBrevoState,
  type ContactStatusFilter,
  type MarketingContact,
} from '@/lib/api/marketingContacts';
import { useContactSyncStatus, useMarketingContacts } from '@/lib/hooks/use-marketing';

const PAGE_SIZE = 50;
const STATUS_FILTERS: ContactStatusFilter[] = ['all', 'optedIn', 'pending', 'withdrawn'];
const BREVO_COLORS: Record<ContactBrevoState, string> = {
  synced: 'green',
  pending: 'yellow',
  failed: 'red',
  notAtBrevo: 'gray',
};

function Count({ label, value, testId }: { label: string; value: number; testId: string }) {
  const f = useFormat();
  return (
    <Paper withBorder radius="md" p="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
        {label}
      </Text>
      <Text size="xl" fw={700} data-testid={testId}>
        {f.number(value)}
      </Text>
    </Paper>
  );
}

function SyncCard() {
  const t = useT('marketingContacts.sync');
  const tAll = useT();
  const f = useFormat();
  const status = useContactSyncStatus();
  const [running, setRunning] = useState(false);
  const [message, setMessage] = useState<{ color: 'red' | 'green'; text: string } | null>(null);

  const syncNow = async () => {
    setRunning(true);
    setMessage(null);
    try {
      await requestContactSyncRun();
      setMessage({ color: 'green', text: t('requested') });
      void status.mutate();
    } catch (error) {
      const code = error instanceof ApiError ? error.code : null;
      const text =
        code === 'sync_disabled' ? t('errDisabled') : code === 'rate_limited' ? t('errRateLimited') : t('errGeneric');
      setMessage({ color: 'red', text });
    } finally {
      setRunning(false);
    }
  };

  if (status.error) {
    return (
      <Alert color="red" variant="light" title={t('loadError')} data-testid="contact-sync-error">
        {describeError(tAll, status.error)}
      </Alert>
    );
  }
  if (!status.data) {
    return (
      <Center h={120}>
        <Loader />
      </Center>
    );
  }
  const s = status.data;
  const dateTime = (iso: string) => f.date(iso, 'dateTime');

  return (
    <Paper withBorder radius="md" p="md" data-testid="contact-sync-card">
      <Stack gap="sm">
        <Group justify="space-between" align="center">
          <Title order={4}>{t('title')}</Title>
          <Button
            leftSection={<IconRefresh size={16} />}
            onClick={() => void syncNow()}
            loading={running}
            disabled={running}
            data-testid="contact-sync-now"
          >
            {t('syncNow')}
          </Button>
        </Group>
        {!s.enabled ? (
          <Text size="sm" data-testid="contact-sync-disabled">
            {t('disabled')}
          </Text>
        ) : !s.configured ? (
          <Text size="sm" data-testid="contact-sync-not-configured">
            {t('notConfigured')}
          </Text>
        ) : null}
        {s.campaignActive ? (
          <Alert color="blue" variant="light" data-testid="contact-sync-campaign">
            {t('campaignActive')}
          </Alert>
        ) : null}
        <Group gap="xs" data-testid="contact-sync-last-run">
          <Text size="sm" c="dimmed">
            {s.lastRunAt ? t('lastRun', { time: dateTime(s.lastRunAt) }) : t('neverRun')}
          </Text>
          {s.lastRunAt && s.lastRunOk === true ? (
            <Badge color="green" variant="light">
              {t('runOk')}
            </Badge>
          ) : null}
          {s.lastRunAt && s.lastRunOk === false ? (
            <Badge color="red" variant="light">
              {s.lastErrorCode ? t('runFailed', { code: s.lastErrorCode }) : t('runFailedNoCode')}
            </Badge>
          ) : null}
        </Group>
        <Text size="sm" c="dimmed" data-testid="contact-sync-reconcile">
          {s.lastReconcileAt ? t('lastReconcile', { time: dateTime(s.lastReconcileAt) }) : t('neverReconciled')}
        </Text>
        <SimpleGrid cols={{ base: 1, xs: 3 }} spacing="md">
          <Count label={t('synced')} value={s.counts.synced} testId="contact-sync-count-synced" />
          <Count label={t('waiting')} value={s.counts.pendingPush} testId="contact-sync-count-waiting" />
          <Count label={t('failed')} value={s.counts.failed} testId="contact-sync-count-failed" />
        </SimpleGrid>
        {message ? (
          <Alert color={message.color} variant="light" role="status" data-testid="contact-sync-message">
            {message.text}
          </Alert>
        ) : null}
      </Stack>
    </Paper>
  );
}

function EditNameModal({
  contact,
  onClose,
  onSaved,
}: {
  contact: MarketingContact;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useT('marketingContacts.edit');
  const [firstName, setFirstName] = useState(contact.firstName ?? '');
  const [lastName, setLastName] = useState(contact.lastName ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await updateContactName(contact.id, {
        firstName: firstName.trim() || null,
        lastName: lastName.trim() || null,
      });
      notifications.show({ message: t('saved'), color: 'green' });
      onSaved();
      onClose();
    } catch (e) {
      setError(e instanceof ApiError && e.code === 'invalid_name' ? t('invalid') : t('error'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title')} centered>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          {contact.email}
        </Text>
        <TextInput
          label={t('firstName')}
          value={firstName}
          maxLength={100}
          onChange={(e) => setFirstName(e.currentTarget.value)}
          data-testid="contact-edit-first-name"
        />
        <TextInput
          label={t('lastName')}
          value={lastName}
          maxLength={100}
          onChange={(e) => setLastName(e.currentTarget.value)}
          data-testid="contact-edit-last-name"
        />
        <Text size="xs" c="dimmed">
          {t('hint')}
        </Text>
        {error ? (
          <Alert color="red" variant="light" role="alert" data-testid="contact-edit-error">
            {error}
          </Alert>
        ) : null}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={saving}>
            {t('cancel')}
          </Button>
          <Button onClick={() => void save()} loading={saving} data-testid="contact-edit-save">
            {t('save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function ContactsTable() {
  const t = useT('marketingContacts');
  const tAll = useT();
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 300);
  const [status, setStatus] = useState<ContactStatusFilter>('all');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<MarketingContact | null>(null);

  const url = contactsUrl({ status, q: debounced, limit: PAGE_SIZE, offset: (page - 1) * PAGE_SIZE });
  const contacts = useMarketingContacts(url);
  const total = contacts.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <Stack gap="md" data-testid="marketing-contacts-table">
      <Group align="flex-end" grow preventGrowOverflow={false}>
        <TextInput
          label={t('filters.search')}
          leftSection={<IconSearch size={16} />}
          value={search}
          onChange={(e) => {
            setSearch(e.currentTarget.value);
            setPage(1);
          }}
          maxLength={200}
          data-testid="contacts-search"
          style={{ minWidth: 200 }}
        />
        <Select
          label={t('filters.status')}
          allowDeselect={false}
          value={status}
          data={STATUS_FILTERS.map((value) => ({ value, label: t(`filters.${value}`) }))}
          onChange={(value) => {
            if (value) {
              setStatus(value as ContactStatusFilter);
              setPage(1);
            }
          }}
          data-testid="contacts-status"
          style={{ minWidth: 160 }}
        />
      </Group>

      {contacts.error ? (
        <Alert color="red" variant="light" title={t('table.loadError')} data-testid="contacts-error">
          {describeError(tAll, contacts.error)}
        </Alert>
      ) : !contacts.data ? (
        <Center h={120}>
          <Loader />
        </Center>
      ) : contacts.data.items.length === 0 ? (
        <Text size="sm" c="dimmed" data-testid="contacts-empty">
          {t('table.empty')}
        </Text>
      ) : (
        <>
          <Text size="sm" c="dimmed" data-testid="contacts-total">
            {t('table.total', { count: total })}
          </Text>
          <Table.ScrollContainer minWidth={640}>
            <Table verticalSpacing="xs" aria-label={t('table.caption')}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">{t('table.email')}</Table.Th>
                  <Table.Th scope="col">{t('table.firstName')}</Table.Th>
                  <Table.Th scope="col">{t('table.lastName')}</Table.Th>
                  <Table.Th scope="col">{t('table.status')}</Table.Th>
                  <Table.Th scope="col">{t('table.source')}</Table.Th>
                  <Table.Th scope="col">{t('table.brevo')}</Table.Th>
                  <Table.Th scope="col">{t('table.actions')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {contacts.data.items.map((c) => (
                  <Table.Tr key={c.id} data-testid={`contact-row-${c.id}`}>
                    <Table.Td>{c.email}</Table.Td>
                    <Table.Td>{c.firstName ?? <Text span c="dimmed">{t('table.noName')}</Text>}</Table.Td>
                    <Table.Td>{c.lastName ?? <Text span c="dimmed">{t('table.noName')}</Text>}</Table.Td>
                    <Table.Td data-testid={`contact-status-${c.id}`}>
                      <Badge variant="light" color="gray">
                        {c.status === 'optedIn' || c.status === 'pending' || c.status === 'withdrawn'
                          ? t(`filters.${c.status}`)
                          : c.status}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      {c.nameSource ? (
                        <Badge variant="light" color="gray">
                          {t(`source.${c.nameSource}`)}
                        </Badge>
                      ) : null}
                    </Table.Td>
                    <Table.Td>
                      <Badge variant="light" color={BREVO_COLORS[c.brevo.state] ?? 'gray'}>
                        {t(`brevoState.${c.brevo.state}`)}
                      </Badge>
                      {c.brevo.errorCode ? (
                        <Text size="xs" c="red" data-testid={`contact-brevo-error-${c.id}`}>
                          {t('table.brevoError', { code: c.brevo.errorCode })}
                        </Text>
                      ) : null}
                    </Table.Td>
                    <Table.Td>
                      <Button
                        size="compact-sm"
                        variant="subtle"
                        leftSection={<IconPencil size={14} />}
                        aria-label={t('table.editFor', { email: c.email })}
                        onClick={() => setEditing(c)}
                        data-testid={`contact-edit-${c.id}`}
                      >
                        {t('table.edit')}
                      </Button>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          {pages > 1 ? (
            <Pagination value={page} onChange={setPage} total={pages} aria-label={t('table.page')} />
          ) : null}
        </>
      )}

      {editing ? (
        <EditNameModal
          key={editing.id}
          contact={editing}
          onClose={() => setEditing(null)}
          onSaved={() => void contacts.mutate()}
        />
      ) : null}
    </Stack>
  );
}

/** Platform admin: the Brevo contact-sync status and the marketing contacts with their names. */
export function MarketingContactsPanel() {
  const t = useT('marketingContacts');
  return (
    <Stack gap="md" data-testid="marketing-contacts-panel">
      <Title order={3}>{t('title')}</Title>
      <SyncCard />
      <ContactsTable />
    </Stack>
  );
}
