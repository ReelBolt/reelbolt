'use client';

import { describeError, readableServerMessage } from '@/lib/api/describe-error';
import React, { useMemo, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Alert, Box, Button, FileButton, Group, Menu, Modal, NumberInput, Progress, Select, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { IconAlertCircle, IconCopyPlus, IconDots, IconPencil, IconPhoto, IconPhotoEdit, IconPlus, IconTrash, IconUpload } from '@tabler/icons-react';
import useSWR from 'swr';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { createImageDocument, deleteImageDocument, duplicateImageDocument, getImageExports, updateImageDocument } from '@/lib/api/image-documents';
import { DEFAULT_NEW_PICTURE_SHAPE_ID, defaultPictureName, NEW_PICTURE_SHAPES, newPictureSize } from '@/lib/editor/new-picture';
import type { EditImageDocumentSummary } from '@/lib/types/image-document';
import { projectFileUrl, uploadFileWithProgress } from '@/lib/api/files';
import { documentForImage, isEditableImageType, thumbnailEditorHref } from '@/lib/editor/open-thumbnail-editor';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useImageDocuments } from '@/lib/hooks/use-image-documents';
import { useRelativeTime } from './ui/useRelativeTime';
import { useT } from '@/i18n';
import { HubListSkeleton } from './hub/TimelinesView';
import classes from './hub/hub.module.css';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { EditorIcon } from './ui';
import { useOutputCheck } from '@/lib/outputs/output-check';

/**
 * The row's picture: the newest export when the picture has been exported, else the photo it was
 * made from, else an icon (a blank picture, or a photo that was deleted from Files).
 */
