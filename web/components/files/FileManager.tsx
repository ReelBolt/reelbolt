'use client';
/* eslint-disable i18next/no-literal-string -- the remaining literals are ids, enum keys and t.rich tag handlers, not user-visible text */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Checkbox, Group, Menu, Modal, SegmentedControl, Select, Stack, Text, TextInput, Drawer,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import {
  IconAlertCircle, IconArrowsMove, IconCheck, IconChevronRight, IconDotsVertical, IconDownload, IconFolder, IconFolderPlus,
  IconLayoutGrid, IconList, IconPencil, IconPhotoEdit, IconRefresh, IconSearch, IconWand, IconStack2, IconTrash, IconUpload, IconX,
} from '@tabler/icons-react';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import { VideoThumb } from '@/components/outputs/VideoThumb';
import { CheckedImage } from '@/components/outputs/CheckedVideo';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import {
  createFolder, deleteFile, deleteFolder, downloadFile, getFilePictureUsage, moveProjectFiles, projectFilePreviewUrl, projectFileUrl, reindexProjectFile,
  reindexProjectFiles, renameFolder, searchProjectFiles,
} from '@/lib/api/files';
import {
  baseName, buildFolderTree, dirOf, fileKind, fileScope, findFolder, joinPath, listFolder, normalizeDir,
  parentDir, timeAgo, type FileKind, type FileScope, type FileSortKey, type FolderNode,
} from '@/lib/files/model';
import { describeDeleteFailures } from '@/lib/files/delete-failures';
import { fromFileList, type PendingUpload } from '@/lib/files/drop';
import { zipsOverLimit } from '@/lib/files/zip-warning';
import { useStorageUsage } from '@/lib/hooks/use-storage';
import { canEditAsThumbnail } from '@/lib/editor/open-thumbnail-editor';
import { useWindowFileDrop } from '@/lib/hooks/use-file-drop';
import { useUploadQueue } from '@/lib/hooks/use-upload-queue';
import type { FileSearchHit, ProjectFile } from '@/lib/types/project';
import type { AvatarFileUse } from '@/lib/avatars/model';
import { PHONE_MEDIA_QUERY } from '@/lib/editing/device-class';
import { KIND_LABEL, KindIcon } from './file-kind';
import { useLongPressSelect } from './useLongPressSelect';
import { FilePreviewDrawer } from './FilePreviewDrawer';
import { FolderTree, INTERNAL_DRAG_TYPE, type FolderAction } from './FolderTree';
import { MoveDialog } from './MoveDialog';
import { RenameFileDialog } from './RenameFileDialog';
import { plainErrorMessage } from '@/lib/api/network-error';
import { UnpackStatus } from './UnpackStatus';
import { UploadTray } from './UploadTray';
import { ACCEPTED_TYPES_NOTE, findDuplicates, isUnusualType, withUniqueNames } from '@/lib/files/upload-guidance';
import { useFormat, useT } from '@/i18n';
import { formatBytes } from '@/lib/utils/bytes';
import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import classes from './files.module.css';

const SORTS: Record<string, { key: FileSortKey; dir: 'asc' | 'desc'; label: Msg }> = {
  newest: { key: 'modified', dir: 'desc', label: msg('files.manager.sort.newest') },
  oldest: { key: 'modified', dir: 'asc', label: msg('files.manager.sort.oldest') },
  nameAsc: { key: 'name', dir: 'asc', label: msg('files.manager.sort.nameAsc') },
  nameDesc: { key: 'name', dir: 'desc', label: msg('files.manager.sort.nameDesc') },
  largest: { key: 'size', dir: 'desc', label: msg('files.manager.sort.largest') },
  smallest: { key: 'size', dir: 'asc', label: msg('files.manager.sort.smallest') },
};

/**
 * The delete dialog's line about pictures made from the photos being deleted (decision D10: each picture
 * keeps its own copy), or null when no picture uses them.
 */
export function pictureUsageSentence(t: T, pictures: number, photos: number): string | null {
  if (!(pictures > 0)) return null;
  return t('pictureUsage', { pictures, photos });
}

const SCOPE_LABEL: Record<FileScope, Msg> = {
  all: msg('files.manager.scope.all'), sources: msg('files.manager.scope.sources'),
  generated: msg('files.manager.scope.generated'), renders: msg('files.manager.scope.renders'),
};

function useStored<T extends string>(key: string, initial: T): [T, (v: T) => void] {
  const [value, setValue] = useState<T>(initial);
  useEffect(() => {
    try {
      const v = window.localStorage.getItem(key);
      if (v) setValue(v as T); // eslint-disable-line react-hooks/set-state-in-effect -- hydrate after mount to match SSR markup
    } catch { /* storage unavailable */ }
  }, [key]);
  return [value, (v: T) => { setValue(v); try { window.localStorage.setItem(key, v); } catch { /* ignore */ } }];
}

interface Props {
  projectId: string;
  files: ProjectFile[];
  onChanged: () => Promise<unknown> | void;
  /** Lets the page open the native file picker from its own header button. */
  registerPicker?: (open: () => void) => void;
  /** Opens an image in the thumbnail editor. When absent the menu item is not offered. */
  onEditAsThumbnail?: (file: ProjectFile) => void;
  /**
   * The avatars that use each file (docs/design/avatars.md "Web", docs/design/avatar-objects.md): such a file is
   * badged "Avatar: <name>" for a person and "Object: <name>" for an object.
   */
  avatarUsesByFile?: ReadonlyMap<string, readonly AvatarFileUse[]>;
}

