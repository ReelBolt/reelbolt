'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Badge,
  Box,
  Button,
  Divider,
  Group,
  Loader,
  Menu,
  Modal,
  Stack,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core';
import {
  IconDots,
  IconFolder,
  IconFolderOpen,
  IconPlus,
  IconWand,
  IconX,
} from '@tabler/icons-react';
import {
  applyDropOptimistically,
  buildBinTree,
  isSmartBin,
  readDragPayload,
  resolveBinDrop,
  smartBinRefusal,
  writeDragPayload,
} from '@/lib/editing/media/bins';
import {
  addBinItem,
  createBin,
  deleteBin,
  fetchBinItems,
  moveBinItem,
  removeBinItem,
  updateBin,
} from '@/lib/editing/api/media';
import type { BinDropPlan } from '@/lib/editing/media/bins';
import { restoreDeletedBins, type DeletedBinSnapshot } from '@/lib/editing/media/bin-restore';
import { notifications } from '@mantine/notifications';
import { SmartBinEditor } from './SmartBinEditor';
import { countSmartMatches, moveTargets, type AnnotationIndex } from '@/lib/editing/media/pool-browse';
import type { MediaPoolItem, MediaSmartFilter } from '@/lib/editing/media/types';
import { NeedsLargerScreen } from '../shell/LargerScreen';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { formatBytes } from '@/lib/editing/media/format';
import type {
  BinNode,
  MediaBin,
  MediaBinItemDetail,
  MediaBinMembership,
  MediaRef,
} from '@/lib/editing/media/types';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useLocale, useT } from '@/i18n';
import { ruleText } from './rule-text';
import { EditorEmpty, EditorIcon } from '../ui';

export interface BinTreeProps {
  projectId: string;
  /** The authoritative bin list, owned by the parent so one list feeds the whole workspace. */
  bins: MediaBin[];
  onBinsChange: (bins: MediaBin[]) => void;
  selectedBinId: string | null;
  onSelect: (binId: string | null) => void;
  /**
   * Real per-bin counts, derived by the parent from the pool items' own pin lists.
   *
   * A missing key means zero. These replace the optimistic increment: a bin's count now moves
   * because a membership moved, and never because a request was *about* to be made.
   */
  countsByBinId: Record<string, number>;
  /** Fired with the pin the server created, so the parent's pool cache gains the membership. */
  onMembershipAdded?: (mediaRef: MediaRef, membership: MediaBinMembership) => void;
  /** Fired with a removed pin's id, so the parent's pool cache loses the membership. */
  onMembershipRemoved?: (mediaRef: MediaRef, itemId: string) => void;
  /**
   * Resolves a dragged clip's current pins, so a drop on "All media" can remove it from every bin.
   *
   * A `mediaRef` alone cannot produce an `itemId`, and the pool is where the pins live. Returning
   * nothing for a clip is a legitimate answer — that clip then simply cannot be removed by a drop,
   * and the refusal is stated rather than executed against a guessed id.
   */
  pinsFor?: BinPinsResolver;
  /** Bumped by the parent after it pins a clip itself ("Add to bin"), so the open listing re-reads. */
  listingVersion?: number;
  /** The pool and its annotations: a smart bin's count is the number of sources its rule matches right now. */
  items?: readonly MediaPoolItem[];
  annotations?: AnnotationIndex;
}

const NO_ANNOTATIONS: AnnotationIndex = new Map();

/** Root id used for the "All media" drop zone, which is the only target that means "no bin". */
const ROOT_DROP_ID = null;


