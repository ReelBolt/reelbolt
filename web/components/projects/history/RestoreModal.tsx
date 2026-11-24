'use client';

import { useEffect, useState } from 'react';
import { Alert, Badge, Button, Group, Loader, Modal, Stack, Text } from '@mantine/core';
import { useSWRConfig } from 'swr';
import { previewProjectHistoryRestore, restoreProjectHistory } from '@/lib/api/project-history';
import { useProjectHistoryVersions } from '@/lib/hooks/use-project-history';
import { formatBytes } from '@/lib/utils/bytes';
import type {
  ProjectHistoryRestorePreview,
  ProjectHistoryRestoreRequest,
  ProjectHistoryRestoreResult,
} from '@/lib/types/project-history';
import { useFormat, useT } from '@/i18n';
import { CHANGE_COLORS, causeMessage, changeMessage, errorReasonCode, groupChangesByKind, kindMessage, reasonMessage, versionTitle } from './labels';

const reasonCauseFallback = causeMessage('beforeRestore');

/**
 * The sibling tabs that show state a restore changes. They stay mounted while the History tab is
 * open, so a restore must mark their SWR keys stale or the Files, Workflows, Pronunciation and
 * profile tabs keep showing (and can save over) the pre-restore project.
 */
export function projectRestoreRevalidationKeys(projectId: string): string[] {
  const base = `/api/v1/projects/${projectId}`;
  return [
    '/api/v1/projects',
    base,
    `${base}/files`,
    `${base}/workflows`,
    `${base}/edit-timelines`,
    `${base}/pronunciation`,
  ];
}

/** The design-system keys are matched by prefix (summary, versions, sources, proposals, defaults). */
export function designSystemRevalidationPrefix(projectId: string): string {
  return `/api/v1/projects/${projectId}/design-system`;
}

/** Other keys matched by prefix: the trash list (?state=deleted) and an open file preview's content. */
export function restoreRevalidationPrefixes(projectId: string): string[] {
  const base = `/api/v1/projects/${projectId}`;
  return [designSystemRevalidationPrefix(projectId), `${base}/edit-timelines`, `${base}/files/`];
}

/** What a restore would change, remove and refuse, each blocked item with its plain reason. */
export function RestorePreviewList({ preview }: { preview: ProjectHistoryRestorePreview }) {
  const t = useT('projectHistory');
  const tr = useT();
  const fm = useFormat();
  const removals = preview.changes.filter((c) => c.change === 'removed');
  const changes = preview.changes.filter((c) => c.change !== 'removed');
  const nameOf = (name: string | null) => name?.trim() || t('detail.itemFallback');
  return (
    <Stack gap="sm" data-testid="restore-preview">
      {preview.changes.length === 0 && preview.blocked.length === 0 && (
        <Text size="sm">{t('restore.nothingChanged')}</Text>
      )}
      {changes.length > 0 && (
        <Stack gap={4}>
          <Text size="sm" fw={600}>{t('restore.willChange')}</Text>
          {groupChangesByKind(changes).flatMap((g) => g.changes.map((c) => (
            <Group key={`${c.kind}:${c.key}`} gap="xs" wrap="nowrap" align="flex-start">
              <Badge size="sm" variant="light" color={CHANGE_COLORS[c.change] ?? 'gray'}>{tr.msg(changeMessage(c.change))}</Badge>
              <Text size="sm">{nameOf(c.name)} <Text span size="xs" c="dimmed">{tr.msg(kindMessage(g.kind))}</Text></Text>
            </Group>
          )))}
        </Stack>
      )}
      {removals.length > 0 && (
        <Stack gap={4}>
          <Text size="sm" fw={600}>{t('restore.willRemove')}</Text>
          {removals.map((c) => (
            <Group key={`${c.kind}:${c.key}`} gap="xs" wrap="nowrap" align="flex-start">
              <Badge size="sm" variant="light" color={CHANGE_COLORS.removed}>{tr.msg(changeMessage('removed'))}</Badge>
              <Text size="sm">{nameOf(c.name)}</Text>
            </Group>
          ))}
        </Stack>
      )}
      {preview.blocked.length > 0 && (
        <Alert color="orange" title={t('restore.blockedCount', { count: preview.blocked.length })}>
          <Stack gap={4}>
            {preview.blocked.map((b) => (
              <Text key={`${b.kind}:${b.key}`} size="sm">
                <Text span fw={600}>{nameOf(b.name)}</Text>: {tr.msg(reasonMessage(b.reason))}
              </Text>
            ))}
          </Stack>
        </Alert>
      )}
      {preview.estimateBytes > 0 && (
        <Text size="sm" c="dimmed">{t('restore.estimate', { size: formatBytes(preview.estimateBytes, fm) })}</Text>
      )}
    </Stack>
  );
}

