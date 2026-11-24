'use client';

import { useMemo, useState } from 'react';
import {
  Alert, Anchor, Badge, Button, Group, Loader, ScrollArea, Stack, Text, TextInput,
} from '@mantine/core';
import { IconAlertTriangle, IconBrandGithub, IconExternalLink, IconLock, IconRefresh, IconSearch } from '@tabler/icons-react';
import { QuietText } from '@/components/dashboard/Panel';
import { describeGitHubRepositoryListError } from '@/lib/api/github';
import { useGitHubRepositories } from '@/lib/hooks/use-github-repositories';
import type { GitHubRepositoryOption } from '@/lib/types/github';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';

/**
 * One account's repositories, as GitHub grouped them. The account is the thing that owns the App
 * installation, so it is what the reader is actually choosing between when several are listed.
 */
export interface RepositoryGroup {
  accountLogin: string;
  accountType: string;
  repositorySelection: string | null;
  manageUrl: string;
  repositories: GitHubRepositoryOption[];
}

/**
 * Groups by account, in a stable order.
 *
 * The order is alphabetical rather than "most recently pushed", because the list must not reshuffle
 * between two renders: a picker that reorders under the cursor is how somebody links the wrong
 * repository. Within an account the same applies to the repositories.
 */
export function groupRepositories(repositories: GitHubRepositoryOption[]): RepositoryGroup[] {
  const groups = new Map<string, RepositoryGroup>();

  for (const repository of repositories) {
    const existing = groups.get(repository.accountLogin);
    if (existing) {
      existing.repositories.push(repository);
      continue;
    }

    groups.set(repository.accountLogin, {
      accountLogin: repository.accountLogin,
      accountType: repository.accountType,
      repositorySelection: repository.repositorySelection,
      manageUrl: repository.manageUrl,
      repositories: [repository],
    });
  }

  return [...groups.values()]
    .sort((a, b) => a.accountLogin.localeCompare(b.accountLogin))
    .map((group) => ({
      ...group,
      repositories: [...group.repositories].sort((a, b) => a.fullName.localeCompare(b.fullName)),
    }));
}

/**
 * Narrows the list to what matches the typed text, across the whole `owner/name`. Filtering happens
 * here rather than on the server because the list is already in hand and a round trip per keystroke
 * would be slower than the thing it filters.
 */
export function filterRepositories(
  repositories: GitHubRepositoryOption[],
  query: string,
): GitHubRepositoryOption[] {
  const needle = query.trim().toLowerCase();
  if (!needle) return repositories;
  return repositories.filter((r) => r.fullName.toLowerCase().includes(needle));
}

/** A repository row's supporting line: what makes it private, and whether it is already taken. */
export function describeRepository(repository: GitHubRepositoryOption): Msg {
  if (repository.linkedProjectId) return msg('integrations.github.repos.alreadyLinked');
  return repository.isPrivate ? msg('integrations.github.repos.private') : msg('integrations.github.repos.public');
}

/**
 * The repository half of "link a repository": a list to choose from, not a text box.
 *
 * Every entry came from GitHub, through this person's own connected account, so the list cannot
 * contain a repository they cannot open. A repository that is missing is missing because ReelBolt was
 * not given access to it on GitHub, and the answer to that is GitHub's own page for giving access,
 * which is offered here (and when the list is empty) rather than left to be discovered.
 */