/**
 * The nested bin tree, with a real membership listing for the selected bin.
 *
 * ## Membership is listed, and it can be edited
 *
 * Selecting a bin loads `GET .../media/bins/{binId}/items` and renders the pins it actually holds.
 * Every row is removable, because the listing supplies the one thing a removal needs and nothing
 * else does: the pin's `itemId`. Dropping a clip on "All media" removes it from every bin it is in
 * (the dragged clip's own pin list is what supplies those ids), and dropping it on a bin adds it.
 *
 * ## Bad rows are shown, not filtered
 *
 * A row whose source row is gone — a DANGLING pin — is rendered with a stated "source unavailable",
 * not hidden. It is returned by the server precisely so it can be cleaned up, and a listing that
 * dropped it would strand it forever. That is also why the bin's listed rows can outnumber the pool
 * items its count is derived from: the count comes from the pool's own pins, and a dangling pin is
 * in no pool item.
 *
 * ## Every drop is decided before it is executed
 *
 * A drop goes through `resolveBinDrop`, which is pure: a refusal (self-drop, cycle, no-op, or a
 * removal of a clip that is in no bin) is decided before any request is issued, and cycles are
 * refused client-side rather than shipped to the server to reject.
 */
export function BinTree({
  projectId,
  bins,
  onBinsChange,
  selectedBinId,
  onSelect,
  countsByBinId,
  onMembershipAdded,
  onMembershipRemoved,
  pinsFor,
  listingVersion = 0,
  items = [],
  annotations = NO_ANNOTATIONS,
}: BinTreeProps) {
  const t = useT('editor-media.bins');
  const locale = useLocale();
  const messageOf = (error: unknown): string => humanizeErrorT(t, error, t('requestFailed'));
  // The latest bins, so an Undo that lands seconds later appends to the list as it is then.
  const binsRef = useRef(bins);
  useEffect(() => {
    binsRef.current = bins;
  }, [bins]);
  // COLLAPSED ids, not expanded ones: a freshly loaded tree is fully visible, which is what a
  // workspace wants on first paint, and the set stays empty until the user actually collapses
  // something.
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set());
  // On a phone bins are not dragged (reparenting is cut there); clips go in by "Add to bin".
  const phone = useDeviceClass() === 'phone';
  const [message, setMessage] = useState<string | null>(null);
  const [newBinName, setNewBinName] = useState('');
  // R05.3: "New sub-bin" from a bin's menu sets the parent the next bin is created under.
  const [newBinParentId, setNewBinParentId] = useState<string | null>(null);
  // R05.6: the smart-bin rule editor (create, or edit an existing bin's rule).
  const [ruleEditor, setRuleEditor] = useState<{ mode: 'create' | 'edit'; binId: string | null } | null>(null);
  const [renamingBinId, setRenamingBinId] = useState<string | null>(null);
  // R04.3: a bin is only deleted after the user has read what goes and what stays.
  const [confirmingDeleteId, setConfirmingDeleteId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const [newBinFocus, setNewBinFocus] = useState(0);
  const newBinInput = React.useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    if (newBinFocus > 0) newBinInput.current?.focus();
  }, [newBinFocus]);

  // ───────────────────────────────────────────────────────────────────────────
  // The selected bin's REAL item listing.
  //
  // The entry carries the binId it belongs to, so switching bins can never render the previous
  // bin's items while the new response is in flight.
  // ───────────────────────────────────────────────────────────────────────────
  const [listing, setListing] = useState<{
    binId: string;
    items: MediaBinItemDetail[] | null;
    error: string | null;
    reloads: number;
  } | null>(null);

  const currentListing = listing && listing.binId === selectedBinId ? listing : null;
  const listedItems = currentListing ? currentListing.items : null;
  const listingError = currentListing ? currentListing.error : null;

  useEffect(() => {
    if (!selectedBinId) return undefined;

    let cancelled = false;
    fetchBinItems(projectId, selectedBinId)
      .then((items) => {
        if (!cancelled) setListing({ binId: selectedBinId, items, error: null, reloads: 0 });
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setListing({ binId: selectedBinId, items: null, error: humanizeErrorT(t, error, t('requestFailed')), reloads: 0 });
        }
      });

    return () => {
      cancelled = true;
    };
    // `listing?.reloads` is a deliberate dependency: a successful add or remove bumps it, which
    // re-reads the listing from the server rather than trusting a locally spliced copy.
  }, [projectId, selectedBinId, listing?.reloads, listingVersion, t]);

  const tree = buildBinTree(bins);

  const toggleExpanded = (binId: string) => {
    setCollapsed((previous) => {
      const next = new Set(previous);
      if (next.has(binId)) next.delete(binId);
      else next.add(binId);
      return next;
    });
  };

  /** Asks the listing to be re-read after a write lands, without a full page reload. */
  const reloadListing = useCallback((binId: string) => {
    setListing((previous) =>
      previous && previous.binId === binId
        ? { ...previous, reloads: previous.reloads + 1 }
        : previous,
    );
  }, []);

  /** Decides a drop and, only if it is legitimate, performs it. */
  const handleDrop = async (
    event: React.DragEvent<HTMLElement>,
    targetBinId: string | null,
  ) => {
    event.preventDefault();
    event.stopPropagation();

    const drag = readDragPayload(event.dataTransfer);
    // A smart bin's contents are its filter's answer; a pin dropped on it has nowhere to go.
    const targetBin = targetBinId ? bins.find((bin) => bin.id === targetBinId) : null;
    if (targetBin && isSmartBin(targetBin)) {
      setMessage(smartBinRefusal(targetBin));
      return;
    }
    // The dragged clip's own pins, when the parent can supply them: they are what makes a drop on
    // the root mean "remove from every bin" and what supplies the itemIds a removal addresses. A
    // clip whose pins are unknown simply cannot be removed by a drop — a stated refusal, never a
    // guessed itemId.
    const memberships =
      drag?.kind === 'clip' ? pinsFor?.(drag.mediaRef) ?? [] : [];

    const plan = resolveBinDrop(drag, targetBinId, bins, memberships);
    if (plan.kind === 'none') {
      setMessage(plan.reason);
      return;
    }

    setMessage(null);
    const previous = bins;
    onBinsChange(applyDropOptimistically(bins, plan));

    try {
      await executePlan(plan);
    } catch (error) {
      onBinsChange(previous);
      setMessage(messageOf(error));
    }

    async function executePlan(settled: Exclude<BinDropPlan, { kind: 'none' }>) {
      if (settled.kind === 'addItem') {
        const created = await addBinItem(projectId, settled.binId, settled.mediaRef);
        onMembershipAdded?.(settled.mediaRef, { binId: settled.binId, itemId: created.id });
        if (selectedBinId === settled.binId) reloadListing(settled.binId);
        return;
      }
      if (settled.kind === 'removeItems') {
        for (const pin of settled.items) {
          await removeBinItem(projectId, pin.binId, pin.itemId);
          onMembershipRemoved?.(settled.mediaRef, pin.itemId);
          if (selectedBinId === pin.binId) reloadListing(pin.binId);
        }
        return;
      }
      await updateBin(projectId, settled.binId, { parentBinId: settled.parentBinId });
    }
  };

  const allowDrop = (event: React.DragEvent<HTMLElement>) => event.preventDefault();

  const handleCreate = async () => {
    const name = newBinName.trim();
    if (name.length === 0) {
      setMessage(t('nameRequired'));
      return;
    }

    setMessage(null);
    try {
      const created = await createBin(projectId, newBinParentId ? { name, parentBinId: newBinParentId } : { name });
      onBinsChange([...bins, created]);
      setNewBinName('');
      setNewBinParentId(null);
    } catch (error) {
      setMessage(messageOf(error));
    }
  };

  const handleRename = async (binId: string) => {
    const name = renameValue.trim();
    if (name.length === 0) {
      setMessage(t('nameRequired'));
      return;
    }

    setMessage(null);
    const previous = bins;
    onBinsChange(bins.map((bin) => (bin.id === binId ? { ...bin, name } : bin)));
    setRenamingBinId(null);

    try {
      await updateBin(projectId, binId, { name });
    } catch (error) {
      onBinsChange(previous);
      setMessage(messageOf(error));
    }
  };

  const saveRule = async (name: string, rule: MediaSmartFilter) => {
    if (ruleEditor?.mode === 'edit' && ruleEditor.binId) {
      const binId = ruleEditor.binId;
      const updated = await updateBin(projectId, binId, { name, smartFilter: rule });
      onBinsChange(bins.map((bin) => (bin.id === binId ? { ...bin, ...updated, name, smartFilter: rule, isSmart: true } : bin)));
      reloadListing(binId);
    } else {
      const created = await createBin(projectId, { name, smartFilter: rule });
      onBinsChange([...bins, created]);
    }
    setRuleEditor(null);
  };

  /** "Move to..." on a bin: the same write a drag makes, reachable by menu and touch. */
  const moveBinTo = async (binId: string, parentBinId: string | null) => {
    setMessage(null);
    const previous = bins;
    onBinsChange(bins.map((bin) => (bin.id === binId ? { ...bin, parentBinId } : bin)));
    try {
      await updateBin(projectId, binId, { parentBinId });
    } catch (error) {
      onBinsChange(previous);
      setMessage(messageOf(error));
    }
  };

  /** "Move to..." on a pin row. */
  const movePin = async (item: MediaBinItemDetail, targetBinId: string) => {
    setMessage(null);
    try {
      const moved = await moveBinItem(projectId, item.binId, item.id, targetBinId);
      onMembershipRemoved?.(item.mediaRef, item.id);
      onMembershipAdded?.(item.mediaRef, { binId: targetBinId, itemId: moved.id });
      reloadListing(item.binId);
    } catch (error) {
      setMessage(messageOf(error));
    }
  };

  const handleDelete = async (binId: string) => {
    setMessage(null);
    const previous = bins;
    const removed = new Set([binId, ...descendantsOf(bins, binId)]);

    // Snapshot the subtree and its pins first: the server drops the pins for good, so this is
    // what "Undo" re-creates. If it cannot be taken, nothing is deleted.
    let snapshots: DeletedBinSnapshot[];
    try {
      snapshots = await Promise.all(
        bins
          .filter((bin) => removed.has(bin.id))
          .map(async (bin) => ({
            bin,
            mediaRefs: isSmartBin(bin) ? [] : (await fetchBinItems(projectId, bin.id)).map((pin) => pin.mediaRef),
          })),
      );
    } catch (error) {
      setMessage(messageOf(error));
      return;
    }

    onBinsChange(bins.filter((bin) => !removed.has(bin.id)));
    try {
      await deleteBin(projectId, binId);
    } catch (error) {
      onBinsChange(previous);
      setMessage(messageOf(error));
      return;
    }
    if (selectedBinId && removed.has(selectedBinId)) onSelect(null);
    showUndoToast(snapshots, bins.find((bin) => bin.id === binId)?.name ?? t('fallbackName'));
  };

  const undoDelete = async (snapshots: DeletedBinSnapshot[], toastId: string) => {
    notifications.hide(toastId);
    try {
      const restored = await restoreDeletedBins(snapshots, {
        createBin: (request) => createBin(projectId, request),
        addBinItem: (targetBinId, mediaRef) => addBinItem(projectId, targetBinId, mediaRef),
      });
      onBinsChange([...binsRef.current, ...restored.bins]);
      for (const { mediaRef, membership } of restored.memberships) onMembershipAdded?.(mediaRef, membership);
      notifications.show({
        color: 'violet',
        message:
          restored.skippedPins > 0
            ? t('restoredSkipped', { count: restored.skippedPins })
            : t('restored'),
      });
    } catch (error) {
      notifications.show({ color: 'red', message: messageOf(error) });
    }
  };

  const showUndoToast = (snapshots: DeletedBinSnapshot[], name: string) => {
    const toastId = `bin-undo-${snapshots[0]?.bin.id ?? name}`;
    notifications.show({
      id: toastId,
      color: 'violet',
      autoClose: 10000,
      withCloseButton: true,
      message: (
        <Group gap="sm" wrap="nowrap" justify="space-between" data-testid="bin-undo-toast">
          <Text size="sm" title={name}>{t('deleted')}</Text>
          <Button size="compact-md" fz="xs" variant="light" mih={44} onClick={() => void undoDelete(snapshots, toastId)} data-testid="bin-undo-delete">
            {t('undo')}
          </Button>
        </Group>
      ),
    });
  };

  /** Removes one pin. Optimistic on the listing, rolled back on failure. */
  const handleRemoveItem = async (item: MediaBinItemDetail) => {
    setMessage(null);
    const before = listedItems;

    setListing((previous) =>
      previous && previous.binId === item.binId && previous.items
        ? { ...previous, items: previous.items.filter((row) => row.id !== item.id) }
        : previous,
    );
    const binsBefore = bins;
    onBinsChange(
      applyDropOptimistically(bins, {
        kind: 'removeItems',
        mediaRef: item.mediaRef,
        items: [{ binId: item.binId, itemId: item.id }],
      }),
    );

    try {
      await removeBinItem(projectId, item.binId, item.id);
      onMembershipRemoved?.(item.mediaRef, item.id);
    } catch (error) {
      setListing((previous) =>
        previous && previous.binId === item.binId ? { ...previous, items: before } : previous,
      );
      onBinsChange(binsBefore);
      setMessage(messageOf(error));
    }
  };

  const renderNode = (node: BinNode): React.ReactNode => {
    const isOpen = !collapsed.has(node.id);
    const hasChildren = node.children.length > 0;
    const smart = isSmartBin(node);
    const count = smart ? countSmartMatches(node.smartFilter ?? {}, items, annotations) : countsByBinId[node.id] ?? 0;
    const moveOptions = moveTargets(bins, new Set([node.id, ...descendantsOf(bins, node.id)])).filter((b) => b.id !== node.parentBinId);

    return (
      <Box key={node.id} data-testid="bin-node" data-bin-id={node.id}>
        <Group
          gap={6}
          wrap="nowrap"
          style={{ marginLeft: node.depth * 14 }}
          onDragOver={allowDrop}
          onDrop={(event) => void handleDrop(event, node.id)}
          data-testid={`bin-drop-target-${node.id}`}
        >
          {hasChildren ? (
            <EditorIcon icon={isOpen ? IconFolderOpen : IconFolder}
              label={isOpen ? t('collapse', { name: node.name }) : t('expand', { name: node.name })}
              onClick={() => toggleExpanded(node.id)} />
          ) : smart ? (
            <IconWand size={16} />
          ) : (
            <IconFolder size={16} />
          )}

          {renamingBinId === node.id ? (
            <Group gap={4} wrap="nowrap" style={{ flex: 1 }}>
              <TextInput
                size={phone ? 'md' : 'xs'}
                styles={phone ? { input: { minHeight: 44 } } : undefined}
                aria-label={t('newNameFor', { name: node.name })}
                value={renameValue}
                onChange={(event) => setRenameValue(event.currentTarget.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') void handleRename(node.id);
                  if (event.key === 'Escape') setRenamingBinId(null);
                }}
              />
              <Button size="compact-xs" fz="xs" h={phone ? 44 : undefined} onClick={() => void handleRename(node.id)}>
                {t('save')}
              </Button>
            </Group>
          ) : (
            <Button
              size="compact-sm" fz="xs"
              variant={selectedBinId === node.id ? 'light' : 'subtle'}
              justify="left"
              style={{ flex: 1 }}
              data-testid={`bin-name-${node.id}`}
              aria-pressed={selectedBinId === node.id}
              onClick={() => onSelect(node.id)}
              draggable={!phone}
              onDragStart={(event: React.DragEvent<HTMLButtonElement>) => {
                if (event.dataTransfer) writeDragPayload(event.dataTransfer, { kind: 'bin', binId: node.id });
              }}
            >
              {node.name}
            </Button>
          )}

          {isSmartBin(node) ? (
            <Tooltip label={ruleText(t, locale, node.smartFilter ?? {})}>
              <Badge size="xs" fz="xs" variant="light" color="violet" data-testid={`bin-smart-${node.id}`}>
                {t('smart')}
              </Badge>
            </Tooltip>
          ) : null}

          <Text
            size="xs"
            c="dimmed"
            data-testid={`bin-count-${node.id}`}
            data-count={count}
            style={{ whiteSpace: 'nowrap' }}
          >
            {smart ? t('matches', { count }) : t('itemCount', { count })}
          </Text>

          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <EditorIcon icon={IconDots} label={t('actionsFor', { name: node.name })} data-testid={`bin-menu-${node.id}`} />
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item
                mih={44}
                data-testid={`bin-action-rename-${node.id}`}
                onClick={() => {
                  setRenamingBinId(node.id);
                  setRenameValue(node.name);
                }}
              >
                {t('rename')}
              </Menu.Item>
              {smart ? (
                <Menu.Item mih={44} data-testid={`bin-action-rule-${node.id}`} onClick={() => setRuleEditor({ mode: 'edit', binId: node.id })}>
                  {t('editRule')}
                </Menu.Item>
              ) : (
                <Menu.Item
                  mih={44}
                  data-testid={`bin-action-subbin-${node.id}`}
                  onClick={() => {
                    setNewBinParentId(node.id);
                    setNewBinFocus((n) => n + 1);
                  }}
                >
                  {t('newSubBin')}
                </Menu.Item>
              )}
              {phone ? null : (
                <>
                  <Menu.Divider />
                  <Menu.Label>{t('moveTo')}</Menu.Label>
                  {node.parentBinId !== null ? (
                    <Menu.Item mih={44} data-testid={`bin-action-move-root-${node.id}`} onClick={() => void moveBinTo(node.id, null)}>
                      {t('topLevel')}
                    </Menu.Item>
                  ) : null}
                  {moveOptions.map((target) => (
                    <Menu.Item key={target.id} mih={44} data-testid={`bin-action-move-${node.id}-${target.id}`} onClick={() => void moveBinTo(node.id, target.id)}>
                      {target.name}
                    </Menu.Item>
                  ))}
                  {node.parentBinId === null && moveOptions.length === 0 ? (
                    <Menu.Item mih={44} disabled>
                      {t('noOtherBinInto')}
                    </Menu.Item>
                  ) : null}
                </>
              )}
              <Menu.Divider />
              <Menu.Item mih={44} color="red" data-testid={`bin-action-delete-${node.id}`} onClick={() => setConfirmingDeleteId(node.id)}>
                {t('deleteBin')}
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>

        {isOpen && hasChildren ? (
          <Stack gap={2} mt={2}>
            {node.children.map(renderNode)}
          </Stack>
        ) : null}
      </Box>
    );
  };

  const confirmingBin = confirmingDeleteId ? bins.find((bin) => bin.id === confirmingDeleteId) ?? null : null;
  const confirmingSubBins = confirmingBin ? descendantsOf(bins, confirmingBin.id) : [];
  const confirmingClips = confirmingBin
    ? [confirmingBin.id, ...confirmingSubBins].reduce((total, id) => total + (countsByBinId[id] ?? 0), 0)
    : 0;

  const selectedBin = selectedBinId ? bins.find((bin) => bin.id === selectedBinId) ?? null : null;

  return (
    <Stack gap="sm" data-testid="bin-tree">
      <Modal
        opened={confirmingBin !== null}
        onClose={() => setConfirmingDeleteId(null)}
        title={t('deleteBin')}
        radius="md"
        centered
      >
        <Stack gap="md">
          <Text size="sm" data-testid="bin-delete-text">
            {t('deleteText', { name: confirmingBin?.name, subs: confirmingSubBins.length, clips: confirmingClips })}
          </Text>
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={() => setConfirmingDeleteId(null)} data-testid="bin-delete-cancel">
              {t('cancel')}
            </Button>
            <Button
              color="red"
              data-testid="bin-delete-confirm"
              onClick={() => {
                const id = confirmingDeleteId;
                setConfirmingDeleteId(null);
                if (id) void handleDelete(id);
              }}
            >
              {t('deleteBin')}
            </Button>
          </Group>
        </Stack>
      </Modal>
      <Box>
        <Text size="sm" fw={600}>
          {t('title')}
        </Text>
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
      </Box>

      <Box
        data-testid="bin-root-drop-target"
        role="button"
        tabIndex={0}
        aria-pressed={selectedBinId === null}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            onSelect(null);
          }
        }}
        onDragOver={allowDrop}
        onDrop={(event) => void handleDrop(event, ROOT_DROP_ID)}
        onClick={() => onSelect(null)}
        style={{
          padding: '6px 8px',
          borderRadius: 6,
          border: `1px dashed ${selectedBinId === null ? 'var(--mantine-color-blue-6)' : 'var(--mantine-color-gray-4)'}`,
          cursor: 'var(--rb-cursor-pointer)',
        }}
      >
        <Text size="sm">
          {t('allMedia')}
        </Text>
        {phone ? null : (
          <Text size="xs" c="dimmed">
            {t('dropToRemove')}
          </Text>
        )}
      </Box>

      <Stack gap={2}>{tree.map(renderNode)}</Stack>
      {phone && bins.length > 1 ? <NeedsLargerScreen feature="bin-reparent" what={t('reparentWhat')} /> : null}

      <Group gap={4} wrap="wrap">
        <TextInput
          size={phone ? 'md' : 'xs'}
          styles={phone ? { input: { minHeight: 44 } } : undefined}
          style={{ flex: '1 1 120px', minWidth: 0 }}
          placeholder={newBinParentId ? t('newInside', { name: bins.find((b) => b.id === newBinParentId)?.name ?? t('theBin') }) : t('newBinName')}
          aria-label={t('newBinName')}
          ref={newBinInput}
          value={newBinName}
          onChange={(event) => setNewBinName(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') void handleCreate();
          }}
        />
        <Button
          size="compact-sm" fz="xs"
          style={{ flex: '0 0 auto' }}
          leftSection={<IconPlus size={14} />}
          onClick={() => void handleCreate()}
        >
          {t('addBin')}
        </Button>
      </Group>
      {newBinParentId ? (
        <Group gap={4} data-testid="new-bin-parent">
          <Text size="xs" c="dimmed">
            {t('inside', { name: bins.find((b) => b.id === newBinParentId)?.name ?? t('theBin') })}
          </Text>
          <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => setNewBinParentId(null)}>
            {t('topLevelInstead')}
          </Button>
        </Group>
      ) : null}
      <Button size="compact-sm" fz="xs" variant="subtle" leftSection={<IconWand size={14} />} onClick={() => setRuleEditor({ mode: 'create', binId: null })} data-testid="smart-bin-new">
        {t('newSmartBin')}
      </Button>
      <SmartBinEditor
        opened={ruleEditor !== null}
        mode={ruleEditor?.mode ?? 'create'}
        initialName={ruleEditor?.binId ? bins.find((b) => b.id === ruleEditor.binId)?.name ?? '' : ''}
        initialRule={ruleEditor?.binId ? bins.find((b) => b.id === ruleEditor.binId)?.smartFilter ?? {} : {}}
        items={items}
        annotations={annotations}
        onClose={() => setRuleEditor(null)}
        onSave={saveRule}
      />

      {selectedBin ? (
        <Box data-testid="bin-items-panel" data-bin-id={selectedBin.id}>
          <Divider mb={6} />
          <Text size="xs" fw={600} mb={4}>
            {selectedBin.name}
          </Text>

          {listingError ? (
            <Text size="xs" c="red" data-testid="bin-items-error">
              {t('contentsError', { error: listingError })}
            </Text>
          ) : listedItems === null ? (
            <Group gap={6}>
              <Loader size="xs" />
              <Text size="xs" c="dimmed" data-testid="bin-items-loading">
                {t('contentsLoading')}
              </Text>
            </Group>
          ) : listedItems.length === 0 ? (
            <EditorEmpty
              compact
              icon={IconFolder}
              title={isSmartBin(selectedBin) ? t('noMatch') : t('empty')}
              hint={isSmartBin(selectedBin) ? undefined : phone ? t('emptyHintPhone') : t('emptyHint')}
              testId="bin-items-empty"
            />
          ) : (
            <Stack gap={4} data-testid="bin-items-list">
              {listedItems.map((row) => (
                <Group
                  key={row.id}
                  gap={6}
                  wrap="nowrap"
                  justify="space-between"
                  data-testid={`bin-item-${row.id}`}
                  data-media-ref={row.mediaRef}
                >
                  <Stack gap={0} style={{ minWidth: 0 }}>
                    <Text size="xs" data-testid={`bin-item-name-${row.id}`}>
                      {/* A dangling pin has no file name to show; the mediaRef is the honest
                          identifier there, because it is the only thing the server still knows. */}
                      {row.fileName ?? row.mediaRef}
                    </Text>
                    <Group gap={4}>
                      {row.fileName === null ? (
                        <Badge size="xs" fz="xs" color="orange" variant="light" data-testid={`bin-item-dangling-${row.id}`}>
                          {t('sourceUnavailable')}
                        </Badge>
                      ) : (
                        <Text size="xs" c="dimmed">
                          {row.sizeBytes === null ? t('sizeNotRecorded') : formatBytes(row.sizeBytes)}
                        </Text>
                      )}
                      {row.kind ? (
                        <Text size="xs" c="dimmed">
                          · {row.kind}
                        </Text>
                      ) : null}
                    </Group>
                  </Stack>

                  {isSmartBin(selectedBin) ? null : (
                    <Menu position="bottom-end" withinPortal>
                      <Menu.Target>
                        <EditorIcon icon={IconDots} size="sm" label={t('moveItem', { name: row.fileName ?? row.mediaRef })} data-testid={`bin-item-move-${row.id}`} />
                      </Menu.Target>
                      <Menu.Dropdown>
                        <Menu.Label>{t('moveTo')}</Menu.Label>
                        {moveTargets(bins, new Set([selectedBin.id])).length === 0 ? (
                          <Menu.Item mih={44} disabled>
                            {t('noOtherBin')}
                          </Menu.Item>
                        ) : (
                          moveTargets(bins, new Set([selectedBin.id])).map((target) => (
                            <Menu.Item key={target.id} mih={44} data-testid={`bin-item-move-${row.id}-${target.id}`} onClick={() => void movePin(row, target.id)}>
                              {target.name}
                            </Menu.Item>
                          ))
                        )}
                      </Menu.Dropdown>
                    </Menu>
                  )}
                  {isSmartBin(selectedBin) ? null : (
                    <Tooltip label={t('removeFromBin')}>
                      <EditorIcon tooltip={false} icon={IconX} size="sm"
                        color="red"
                        label={t('removeItem', { name: row.fileName ?? row.mediaRef, bin: selectedBin.name })}
                        data-testid={`bin-item-remove-${row.id}`}
                        onClick={() => void handleRemoveItem(row)} />
                    </Tooltip>
                  )}
                </Group>
              ))}
            </Stack>
          )}
        </Box>
      ) : null}

      {message ? (
        <Text size="xs" c="orange" data-testid="bin-message" role="status">
          {message}
        </Text>
      ) : null}
    </Stack>
  );
}

