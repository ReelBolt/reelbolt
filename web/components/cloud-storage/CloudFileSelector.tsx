'use client';

import { useCallback, useEffect, useRef, useState, type MutableRefObject } from 'react';
import { ActionIcon, Alert, Checkbox, Group, Loader, Radio, Stack, Text } from '@mantine/core';
import { IconAlertTriangle, IconChevronDown, IconChevronRight, IconFile, IconFolder } from '@tabler/icons-react';
import { describeCloudError } from '@/lib/api/cloudStorage';
import { useCloudFolder } from './CloudFolderBrowser';
import { checkStateOf, joinPath, toggle, toIncludePaths, type ChildrenOf } from './selection';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';

/**
 * What the person chose. `includePaths` `[]` means every file of the folder; `null` means the choice
 * is not usable yet (`problem` says why).
 */
export interface CloudFileChoice {
  includePaths: string[] | null;
  problem: Msg | null;
}

/** The choice a selector starts from, before anything is clicked. */
export function initialChoice(includePaths: string[] | null | undefined): CloudFileChoice {
  return { includePaths: includePaths && includePaths.length > 0 ? [...includePaths] : [], problem: null };
}

const NOTHING_TICKED = msg('integrations.cloud.selector.nothingTicked');

type Listings = MutableRefObject<Map<string, { paths: string[]; complete: boolean }>>;

/**
 * Chooses which files of the linked folder are synced: everything (the default, and the only choice
 * that also brings files added to the folder later anywhere in it), or only what is ticked. The tree is
 * loaded one folder at a time, when it is opened.
 */
export function CloudFileSelector({
  connectionId,
  driveId,
  folderId,
  initialIncludePaths,
  onChange,
}: {
  connectionId: string;
  driveId: string | null;
  folderId: string | null;
  initialIncludePaths?: string[] | null;
  onChange: (choice: CloudFileChoice) => void;
}) {
  const t = useT('integrations.cloud.selector');
  const [mode, setMode] = useState<'all' | 'some'>(initialIncludePaths && initialIncludePaths.length > 0 ? 'some' : 'all');
  const [selected, setSelected] = useState<Set<string>>(() => new Set(initialIncludePaths ?? []));
  // What each opened folder holds. Read only when something is clicked, so a ref, not state.
  const listings: Listings = useRef(new Map());
  const childrenOf: ChildrenOf = useCallback((path) => listings.current.get(path), []);

  const report = (nextMode: 'all' | 'some', nextSelected: ReadonlySet<string>) => {
    if (nextMode === 'all') onChange({ includePaths: [], problem: null });
    else if (nextSelected.size === 0) onChange({ includePaths: null, problem: NOTHING_TICKED });
    else onChange({ includePaths: toIncludePaths(nextSelected), problem: null });
  };

  const onMode = (value: string) => {
    const next = value === 'some' ? 'some' : 'all';
    setMode(next);
    report(next, selected);
  };

  const onToggle = (path: string) => {
    const next = toggle(path, selected, childrenOf);
    setSelected(next);
    report('some', next);
  };

  return (
    <Stack gap="sm">
      <Radio.Group value={mode} onChange={onMode} label={t('whichFiles')}>
        <Stack gap={6} mt={6}>
          <Radio value="all" label={t('all')} />
          <Radio value="some" label={t('some')} />
        </Stack>
      </Radio.Group>

      {mode === 'some' && (
        <Stack gap={4}>
          <FolderRows
            connectionId={connectionId}
            driveId={driveId}
            folderId={folderId}
            path=""
            depth={0}
            selected={selected}
            listings={listings}
            onToggle={onToggle}
          />
          {selected.size === 0
            ? <Text size="sm" c="dimmed">{t.msg(NOTHING_TICKED)}</Text>
            : <Text size="sm" c="dimmed">{t('ticked', { count: selected.size })}</Text>}
        </Stack>
      )}
    </Stack>
  );
}

function FolderRows({
  connectionId,
  driveId,
  folderId,
  path,
  depth,
  selected,
  listings,
  onToggle,
}: {
  connectionId: string;
  driveId: string | null;
  folderId: string | null;
  path: string;
  depth: number;
  selected: ReadonlySet<string>;
  listings: Listings;
  onToggle: (path: string) => void;
}) {
  const t = useT('integrations.cloud.selector');
  const { data, error, isLoading } = useCloudFolder(connectionId, driveId, folderId);
  const [open, setOpen] = useState<Set<string>>(new Set());

  useEffect(() => {
    if (!data) return;
    listings.current.set(path, { paths: data.items.map((i) => joinPath(path, i.name)), complete: !data.truncated });
  }, [data, path, listings]);

  const indent = { paddingLeft: depth * 22 };

  if (isLoading) return <Group gap="xs" style={indent}><Loader size="xs" /><Text size="sm" c="dimmed">{t('loading')}</Text></Group>;
  if (error) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" style={indent}>
        {describeCloudError(error, t('openFailed'))}
      </Alert>
    );
  }
  if (!data) return null;
  if (data.items.length === 0) return <Text size="sm" c="dimmed" style={indent}>{t('empty')}</Text>;

  return (
    <>
      {data.items.map((item) => {
        const itemPath = joinPath(path, item.name);
        const state = checkStateOf(itemPath, selected);
        const isOpen = open.has(item.id);
        return (
          <div key={item.id}>
            <Group gap={6} wrap="nowrap" style={{ ...indent, minHeight: 36 }}>
              {item.isFolder ? (
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="sm"
                  aria-label={t('toggleFolderAria', { open: isOpen ? 'yes' : 'no', name: item.name })}
                  aria-expanded={isOpen}
                  onClick={() => {
                    const next = new Set(open);
                    if (isOpen) next.delete(item.id); else next.add(item.id);
                    setOpen(next);
                  }}
                >
                  {isOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
                </ActionIcon>
              ) : <span style={{ width: 22, display: 'inline-block' }} aria-hidden />}
              <Checkbox
                checked={state === 'checked'}
                indeterminate={state === 'partial'}
                onChange={() => onToggle(itemPath)}
                aria-label={item.isFolder ? t('folderAria', { name: item.name }) : item.name}
              />
              {item.isFolder ? <IconFolder size={15} aria-hidden /> : <IconFile size={15} aria-hidden />}
              <Text size="sm" truncate>{item.name}</Text>
            </Group>
            {item.isFolder && isOpen && (
              <FolderRows
                connectionId={connectionId}
                driveId={driveId}
                folderId={item.id}
                path={itemPath}
                depth={depth + 1}
                selected={selected}
                listings={listings}
                onToggle={onToggle}
              />
            )}
          </div>
        );
      })}
      {data.truncated && <Text size="xs" c="dimmed" style={indent}>{t('truncated')}</Text>}
    </>
  );
}
