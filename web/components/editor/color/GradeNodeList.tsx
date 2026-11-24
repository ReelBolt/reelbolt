'use client';

import React, { useEffect, useId, useRef, useState } from 'react';
import { Badge, Button, Group, Stack, Switch, Text, TextInput, VisuallyHidden } from '@mantine/core';
import { IconAdjustments, IconArrowDown, IconArrowUp, IconCopy, IconGripVertical, IconPencil, IconRestore, IconTrash } from '@tabler/icons-react';
import type { GradeNode, GradeStack } from '@/lib/editing/types';
import { withNode, withoutNode } from '@/lib/editing/color/grade';
import { canAddNode, duplicateNode, duplicateNodeName, isIdentityNode, moveNode, NODE_NAME_MAX, renameNode, reorderNodes, resetNode } from '@/lib/editing/color/grade-nodes';
import { EditorEmpty } from '../ui/EditorEmpty';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';
import { useDefaultNameText } from '../shell/default-names';
import classes from './colour-depth.module.css';

export interface GradeNodeListProps {
  stack: GradeStack | null | undefined;
  readOnly: boolean;
  /** Where this stack lives, for the undo labels. */
  subject: string;
  /** The selected node (the list never selects on its own). */
  selectedId: string | null;
  onSelect: (id: string) => void;
  /** Writes the whole stack: one undo step per call. */
  onChange: (stack: GradeStack | null, label: string) => void;
  newId: () => string;
  /** Solo is view state held by the Colour page; absent, the list offers no solo. */
  soloNodeId?: string | null;
  onSolo?: (nodeId: string | null) => void;
}

/**
 * The corrections of one stack, in the order they are applied: switch one on or off, rename it in place,
 * reorder it (drag, the keyboard on the grip, or Move up / Move down), solo it, duplicate it, reset it or
 * remove it. Every change is one whole-stack write; solo writes nothing.
 */
