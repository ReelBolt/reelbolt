'use client';

import { Loader, Select, Text } from '@mantine/core';
import { IconGitBranch } from '@tabler/icons-react';
import { describeGitHubFilesError } from '@/lib/api/github';
import { useGitHubBranches } from '@/lib/hooks/use-github-repositories';
import { useT } from '@/i18n';

/**
 * The branch to follow, chosen from the repository's real branches rather than typed. The default
 * branch is listed first and marked, because it is the one almost everybody wants; while the list is
 * loading (or if it cannot be read) the current choice is still shown, so the form never blanks out.
 */
export function GitHubBranchSelect({
  projectId,
  repositoryId,
  defaultBranch,
  value,
  onChange,
  disabled = false,
}: {
  projectId: string;
  /** null = the repository already linked to the project. */
  repositoryId: number | null;
  defaultBranch: string;
  value: string;
  onChange: (branch: string) => void;
  disabled?: boolean;
}) {
  const t = useT('integrations.github.branch');
  const { data, isLoading, error } = useGitHubBranches(projectId, repositoryId);

  const names = data?.branches ?? [defaultBranch];
  const options = (names.includes(value) ? names : [value, ...names]).map((name) => ({
    value: name,
    label: name === (data?.defaultBranch ?? defaultBranch) ? t('default', { name }) : name,
  }));

  return (
    <div>
      <Select
        label={t('label')}
        description={t('description')}
        data={options}
        value={value}
        onChange={(next) => next && onChange(next)}
        searchable={options.length > 8}
        allowDeselect={false}
        disabled={disabled}
        leftSection={<IconGitBranch size={16} />}
        rightSection={isLoading ? <Loader size={14} /> : undefined}
        nothingFoundMessage={t('nothingFound')}
        comboboxProps={{ withinPortal: true }}
      />
      {error && (
        <Text size="xs" c="dimmed" mt={4}>
          {t('listFailed', { value, reason: describeGitHubFilesError(error) })}
        </Text>
      )}
      {data?.truncated && (
        <Text size="xs" c="dimmed" mt={4}>{t('truncated', { count: data.branches.length })}</Text>
      )}
    </div>
  );
}
