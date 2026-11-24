'use client';

import { useEffect, useMemo, useState } from 'react';
import {
  ActionIcon, Alert, Button, Checkbox, Group, Loader, ScrollArea, Stack, Text, TextInput, UnstyledButton,
} from '@mantine/core';
import {
  IconAlertTriangle, IconChevronDown, IconChevronRight, IconFile, IconFolder, IconSearch, IconX,
} from '@tabler/icons-react';
import { describeGitHubFilesError } from '@/lib/api/github';
import { formatBytes } from '@/lib/files/model';
import {
  buildSelectionTree, checkStates, defaultSelection, filesUnder, fromIncludePaths, matchingFiles, selectionProblem,
  toIncludePaths, toggle, totals, visibleForSearch,
  type CheckState, type SelectionNode, type SelectionProblem, type SelectionTotals,
} from '@/lib/github/selection';
import { useGitHubFiles } from '@/lib/hooks/use-github-repositories';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';

/** What the selector reports up every time the choice changes. */
export interface FileSelection {
  /** What to save: `[]` = every file, a list = those paths, null = nothing chosen (cannot be saved). */
  includePaths: string[] | null;
  totals: SelectionTotals;
  /** Why the choice cannot be saved; null when it can. */
  problem: SelectionProblem | null;
  /** The commit the listing was read at, for the "you are choosing from" line. */
  commit: string | null;
  /** Files on the branch (inside the old single-folder option's folder, when there is one). */
  available: number;
}

/** Folders with more children than this show the first ones and a hint to search. */
const MAX_CHILDREN_SHOWN = 300;

/** The sentence for a selection that cannot be saved. */
export function describeSelectionProblem(problem: SelectionProblem): Msg {
  switch (problem.kind) {
    case 'empty':
      return msg('integrations.github.files.problem.empty');
    case 'too_many_files':
      return msg('integrations.github.files.problem.tooManyFiles', { files: problem.files, limit: problem.limit });
    case 'too_large':
      return msg('integrations.github.files.problem.tooLarge', {
        size: formatBytes(problem.bytes),
        limit: formatBytes(problem.limit),
      });
  }
}

/**
 * The file half of "link a repository": a tree of the branch with a checkbox on every file and folder.
 * A folder's checkbox ticks everything inside it (and shows a dash when only part of it is ticked);
 * a folder that is ticked whole also brings files added to it later. Search narrows the tree, and
 * "Select all"/"Clear" act on whatever the search shows.
 *
 * The running count is checked against the limits the server sent with the listing, so a selection
 * that one sync could not take is caught here, with the reason, rather than after linking.
 */