/** P6: a smart bin carries a filter; its contents are computed, never pinned. */
export { isSmartBin };

/**
 * The pins of a clip, looked up by the parent.
 *
 * A drop is authorised from the DRAG EVENT, which carries only a mediaRef — and a `mediaRef` alone
 * cannot produce an `itemId`. The pool is where the pins live, so the parent supplies them here; a
 * clip whose pins the parent cannot resolve is refused by `resolveBinDrop` rather than removed with
 * a guessed id.
 */
export type BinPinsResolver = (mediaRef: MediaRef) => readonly MediaBinMembership[];

/** Every descendant id of `binId`, cycle-safe. Mirrors the server's explicit subtree walk. */
function descendantsOf(bins: readonly MediaBin[], binId: string): string[] {
  const byParent = new Map<string, string[]>();
  for (const bin of bins) {
    if (!bin.parentBinId || bin.parentBinId === bin.id) continue;
    const list = byParent.get(bin.parentBinId) ?? [];
    list.push(bin.id);
    byParent.set(bin.parentBinId, list);
  }

  const found: string[] = [];
  const queue = [...(byParent.get(binId) ?? [])];
  const seen = new Set<string>([binId]);
  while (queue.length > 0) {
    const id = queue.shift() as string;
    if (seen.has(id)) continue;
    seen.add(id);
    found.push(id);
    queue.push(...(byParent.get(id) ?? []));
  }
  return found;
}
