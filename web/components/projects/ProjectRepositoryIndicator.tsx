'use client';

import { Badge, UnstyledButton } from '@mantine/core';
import { IconAlertTriangle, IconBrandGithub } from '@tabler/icons-react';
import { useProjectGitHub } from '@/lib/hooks/use-project-github';
import { useT } from '@/i18n';

/**
 * A small "Linked to GitHub" marker for the project header, so a linked repository is visible from
 * every tab — not only from the Repository tab. Red when the last sync failed, because that is the
 * one thing about the link somebody needs to notice. Renders nothing when the project has no link,
 * or while it is being read.
 */
export function ProjectRepositoryIndicator({ projectId, onOpen }: { projectId: string; onOpen: () => void }) {
  const t = useT('projects.repoIndicator');
  const { data: link } = useProjectGitHub(projectId);
  if (!link) return null;

  const failed = Boolean(link.lastError);
  return (
    <div style={{ marginTop: -8, marginBottom: 12 }}>
      <UnstyledButton
        onClick={onOpen}
        aria-label={failed
          ? t('ariaFailed', { repository: link.repository })
          : t('ariaLinked', { repository: link.repository })}
        style={{ display: 'inline-flex', minHeight: 32, alignItems: 'center', maxWidth: '100%' }}
      >
        <Badge
          variant="light"
          color={failed ? 'red' : 'gray'}
          size="lg"
          radius="sm"
          leftSection={failed ? <IconAlertTriangle size={14} /> : <IconBrandGithub size={14} />}
          styles={{ label: { textTransform: 'none', fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis' } }}
          style={{ maxWidth: '100%', cursor: 'var(--rb-cursor-pointer)' }}
        >
          {failed ? t('syncFailed', { repository: link.repository }) : t('linked', { repository: link.repository })}
        </Badge>
      </UnstyledButton>
    </div>
  );
}