export function FileManager({ projectId, files, onChanged, registerPicker, onEditAsThumbnail, avatarUsesByFile }: Props) {
  const t = useT('files.manager');
  const tr = useT();
  const fm = useFormat();
  const isDesktop = useMediaQuery('(min-width: 62em)');
  const phone = useMediaQuery(PHONE_MEDIA_QUERY);
  const coarse = useMediaQuery('(pointer: coarse)');
  const { openWithPrompt } = useAssistantShell();

  const [scope, setScope] = useState<FileScope>(() => (files.some((f) => fileScope(f) === 'sources') ? 'sources' : 'all'));
  const [kind, setKind] = useState<FileKind | 'all'>('all');
  const [query, setQuery] = useState('');
  const [path, setPath] = useState('');
  const [sortId, setSortId] = useState('newest');
  const [view, setView] = useStored<'list' | 'grid'>('rf-files-view', 'list');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // Selection mode (entered by press-and-hold or the Select menu item): a tap toggles instead of opening the preview.
  const [selectMode, setSelectMode] = useState(false);
  const clearSelection = useCallback(() => { setSelected(new Set()); setSelectMode(false); }, []);
  const [preview, setPreview] = useState<ProjectFile | null>(null);
  const [treeOpen, setTreeOpen] = useState(false);
  const [moveIds, setMoveIds] = useState<string[] | null>(null);
  const [moving, setMoving] = useState(false);
  const [deleteIds, setDeleteIds] = useState<string[] | null>(null);
  const [deleting, setDeleting] = useState(false);
  // How many pictures use the images about to be deleted, keyed by the ids it was counted for.
  const [pictureUsage, setPictureUsage] = useState<{ key: string; pictures: number; photos: number } | null>(null);
  const [renaming, setRenaming] = useState<ProjectFile | null>(null);
  const [reindexOpen, setReindexOpen] = useState(false);
  const [folderDialog, setFolderDialog] = useState<{ action: FolderAction; node: FolderNode | null } | null>(null);
  const [folderName, setFolderName] = useState('');
  const [folderBusy, setFolderBusy] = useState(false);
  const [dropRow, setDropRow] = useState<string | null>(null);

  const deleteKey = deleteIds ? deleteIds.join(',') : null;
  useEffect(() => {
    if (!deleteKey) return;
    const photos = deleteKey.split(',').filter((id) => {
      const f = files.find((x) => x.id === id);
      return f ? canEditAsThumbnail(f) : false;
    });
    if (photos.length === 0) return;
    let live = true;
    // A count that cannot be read just leaves the line out; deleting works the same either way.
    void Promise.all(photos.map((id) => getFilePictureUsage(projectId, id).catch(() => 0))).then((counts) => {
      if (live) setPictureUsage({ key: deleteKey, pictures: counts.reduce((a, b) => a + b, 0), photos: photos.length });
    });
    return () => {
      live = false;
    };
  }, [deleteKey, files, projectId]);
  const usageLine =
    pictureUsage && pictureUsage.key === deleteKey ? pictureUsageSentence(t, pictureUsage.pictures, pictureUsage.photos) : null;

  // Folders created in the UI have no files yet, and the API only materializes a folder when a
  // file lands in it, so they are remembered on this device until then.
  const vfKey = `rf-virtual-folders:${projectId}`;
  const [virtualFolders, setVirtualFolders] = useState<string[]>([]);
  useEffect(() => {
    try {
      const raw = window.localStorage.getItem(vfKey);
      if (raw) setVirtualFolders(JSON.parse(raw) as string[]); // eslint-disable-line react-hooks/set-state-in-effect -- hydrate after mount
    } catch { /* ignore */ }
  }, [vfKey]);
  const saveVirtual = useCallback((next: string[]) => {
    setVirtualFolders(next);
    try { window.localStorage.setItem(vfKey, JSON.stringify(next)); } catch { /* ignore */ }
  }, [vfKey]);

  const scoped = useMemo(() => files.filter((f) => scope === 'all' || fileScope(f) === scope), [files, scope]);
  const tree = useMemo(() => buildFolderTree(scoped, virtualFolders), [scoped, virtualFolders]);
  const currentPath = findFolder(tree, path) ? normalizeDir(path) : '';
  const sort = SORTS[sortId];
  const listing = useMemo(
    () => listFolder(files, tree, currentPath, { scope, kind, query }, sort),
    [files, tree, currentPath, scope, kind, query, sort],
  );
  const searching = query.trim() !== '';
  const scopeCounts = useMemo(() => {
    const c: Record<FileScope, number> = { all: files.length, sources: 0, generated: 0, renders: 0 };
    for (const f of files) c[fileScope(f)] += 1;
    return c;
  }, [files]);

  const byId = useMemo(() => new Map(files.map((f) => [f.id, f])), [files]);

  // "Open in project files" (Deliver) links here with `?file=<id>`: show that file's folder, select it and scroll to it.
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const handledFocus = useRef<string | null>(null);
  useEffect(() => {
    const wanted = new URLSearchParams(window.location.search).get('file');
    if (!wanted || handledFocus.current === wanted) return;
    const target = byId.get(wanted);
    if (!target) return;
    handledFocus.current = wanted;
    setScope('all'); // eslint-disable-line react-hooks/set-state-in-effect -- apply the link once the file is in the list
    setKind('all');
    setQuery('');
    setPath(dirOf(target));
    setSelected(new Set([wanted]));
    setFocusedId(wanted);
  }, [byId]);
  useEffect(() => {
    if (!focusedId) return;
    document.querySelector(`[data-file-id="${focusedId}"]`)?.scrollIntoView({ block: 'center' });
  }, [focusedId, listing]);

  // ---- uploads -------------------------------------------------------------------------------
  const queue = useUploadQueue(projectId, () => { void onChanged(); });
  // Files whose name already exists in the target folder wait here for a decision (keep both / skip / cancel).
  const [dupPrompt, setDupPrompt] = useState<{ items: PendingUpload[]; dupes: PendingUpload[] } | null>(null);
  const enqueueNow = useCallback((items: PendingUpload[]) => {
    if (items.length === 0) return;
    queue.enqueue(items, currentPath);
    // Uploads default into a scope the user can see them in.
    if (scope === 'renders' || scope === 'generated') setScope('all');
    const odd = items.filter(isUnusualType);
    if (odd.length > 0) {
      notifications.show({
        title: t('unusualTitle'),
        message: t('unusualMessage', { subject: odd.length === 1 ? odd[0].file.name : t('unusualMany', { count: odd.length }), note: ACCEPTED_TYPES_NOTE }),
        color: 'yellow',
      });
    }
  }, [queue, currentPath, scope, t]);
  const checkDuplicates = useCallback((items: PendingUpload[]) => {
    if (items.length === 0) return;
    const dupes = findDuplicates(items, files, currentPath);
    if (dupes.length > 0) setDupPrompt({ items, dupes });
    else enqueueNow(items);
  }, [files, currentPath, enqueueNow]);
  // A zip over the unpack limit is still stored, but it is not read: say so before it goes up. Advice only.
  const { data: storageUsage } = useStorageUsage();
  const zipLimitMb = storageUsage?.zipUnpackLimits?.maxMb ?? null;
  const [zipPrompt, setZipPrompt] = useState<{ items: PendingUpload[]; zips: PendingUpload[] } | null>(null);
  const startUpload = useCallback((items: PendingUpload[]) => {
    if (items.length === 0) return;
    const zips = zipsOverLimit(items.map((i) => ({ item: i, name: i.file.name, size: i.file.size, type: i.file.type })), zipLimitMb).map((z) => z.item);
    if (zips.length > 0) setZipPrompt({ items, zips });
    else checkDuplicates(items);
  }, [zipLimitMb, checkDuplicates]);
  const dragging = useWindowFileDrop(startUpload);

  const fileInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);
  useEffect(() => { registerPicker?.(() => fileInput.current?.click()); }, [registerPicker]);

  // Drop folders that now exist for real from the local list.
  useEffect(() => {
    if (virtualFolders.length === 0) return;
    const real = buildFolderTree(files, []);
    const still = virtualFolders.filter((v) => !findFolder(real, v));
    if (still.length !== virtualFolders.length) saveVirtual(still); // eslint-disable-line react-hooks/set-state-in-effect -- reconcile local list with server truth
  }, [files, virtualFolders, saveVirtual]);

  // ---- actions -------------------------------------------------------------------------------
  const fail = (title: string, e: unknown) =>
    notifications.show({ title, message: plainErrorMessage(e), color: 'red' });

  const doDownload = async (file: ProjectFile) => {
    try {
      const blob = await downloadFile(projectId, file.id);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = file.originalFileName;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (e) { fail(t('downloadFailed'), e); }
  };

  const doMove = async (target: string) => {
    if (!moveIds) return;
    setMoving(true);
    try {
      await moveProjectFiles(projectId, { fileIds: moveIds, targetDirectoryPath: target || undefined });
      await onChanged();
      notifications.show({ message: t('moved', { count: moveIds.length, target: target ? `/${target}` : t('theProjectRoot') }), color: 'green' });
      clearSelection();
      setMoveIds(null);
    } catch (e) { fail(t('moveFailed'), e); } finally { setMoving(false); }
  };

  const doDelete = async () => {
    if (!deleteIds) return;
    setDeleting(true);
    try {
      const results = await Promise.allSettled(deleteIds.map((id) => deleteFile(projectId, id)));
      const failure = describeDeleteFailures(results, deleteIds.length, tr, (id) => byId.get(id)?.originalFileName ?? null);
      await onChanged();
      if (failure) notifications.show({ title: t('deletePartialTitle'), message: failure, color: 'red' });
      else notifications.show({ message: t('deleted', { count: deleteIds.length }), color: 'green' });
      clearSelection();
      setDeleteIds(null);
      setPreview((p) => (p && deleteIds.includes(p.id) ? null : p));
    } finally { setDeleting(false); }
  };

  const doReindexFile = async (f: ProjectFile) => {
    try { await reindexProjectFile(projectId, f.id); await onChanged(); notifications.show({ message: t('reindexQueuedFor', { name: f.originalFileName }), color: 'green' }); }
    catch (e) { fail(t('reindexFailed'), e); }
  };

  const doReindexProject = async () => {
    try {
      const r = await reindexProjectFiles(projectId, { maxFiles: 25, includeIndexed: false });
      await onChanged();
      notifications.show({ message: r.hasMore ? t('queuedMore', { count: r.queuedFiles }) : t('queued', { count: r.queuedFiles }), color: 'green' });
      setReindexOpen(false);
    } catch (e) { fail(t('reindexFailed'), e); }
  };

  const onFolderAction = (action: FolderAction, node: FolderNode | null) => {
    setFolderName(action === 'rename' && node ? node.name : '');
    setFolderDialog({ action, node });
  };

  const submitFolder = async () => {
    if (!folderDialog) return;
    const { action, node } = folderDialog;
    const name = folderName.trim().replace(/[\\/]+/g, '-');
    setFolderBusy(true);
    try {
      if (action === 'new') {
        if (!name) return;
        const full = joinPath(node ? node.path : currentPath, name);
        await createFolder(projectId, full);
        saveVirtual([...new Set([...virtualFolders, full])]);
        setPath(full);
      } else if (action === 'rename' && node) {
        if (!name || name === node.name) { setFolderDialog(null); return; }
        const target = joinPath(parentDir(node.path), name);
        if (node.fileCount > 0) await renameFolder(projectId, { sourcePath: node.path, targetPath: target });
        saveVirtual(virtualFolders.map((v) => (v === node.path ? target : v.startsWith(`${node.path}/`) ? target + v.slice(node.path.length) : v)));
        if (currentPath === node.path || currentPath.startsWith(`${node.path}/`)) setPath(target + currentPath.slice(node.path.length));
        await onChanged();
      } else if (action === 'delete' && node) {
        if (node.fileCount > 0) await deleteFolder(projectId, node.path, true);
        saveVirtual(virtualFolders.filter((v) => v !== node.path && !v.startsWith(`${node.path}/`)));
        if (currentPath === node.path || currentPath.startsWith(`${node.path}/`)) setPath(parentDir(node.path));
        await onChanged();
      }
      setFolderDialog(null);
    } catch (e) { fail(action === 'new' ? t('createFolderFailed') : action === 'rename' ? t('renameFolderFailed') : t('deleteFolderFailed'), e); }
    finally { setFolderBusy(false); }
  };

  const moveByDrop = async (target: string, ids: string[]) => {
    const movable = ids.filter((id) => dirOf(byId.get(id) ?? { directoryPath: undefined }) !== target);
    if (movable.length === 0) return;
    try {
      await moveProjectFiles(projectId, { fileIds: movable, targetDirectoryPath: target || undefined });
      await onChanged();
      clearSelection();
      notifications.show({ message: t('moved', { count: movable.length, target: target ? `/${target}` : t('theProjectRoot') }), color: 'green' });
    } catch (e) { fail(t('moveFailed'), e); }
  };

  const toggle = (id: string) => {
    const n = new Set(selected);
    if (n.has(id)) n.delete(id); else n.add(id);
    setSelected(n);
    if (n.size === 0) setSelectMode(false);
  };
  const startSelecting = (id: string) => { setSelectMode(true); setSelected((s) => new Set(s).add(id)); };
  const selecting = selectMode && selected.size > 0;
  const longPress = useLongPressSelect(!!(phone || coarse), startSelecting);
  const lastClicked = useRef<string | null>(null);
  // Desktop: shift-click on a checkbox selects the range since the last one clicked.
  const toggleWithRange = (id: string, shift: boolean) => {
    const from = lastClicked.current ? visibleIds.indexOf(lastClicked.current) : -1;
    const to = visibleIds.indexOf(id);
    if (shift && from >= 0 && to >= 0) {
      const [a, b] = from < to ? [from, to] : [to, from];
      setSelected((s) => { const n = new Set(s); for (const x of visibleIds.slice(a, b + 1)) n.add(x); return n; });
    } else toggle(id);
    lastClicked.current = id;
  };
  useEffect(() => {
    if (selected.size === 0) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') clearSelection(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [selected.size, clearSelection]);
  const downloadSelected = async () => {
    for (const id of selected) { const f = byId.get(id); if (f) await doDownload(f); }
  };
  const visibleIds = listing.files.map((f) => f.id);
  const allSelected = visibleIds.length > 0 && visibleIds.every((id) => selected.has(id));

  const startDrag = (e: React.DragEvent, file: ProjectFile) => {
    const ids = selected.has(file.id) ? [...selected] : [file.id];
    e.dataTransfer.setData(INTERNAL_DRAG_TYPE, JSON.stringify(ids));
    e.dataTransfer.effectAllowed = 'move';
  };

  const noFilesAtAll = files.length === 0;
  const filtersActive = kind !== 'all' || searching;
  const segments = currentPath ? currentPath.split('/') : [];

  const treeNode = (
    <FolderTree
      root={tree} current={currentPath}
      onSelect={(p) => { setPath(p); setTreeOpen(false); setQuery(''); }}
      onFolderAction={onFolderAction}
      onMoveDrop={(t, ids) => void moveByDrop(t, ids)}
    />
  );

  return (
    <div className={classes.shell}>
      {/* Hidden native pickers: the visible Upload button is the only affordance a phone has. */}
      <input ref={fileInput} type="file" multiple hidden onChange={(e) => { startUpload(fromFileList(e.currentTarget.files)); e.currentTarget.value = ''; }} />
      <input
        ref={folderInput} type="file" hidden multiple
        {...({ webkitdirectory: '', directory: '' } as Record<string, string>)}
        onChange={(e) => { startUpload(fromFileList(e.currentTarget.files)); e.currentTarget.value = ''; }}
      />

      <aside className={classes.treePane} aria-label={t('folderNav')}>
        <Group justify="space-between" mb={4} pl={8}>
          <Text size="sm" fw={600}>{t('folders')}</Text>
          <ActionIcon variant="subtle" color="gray" size={32} aria-label={t('newFolder')} onClick={() => onFolderAction('new', null)}><IconFolderPlus size={16} /></ActionIcon>
        </Group>
        {treeNode}
      </aside>

      <Drawer opened={!isDesktop && treeOpen} onClose={() => setTreeOpen(false)} closeButtonProps={{ size: 44 }} position="left" size="min(88vw, 340px)" title={t('folders')}>
        {treeNode}
        <Button mt="md" fullWidth variant="light" leftSection={<IconFolderPlus size={16} />} onClick={() => { setTreeOpen(false); onFolderAction('new', null); }} style={{ minHeight: 44 }}>{t('newFolder')}</Button>
      </Drawer>

      <Stack gap="md" style={{ minWidth: 0 }}>
        <UnpackStatus projectId={projectId} files={files} onChanged={onChanged} />
        <div className={classes.toolbar}>
          <div className={classes.toolbarRow}>
            <Menu position="bottom-start" withinPortal>
              <Menu.Target>
                <Button leftSection={<IconUpload size={16} />} style={{ minHeight: 44, flex: 'none' }}>{t('uploadFiles')}</Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item leftSection={<IconUpload size={16} />} onClick={() => fileInput.current?.click()}>{t('chooseFiles')}</Menu.Item>
                <Menu.Item leftSection={<IconFolder size={16} />} onClick={() => folderInput.current?.click()}>{t('chooseFolder')}</Menu.Item>
              </Menu.Dropdown>
            </Menu>
            <TextInput
              aria-label={t('filterLabel')}
              placeholder={t('filterPlaceholder')}
              leftSection={<IconSearch size={16} />}
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              rightSection={query ? <ActionIcon variant="subtle" color="gray" size={32} aria-label={t('clearFilter')} onClick={() => setQuery('')}><IconX size={14} /></ActionIcon> : null}
              style={{ flex: 1, minWidth: 0 }}
              size="md"
            />
            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <ActionIcon variant="default" size={44} aria-label={t('moreActions')}><IconDotsVertical size={18} /></ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item leftSection={<IconFolderPlus size={16} />} onClick={() => onFolderAction('new', null)}>{t('newFolderHere')}</Menu.Item>
                <Menu.Item leftSection={<IconRefresh size={16} />} onClick={() => setReindexOpen(true)}>{t('reindexForSearch')}</Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </div>

          <div className={classes.hScroll}>
            {!isDesktop && (
              <Button variant="default" leftSection={<IconFolder size={16} />} onClick={() => setTreeOpen(true)} style={{ minHeight: 36 }}>
                {currentPath ? baseName(currentPath) : t('folders')}
              </Button>
            )}
            <SegmentedControl
              aria-label={t('whichFiles')}
              value={scope}
              onChange={(v) => { setScope(v as FileScope); setPath(''); }}
              data={(['sources', 'generated', 'renders', 'all'] as FileScope[]).map((s) => ({ value: s, label: `${t.msg(SCOPE_LABEL[s])} ${scopeCounts[s]}` }))}
            />
            {(['all', 'video', 'audio', 'image', 'text'] as const).map((k) => (
              <button key={k} type="button" className={classes.chip} aria-pressed={kind === k} onClick={() => setKind(k)}>{t.msg(KIND_LABEL[k])}</button>
            ))}
            <div className={classes.toolbarEnd}>
              <Select
                aria-label={t('sortLabel')} value={sortId} onChange={(v) => v && setSortId(v)} allowDeselect={false}
                data={Object.entries(SORTS).map(([value, s]) => ({ value, label: t.msg(s.label) }))} miw={160}
              />
              <SegmentedControl
                aria-label={t('view')}
                value={view} onChange={(v) => setView(v as 'list' | 'grid')}
                data={[
                  { value: 'list', label: <IconList size={16} aria-label={t('listView')} /> },
                  { value: 'grid', label: <IconLayoutGrid size={16} aria-label={t('gridView')} /> },
                ]}
              />
            </div>
          </div>
        </div>

        <Text size="xs" c="dimmed" data-testid="file-scope-legend">
          {t('scopeLegend', { count: scopeCounts.all })}
        </Text>

        {!searching && (
          <nav aria-label={t('currentFolder')} style={{ overflowX: 'auto' }}>
            <Group gap={2} wrap="nowrap" style={{ whiteSpace: 'nowrap' }}>
              <Button variant="subtle" color={currentPath ? 'gray' : 'violet'} size="compact-md" leftSection={<IconStack2 size={15} />} onClick={() => setPath('')} aria-current={!currentPath}>{t('allFiles')}</Button>
              {segments.map((seg, i) => {
                const p = segments.slice(0, i + 1).join('/');
                return (
                  <Group key={p} gap={2} wrap="nowrap">
                    <IconChevronRight size={14} style={{ opacity: 0.5 }} aria-hidden />
                    <Button variant="subtle" color={i === segments.length - 1 ? 'violet' : 'gray'} size="compact-md" onClick={() => setPath(p)} aria-current={i === segments.length - 1 ? 'page' : undefined}>{seg}</Button>
                  </Group>
                );
              })}
            </Group>
          </nav>
        )}

        {searching && <SemanticSearch projectId={projectId} query={query.trim()} byId={byId} onOpen={setPreview} />}

        {selected.size > 0 && (
          <Group justify="space-between" wrap="wrap" p="xs" px="md" style={{ borderRadius: 12, background: 'var(--mantine-color-violet-light)' }} role="region" aria-label={t('selection')}>
            <Group gap="xs">
              <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('exitSelection')} onClick={clearSelection}><IconX size={18} /></ActionIcon>
              <Text size="sm" fw={600}>{t('selectedCount', { count: selected.size })}</Text>
            </Group>
            <Group gap="xs">
              {!allSelected && (
                <Button size="sm" variant="subtle" color="gray" onClick={() => setSelected(new Set(visibleIds))} style={{ minHeight: 44 }}>{t('selectAllShort')}</Button>
              )}
              <Button size="sm" variant="white" leftSection={<IconDownload size={15} />} onClick={() => void downloadSelected()} style={{ minHeight: 44 }}>{t('download')}</Button>
              <Button size="sm" variant="white" leftSection={<IconArrowsMove size={15} />} onClick={() => setMoveIds([...selected])} style={{ minHeight: 44 }}>{t('move')}</Button>
              <Button size="sm" variant="white" color="red" leftSection={<IconTrash size={15} />} onClick={() => setDeleteIds([...selected])} style={{ minHeight: 44 }}>{t('delete')}</Button>
              <Button size="sm" variant="subtle" color="gray" onClick={clearSelection} style={{ minHeight: 44 }}>{t('clear')}</Button>
            </Group>
          </Group>
        )}
        <div role="status" aria-live="polite" style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clip: 'rect(0 0 0 0)' }}>
          {selected.size > 0 ? t('selectedCount', { count: selected.size }) : ''}
        </div>

        {noFilesAtAll ? (
          <Stack align="center" gap="sm" py={48} px="md" style={{ border: '2px dashed var(--mantine-color-default-border)', borderRadius: 16 }}>
            <IconUpload size={34} stroke={1.4} aria-hidden />
            <Text fw={650} size="lg" ta="center">{t('emptyTitle')}</Text>
            <Text size="sm" c="dimmed" ta="center" maw={420}>
              {t('emptyBody')}
            </Text>
            <Text size="xs" c="dimmed" ta="center" maw={460}>{ACCEPTED_TYPES_NOTE}</Text>
            <Group>
              <Button leftSection={<IconUpload size={16} />} onClick={() => fileInput.current?.click()} style={{ minHeight: 44 }}>{t('uploadFiles')}</Button>
              <Button variant="default" leftSection={<IconWand size={16} />} style={{ minHeight: 44 }}
                onClick={() => openWithPrompt(t('whatToUploadPrompt'), { projectId }, { send: true })}>
                {t('whatToUpload')}
              </Button>
            </Group>
          </Stack>
        ) : listing.folders.length === 0 && listing.files.length === 0 ? (
          searching && query.trim().length >= 3 ? (
            <Text size="sm" c="dimmed" py="xs">{t('noNameMatch', { query: query.trim() })}</Text>
          ) : (
          <Stack align="center" gap="xs" py={40}>
            <Text fw={600}>{searching || filtersActive ? t('nothingMatches') : currentPath ? t('folderEmpty') : t('noScopeFiles', { scope })}</Text>
            <Text size="sm" c="dimmed" ta="center">
              {searching || filtersActive ? t('tryAnother') : t('dropHere')}
            </Text>
            {(searching || filtersActive) ? (
              <Button variant="light" onClick={() => { setQuery(''); setKind('all'); }} style={{ minHeight: 44 }}>{t('clearFilters')}</Button>
            ) : scope !== 'all' && scopeCounts.all > 0 ? (
              <Button variant="light" onClick={() => setScope('all')} style={{ minHeight: 44 }}>{t('showAll')}</Button>
            ) : null}
          </Stack>
          )
        ) : view === 'list' ? (
          <div className={classes.list} role="list">
            {listing.folders.map((f) => (
              <div key={f.path} role="listitem" className={classes.row} data-drop={dropRow === f.path}
                onDragOver={(e) => { if (e.dataTransfer.types.includes(INTERNAL_DRAG_TYPE)) { e.preventDefault(); setDropRow(f.path); } }}
                onDragLeave={() => setDropRow((d) => (d === f.path ? null : d))}
                onDrop={(e) => { setDropRow(null); const raw = e.dataTransfer.getData(INTERNAL_DRAG_TYPE); if (raw) { e.preventDefault(); void moveByDrop(f.path, JSON.parse(raw) as string[]); } }}>
                <span style={{ width: 44 }} aria-hidden />
                <button type="button" className={classes.item} onClick={() => { setPath(f.path); setQuery(''); }} aria-label={t('openFolder', { name: f.name })}>
                  <span className={classes.fileIcon} style={{ background: 'var(--mantine-color-yellow-light)', color: 'var(--mantine-color-yellow-light-color)' }}><IconFolder size={19} stroke={1.6} /></span>
                  <div className={classes.itemMain}>
                    <Text size="sm" fw={600} truncate style={{ fontStyle: f.virtual ? 'italic' : undefined }}>{f.name}</Text>
                    <Text size="xs" c="dimmed">{f.virtual ? t('virtualEmpty') : t('count', { count: f.fileCount })}</Text>
                  </div>
                  <IconChevronRight size={16} style={{ opacity: 0.4 }} aria-hidden />
                </button>
                <Menu position="bottom-end" withinPortal>
                  <Menu.Target><ActionIcon variant="subtle" color="gray" size={44} aria-label={t('actionsForFolder', { name: f.name })}><IconDotsVertical size={16} /></ActionIcon></Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item onClick={() => onFolderAction('rename', f)}>{t('rename')}</Menu.Item>
                    <Menu.Item color="red" onClick={() => onFolderAction('delete', f)}>{t('delete')}</Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              </div>
            ))}
            {listing.files.length > 0 && (
              <div className={classes.row} style={{ minHeight: 40, background: 'transparent' }}>
                <Checkbox
                  aria-label={t('selectAll')} checked={allSelected} indeterminate={!allSelected && visibleIds.some((id) => selected.has(id))}
                  onChange={() => { if (allSelected) clearSelection(); else setSelected(new Set(visibleIds)); }}
                  styles={{ root: { width: 44, display: 'grid', placeItems: 'center' } }}
                />
                <Text size="xs" c="dimmed">{t('count', { count: listing.files.length })}</Text>
              </div>
            )}
            {listing.files.map((f) => (
              <div key={f.id} role="listitem" className={classes.row} data-file-id={f.id} data-selected={selected.has(f.id)} draggable onDragStart={(e) => startDrag(e, f)}>
                <Checkbox
                  aria-label={t('selectFile', { name: f.originalFileName })} checked={selected.has(f.id)} onChange={() => toggle(f.id)}
                  onClick={(e) => { if (e.shiftKey) { e.preventDefault(); toggleWithRange(f.id, true); } else lastClicked.current = f.id; }}
                  styles={{ root: { width: 44, display: 'grid', placeItems: 'center', minHeight: 44 } }}
                />
                <button
                  type="button" className={classes.item}
                  {...(selecting ? { role: 'checkbox', 'aria-checked': selected.has(f.id) } : {})}
                  {...longPress(f.id)}
                  onClick={() => (selecting ? toggle(f.id) : setPreview(f))}
                >
                  <KindIcon kind={fileKind(f)} />
                  <div className={classes.itemMain}>
                    <Text size="sm" fw={500} truncate>{f.originalFileName}</Text>
                    {avatarUsesByFile?.get(f.id)?.map((use) => (
                      <Badge key={`${use.kind}:${use.name}`} variant="light" color={use.kind === 'Object' ? 'teal' : 'grape'} fz="xs" maw="100%" data-testid="file-avatar-badge">
                        {use.kind === 'Object' ? t('objectBadge', { name: use.name }) : t('avatarBadge', { name: use.name })}
                      </Badge>
                    ))}
                    <Text size="xs" c="dimmed" truncate>
                      {searching && dirOf(f) ? `/${dirOf(f)} - ` : ''}{formatBytes(f.sizeBytes, fm)} - {timeAgo(f.uploadedAt, undefined, tr, fm.locale)}
                    </Text>
                  </div>
                  <span className={classes.itemMeta}>
                    <span>{formatBytes(f.sizeBytes, fm)}</span>
                    <span>{timeAgo(f.uploadedAt, undefined, tr, fm.locale)}</span>
                  </span>
                </button>
                <FileMenu file={f} onDownload={doDownload} onRename={setRenaming} onMove={(x) => setMoveIds([x.id])} onDelete={(x) => setDeleteIds([x.id])} onReindex={doReindexFile} onEditAsThumbnail={onEditAsThumbnail} onSelect={(x) => startSelecting(x.id)} />
              </div>
            ))}
          </div>
        ) : (
          <div className={classes.gridWrap} role="list">
            {listing.folders.map((f) => (
              <button key={f.path} type="button" role="listitem" className={classes.tile} onClick={() => { setPath(f.path); setQuery(''); }} aria-label={t('openFolder', { name: f.name })}>
                <span className={classes.tilePreview}><IconFolder size={40} stroke={1.3} color="var(--mantine-color-yellow-6)" /></span>
                <div><Text size="sm" fw={600} truncate>{f.name}</Text><Text size="xs" c="dimmed">{t('count', { count: f.fileCount })}</Text></div>
              </button>
            ))}
            {listing.files.map((f) => {
              const k = fileKind(f);
              return (
                <button
                  key={f.id} type="button" className={classes.tile} data-selected={selected.has(f.id)} draggable onDragStart={(e) => startDrag(e, f)}
                  {...(selecting ? { role: 'checkbox', 'aria-checked': selected.has(f.id) } : { role: 'listitem' })}
                  {...longPress(f.id)}
                  onClick={() => (selecting ? toggle(f.id) : setPreview(f))}
                  onKeyDown={(e) => { if (e.key === 'Enter' && e.shiftKey && !selecting) { e.preventDefault(); startSelecting(f.id); } }}
                >
                  <span className={classes.tilePreview}>
                    {selecting && (
                      <span
                        aria-hidden
                        style={{
                          position: 'absolute', top: 8, left: 8, zIndex: 1, width: 22, height: 22, display: 'grid', placeItems: 'center', borderRadius: '50%',
                          color: 'white', border: '2px solid white',
                          background: selected.has(f.id) ? 'var(--mantine-color-violet-6)' : 'rgba(0, 0, 0, 0.35)',
                        }}
                      >{selected.has(f.id) && <IconCheck size={14} />}</span>
                    )}
                    {k === 'image' ? (
                      <CheckedImage src={projectFilePreviewUrl(projectId, f.id, 320)} alt="" loading="lazy" />
                    ) : k === 'video' ? (
                      <VideoThumb src={projectFileUrl(projectId, f.id)} showPlay={false} />
                    ) : (
                      <KindIcon kind={k} size={56} />
                    )}
                  </span>
                  <div>
                    <Text size="sm" fw={500} truncate>{f.originalFileName}</Text><Text size="xs" c="dimmed">{formatBytes(f.sizeBytes, fm)}</Text>
                    {avatarUsesByFile?.get(f.id)?.map((use) => (
                      <Badge key={`${use.kind}:${use.name}`} variant="light" color={use.kind === 'Object' ? 'teal' : 'grape'} fz="xs" maw="100%">
                        {use.kind === 'Object' ? t('objectBadge', { name: use.name }) : t('avatarBadge', { name: use.name })}
                      </Badge>
                    ))}
                  </div>
                </button>
              );
            })}
          </div>
        )}
      </Stack>

      {dragging && (
        <div className={classes.dropOverlay} role="presentation">
          <div className={classes.dropBox}>
            <IconUpload size={44} stroke={1.4} color="var(--mantine-color-violet-6)" />
            <Text fw={700} size="xl">{t('dropToUpload')}</Text>
            <Text c="dimmed" size="sm">{t('dropInto', { target: currentPath ? `/${currentPath}` : t('theProjectRoot') })}</Text>
          </div>
        </div>
      )}

      <Modal
        opened={!!zipPrompt} onClose={() => setZipPrompt(null)} closeButtonProps={{ size: 44 }} centered
        title={t('zipTooBigTitle')} data-testid="zip-too-big-modal"
      >
        <Stack>
          <Text size="sm">
            {t('zipTooBigBody', { count: zipPrompt?.zips.length ?? 0, name: zipPrompt?.zips[0]?.file.name ?? '', limit: zipLimitMb ?? 0 })}
          </Text>
          <Text size="sm">{t('zipTooBigInstead')}</Text>
          <Stack component="ul" gap={4} m={0} pl="lg">
            <li><Text size="sm">{t('zipTooBigGithub')}</Text></li>
            <li><Text size="sm">{t('zipTooBigSmaller')}</Text></li>
            <li><Text size="sm">{t('zipTooBigFolders')}</Text></li>
          </Stack>
          <Group justify="flex-end">
            <Button variant="default" style={{ minHeight: 44 }} onClick={() => setZipPrompt(null)}>{t('cancelUpload')}</Button>
            {zipPrompt && zipPrompt.items.length > zipPrompt.zips.length && (
              <Button
                variant="default" style={{ minHeight: 44 }}
                onClick={() => { checkDuplicates(zipPrompt.items.filter((i) => !zipPrompt.zips.includes(i))); setZipPrompt(null); }}
              >
                {t('zipUploadOthers', { count: zipPrompt.zips.length })}
              </Button>
            )}
            <Button style={{ minHeight: 44 }} onClick={() => { if (zipPrompt) checkDuplicates(zipPrompt.items); setZipPrompt(null); }}>{t('zipUploadAnyway')}</Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={!!dupPrompt} onClose={() => setDupPrompt(null)} closeButtonProps={{ size: 44 }} centered
        title={dupPrompt && dupPrompt.dupes.length === 1 ? t('dupTitleOne') : t('dupTitleMany')}
      >
        <Stack>
          <Text size="sm">
            {t('dupBody', { count: dupPrompt?.dupes.length ?? 0, name: dupPrompt?.dupes[0]?.file.name ?? '', location: currentPath ? `/${currentPath}` : t('theProjectRoot') })}
          </Text>
          <Group justify="flex-end">
            <Button variant="default" style={{ minHeight: 44 }} onClick={() => setDupPrompt(null)}>{t('cancelUpload')}</Button>
            <Button
              variant="default" style={{ minHeight: 44 }}
              onClick={() => { if (dupPrompt) enqueueNow(dupPrompt.items.filter((i) => !dupPrompt.dupes.includes(i))); setDupPrompt(null); }}
            >
              {t('skipDuplicates')}
            </Button>
            <Button style={{ minHeight: 44 }} onClick={() => { if (dupPrompt) enqueueNow(withUniqueNames(dupPrompt.items, files, currentPath)); setDupPrompt(null); }}>{t('keepBoth')}</Button>
          </Group>
        </Stack>
      </Modal>

      {/* Room for the floating upload panel, so it never sits on top of the size and date columns. */}
      {queue.entries.length > 0 && <div aria-hidden style={{ height: 'min(55dvh, 340px)' }} />}
      <UploadTray entries={queue.entries} onRetry={queue.retry} onClear={queue.clearFinished} />

      <FilePreviewDrawer
        projectId={projectId} file={preview} siblings={listing.files} onSelect={setPreview} onClose={() => setPreview(null)}
        onSaved={() => { void onChanged(); }} onDownload={doDownload}
        onMove={(f) => setMoveIds([f.id])} onDelete={(f) => setDeleteIds([f.id])} onReindex={doReindexFile} onRename={setRenaming} onEditAsThumbnail={onEditAsThumbnail}
      />
      <MoveDialog
        key={moveIds ? moveIds.join(',') : 'closed'}
        opened={!!moveIds} count={moveIds?.length ?? 0} root={tree} currentPath={currentPath} loading={moving}
        onClose={() => setMoveIds(null)} onMove={(t) => void doMove(t)}
      />
      <ConfirmModal
        opened={!!deleteIds} onClose={() => setDeleteIds(null)} onConfirm={() => void doDelete()} loading={deleting}
        title={t('deleteTitle', { count: deleteIds?.length ?? 0 })}
        message={usageLine ? t('deleteMessageWithUsage', { usage: usageLine }) : t('deleteMessage')}
      />
      <RenameFileDialog
        projectId={projectId} file={renaming} onClose={() => setRenaming(null)}
        onRenamed={async (updated) => {
          setPreview((p) => (p && p.id === updated?.id ? { ...p, ...updated } : p));
          await onChanged();
        }}
      />
      <ConfirmModal
        opened={reindexOpen} onClose={() => setReindexOpen(false)} onConfirm={() => void doReindexProject()}
        title={t('reindexForSearch')} confirmLabel={t('queueReindex')}
        message={t('reindexMessage')}
      />
      <Modal
        opened={!!folderDialog} onClose={() => setFolderDialog(null)} centered radius="lg"
        title={folderDialog?.action === 'new' ? t('newFolder') : folderDialog?.action === 'rename' ? t('renameFolder') : t('deleteFolder')}
      >
        {folderDialog?.action === 'delete' ? (
          <Stack>
            <Text size="sm">
              {folderDialog.node && folderDialog.node.fileCount > 0
                ? t('deleteFolderWithFiles', { path: folderDialog.node.path, count: folderDialog.node.fileCount })
                : t('deleteEmptyFolder', { path: folderDialog.node?.path ?? '' })}
            </Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setFolderDialog(null)} style={{ minHeight: 44 }}>{t('cancel')}</Button>
              <Button color="red" loading={folderBusy} onClick={() => void submitFolder()} style={{ minHeight: 44 }}>{t('delete')}</Button>
            </Group>
          </Stack>
        ) : (
          <form onSubmit={(e) => { e.preventDefault(); void submitFolder(); }}>
            <Stack>
              <TextInput
                label={t('name')} data-autofocus value={folderName} onChange={(e) => setFolderName(e.currentTarget.value)}
                description={folderDialog?.action === 'new' ? t('createdInside', { location: (folderDialog.node?.path ?? currentPath) ? `/${folderDialog.node?.path ?? currentPath}` : t('theProjectRoot') }) : undefined}
              />
              <Group justify="flex-end">
                <Button variant="default" onClick={() => setFolderDialog(null)} style={{ minHeight: 44 }}>{t('cancel')}</Button>
                <Button type="submit" loading={folderBusy} disabled={!folderName.trim()} style={{ minHeight: 44 }}>{folderDialog?.action === 'new' ? t('create') : t('rename')}</Button>
              </Group>
            </Stack>
          </form>
        )}
      </Modal>
    </div>
  );
}

