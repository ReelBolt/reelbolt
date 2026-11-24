'use client';

import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { IconAlertTriangle, IconFolderCheck } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { unpackArchive, type ArchiveUnpack } from '@/lib/api/files';
import { plainErrorMessage } from '@/lib/api/network-error';
import type { ProjectFile } from '@/lib/types/project';
import { useArchiveUnpacks } from '@/lib/hooks/use-archive-unpacks';
import { useStorageUsage } from '@/lib/hooks/use-storage';

const KNOWN_CODES = new Set([
  'ARCHIVE_TOO_LARGE', 'TOO_MANY_FILES', 'TOTAL_TOO_LARGE', 'BOMB_SUSPECTED', 'NOT_A_ZIP',
  'NOTHING_TO_UNPACK', 'STORAGE_QUOTA_EXCEEDED', 'WORKER_UNAVAILABLE',
  'TOO_MANY_ENTRIES', 'ARCHIVE_INVALID', 'SOURCE_GONE', 'WRITE_FAILED', 'UNPACK_INTERRUPTED',
  'WORKER_BAD_RESPONSE', 'MANIFEST_TOO_LARGE', 'PASSWORD_PROTECTED', 'UNPACK_BLOCKED',
]);

/** After a new zip lands in the file list, re-check the unpack list at these delays (ms) so the note shows up. */
const RECHECK_AFTER_UPLOAD_MS = [500, 2000, 5000, 10000, 20000];

/** Done rows only show for a day, so the note does not sit there forever. */
const DONE_VISIBLE_MS = 24 * 60 * 60 * 1000;

export function failureKey(code: string | null): string {
  const c = code ? code.trim().toUpperCase() : '';
  if (c === 'ARCHIVE_PASSWORD_PROTECTED' || c === 'ARCHIVE_ENCRYPTED' || c === 'ZIP_PASSWORD_PROTECTED' || c === 'ENCRYPTED') return 'PASSWORD_PROTECTED';
  return c && KNOWN_CODES.has(c) ? c : 'default';
}

interface Props {
  projectId: string;
  /** Refresh the file list (the zip is gone, a folder appeared). */
  onChanged: () => Promise<unknown> | void;
  /** The listed files: the zip row to retry is found by its name (the unpack row carries no file id). */
  files: ProjectFile[];
  canEdit?: boolean;
}

export function UnpackStatus({ projectId, onChanged, files, canEdit = true }: Props) {
  const t = useT('files.unpack');
  const zipLimitMb = useStorageUsage().data?.zipUnpackLimits?.maxMb ?? null;
  const { data, mutate } = useArchiveUnpacks(projectId, () => { void onChanged(); });
  const [retrying, setRetrying] = useState<Set<string>>(new Set());
  const [errors, setErrors] = useState<Record<string, string>>({});

  // A zip that appears after the first listing was just uploaded: pick up its unpack row without a reload.
  const zipIds = files.filter((f) => /\.zip$/i.test(f.originalFileName ?? '')).map((f) => f.id);
  const knownZips = useRef<Set<string> | null>(null);
  const zipKey = zipIds.join(',');
  useEffect(() => {
    const ids = zipKey ? zipKey.split(',') : [];
    const prev = knownZips.current;
    knownZips.current = new Set(ids);
    if (!prev || !ids.some((id) => !prev.has(id))) return;
    const timers = RECHECK_AFTER_UPLOAD_MS.map((ms) => setTimeout(() => { void mutate(); }, ms));
    return () => timers.forEach(clearTimeout);
  }, [zipKey, mutate]);

  if (!data || data.length === 0) return null;

  const retry = async (row: ArchiveUnpack, fileId: string) => {
    setRetrying((s) => new Set(s).add(row.id));
    try {
      await unpackArchive(projectId, fileId);
      setErrors((e) => { const { [row.id]: _gone, ...rest } = e; return rest; });
      await mutate();
    } catch (err) {
      setErrors((e) => ({ ...e, [row.id]: plainErrorMessage(err) }));
    } finally {
      setRetrying((s) => { const n = new Set(s); n.delete(row.id); return n; });
    }
  };

  const failureText = (key: string) =>
    key === 'ARCHIVE_TOO_LARGE'
      ? zipLimitMb ? t('failure.ARCHIVE_TOO_LARGE', { limit: zipLimitMb }) : t('failure.ARCHIVE_TOO_LARGE_NOLIMIT')
      : t(`failure.${key}`);
  const zipFor = (r: ArchiveUnpack) => files.find((f) => f.originalFileName === r.archiveName);
  const now = Date.now();
  const rows = data.filter((r) => {
    if (r.status !== 'Done') return true;
    const at = Date.parse(r.finishedAt ?? r.createdAt);
    return Number.isNaN(at) || now - at < DONE_VISIBLE_MS;
  });
  // One row per zip name, newest attempt wins: an older Failed row never sits beside a newer attempt.
  const newest = new Map<string, ArchiveUnpack>();
  for (const r of rows) {
    const cur = newest.get(r.archiveName);
    if (!cur || Date.parse(r.createdAt) >= Date.parse(cur.createdAt)) newest.set(r.archiveName, r);
  }
  const shown = rows.filter((r) => newest.get(r.archiveName) === r);
  const activeNames = new Set(data.filter((r) => r.status === 'Pending' || r.status === 'Running').map((r) => r.archiveName));
  if (shown.length === 0) return null;

  return (
    <Stack gap="xs" role="region" aria-label={t('region')}>
      {shown.map((r) => {
        if (r.status === 'Pending' || r.status === 'Running') {
          return (
            <Alert key={r.id} color="blue" variant="light" icon={<Loader size={16} />}>
              {t('running', { name: r.archiveName })}
            </Alert>
          );
        }
        if (r.status === 'Done') {
          return (
            <Alert key={r.id} color="teal" variant="light" icon={<IconFolderCheck size={16} />}>
              {t('done', { name: r.archiveName, count: r.filesCreated, folder: r.targetDirectory ?? '' })}
            </Alert>
          );
        }
        return (
          <Alert key={r.id} color="red" variant="light" icon={<IconAlertTriangle size={16} />}>
            <Group justify="space-between" wrap="wrap" gap="xs">
              <Text size="sm">{t('failed', { name: r.archiveName, reason: failureText(failureKey(r.failureCode)) })}</Text>
              {canEdit && failureKey(r.failureCode) !== 'UNPACK_BLOCKED' && zipFor(r) && !activeNames.has(r.archiveName) && (
                <Button
                  size="xs" variant="light" color="red" loading={retrying.has(r.id)}
                  onClick={() => void retry(r, zipFor(r)!.id)}
                >
                  {t('retry')}
                </Button>
              )}
            </Group>
            {errors[r.id] && <Text size="xs" c="red" mt={4}>{errors[r.id]}</Text>}
          </Alert>
        );
      })}
    </Stack>
  );
}
