'use client';

import { useMemo } from 'react';
import Link from 'next/link';
import { SimpleGrid, UnstyledButton, Paper, Text, Stack, Group, Button, Badge } from '@mantine/core';
import { IconCheck, IconTemplate } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { useDesignSystem } from '@/lib/hooks/use-design-system';
import { useWorkflowApps } from '@/lib/hooks/use-workflow-apps';
import type { WorkflowTemplateSummary } from '@/lib/types/workflow';

interface TemplatePickerProps {
  /** The project the templates are for; lets the picker tell which ones its profile can run. */
  projectId?: string;
  templates: WorkflowTemplateSummary[] | undefined;
  selectedKey: string | null;
  onSelect: (key: string | null) => void;
  onApply: () => void;
  loading?: boolean;
}

/**
 * The templates a customer picks from.
 *
 * A template marked `autoCreateOnProject` (the promo from code) is the one a new project is given
 * unless it was started from something else: a project started from clips gets the footage edit
 * instead and has no promo workflow at all. So that template is left out only when the project
 * really has a workflow made from it; offering it again there would just reopen the same one.
 *
 * `madeFrom` is the template key of each of the project's workflows (the `source` of its apps).
 * `undefined` means it is not known yet (still loading, or no project was given): the template
 * stays out until it is, so a card never appears and then vanishes.
 */
export function pickableTemplates(
  templates: WorkflowTemplateSummary[] | undefined,
  madeFrom?: ReadonlySet<string>,
): WorkflowTemplateSummary[] {
  return (templates ?? []).filter((t) => !t.autoCreateOnProject || (madeFrom !== undefined && !madeFrom.has(t.key)));
}

/**
 * "Start from a template": one card per template with its name and one-line description, exactly
 * as the API returns them. Internal noise (version, step count) is deliberately not shown — it told
 * a customer nothing about which template makes the video they want.
 */
export function TemplatePicker({ projectId, templates, selectedKey, onSelect, onApply, loading }: TemplatePickerProps) {
  const tr = useT('templates.picker');
  // Which templates the project's workflows were made from. If that cannot be read, nothing is
  // held back: applying a template the project already has opens the existing workflow.
  const { data: gallery, error: galleryError } = useWorkflowApps(projectId);
  const madeFrom = useMemo(
    () => (gallery ? new Set(gallery.apps.map((app) => app.source)) : galleryError ? new Set<string>() : undefined),
    [gallery, galleryError],
  );
  const list = pickableTemplates(templates, madeFrom);
  const { data: profile } = useDesignSystem(projectId ?? null);
  // Gate only once the answer is known: while loading, or if it cannot be read, nothing is blocked
  // (the server still refuses a run without a profile).
  const hasProfile = profile
    ? Boolean(
        // The server pin accepts a design inherited from a linked library (the summary does not say
        // whether the library has an approved version; PROFILE_MISSING stays the server backstop).
        // A library never provides a product map, so that one must be the project's own.
        (profile.design?.currentVersion || profile.design?.parentLibrary) &&
          profile.productMap?.currentVersion,
      )
    : true;
  if (templates && list.length === 0) return null;

  return (
    <Stack gap="xs">
      <Group gap="xs">
        <IconTemplate size={18} />
        <Text fw={600}>{tr('heading')}</Text>
      </Group>
      <Text size="sm" c="dimmed">
        {tr('intro')}
      </Text>
      <SimpleGrid cols={{ base: 1, sm: 2, md: 3 }} spacing="sm" role="radiogroup" aria-label={tr('ariaLabel')}>
        {list.map((t) => {
          const selected = t.key === selectedKey;
          const needsProfile = Boolean(t.requiresProfile) && !hasProfile;
          const reasonId = `template-${t.key}-reason`;
          return (
            <Stack key={t.key} gap={4}>
            <UnstyledButton
              role="radio"
              aria-checked={selected}
              aria-describedby={needsProfile ? reasonId : undefined}
              onClick={() => onSelect(selected ? null : t.key)}
              disabled={loading || needsProfile}
              style={{ display: 'block', height: '100%', opacity: needsProfile ? 0.6 : undefined }}
            >
              <Paper
                withBorder
                p="sm"
                radius="md"
                h="100%"
                style={{
                  borderColor: selected ? 'var(--mantine-color-violet-6)' : undefined,
                  borderWidth: selected ? 2 : undefined,
                  background: selected ? 'var(--mantine-color-violet-light)' : undefined,
                }}
              >
                <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
                  <Text fw={600} size="sm">{t.name}</Text>
                  {selected && <IconCheck size={16} color="var(--mantine-color-violet-6)" />}
                </Group>
                {t.description && <Text size="xs" c="dimmed" mt={4} lineClamp={3}>{t.description}</Text>}
                {t.requiresUserInput && (
                  <Badge size="xs" variant="light" color="gray" tt="none" mt={6}>{tr('asksForBrief')}</Badge>
                )}
              </Paper>
            </UnstyledButton>
            {needsProfile && (
              <Text size="xs" c="dimmed" id={reasonId}>
                {tr('needsProfile')}{' '}
                {projectId && (
                  <Text component={Link} href={`/projects/${projectId}?tab=profile`} size="xs" c="violet" td="underline">
                    {tr('setUpProfile')}
                  </Text>
                )}
              </Text>
            )}
            </Stack>
          );
        })}
      </SimpleGrid>
      <Button
        variant="light"
        onClick={onApply}
        disabled={!selectedKey}
        loading={loading}
        style={{ alignSelf: 'flex-start' }}
      >
        {tr('useTemplate')}
      </Button>
    </Stack>
  );
}