export function GradeNodeList({ stack, readOnly, subject, selectedId, onSelect, onChange, newId, soloNodeId = null, onSolo }: GradeNodeListProps) {
  const t = useT('editor-color.grade');
  const tNode = useT('editor-color.nodes');
  const nameText = useDefaultNameText();
  const nodes = stack?.nodes ?? [];
  const labelOf = (node: GradeNode, index: number): string => (node.name ? nameText(node.name) : t('correctionN', { n: index + 1 }));
  const selected = nodes.find((n) => n.id === selectedId) ?? null;
  const selectedIndex = selected ? nodes.indexOf(selected) : -1;

  // Rename in place.
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  // True once this rename is over (committed or cancelled): a blur that follows must not commit again.
  const settled = useRef(false);
  const startRename = (node: GradeNode, index: number) => {
    if (readOnly) return;
    settled.current = false;
    setDraft(labelOf(node, index));
    setRenamingId(node.id);
    onSelect(node.id);
  };
  const commitRename = (node: GradeNode, index: number) => {
    setRenamingId(null);
    if (settled.current) return;
    settled.current = true;
    // Left as shown (a default name is shown translated): nothing was typed, nothing is written.
    if (draft.trim() === labelOf(node, index)) return;
    const next = renameNode(stack, node.id, draft);
    if (next) onChange(next, tNode('undoRename', { subject }));
  };

  // Reorder: pointer drag (the browser's own), or the grip with the keyboard.
  const [dragId, setDragId] = useState<string | null>(null);
  const [overIndex, setOverIndex] = useState<number | null>(null);
  const [lift, setLift] = useState<{ id: string; to: number } | null>(null);
  const [announce, setAnnounce] = useState('');
  const rows = useRef(new Map<string, HTMLDivElement>());
  const refocus = useRef<string | null>(null);
  const hintId = useId();
  // A keyboard move reorders the rows, which can drop focus: put it back on the moved row's grip.
  useEffect(() => {
    if (!refocus.current) return;
    rows.current.get(refocus.current)?.querySelector<HTMLButtonElement>('button[data-node-handle]')?.focus();
    refocus.current = null;
  });
  const writeReorder = (from: number, to: number) => {
    const next = reorderNodes(stack, from, to);
    if (!next) return false;
    onChange(next, tNode('undoMove', { subject, name: labelOf(nodes[from], from) }));
    return true;
  };
  const dropTarget = lift ? lift.to : overIndex;
  const movingIndex = lift ? nodes.findIndex((n) => n.id === lift.id) : dragId ? nodes.findIndex((n) => n.id === dragId) : -1;
  const dropMark = (index: number): 'before' | 'after' | undefined => {
    if (dropTarget === null || movingIndex < 0 || index !== dropTarget || dropTarget === movingIndex) return undefined;
    return dropTarget < movingIndex ? 'before' : 'after';
  };
  const onHandleKey = (e: React.KeyboardEvent, node: GradeNode, index: number) => {
    if (readOnly) return;
    if (!lift) {
      if (e.key === ' ' || e.key === 'Enter') {
        e.preventDefault();
        setLift({ id: node.id, to: index });
        setAnnounce(tNode('liftAnnounce', { name: labelOf(node, index), position: index + 1, count: nodes.length }));
      }
      return;
    }
    if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      e.preventDefault();
      const to = Math.min(nodes.length - 1, Math.max(0, lift.to + (e.key === 'ArrowUp' ? -1 : 1)));
      setLift({ id: lift.id, to });
      setAnnounce(tNode('moveAnnounce', { position: to + 1, count: nodes.length }));
    } else if (e.key === ' ' || e.key === 'Enter') {
      e.preventDefault();
      const moved = writeReorder(index, lift.to);
      setAnnounce(moved ? tNode('dropAnnounce', { name: labelOf(node, index), position: lift.to + 1, count: nodes.length }) : tNode('cancelAnnounce'));
      if (moved) refocus.current = node.id;
      setLift(null);
    } else if (e.key === 'Escape') {
      // Escape here ends the move only: it must not also leave solo.
      e.preventDefault();
      e.stopPropagation();
      setLift(null);
      setAnnounce(tNode('cancelAnnounce'));
    }
  };

  const move = (delta: -1 | 1) => {
    if (!selected) return;
    const next = moveNode(stack, selected.id, delta);
    if (next) onChange(next, tNode('undoMove', { subject, name: labelOf(selected, selectedIndex) }));
  };
  const duplicate = () => {
    if (!selected) return;
    const id = newId();
    const next = duplicateNode(stack, selected.id, id, duplicateNodeName(selected.name, nodes.length));
    if (!next) return;
    onChange(next, tNode('undoDuplicate', { subject, name: labelOf(selected, selectedIndex) }));
    onSelect(id);
  };
  const reset = () => {
    if (!selected) return;
    const next = resetNode(stack, selected.id);
    if (next) onChange(next, tNode('undoReset', { subject, name: labelOf(selected, selectedIndex) }));
  };

  return (
    <Stack gap={4} data-testid="grade-node-list">
      {nodes.length === 0 && <EditorEmpty compact icon={IconAdjustments} title={t('noCorrections')} hint={t('noCorrectionsHint')} testId="grade-nodes-empty" />}
      {nodes.length > 0 && (
        <Stack gap={0} role="list" aria-label={tNode('listLabel', { subject })}>
          {nodes.map((node, index) => {
            const label = labelOf(node, index);
            const soloed = soloNodeId === node.id;
            return (
              <div
                key={node.id}
                ref={(el) => {
                  if (el) rows.current.set(node.id, el);
                  else rows.current.delete(node.id);
                }}
                role="listitem"
                className={classes.nodeRow}
                data-testid={`grade-node-${index}`}
                data-selected={selected?.id === node.id}
                data-solo={soloed}
                data-lifted={lift?.id === node.id}
                data-dragging={dragId === node.id}
                data-drop={dropMark(index)}
                draggable={!readOnly && renamingId === null && nodes.length > 1}
                onDragStart={(e) => {
                  setDragId(node.id);
                  e.dataTransfer.effectAllowed = 'move';
                  e.dataTransfer.setData('text/plain', node.id);
                }}
                onDragOver={(e) => {
                  if (!dragId) return;
                  e.preventDefault();
                  e.dataTransfer.dropEffect = 'move';
                  if (overIndex !== index) setOverIndex(index);
                }}
                onDrop={(e) => {
                  e.preventDefault();
                  const from = nodes.findIndex((n) => n.id === dragId);
                  if (from >= 0) writeReorder(from, index);
                  setDragId(null);
                  setOverIndex(null);
                }}
                onDragEnd={() => {
                  setDragId(null);
                  setOverIndex(null);
                }}
              >
                <EditorIcon
                  icon={IconGripVertical}
                  data-node-handle
                  size="sm"
                  dense
                  tooltip={false}
                  className={classes.nodeHandle}
                  disabled={readOnly || nodes.length < 2}
                  label={tNode('reorder', { name: label })}
                  aria-describedby={hintId}
                  aria-pressed={lift?.id === node.id}
                  onKeyDown={(e) => onHandleKey(e, node, index)}
                  onBlur={() => lift?.id === node.id && setLift(null)}
                  data-testid={`grade-node-handle-${index}`}
                />
                <Switch size="xs" checked={node.enabled !== false} disabled={readOnly} onChange={(e) => onChange(withNode(stack, { ...node, enabled: e.currentTarget.checked }), t(e.currentTarget.checked ? 'undoEnableNode' : 'undoBypassNode', { subject, name: node.name ? nameText(node.name) : t('correction') }))} aria-label={t('enableNode', { name: label })} data-testid={`grade-node-enabled-${index}`} />
                {renamingId === node.id ? (
                  <TextInput
                    size="xs"
                    className={classes.nodeName}
                    value={draft}
                    maxLength={NODE_NAME_MAX}
                    autoFocus
                    aria-label={tNode('nameField', { name: label })}
                    onChange={(e) => setDraft(e.currentTarget.value)}
                    onFocus={(e) => e.currentTarget.select()}
                    onBlur={() => commitRename(node, index)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault();
                        commitRename(node, index);
                      } else if (e.key === 'Escape') {
                        // Escape cancels the rename only: it must not also leave solo.
                        e.preventDefault();
                        e.stopPropagation();
                        settled.current = true;
                        setRenamingId(null);
                      }
                    }}
                    data-testid={`grade-node-name-input-${index}`}
                  />
                ) : (
                  <Button
                    size="compact-xs" fz="xs"
                    variant="subtle"
                    className={classes.nodeName}
                    justify="left"
                    title={readOnly ? undefined : tNode('renameHint')}
                    onClick={() => onSelect(node.id)}
                    onDoubleClick={() => startRename(node, index)}
                    onKeyDown={(e) => {
                      if (e.key === 'F2') {
                        e.preventDefault();
                        startRename(node, index);
                      }
                    }}
                    data-testid={`grade-node-select-${index}`}
                  >
                    {label}
                  </Button>
                )}
                {node.lut && <Badge size="xs" fz="xs" variant="light" style={{ flexShrink: 0 }}>{t('lutBadge')}</Badge>}
                {node.curves && <Badge size="xs" fz="xs" variant="light" color="teal" style={{ flexShrink: 0 }}>{t('curvesBadge')}</Badge>}
                {onSolo && (
                  <EditorIcon
                    size="sm"
                    dense
                    variant={soloed ? 'filled' : 'subtle'}
                    color={soloed ? 'yellow' : 'gray'}
                    label={soloed ? tNode('leaveSoloOf', { name: label }) : tNode('solo', { name: label })}
                    tooltip={soloed ? tNode('leaveSoloTip') : tNode('soloTip')}
                    aria-pressed={soloed}
                    onClick={() => {
                      onSelect(node.id);
                      onSolo(soloed ? null : node.id);
                    }}
                    data-testid={`grade-node-solo-${index}`}
                  >
                    <Text component="span" fz={12} fw={700} lh={1}>
                      {tNode('soloShort')}
                    </Text>
                  </EditorIcon>
                )}
                <EditorIcon icon={IconTrash} size="sm" dense color="red" disabled={readOnly} onClick={() => onChange(withoutNode(stack, node.id), t('undoRemoveNode', { subject, name: node.name ? nameText(node.name) : t('correction') }))} label={t('removeNode', { name: label })} data-testid={`grade-node-remove-${index}`} />
              </div>
            );
          })}
        </Stack>
      )}
      {nodes.length > 0 && (
        <Group gap={2} wrap="nowrap" role="toolbar" aria-label={tNode('toolbar')} data-testid="grade-node-actions">
          <EditorIcon icon={IconPencil} size="sm" disabled={readOnly || !selected} onClick={() => selected && startRename(selected, selectedIndex)} label={tNode('rename')} data-testid="grade-node-rename" />
          <EditorIcon icon={IconArrowUp} size="sm" disabled={readOnly || !selected || selectedIndex <= 0} onClick={() => move(-1)} label={tNode('moveUp')} data-testid="grade-node-up" />
          <EditorIcon icon={IconArrowDown} size="sm" disabled={readOnly || !selected || selectedIndex >= nodes.length - 1} onClick={() => move(1)} label={tNode('moveDown')} data-testid="grade-node-down" />
          <EditorIcon icon={IconCopy} size="sm" disabled={readOnly || !selected || !canAddNode(stack)} onClick={duplicate} label={tNode('duplicate')} data-testid="grade-node-duplicate" />
          <EditorIcon icon={IconRestore} size="sm" disabled={readOnly || !selected || isIdentityNode(selected)} onClick={reset} label={tNode('reset')} data-testid="grade-node-reset" />
          {selected && (
            <Text size="xs" c="dimmed" truncate style={{ minWidth: 0 }} data-testid="grade-node-actions-target">
              {labelOf(selected, selectedIndex)}
            </Text>
          )}
        </Group>
      )}
      <VisuallyHidden id={hintId}>{tNode('reorderHint')}</VisuallyHidden>
      <VisuallyHidden role="status" aria-live="polite" data-testid="grade-node-announce">
        {announce}
      </VisuallyHidden>
    </Stack>
  );
}
