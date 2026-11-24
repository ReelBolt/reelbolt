'use client';

import { useState } from 'react';
import { Alert, Badge, Button, Group, Loader, Modal, Pagination, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconCheck, IconPlus, IconSearch } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { QuietText } from '@/components/dashboard/Panel';
import { ApiError } from '@/lib/api/client';
import { createLibraryVoice } from '@/lib/api/voices';
import { useVoiceLibrary } from '@/lib/hooks/use-voices';
import { VOICE_LIBRARY_NOT_SUPPORTED, type VoiceLibraryItem } from '@/lib/types/voices';
import { useFormat, useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import type { T } from '@/i18n/translator';

/** Language filter values are Fish's language codes; the names come from Intl in the active language. */
const LANGUAGE_CODES = ['en', 'zh', 'ja', 'ko', 'es', 'fr', 'de', 'it', 'pt', 'ru', 'ar'];

function languageOptions(locale: string) {
  const names = new Intl.DisplayNames(locale === 'en-XA' ? 'en' : locale, { type: 'language' });
  return LANGUAGE_CODES.map((value) => {
    const name = names.of(value) ?? value;
    return { value, label: name.charAt(0).toLocaleUpperCase(locale) + name.slice(1) };
  });
}

const SEARCH_DEBOUNCE_MS = 300;

/** The API answers 400 for a page above this (ProjectVoicesController.MaxLibraryPage). */
export const MAX_LIBRARY_PAGE = 200;

function errorText(t: T, tAll: T, e: unknown): string {
  if (e instanceof ApiError && e.code === VOICE_LIBRARY_NOT_SUPPORTED) {
    return t('notSupported');
  }
  return describeError(tAll, e, t('loadFailed'));
}

/**
 * "Browse voices": search and page through the speech provider's public voice library and add a
 * voice to the project with one click (the same register-library-voice call as "Add library
 * voice"). `registeredIds` are the provider voice ids the project already has.
 */
export function VoiceLibraryBrowser({
  projectId, opened, onClose, onAdded, registeredIds,
}: {
  projectId: string;
  opened: boolean;
  onClose: () => void;
  onAdded: () => void;
  registeredIds: ReadonlySet<string>;
}) {
  const t = useT('voices.browser');
  const tAll = useT();
  const f = useFormat();
  const [search, setSearch] = useState('');
  const [language, setLanguage] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [addingId, setAddingId] = useState<string | null>(null);
  const [addedIds, setAddedIds] = useState<ReadonlySet<string>>(new Set());
  const [debounced] = useDebouncedValue(search, SEARCH_DEBOUNCE_MS);

  const { data, error, isLoading } = useVoiceLibrary(projectId, opened, debounced, language, page);
  const items = data?.items ?? [];
  const pages = data ? Math.min(MAX_LIBRARY_PAGE, Math.max(1, Math.ceil(data.total / Math.max(1, data.pageSize)))) : 1;

  const add = async (voice: VoiceLibraryItem) => {
    setAddingId(voice.voiceId);
    try {
      await createLibraryVoice(projectId, {
        name: voice.name,
        remoteVoiceId: voice.voiceId,
        description: voice.tags.length > 0 ? voice.tags.join(', ') : null,
      });
      setAddedIds((prev) => new Set(prev).add(voice.voiceId));
      notifications.show({ color: 'green', message: t('added', { name: voice.name }) });
      onAdded();
    } catch (e) {
      notifications.show({ color: 'red', message: describeError(tAll, e, t('addFailed')) });
    } finally {
      setAddingId(null);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('title')} size="xl">
      <Stack gap="sm">
        <QuietText>{t('intro')}</QuietText>
        <Group gap="sm" align="flex-end" wrap="wrap">
          <TextInput
            label={t('search')}
            placeholder={t('searchPlaceholder')}
            leftSection={<IconSearch size={14} />}
            value={search}
            onChange={(e) => { setSearch(e.currentTarget.value); setPage(1); }}
            maxLength={100}
            style={{ flex: '1 1 14rem' }}
          />
          <Select
            label={t('language')}
            data={languageOptions(f.locale)}
            value={language}
            onChange={(v) => { setLanguage(v); setPage(1); }}
            clearable
            placeholder={t('anyLanguage')}
            style={{ flex: '0 1 12rem' }}
          />
        </Group>

        {error ? (
          <Alert color="red" variant="light">{errorText(t, tAll, error)}</Alert>
        ) : isLoading && !data ? (
          <Group justify="center" py="lg"><Loader size="sm" /></Group>
        ) : items.length === 0 ? (
          <QuietText>{t('noMatch')}</QuietText>
        ) : (
          <Table.ScrollContainer minWidth={520}>
            <Table striped highlightOnHover withTableBorder>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('colVoice')}</Table.Th>
                  <Table.Th>{t('colLanguages')}</Table.Th>
                  <Table.Th>{t('colTags')}</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {items.map((v) => {
                  const added = addedIds.has(v.voiceId) || registeredIds.has(v.voiceId);
                  return (
                    <Table.Tr key={v.voiceId} data-testid={`library-voice-${v.voiceId}`}>
                      <Table.Td>
                        <Text size="sm" fw={600}>{v.name}</Text>
                        <Text size="xs" c="dimmed" ff="monospace">{v.voiceId}</Text>
                      </Table.Td>
                      <Table.Td>
                        <Group gap={4}>{v.languages.map((l) => <Badge key={l} variant="light" color="blue">{l}</Badge>)}</Group>
                      </Table.Td>
                      <Table.Td>
                        <Group gap={4}>{v.tags.map((tag) => <Badge key={tag} variant="light" color="gray">{tag}</Badge>)}</Group>
                      </Table.Td>
                      <Table.Td>
                        <Button
                          size="compact-sm"
                          variant={added ? 'subtle' : 'default'}
                          leftSection={added ? <IconCheck size={12} /> : <IconPlus size={12} />}
                          disabled={added || addingId !== null}
                          loading={addingId === v.voiceId}
                          onClick={() => void add(v)}
                          aria-label={added ? t('addedLabel', { name: v.name }) : t('addLabel', { name: v.name })}
                        >
                          {added ? t('addedButton') : t('addButton')}
                        </Button>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}

        {pages > 1 && (
          <Group justify="center">
            <Pagination total={pages} value={page} onChange={setPage} size="sm" />
          </Group>
        )}
      </Stack>
    </Modal>
  );
}
