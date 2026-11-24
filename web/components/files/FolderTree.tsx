'use client';

import { useState } from 'react';
import { ActionIcon, Menu, Text } from '@mantine/core';
import {
  IconChevronRight, IconDotsVertical, IconFolder, IconFolderOpen, IconFolderPlus, IconPencil, IconStack2, IconTrash,
} from '@tabler/icons-react';
import type { FolderNode } from '@/lib/files/model';
import { useT } from '@/i18n';
import classes from './files.module.css';

export const INTERNAL_DRAG_TYPE = 'application/x-reelbolt-files';

export type FolderAction = 'new' | 'rename' | 'delete';

interface Props {
  root: FolderNode;
  current: string;
  onSelect: (path: string) => void;
  onFolderAction: (action: FolderAction, node: FolderNode) => void;
  /** Files from the list were dropped on a folder (moves them). */
  onMoveDrop: (targetPath: string, fileIds: string[]) => void;
}

export function FolderTree({ root, current, onSelect, onFolderAction, onMoveDrop }: Props) {
  const t = useT('files.folders');
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [dropTarget, setDropTarget] = useState<string | null>(null);

  const dropProps = (path: string) => ({
    onDragOver: (e: React.DragEvent) => {
      if (!e.dataTransfer.types.includes(INTERNAL_DRAG_TYPE)) return;
      e.preventDefault();
      setDropTarget(path);
    },
    onDragLeave: () => setDropTarget((t) => (t === path ? null : t)),
    onDrop: (e: React.DragEvent) => {
      const raw = e.dataTransfer.getData(INTERNAL_DRAG_TYPE);
      setDropTarget(null);
      if (!raw) return;
      e.preventDefault();
      try {
        const ids = JSON.parse(raw) as string[];
        if (Array.isArray(ids) && ids.length > 0) onMoveDrop(path, ids);
      } catch {
        // Not our payload; ignore.
      }
    },
  });

  const renderNode = (node: FolderNode, depth: number) => {
    const hasKids = node.children.length > 0;
    const open = expanded.has(node.path) || current === node.path || current.startsWith(`${node.path}/`);
    const selected = current === node.path;
    return (
      <li key={node.path} role="none">
        <div style={{ display: 'flex', alignItems: 'center', paddingLeft: depth * 12 }}>
          <ActionIcon
            variant="transparent" color="gray" size={32}
            aria-label={open ? t('collapse', { name: node.name }) : t('expand', { name: node.name })}
            aria-expanded={hasKids ? open : undefined}
            style={{ visibility: hasKids ? 'visible' : 'hidden', flex: 'none' }}
            onClick={() => setExpanded((s) => { const n = new Set(s); if (n.has(node.path)) n.delete(node.path); else n.add(node.path); return n; })}
          >
            <IconChevronRight size={14} style={{ transform: open ? 'rotate(90deg)' : undefined, transition: 'transform 160ms cubic-bezier(0.32,0.72,0,1)' }} />
          </ActionIcon>
          <button
            type="button" className={classes.treeBtn} aria-current={selected} data-drop={dropTarget === node.path}
            onClick={() => onSelect(node.path)} {...dropProps(node.path)}
          >
            {selected || open ? <IconFolderOpen size={17} stroke={1.6} /> : <IconFolder size={17} stroke={1.6} />}
            <Text span size="sm" truncate style={{ flex: 1, fontStyle: node.virtual ? 'italic' : undefined }}>{node.name}</Text>
            <Text span size="xs" c="dimmed" style={{ fontVariantNumeric: 'tabular-nums' }}>{node.fileCount || ''}</Text>
          </button>
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <ActionIcon variant="subtle" color="gray" size={32} aria-label={t('actionsFor', { name: node.name })}><IconDotsVertical size={15} /></ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item leftSection={<IconFolderPlus size={15} />} onClick={() => onFolderAction('new', node)}>{t('newSubfolder')}</Menu.Item>
              <Menu.Item leftSection={<IconPencil size={15} />} onClick={() => onFolderAction('rename', node)}>{t('rename')}</Menu.Item>
              <Menu.Item color="red" leftSection={<IconTrash size={15} />} onClick={() => onFolderAction('delete', node)}>{t('delete')}</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </div>
        {hasKids && open && <ul role="group" style={{ listStyle: 'none', margin: 0, padding: 0 }}>{node.children.map((c) => renderNode(c, depth + 1))}</ul>}
      </li>
    );
  };

  return (
    <nav aria-label={t('navLabel')}>
      <ul role="tree" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
        <li role="none">
          <div style={{ display: 'flex', alignItems: 'center' }}>
            <button
              type="button" className={classes.treeBtn} aria-current={current === ''} data-drop={dropTarget === ''}
              onClick={() => onSelect('')} {...dropProps('')}
            >
              <IconStack2 size={17} stroke={1.6} />
              <Text span size="sm" style={{ flex: 1 }}>{t('allFiles')}</Text>
              <Text span size="xs" c="dimmed" style={{ fontVariantNumeric: 'tabular-nums' }}>{root.fileCount || ''}</Text>
            </button>
          </div>
          <ul role="group" style={{ listStyle: 'none', margin: 0, padding: 0 }}>{root.children.map((c) => renderNode(c, 0))}</ul>
        </li>
      </ul>
    </nav>
  );
}