export function GitHubFileSelector({
  projectId,
  repositoryId,
  branch,
  root = '',
  initialIncludePaths,
  onChange,
}: {
  projectId: string;
  /** null = the repository already linked to the project. */
  repositoryId: number | null;
  /** null = the linked branch, or the repository's default branch for a new pick. */
  branch: string | null;
  /** The old single-folder option's folder: only files inside it can be chosen. */
  root?: string;
  /** The saved choice to start from; undefined starts a new link (everything, if it fits). */
  initialIncludePaths?: string[] | null;
  onChange: (selection: FileSelection) => void;
}) {
  const t = useT('integrations.github.files');
  const { data, isLoading, error, mutate } = useGitHubFiles(projectId, repositoryId, branch);
  const tree = useMemo(() => (data ? buildSelectionTree(data.entries, root) : null), [data, root]);
  const limits = useMemo(
    () => ({ maxFiles: data?.maxFiles ?? 0, maxTotalBytes: data?.maxTotalBytes ?? 0 }),
    [data],
  );

  const [selected, setSelected] = useState<Set<string> | null>(null);
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(['']));
  const [query, setQuery] = useState('');

  // The starting ticks, once per listing: the saved choice when editing, otherwise everything that fits.
  // Adjusted during render (React's "storing information from previous renders" pattern) rather than in
  // an effect, so the first painted tree already carries its ticks. initialIncludePaths is read once
  // per listing on purpose: re-seeding on every parent render would throw away what the person ticked.
  const [seededFor, setSeededFor] = useState<SelectionNode | null>(null);
  if (tree && tree !== seededFor) {
    setSeededFor(tree);
    setSelected(initialIncludePaths !== undefined
      ? fromIncludePaths(tree, initialIncludePaths)
      : defaultSelection(tree, limits));
    setExpanded(new Set([tree.path]));
  }

  const states = useMemo(
    () => (tree && selected ? checkStates(tree, selected) : new Map<string, CheckState>()),
    [tree, selected],
  );
  const sums = useMemo(
    () => (tree && selected ? totals(tree, selected, data?.maxFileBytes ?? 0) : { files: 0, bytes: 0, oversized: 0 }),
    [tree, selected, data],
  );
  const problem = useMemo(() => (tree && selected ? selectionProblem(sums, limits) : null), [tree, selected, sums, limits]);
  const visible = useMemo(() => (tree ? visibleForSearch(tree, query) : null), [tree, query]);

  useEffect(() => {
    if (!tree || !selected) return;
    onChange({
      includePaths: toIncludePaths(tree, selected),
      totals: sums,
      problem,
      commit: data?.commit ?? null,
      available: tree.fileCount,
    });
    // onChange is a callback prop; reporting on every new identity would loop with a parent that
    // stores what it receives.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tree, selected, sums, problem, data]);

  if (isLoading || (data && !selected)) {
    return (
      <Group gap="xs" wrap="nowrap" role="status">
        <Loader size="xs" />
        <Text size="sm" c="dimmed">{t('reading', { hasBranch: branch ? 'yes' : 'no', branch: branch ?? '' })}</Text>
      </Group>
    );
  }

  if (error || !data || !tree || !selected) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t('listFailedTitle')}>
        <Stack gap="sm" align="flex-start">
          <Text size="sm">{error ? describeGitHubFilesError(error) : t('listFailedFallback')}</Text>
          <Button variant="default" size="sm" style={{ minHeight: 44 }} onClick={() => void mutate()}>{t('tryAgain')}</Button>
        </Stack>
      </Alert>
    );
  }

  if (tree.fileCount === 0) {
    return (
      <Alert color="gray" icon={<IconFolder size={16} />} role="status" title={t('emptyTitle')}>
        <Text size="sm">
          {data.commit
            ? root
              ? t('emptyFolder', { root })
              : t('emptyBranch')
            : t('emptyRepo')}
        </Text>
      </Alert>
    );
  }

  const searchFiles = visible ? matchingFiles(tree, visible) : null;
  const selectScope = (on: boolean) => {
    const scope = searchFiles ?? null;
    setSelected((current) => {
      const next = new Set(current ?? []);
      if (scope === null) return on ? new Set(filesUnder(tree)) : new Set();
      scope.forEach((p) => (on ? next.add(p) : next.delete(p)));
      return next;
    });
  };

  const toggleExpanded = (path: string) => setExpanded((current) => {
    const next = new Set(current);
    if (next.has(path)) next.delete(path);
    else next.add(path);
    return next;
  });

  const over = problem && problem.kind !== 'empty';

  return (
    <Stack gap="sm">
      {data.truncated && (
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />} role="status">
          <Text size="sm">
            {t('truncated')}
          </Text>
        </Alert>
      )}

      <TextInput
        aria-label={t('searchAria')}
        placeholder={t('searchPlaceholder')}
        value={query}
        leftSection={<IconSearch size={16} />}
        rightSection={query ? (
          <ActionIcon variant="subtle" color="gray" aria-label={t('clearSearchAria')} onClick={() => setQuery('')}>
            <IconX size={14} />
          </ActionIcon>
        ) : undefined}
        onChange={(e) => setQuery(e.currentTarget.value)}
      />

      <Group gap="xs" wrap="wrap">
        <Button variant="default" size="compact-sm" style={{ minHeight: 36 }} onClick={() => selectScope(true)}
          disabled={searchFiles !== null && searchFiles.length === 0}>
          {searchFiles ? t('selectMatches', { count: searchFiles.length }) : t('selectAll')}
        </Button>
        <Button variant="default" size="compact-sm" style={{ minHeight: 36 }} onClick={() => selectScope(false)}
          disabled={searchFiles !== null && searchFiles.length === 0}>
          {searchFiles ? t('clearMatches') : t('clear')}
        </Button>
      </Group>

      <div
        style={{
          border: '1px solid var(--mantine-color-default-border)',
          borderRadius: 'var(--mantine-radius-md)',
        }}
      >
        <ScrollArea.Autosize mah={380} type="auto" offsetScrollbars>
          <div role="tree" aria-label={t('treeAria')} style={{ padding: 4 }}>
            {visible && visible.size === 0 ? (
              <Text size="sm" c="dimmed" p="sm">{t('noMatch', { query: query.trim() })}</Text>
            ) : (
              tree.children.map((child) => (
                <TreeRow
                  key={child.path}
                  node={child}
                  depth={0}
                  states={states}
                  expanded={expanded}
                  visible={visible}
                  onToggle={(node) => setSelected((current) => toggle(node, current ?? new Set()))}
                  onExpand={toggleExpanded}
                />
              ))
            )}
          </div>
        </ScrollArea.Autosize>
      </div>

      <Text size="sm" c={over ? 'red' : undefined} fw={over ? 600 : undefined} aria-live="polite">
        {t('chosen', { chosen: sums.files, total: tree.fileCount, size: formatBytes(sums.bytes) })}
        {limits.maxFiles > 0 && (
          <Text span size="sm" c={over ? 'red' : 'dimmed'} fw={400}>
            {' '}
            {t('limit', {
              files: limits.maxFiles,
              hasBytes: limits.maxTotalBytes > 0 ? 'yes' : 'no',
              size: limits.maxTotalBytes > 0 ? formatBytes(limits.maxTotalBytes) : '',
            })}
          </Text>
        )}
      </Text>

      {problem && problem.kind !== 'empty' && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t('tooMuchTitle')}>
          <Text size="sm">{t.msg(describeSelectionProblem(problem))}</Text>
        </Alert>
      )}

      {sums.oversized > 0 && (
        <Text size="xs" c="dimmed">
          {t('oversized', { count: sums.oversized, size: formatBytes(data.maxFileBytes) })}
        </Text>
      )}

      <Text size="xs" c="dimmed">
        {t('hint')}
      </Text>
    </Stack>
  );
}