function FileMenu({ file, onDownload, onRename, onMove, onDelete, onReindex, onEditAsThumbnail, onSelect }: {
  file: ProjectFile;
  onDownload: (f: ProjectFile) => void; onRename: (f: ProjectFile) => void; onMove: (f: ProjectFile) => void;
  onDelete: (f: ProjectFile) => void; onReindex: (f: ProjectFile) => void; onEditAsThumbnail?: (f: ProjectFile) => void;
  onSelect: (f: ProjectFile) => void;
}) {
  const t = useT('files.manager');
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('actionsFor', { name: file.originalFileName })}><IconDotsVertical size={16} /></ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        {onEditAsThumbnail && canEditAsThumbnail(file) && (
          <Menu.Item leftSection={<IconPhotoEdit size={15} />} onClick={() => onEditAsThumbnail(file)}>{t('editAsPicture')}</Menu.Item>
        )}
        <Menu.Item leftSection={<IconCheck size={15} />} onClick={() => onSelect(file)}>{t('selectAction')}</Menu.Item>
        <Menu.Item leftSection={<IconDownload size={15} />} onClick={() => onDownload(file)}>{t('download')}</Menu.Item>
        <Menu.Item leftSection={<IconPencil size={15} />} onClick={() => onRename(file)}>{t('rename')}</Menu.Item>
        <Menu.Item leftSection={<IconArrowsMove size={15} />} onClick={() => onMove(file)}>{t('moveTo')}</Menu.Item>
        <Menu.Item leftSection={<IconRefresh size={15} />} onClick={() => onReindex(file)}>{t('reindex')}</Menu.Item>
        <Menu.Divider />
        <Menu.Item color="red" leftSection={<IconTrash size={15} />} onClick={() => onDelete(file)}>{t('delete')}</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