export function GitHubRepositoryPicker({
  projectId,
  value,
  onChange,
  enabled = true,
}: {
  projectId: string;
  value: GitHubRepositoryOption | null;
  onChange: (repository: GitHubRepositoryOption) => void;
  enabled?: boolean;
}) {
  const t = useT('integrations.github.repos');
  const { data, isLoading, error, mutate } = useGitHubRepositories(projectId, enabled);
  const [query, setQuery] = useState('');

  const matches = useMemo(() => filterRepositories(data?.repositories ?? [], query), [data, query]);
  const groups = useMemo(() => groupRepositories(matches), [matches]);

  if (isLoading) {
    return (
      <Group gap="xs" wrap="nowrap">
        <Loader size="xs" />
        <Text size="sm" c="dimmed">{t('reading')}</Text>
      </Group>
    );
  }

  if (error) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t('readFailed')}>
        <Stack gap="sm" align="flex-start">
          <Text size="sm">{describeGitHubRepositoryListError(error)}</Text>
          <Button variant="default" size="sm" style={{ minHeight: 44 }} onClick={() => void mutate()}>{t('tryAgain')}</Button>
        </Stack>
      </Alert>
    );
  }

  const total = data?.repositories.length ?? 0;

  return (
    <Stack gap="sm">
      {total === 0 ? (
        <Stack gap="xs">
          <Text size="sm">
            {t('noAccess')}
          </Text>
          <QuietText>
            {t('noAccessHint')}
          </QuietText>
        </Stack>
      ) : (
        <>
          <TextInput
            label={t('findLabel')}
            placeholder={t('findPlaceholder')}
            value={query}
            leftSection={<IconSearch size={16} />}
            onChange={(e) => setQuery(e.currentTarget.value)}
            description={t('count', { count: total })}
          />

          {matches.length === 0 ? (
            <Text size="sm" c="dimmed">{t('noMatch', { query })}</Text>
          ) : (
            <ScrollArea.Autosize mah={320} type="auto">
              <Stack gap="md">
                {groups.map((group) => (
                  <Stack key={group.accountLogin} gap={6}>
                    <Group gap={6} wrap="nowrap">
                      <IconBrandGithub size={14} />
                      <Text size="xs" fw={650} tt="uppercase" c="dimmed">{group.accountLogin}</Text>
                      {group.accountType === 'Organization' && <Badge size="xs" variant="light" color="gray">{t('organization')}</Badge>}
                    </Group>

                    {group.repositories.map((repository) => {
                      const selected = value?.id === repository.id;
                      return (
                        <Button
                          key={repository.id}
                          variant={selected ? 'light' : 'default'}
                          color={selected ? 'blue' : undefined}
                          justify="space-between"
                          fullWidth
                          style={{ minHeight: 44, height: 'auto', paddingTop: 8, paddingBottom: 8 }}
                          onClick={() => onChange(repository)}
                          disabled={Boolean(repository.linkedProjectId)}
                          aria-pressed={selected}
                          rightSection={repository.isPrivate ? <IconLock size={14} /> : undefined}
                        >
                          <Stack gap={0} align="flex-start" style={{ minWidth: 0 }}>
                            <Text size="sm" ff="monospace" truncate>{repository.fullName}</Text>
                            <Text size="xs" c="dimmed">{t.msg(describeRepository(repository))}</Text>
                          </Stack>
                        </Button>
                      );
                    })}

                    {group.repositorySelection === 'selected' && (
                      <Text size="xs" c="dimmed">
                        {t.rich('onlySome', {
                          login: group.accountLogin,
                          link: (chunks) => (
                            <Anchor href={group.manageUrl} target="_blank" rel="noreferrer noopener">
                              {chunks}
                            </Anchor>
                          ),
                        })}
                      </Text>
                    )}
                  </Stack>
                ))}
              </Stack>
            </ScrollArea.Autosize>
          )}

          {data?.truncated && (
            <Text size="xs" c="dimmed">
              {t('moreThanShown')}
            </Text>
          )}
        </>
      )}

      {data?.installUrl && (
        <Group gap="xs" wrap="wrap">
          <Button
            component="a"
            href={data.installUrl}
            target="_blank"
            rel="noreferrer noopener"
            variant={total === 0 ? 'filled' : 'subtle'}
            size={total === 0 ? 'sm' : 'compact-sm'}
            style={{ minHeight: total === 0 ? 44 : 32 }}
            leftSection={<IconExternalLink size={14} />}
          >
            {t('giveAccess')}
          </Button>
          <Button variant="subtle" color="gray" size="compact-sm" style={{ minHeight: 32 }} leftSection={<IconRefresh size={14} />} onClick={() => void mutate()}>
            {t('reload')}
          </Button>
        </Group>
      )}
    </Stack>
  );
}