function TreeRow({
  node,
  depth,
  states,
  expanded,
  visible,
  onToggle,
  onExpand,
}: {
  node: SelectionNode;
  depth: number;
  states: Map<string, CheckState>;
  expanded: Set<string>;
  visible: Set<string> | null;
  onToggle: (node: SelectionNode) => void;
  onExpand: (path: string) => void;
}) {
  const t = useT('integrations.github.files');
  if (visible && !visible.has(node.path)) return null;

  const state = states.get(node.path) ?? 'unchecked';
  // While searching, every folder on the way to a match is open, so the match can be seen.
  const open = node.isDirectory && (visible !== null || expanded.has(node.path));
  const children = open ? node.children.filter((c) => !visible || visible.has(c.path)) : [];
  const shown = children.slice(0, MAX_CHILDREN_SHOWN);

  return (
    <div role="treeitem" aria-expanded={node.isDirectory ? open : undefined} aria-selected={state === 'checked'}>
      <Group
        gap={6}
        wrap="nowrap"
        style={{ minHeight: 40, paddingLeft: Math.min(depth, 8) * 14, paddingRight: 8 }}
      >
        {node.isDirectory ? (
          <ActionIcon
            variant="subtle"
            color="gray"
            size={32}
            aria-label={t('collapseAria', { open: open ? 'yes' : 'no', name: node.name })}
            onClick={() => onExpand(node.path)}
            disabled={visible !== null}
          >
            {open ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
          </ActionIcon>
        ) : (
          <span style={{ width: 32, flex: '0 0 32px' }} />
        )}
        <Checkbox
          aria-label={node.isDirectory ? t('folderAria', { path: node.path }) : node.path}
          checked={state === 'checked'}
          indeterminate={state === 'indeterminate'}
          onChange={() => onToggle(node)}
        />
        <UnstyledButton
          onClick={() => (node.isDirectory ? onExpand(node.path) : onToggle(node))}
          style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, flex: 1, minHeight: 32 }}
          tabIndex={-1}
          aria-hidden
        >
          {node.isDirectory
            ? <IconFolder size={16} style={{ flex: '0 0 auto' }} />
            : <IconFile size={16} style={{ flex: '0 0 auto', opacity: 0.6 }} />}
          <Text size="sm" ff="monospace" truncate style={{ minWidth: 0 }}>{node.name}</Text>
        </UnstyledButton>
        <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>
          {node.isDirectory ? t('folderCount', { count: node.fileCount }) : formatBytes(node.size)}
        </Text>
      </Group>

      {open && shown.map((child) => (
        <TreeRow
          key={child.path}
          node={child}
          depth={depth + 1}
          states={states}
          expanded={expanded}
          visible={visible}
          onToggle={onToggle}
          onExpand={onExpand}
        />
      ))}
      {open && children.length > shown.length && (
        <Text size="xs" c="dimmed" style={{ paddingLeft: (Math.min(depth, 8) + 1) * 14 + 40 }} py={4}>
          {t('moreChildren', { count: children.length - shown.length })}
        </Text>
      )}
    </div>
  );
}