function PictureTile({ projectId, doc }: { projectId: string; doc: EditImageDocumentSummary }) {
  const { data: exports } = useSWR(['image-exports', projectId, doc.id], () => getImageExports(projectId, doc.id), {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
  const [broken, setBroken] = useState(false);
  const latest = broken
    ? undefined
    : [...(exports ?? [])]
        .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
        .find((x) => x.fileUrl || x.projectFileId);
  const src = latest
    ? (latest.fileUrl ?? (latest.projectFileId ? projectFileUrl(projectId, latest.projectFileId) : null))
    : doc.sourceProjectFileId
      ? projectFileUrl(projectId, doc.sourceProjectFileId)
      : null;
  // The newest export first; if that fails, the picture's source; a source still being checked shows the icon and loads by itself.
  const check = useOutputCheck(src, { neutral: true });
  return (
    <span className={classes.tile} aria-hidden>
      {src && check.status === 'unknown' ? (
        /* eslint-disable-next-line @next/next/no-img-element -- an authenticated API route, not a static asset */
        <img key={check.remountKey} src={src} alt="" loading="lazy" onError={() => (!broken && latest ? setBroken(true) : check.onError())} />
      ) : (
        <IconPhoto size={20} stroke={1.4} color="var(--mantine-color-dimmed)" />
      )}
    </span>
  );
}

export interface PicturesPanelProps {
  projectId: string;
}

/**
 * The Editor page's Pictures view: every picture (layer document) in the project, each opening in
 * the picture editor, and a way to start one from any of the project's images. Opening an image
 * that already has a picture reuses it rather than starting a second copy.
 */
export function PicturesPanel({ projectId }: PicturesPanelProps) {
  const t = useT('editor-pictures');
  const tAll = useT();
  const relativeTime = useRelativeTime();
  const router = useRouter();
  const { data: documents, isLoading, error, mutate: refreshDocuments } = useImageDocuments(projectId);
  const { data: files } = useProjectFiles(projectId);
  const [imageId, setImageId] = useState<string | null>(null);
  const [opening, setOpening] = useState(false);
  const [openError, setOpenError] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<EditImageDocumentSummary | null>(null);
  const [newName, setNewName] = useState('');
  const [deleting, setDeleting] = useState<EditImageDocumentSummary | null>(null);
  const [busy, setBusy] = useState(false);
  const [manageError, setManageError] = useState<string | null>(null);
  const [shapeId, setShapeId] = useState<string>(DEFAULT_NEW_PICTURE_SHAPE_ID);
  const [customSize, setCustomSize] = useState<{ width: number | string; height: number | string }>({ width: 1080, height: 1080 });
  const [pictureName, setPictureName] = useState('');
  const [creating, setCreating] = useState(false);
  const [uploadPercent, setUploadPercent] = useState<number | null>(null);
  const [search, setSearch] = useState('');

  const shape = NEW_PICTURE_SHAPES.find((x) => x.id === shapeId) ?? NEW_PICTURE_SHAPES[0];
  const size = newPictureSize(shape, customSize);
  // The name stored with a new picture is the language-neutral English default (defaultPictureName); only what is shown is translated.
  const shownDefault = (s: Pick<typeof shape, 'id'>) => t(`defaultName.${s.id}`);

  const createBlank = async () => {
    if (!size) return;
    setCreating(true);
    setOpenError(null);
    try {
      const created = await createImageDocument(projectId, {
        name: pictureName.trim() || defaultPictureName(shape),
        width: size.width,
        height: size.height,
      });
      await refreshDocuments();
      router.push(thumbnailEditorHref(projectId, created.id));
    } catch (cause: unknown) {
      setOpenError(describeError(tAll, cause, t('createFailed')));
      setCreating(false);
    }
  };

  const uploadPhoto = async (file: File | null) => {
    if (!file) return;
    setUploadPercent(0);
    setOpenError(null);
    try {
      const uploaded = await uploadFileWithProgress(projectId, file, setUploadPercent);
      router.push(thumbnailEditorHref(projectId, await documentForImage(projectId, uploaded)));
    } catch (cause: unknown) {
      setOpenError(
        readableServerMessage(cause) !== null
          ? t('uploadFailedWith', { message: describeError(tAll, cause) })
          : t('uploadFailed'),
      );
      setUploadPercent(null);
    }
  };

  /** A fresh picture on the same photo, leaving the existing one untouched. */
  const startAnother = async (doc: EditImageDocumentSummary) => {
    if (!doc.sourceProjectFileId) return;
    setOpenError(null);
    try {
      const created = await createImageDocument(projectId, {
        sourceProjectFileId: doc.sourceProjectFileId,
        name: `${storedName(doc)} (new)`,
      });
      await refreshDocuments();
      router.push(thumbnailEditorHref(projectId, created.id));
    } catch (cause: unknown) {
      setOpenError(describeError(tAll, cause, t('createFailed')));
    }
  };

  const images = useMemo(() => (files ?? []).filter((f) => isEditableImageType(f.mimeType)), [files]);
  const imageOptions = images.map((f) => ({ value: f.id, label: f.originalFileName }));
  const fileName = (id: string | null) => (id ? images.find((f) => f.id === id)?.originalFileName ?? null : null);

  const start = async () => {
    const file = images.find((f) => f.id === imageId);
    if (!file) return;
    setOpening(true);
    setOpenError(null);
    try {
      router.push(thumbnailEditorHref(projectId, await documentForImage(projectId, file)));
    } catch (cause: unknown) {
      setOpenError(humanizeErrorT(t, cause, t('openFailed')));
      setOpening(false);
    }
  };

  /** What is stored and sent to the API for a picture: its own name, else the English default (never translated). */
  const storedName = (doc: EditImageDocumentSummary) => doc.name?.trim() || fileName(doc.sourceProjectFileId) || 'Untitled picture';
  /** What is shown: a known English default name reads in the person's language; a name they typed is shown as typed. */
  const displayName = (doc: EditImageDocumentSummary) => {
    const stored = storedName(doc);
    const shapeOf = NEW_PICTURE_SHAPES.find((x) => defaultPictureName(x) === stored);
    return shapeOf ? shownDefault(shapeOf) : stored;
  };

  const saveRename = async () => {
    if (!renaming) return;
    setBusy(true);
    setManageError(null);
    try {
      await updateImageDocument(projectId, renaming.id, { name: newName.trim() });
      await refreshDocuments();
      setRenaming(null);
    } catch (cause: unknown) {
      setManageError(humanizeErrorT(t, cause, t('renameFailed')));
    } finally {
      setBusy(false);
    }
  };

  const duplicate = async (doc: { id: string }) => {
    setBusy(true);
    setManageError(null);
    try {
      await duplicateImageDocument(projectId, doc.id);
      await refreshDocuments();
    } catch (cause: unknown) {
      setManageError(humanizeErrorT(t, cause, t('duplicateFailed')));
    } finally {
      setBusy(false);
    }
  };

  const confirmDelete = async () => {
    if (!deleting) return;
    setBusy(true);
    setManageError(null);
    try {
      await deleteImageDocument(projectId, deleting.id);
      await refreshDocuments();
      setDeleting(null);
    } catch (cause: unknown) {
      setManageError(humanizeErrorT(t, cause, t('deleteFailed')));
      setDeleting(null);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Stack gap="xl" className={classes.enter}>
      <Stack gap="sm">
        <Text className={classes.sectionTitle}>{t('startTitle')}</Text>
        <Text size="sm" fw={600}>
          {t('newPicture')}
        </Text>
        <Group gap={6} role="radiogroup" aria-label={t('shapeLabel')} wrap="wrap">
          {NEW_PICTURE_SHAPES.map((x) => {
            const active = x.id === shapeId;
            return (
              <UnstyledButton
                key={x.id}
                role="radio"
                aria-checked={active}
                data-testid={`picture-shape-${x.id}`}
                onClick={() => setShapeId(x.id)}
                style={{
                  padding: '6px 12px',
                  minHeight: 44,
                  borderRadius: 8,
                  border: `1px solid ${active ? 'var(--mantine-color-blue-5)' : 'var(--mantine-color-default-border)'}`,
                  background: active ? 'rgba(77,171,247,0.10)' : 'transparent',
                }}
              >
                <Text size="sm" fw={600}>
                  {t(`shapes.${x.id}.label`)}
                </Text>
                <Text size="xs" c="dimmed">
                  {t(`shapes.${x.id}.hint`, { width: x.width, height: x.height })}
                </Text>
              </UnstyledButton>
            );
          })}
        </Group>
        <Group gap="xs" align="flex-end" wrap="wrap">
          {shape.id === 'custom' && (
            <>
              <NumberInput
                label={t('width')}
                aria-label={t('widthPx')}
                w={110}
                min={16}
                max={8192}
                value={customSize.width}
                onChange={(v) => setCustomSize((c) => ({ ...c, width: v }))}
                suffix={t('pxSuffix')}
              />
              <NumberInput
                label={t('height')}
                aria-label={t('heightPx')}
                w={110}
                min={16}
                max={8192}
                value={customSize.height}
                onChange={(v) => setCustomSize((c) => ({ ...c, height: v }))}
                suffix={t('pxSuffix')}
              />
            </>
          )}
          <TextInput
            label={t('nameLabel')}
            style={{ flex: '1 1 200px', minWidth: 0 }}
            placeholder={shownDefault(shape)}
            value={pictureName}
            onChange={(e) => setPictureName(e.currentTarget.value)}
            maxLength={200}
          />
          <Button
            data-testid="picture-new-create"
            leftSection={<IconPlus size={16} stroke={1.8} />}
            onClick={() => void createBlank()}
            loading={creating}
            disabled={!size}
          >
            {t('newPicture')}
          </Button>
        </Group>

        <Text size="sm" fw={600} mt="md">
          {t('fromPhoto')}
        </Text>
        <Group gap="xs" align="flex-end" wrap="wrap">
          <Select
            size="sm"
            style={{ flex: '1 1 240px', minWidth: 0 }}
            searchable
            aria-label={t('fromImageLabel')}
            placeholder={imageOptions.length ? t('pickImage') : t('noImages')}
            data={imageOptions}
            value={imageId}
            onChange={setImageId}
            disabled={imageOptions.length === 0}
            leftSection={<IconPhoto size={16} stroke={1.6} />}
          />
          <Button
            leftSection={<IconPlus size={16} stroke={1.8} />}
            onClick={() => void start()}
            loading={opening}
            disabled={!imageId}
            style={{ flex: '0 0 auto' }}
          >
            {t('openInEditor')}
          </Button>
          <FileButton onChange={(f) => void uploadPhoto(f)} accept="image/png,image/jpeg,image/webp,image/avif,image/gif">
            {(props) => (
              <Button
                {...props}
                data-testid="picture-upload-photo"
                variant="default"
                leftSection={<IconUpload size={16} stroke={1.8} />}
                loading={uploadPercent !== null}
              >
                {t('uploadPhoto')}
              </Button>
            )}
          </FileButton>
        </Group>
        {uploadPercent !== null && (
          <Progress value={uploadPercent} size="sm" aria-label={t('uploadProgress')} />
        )}
        {openError && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
            {openError}
          </Alert>
        )}
        {manageError && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" withCloseButton onClose={() => setManageError(null)}>
            {manageError}
          </Alert>
        )}
      </Stack>

      <Stack gap="sm">
        <Text className={classes.sectionTitle}>{t('yourPictures')}</Text>
        {documents && documents.length > 6 && (
          <TextInput
            aria-label={t('search')}
            placeholder={t('search')}
            value={search}
            onChange={(e) => setSearch(e.currentTarget.value)}
          />
        )}
        {isLoading ? (
          <HubListSkeleton rows={2} />
        ) : error ? (
          <Alert icon={<IconAlertCircle size={16} />} color="yellow" variant="light">
            {t('loadFailed', { reason: humanizeErrorT(t, error, t('tryAgain')) })}
          </Alert>
        ) : !documents || documents.length === 0 ? (
          <div className={classes.empty}>
            <Stack align="center" gap="sm">
              <IconPhotoEdit size={28} stroke={1.4} color="var(--mantine-color-dimmed)" />
              <Text fw={600}>{t('emptyTitle')}</Text>
              <Text size="sm" c="dimmed" maw={420}>
                {t('emptyBody')}
              </Text>
            </Stack>
          </div>
        ) : (
          <div className={classes.list} role="list" aria-label={t('yourPictures')}>
            {documents
              .filter((doc) => !search.trim() || displayName(doc).toLowerCase().includes(search.trim().toLowerCase()))
              .map((doc) => {
              const name = displayName(doc);
              return (
                <div key={doc.id} className={classes.row} role="listitem">
                  <Link href={thumbnailEditorHref(projectId, doc.id)} className={classes.rowLink} aria-label={t('open', { name })}>
                    <PictureTile projectId={projectId} doc={doc} />
                    <Box style={{ minWidth: 0 }}>
                      <Text size="sm" fw={600} truncate>
                        {name}
                      </Text>
                      <Text className={classes.meta}>
                        {t('meta', { count: doc.layerCount, when: relativeTime(doc.updatedAt).toLowerCase() })}
                      </Text>
                    </Box>
                  </Link>
                  <Menu position="bottom-end" withinPortal>
                    <Menu.Target>
                      <EditorIcon icon={IconDots} size="lg" label={t('more', { name })} />
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item
                        leftSection={<IconPencil size={14} />}
                        onClick={() => {
                          setNewName(doc.name?.trim() || '');
                          setManageError(null);
                          setRenaming(doc);
                        }}
                      >
                        {t('rename')}
                      </Menu.Item>
                      <Menu.Item leftSection={<IconCopyPlus size={14} />} onClick={() => void duplicate(doc)} data-testid="picture-duplicate">
                        {t('duplicate')}
                      </Menu.Item>
                      {doc.sourceProjectFileId && (
                        <Menu.Item leftSection={<IconCopyPlus size={14} />} onClick={() => void startAnother(doc)}>
                          {t('startAnother')}
                        </Menu.Item>
                      )}
                      <Menu.Item
                        color="red"
                        leftSection={<IconTrash size={14} />}
                        onClick={() => {
                          setManageError(null);
                          setDeleting(doc);
                        }}
                      >
                        {t('deletePicture')}
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                </div>
              );
            })}
          </div>
        )}
      </Stack>

      <Modal opened={renaming !== null} onClose={() => setRenaming(null)} title={t('renameTitle')} centered size="sm">
        <form
          onSubmit={(e) => {
            e.preventDefault();
            void saveRename();
          }}
        >
          <Stack gap="sm">
            <TextInput
              label={t('pictureName')}
              description={t('pictureNameHint')}
              value={newName}
              onChange={(e) => setNewName(e.currentTarget.value)}
              maxLength={200}
              data-autofocus
            />
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setRenaming(null)}>
                {t('cancel')}
              </Button>
              <Button type="submit" loading={busy}>
                {t('saveName')}
              </Button>
            </Group>
          </Stack>
        </form>
      </Modal>

      <ConfirmModal
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        onConfirm={() => void confirmDelete()}
        loading={busy}
        title={t('deleteTitle')}
        message={t('deleteMessage', { name: deleting ? displayName(deleting) : '' })}
        confirmLabel={t('deletePicture')}
      />
    </Stack>
  );
}
