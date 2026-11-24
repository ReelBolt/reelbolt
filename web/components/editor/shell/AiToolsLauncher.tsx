'use client';

import React, { useMemo, useRef, useState } from 'react';
import { Badge, Button, Drawer, Popover, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { IconSearch, IconWand } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { InlineJobStatus } from '@/components/activity';
import { useJobsWhere } from '@/lib/activity/ActivityProvider';
import { isActiveState } from '@/lib/activity/notifier';
import { EDITOR_AI_TOOL_GROUPS, listEditorAiTools, toolNeedMet, type EditorAiToolContext, type EditorAiToolDef } from '@/lib/editing/ai-tools';
import '@/lib/editing/ai-tools.builtin';
import classes from './AiToolsLauncher.module.css';

export interface AiToolsLauncherProps {
  /** What the workspace gives a tool to open itself. */
  context: EditorAiToolContext;
  /** Running chips show jobs of THIS project only. */
  projectId: string;
  /** Phone: no header button; a bottom drawer controlled by `opened` / `onClose` (the phone header opens it). */
  phone?: boolean;
  opened?: boolean;
  onClose?: () => void;
}

interface ListProps {
  context: EditorAiToolContext;
  projectId: string;
  onPicked: () => void;
}

/** The searchable, grouped list of every AI tool: name, one line, cost, running chip, and a hint when a selection is missing. */
function AiToolsList({ context, projectId, onPicked }: ListProps) {
  const t = useT('editor-ai-tools');
  const [query, setQuery] = useState('');
  const listRef = useRef<HTMLDivElement>(null);
  const jobs = useJobsWhere((j) => j.projectId === projectId && isActiveState(j.state));
  const q = query.trim().toLowerCase();
  const groups = useMemo(() => {
    const all = listEditorAiTools().map((def) => ({ def, label: t(def.labelKey), description: t(def.descriptionKey) }));
    const shown = q ? all.filter((x) => x.label.toLowerCase().includes(q) || x.description.toLowerCase().includes(q)) : all;
    return EDITOR_AI_TOOL_GROUPS.map((group) => ({ group, rows: shown.filter((x) => x.def.group === group) })).filter((g) => g.rows.length > 0);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `t` is stable per locale
  }, [q]);

  const rows = () => Array.from(listRef.current?.querySelectorAll<HTMLElement>('[data-ai-tool-row]') ?? []);
  const move = (from: HTMLElement | null, step: 1 | -1) => {
    const all = rows();
    const index = from ? all.indexOf(from) : -1;
    all[Math.max(0, Math.min(all.length - 1, index + step))]?.focus();
  };
  const choose = (def: EditorAiToolDef) => {
    if (!toolNeedMet(def.needs, context)) return;
    onPicked();
    def.open(context);
  };

  return (
    <Stack gap="xs" data-testid="ai-tools-list">
      <TextInput
        size="sm"
        aria-label={t('launcher.searchLabel')}
        placeholder={t('launcher.searchPlaceholder')}
        leftSection={<IconSearch size={16} aria-hidden />}
        value={query}
        onChange={(e) => setQuery(e.currentTarget.value)}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown') {
            e.preventDefault();
            move(null, 1);
          }
        }}
        data-autofocus
        data-testid="ai-tools-search"
      />
      <div
        ref={listRef}
        className={classes.list}
        role="group"
        aria-label={t('launcher.listLabel')}
        onKeyDown={(e) => {
          if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
          const target = (e.target as HTMLElement).closest<HTMLElement>('[data-ai-tool-row]');
          if (!target) return;
          e.preventDefault();
          move(target, e.key === 'ArrowDown' ? 1 : -1);
        }}
      >
        {groups.length === 0 && (
          <Text size="sm" c="dimmed" role="status">
            {t('launcher.empty')}
          </Text>
        )}
        {groups.map(({ group, rows: groupRows }) => (
          <Stack key={group} gap={2} role="group" aria-label={t(`groups.${group}`)}>
            <Text fz={12} fw={600} c="dimmed" px="xs">
              {t(`groups.${group}`)}
            </Text>
            {groupRows.map(({ def, label, description }) => {
              const Icon = def.icon;
              const met = toolNeedMet(def.needs, context);
              const running = def.activityTypes ? jobs.find((j) => def.activityTypes?.includes(j.type)) : undefined;
              const hintId = `ai-tool-hint-${def.id}`;
              return (
                <div key={def.id} className={classes.row} data-testid={`ai-tool-${def.id}`}>
                  <UnstyledButton
                    className={classes.rowMain}
                    data-ai-tool-row
                    aria-disabled={met ? undefined : true}
                    aria-describedby={met ? undefined : hintId}
                    onClick={() => choose(def)}
                  >
                    <Icon size={20} stroke={1.6} aria-hidden />
                    <span className={classes.rowText}>
                      <Text size="sm" fw={600}>
                        {label}
                      </Text>
                      <Text size="sm" c="dimmed">
                        {description}
                      </Text>
                      {!met && def.needs !== 'none' && (
                        <Text id={hintId} size="sm" c="orange" data-testid={`ai-tool-hint-${def.id}`}>
                          {def.needs === 'clip' ? t('launcher.needsClip') : def.needs === 'videoClip' ? t('launcher.needsVideoClip') : t('launcher.needsRange')}
                        </Text>
                      )}
                    </span>
                    <Badge variant={def.cost === 'free' ? 'light' : 'outline'} color={def.cost === 'free' ? 'green' : 'gray'} size="md" fz={12} tt="none">
                      {def.cost === 'free' ? t('launcher.free') : t('launcher.credits')}
                    </Badge>
                  </UnstyledButton>
                  {running && (
                    <div className={classes.chip} data-testid={`ai-tool-running-${def.id}`}>
                      <InlineJobStatus job={running} compact />
                    </div>
                  )}
                </div>
              );
            })}
          </Stack>
        ))}
      </div>
    </Stack>
  );
}

/**
 * The editor's one entry point to every AI tool: a labelled "AI tools" button in the header that opens a
 * searchable list (a popover on desktop). On phone it renders a bottom drawer that the phone header opens.
 * The list comes from the registry in `lib/editing/ai-tools.ts`.
 */
export function AiToolsLauncher({ context, projectId, phone = false, opened, onClose }: AiToolsLauncherProps) {
  const t = useT('editor-ai-tools');
  const [open, setOpen] = useState(false);
  if (phone) {
    return (
      <Drawer opened={opened ?? false} onClose={onClose ?? (() => undefined)} position="bottom" size="85%" title={t('launcher.title')} closeButtonProps={{ 'aria-label': t('launcher.close') }} zIndex={400} data-testid="ai-tools-drawer">
        <AiToolsList context={context} projectId={projectId} onPicked={() => onClose?.()} />
      </Drawer>
    );
  }
  return (
    <Popover opened={open} onChange={setOpen} position="bottom-end" width={400} shadow="md" withinPortal trapFocus returnFocus>
      <Popover.Target>
        <Button size="xs" fz="xs" variant="light" miw="max-content" style={{ flexShrink: 0 }} leftSection={<IconWand size={14} stroke={1.8} aria-hidden />} aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen((o) => !o)} data-testid="ai-tools-button">
          {t('launcher.button')}
        </Button>
      </Popover.Target>
      <Popover.Dropdown aria-label={t('launcher.title')}>
        <AiToolsList context={context} projectId={projectId} onPicked={() => setOpen(false)} />
      </Popover.Dropdown>
    </Popover>
  );
}