/** "Search inside files" by meaning. Degrades to a plain note when embeddings are not set up. */
function SemanticSearch({ projectId, query, byId, onOpen }: {
  projectId: string; query: string; byId: Map<string, ProjectFile>; onOpen: (f: ProjectFile) => void;
}) {
  const t = useT('files.manager');
  const [state, setState] = useState<{ q: string; status: 'loading' | 'ok' | 'unavailable'; hits: FileSearchHit[] } | null>(null);
  const current = state && state.q === query ? state : null;

  const run = async () => {
    setState({ q: query, status: 'loading', hits: [] });
    try {
      const res = await searchProjectFiles(projectId, query);
      setState({ q: query, status: res.indexNotReady ? 'unavailable' : 'ok', hits: res.results });
    } catch {
      setState({ q: query, status: 'unavailable', hits: [] });
    }
  };

  if (query.length < 3) return null;
  return (
    <Stack gap="xs">
      {!current && (
        <Button variant="light" leftSection={<IconWand size={16} />} onClick={() => void run()} style={{ minHeight: 44, justifyContent: 'flex-start' }}>
          {t('searchInside', { query })}
        </Button>
      )}
      {current?.status === 'loading' && <Text size="sm" c="dimmed" aria-live="polite">{t('searching')}</Text>}
      {current?.status === 'unavailable' && (
        <Alert color="gray" icon={<IconAlertCircle size={16} />} title={t('searchUnavailableTitle')}>
          <Text size="sm">{t('searchUnavailableBody')}</Text>
        </Alert>
      )}
      {current?.status === 'ok' && (
        current.hits.length === 0 ? (
          <Text size="sm" c="dimmed">{t('noContentMatch', { query })}</Text>
        ) : (
          <div className={classes.list} role="list" aria-label={t('contentMatches')}>
            {current.hits.map((h, i) => {
              const f = byId.get(h.fileId);
              return (
                <div key={`${h.fileId}-${h.chunkIndex}-${i}`} role="listitem" className={classes.row}>
                  <button type="button" className={classes.item} disabled={!f} onClick={() => f && onOpen(f)} style={{ alignItems: 'flex-start', paddingBlock: 10 }}>
                    <div className={classes.itemMain}>
                      <Group justify="space-between" wrap="nowrap" gap="xs">
                        <Text size="sm" fw={600} truncate>{h.fileName}</Text>
                        <Text size="xs" c="dimmed" style={{ fontVariantNumeric: 'tabular-nums' }}>{t('scoreMatch', { score: Math.round(h.score * 100) })}</Text>
                      </Group>
                      <Text size="xs" c="dimmed" lineClamp={2}>{h.content}</Text>
                    </div>
                  </button>
                </div>
              );
            })}
          </div>
        )
      )}
    </Stack>
  );
}