/** The outcome: restored, removed and skipped items (with reasons) and the safety version's name. */
export function RestoreResultView({ result, safetyName }: { result: ProjectHistoryRestoreResult; safetyName: string }) {
  const t = useT('projectHistory');
  const tr = useT();
  const nameOf = (name: string | null) => name?.trim() || t('detail.itemFallback');
  return (
    <Stack gap="sm" data-testid="restore-result">
      <Text size="sm">{t('restore.safety', { name: safetyName })}</Text>
      {result.restored.length > 0 && (
        <Stack gap={2}>
          <Text size="sm" fw={600}>{t('restore.restored')}</Text>
          {result.restored.map((r) => <Text key={`${r.kind}:${r.key}`} size="sm">{nameOf(r.name)}</Text>)}
        </Stack>
      )}
      {result.removed.length > 0 && (
        <Stack gap={2}>
          <Text size="sm" fw={600}>{t('restore.removed')}</Text>
          {result.removed.map((r) => <Text key={`${r.kind}:${r.key}`} size="sm">{nameOf(r.name)}</Text>)}
        </Stack>
      )}
      {result.skipped.length > 0 && (
        <Alert color="orange" title={t('restore.skipped')}>
          <Stack gap={4}>
            {result.skipped.map((s) => (
              <Text key={`${s.kind}:${s.key}`} size="sm">
                <Text span fw={600}>{nameOf(s.name)}</Text>: {tr.msg(reasonMessage(s.reason))}
              </Text>
            ))}
          </Stack>
        </Alert>
      )}
      {result.notices.length > 0 && (
        <Stack gap={2}>
          <Text size="sm" fw={600}>{t('restore.notices')}</Text>
          {result.notices.map((n) => <Text key={`${n.kind}:${n.key}:${n.code}`} size="sm">{tr.msg(reasonMessage(n.code))}</Text>)}
        </Stack>
      )}
      {result.restored.length === 0 && result.removed.length === 0 && result.skipped.length === 0 && (
        <Text size="sm">{t('restore.nothingChanged')}</Text>
      )}
    </Stack>
  );
}

export interface RestoreModalProps {
  opened: boolean;
  onClose: () => void;
  projectId: string;
  versionId: string;
  request: ProjectHistoryRestoreRequest;
  /** The name of the one item for an item restore. */
  itemName?: string;
  /** Called once a restore has finished so the page can refresh. */
  onRestored: () => void;
}

/** Preview, then confirm, then show the result. Restoring is never one click. */
export function RestoreModal({ opened, onClose, projectId, versionId, request, itemName, onRestored }: RestoreModalProps) {
  const t = useT('projectHistory');
  const tr = useT();
  const [preview, setPreview] = useState<ProjectHistoryRestorePreview | null>(null);
  const [result, setResult] = useState<ProjectHistoryRestoreResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [safetyName, setSafetyName] = useState('');
  const { mutate: refreshVersions } = useProjectHistoryVersions(opened ? projectId : null);
  const { mutate: revalidate } = useSWRConfig();
  const scope = request.scope;
  const itemsKey = JSON.stringify(request.items ?? []);

  useEffect(() => {
    if (!opened) return;
    let cancelled = false;
    previewProjectHistoryRestore(projectId, versionId, { scope, items: JSON.parse(itemsKey) as ProjectHistoryRestoreRequest['items'] })
      .then((p) => { if (!cancelled) setPreview(p); })
      .catch((e: unknown) => {
        if (cancelled) return;
        const code = errorReasonCode(e);
        setError(code ? tr.msg(reasonMessage(code)) : t('restore.previewFailed'));
      });
    return () => { cancelled = true; };
    // `tr` and `t` are stable per locale; the preview only reruns for a different target.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, projectId, versionId, scope, itemsKey]);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      const done = await restoreProjectHistory(projectId, versionId, request);
      const fresh = await refreshVersions();
      // The restore changed the project itself, so every kept-mounted tab must refetch.
      await Promise.all([
        ...projectRestoreRevalidationKeys(projectId).map((key) => revalidate(key)),
        revalidate((key) => typeof key === 'string' && restoreRevalidationPrefixes(projectId).some((p) => key.startsWith(p))),
      ]);
      const safety = fresh?.versions.find((v) => v.id === done.safetyVersionId);
      setSafetyName(safety ? versionTitle(safety, (m) => tr.msg(m)) : tr.msg(reasonCauseFallback));
      setResult(done);
      onRestored();
    } catch (e) {
      const code = errorReasonCode(e);
      setError(t('restore.failed', { reason: code ? tr.msg(reasonMessage(code)) : t('genericError') }));
    } finally {
      setBusy(false);
    }
  };

  const title = result ? t('restore.doneTitle') : scope === 'project' ? t('restore.projectTitle') : t('restore.itemTitle');
  const canConfirm = preview !== null && (preview.changes.length > 0) && !busy;
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered size="lg" fullScreen={false}>
      <Stack gap="md">
        {!result && (
          <Text size="sm">
            {scope === 'project' ? t('restore.projectIntro') : t('restore.itemIntro', { name: itemName ?? t('detail.itemFallback') })}
          </Text>
        )}
        {error && <Alert color="red" role="alert">{error}</Alert>}
        {!result && !preview && !error && (
          <Group gap="xs"><Loader size="xs" /><Text size="sm">{t('restore.previewLoading')}</Text></Group>
        )}
        {!result && preview && <RestorePreviewList preview={preview} />}
        {result && <RestoreResultView result={result} safetyName={safetyName} />}
        <Group justify="flex-end">
          {result ? (
            <Button onClick={onClose}>{t('restore.close')}</Button>
          ) : (
            <>
              <Button variant="default" onClick={onClose}>{t('restore.cancel')}</Button>
              <Button onClick={() => void confirm()} loading={busy} disabled={!canConfirm}>{t('restore.confirm')}</Button>
            </>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
