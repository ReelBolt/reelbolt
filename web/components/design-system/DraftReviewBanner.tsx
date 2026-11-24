'use client';

import { useState } from 'react';
import { Button, Group, Paper, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { approveVersion, rejectVersion } from '@/lib/api/design-system';
import { useVersionDiff } from '@/lib/hooks/use-design-system';
import { useRevalidateDesignSystem } from '@/lib/hooks/use-revalidate-design-system';
import type { DesignSystemKind, DesignSystemSummary } from '@/lib/types/design-system';
import {
  APPROVE_BUTTON_LABEL,
  draftAlsoChanged,
  draftComparedWith,
  draftConfirmButton,
  draftConfirmFirstText,
  draftConfirmText,
  draftNamesList,
  draftReviewTitle,
  draftSectionCounts,
  draftSummaryCounts,
  DRAFT_KEEP_REVIEWING_LABEL,
  DRAFT_SUMMARY_ERROR,
  DRAFT_SUMMARY_NO_CHANGES,
  EDIT_ONLY_TOOLTIP,
  REJECT_BUTTON_LABEL,
} from './copy';
import { isEmptySummary, summarizeDraftDiff } from './draft-summary';
import mobile from './mobile.module.css';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface DraftReviewBannerProps {
  projectId: string;
  summary: DesignSystemSummary;
  kind: DesignSystemKind;
  canEdit: boolean;
  onChanged: () => void;
  /**
   * A plain-language warning about this draft (an import that found nothing, or less than the
   * current version has). It is shown in the banner and makes Approve ask for a confirm step even
   * when there is no current version to compare with.
   */
  warning?: string | null;
}

/** How many token names the summary spells out before "and N more". */
const NAMES_SHOWN = 5;

/**
 * The draft-review banner (P2-22, SPEC §17): a draft is the only thing standing between a read and
 * an approved profile, so the approve/reject pair is visible whenever one exists and disabled with
 * a visible reason when the caller's role cannot write. When there is a current version the banner
 * also summarises what the draft changes against it and asks for a confirm step before approving.
 */
export function DraftReviewBanner({
  projectId,
  summary,
  canEdit,
  onChanged,
  warning = null,
}: DraftReviewBannerProps) {
  const tr = useT();
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const revalidate = useRevalidateDesignSystem(projectId);
  const draft = summary.latestDraft;
  const current = summary.currentVersion;

  const { data: diff, error: diffError } = useVersionDiff(
    projectId,
    draft?.id ?? null,
    current?.id ?? null,
  );

  if (!draft) return null;

  const act = async (action: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await action();
      setConfirming(false);
      onChanged();
      // The History list, the draft slot and the editor's brand defaults all follow "current".
      void revalidate();
    } catch (e) {
      notifications.show({
        color: 'red',
        message: describeError(tr, e, tr('brandLibraries.profile.draftReview.updateFailed')),
      });
    } finally {
      setBusy(false);
    }
  };

  const disabled = !canEdit || busy;
  const reasonId = `draft-${draft.id}-reason`;
  const parsed = summarizeDraftDiff(diff);
  const names = parsed?.names ?? [];
  const shownNames = names.slice(0, NAMES_SHOWN);
  const approve = () => void act(() => approveVersion(projectId, draft.id));

  return (
    <Paper withBorder radius="lg" p="md" data-testid="draft-review-banner">
      <Stack gap="sm">
        <Text fw={600}>{tr.msg(draftReviewTitle(draft.versionNumber))}</Text>
        {warning && (
          <Text size="sm" c="red" role="alert" data-testid="draft-warning">
            {warning}
          </Text>
        )}

        {current && parsed && (
          <Stack gap={2} data-testid="draft-summary">
            <Text size="sm" c="dimmed">{tr.msg(draftComparedWith(current.versionNumber))}</Text>
            {parsed.lines.map((l) => (
              <Text key={l.label?.$msg ?? 'all'} size="sm" fw={600}>
                {l.label === null
                  ? tr.msg(draftSummaryCounts(l.added, l.changed, l.removed))
                  : tr.msg(draftSectionCounts(l.label, l.added, l.changed, l.removed))}
              </Text>
            ))}
            {parsed.otherSections.length > 0 && (
              <Text size="sm">{tr.msg(draftAlsoChanged(parsed.otherSections.map((section) => (typeof section === 'string' ? section : tr.msg(section)))))}</Text>
            )}
            {shownNames.length > 0 ? (
              <Text size="xs" ff="monospace">
                {tr.msg(draftNamesList(shownNames.join(', '), names.length - shownNames.length))}
              </Text>
            ) : isEmptySummary(parsed) ? (
              <Text size="xs" c="dimmed">{tr.msg(DRAFT_SUMMARY_NO_CHANGES)}</Text>
            ) : null}
          </Stack>
        )}
        {current && !parsed && !(diff === undefined && !diffError) && (
          <Text size="sm" c="dimmed" role="status">{tr.msg(DRAFT_SUMMARY_ERROR)}</Text>
        )}

        {confirming && (current || warning) ? (
          <Stack gap="xs" data-testid="draft-confirm">
            <Text size="sm">
              {current
                ? tr.msg(draftConfirmText(draft.versionNumber, current.versionNumber))
                : tr.msg(draftConfirmFirstText(draft.versionNumber))}
            </Text>
            <Group gap="xs" className={mobile.actionRow}>
              <Button size="sm" disabled={disabled} loading={busy} onClick={approve}>
                {tr.msg(draftConfirmButton(draft.versionNumber))}
              </Button>
              <Button
                size="sm"
                variant="subtle"
                disabled={busy}
                onClick={() => setConfirming(false)}
              >
                {tr.msg(DRAFT_KEEP_REVIEWING_LABEL)}
              </Button>
            </Group>
          </Stack>
        ) : (
          <Group gap="xs" className={mobile.actionRow}>
            <Button
              size="sm"
              disabled={disabled}
              aria-describedby={canEdit ? undefined : reasonId}
              onClick={() => (current || warning ? setConfirming(true) : approve())}
            >
              {tr.msg(APPROVE_BUTTON_LABEL)}
            </Button>
            <Button
              size="sm"
              variant="light"
              disabled={disabled}
              aria-describedby={canEdit ? undefined : reasonId}
              onClick={() => void act(() => rejectVersion(projectId, draft.id))}
            >
              {tr.msg(REJECT_BUTTON_LABEL)}
            </Button>
          </Group>
        )}
        {!canEdit && (
          <Text size="xs" c="dimmed" id={reasonId}>{tr.msg(EDIT_ONLY_TOOLTIP)}</Text>
        )}
      </Stack>
    </Paper>
  );
}
