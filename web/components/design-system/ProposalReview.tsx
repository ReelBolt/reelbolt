'use client';

import { useState } from 'react';
import {
  Badge,
  Button,
  Center,
  Group,
  Loader,
  Paper,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { approveProposal, rejectProposal } from '@/lib/api/design-system';
import { useDesignProposals } from '@/lib/hooks/use-design-system';
import { useRevalidateDesignSystem } from '@/lib/hooks/use-revalidate-design-system';
import type { DesignDiffChange, DesignProposal, DesignSystemKind } from '@/lib/types/design-system';
import { ColorSwatch } from './ColorSwatch';
import { EditReasonText, PanelError } from './PanelNotes';
import {
  APPROVE_BUTTON_LABEL,
  DIFF_ADDED_LABEL,
  DIFF_AFTER_LABEL,
  DIFF_BEFORE_LABEL,
  DIFF_CHANGED_LABEL,
  DIFF_REMOVED_LABEL,
  LOCKED_LABEL,
  PROPOSALS_EMPTY,
  PROPOSAL_CHANGE_SCORE_LABEL,
  PROPOSAL_CONFLICT_BADGE,
  PROPOSAL_ERROR,
  PROPOSAL_TRIGGER_LABELS,
  proposalScore,
  REJECT_BUTTON_LABEL,
  REJECT_NOTE_LABEL,
  REJECT_NOTE_PLACEHOLDER,
} from './copy';
import { fontStackText, isFontFamilyPath } from '@/lib/design-system/font-diff';
import { TypeSpecimen } from './TypeSpecimen';
import mobile from './mobile.module.css';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface ProposalReviewProps {
  projectId: string;
  kind: DesignSystemKind;
  canEdit: boolean;
  /** Called after a decision so the tab refreshes its summaries. */
  onChanged: () => void;
}

/** The `$value.hex` of one side of a diff, when that side is a colour; null for any other shape. */
function hexOf(value: unknown): string | null {
  if (typeof value !== 'object' || value === null) return null;
  const hex = (value as Record<string, unknown>).hex;
  return typeof hex === 'string' ? hex : null;
}

/** A compact rendering of a non-colour diff value. */
function valueText(value: unknown): string | null {
  if (value === undefined || value === null) return null;
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  try {
    return JSON.stringify(value);
  } catch {
    return null;
  }
}

/** One path the proposal touches: before/after side by side, with its badges and provenance. */
function DiffRow({ entry }: { entry: DesignDiffChange }) {
  const tr = useT();
  const beforeHex = hexOf(entry.before);
  const afterHex = hexOf(entry.after);
  const beforeText = beforeHex ? null : valueText(entry.before);
  const afterText = afterHex ? null : valueText(entry.after);
  const fontPath = isFontFamilyPath(entry.path);
  const beforeFont = fontPath ? fontStackText(entry.before) : null;
  const afterFont = fontPath ? fontStackText(entry.after) : null;

  return (
    <Group gap="xs" wrap="nowrap" align="center" className={mobile.wrapRow} data-testid={`proposal-change-${entry.path}`}>
      <Text size="sm" ff="monospace" className={mobile.breakText}>{entry.path}</Text>
      {entry.conflict && (
        <Badge size="xs" variant="light" color="yellow" data-testid="proposal-conflict">
          {tr.msg(PROPOSAL_CONFLICT_BADGE)}
        </Badge>
      )}
      {entry.locked && (
        <Badge size="xs" variant="light" color="gray">
          {tr.msg(LOCKED_LABEL)}
        </Badge>
      )}
      {beforeHex ? (
        <ColorSwatch name={tr.msg(DIFF_BEFORE_LABEL)} hex={beforeHex} />
      ) : beforeText ? (
        <Text size="xs" c="dimmed">{beforeText}</Text>
      ) : null}
      {beforeFont && <TypeSpecimen compact family={beforeFont} />}
      {afterHex ? (
        <ColorSwatch name={tr.msg(DIFF_AFTER_LABEL)} hex={afterHex} />
      ) : afterText ? (
        <Text size="xs" c="dimmed">{afterText}</Text>
      ) : null}
      {afterFont && <TypeSpecimen compact family={afterFont} />}
      {entry.prov?.loc && (
        <Text size="xs" c="dimmed" data-testid={`proposal-loc-${entry.path}`}>{entry.prov.loc}</Text>
      )}
    </Group>
  );
}

/** One pending proposal: its trigger, change score, counts, rows and the Approve/Reject pair. */
function ProposalCard({
  projectId,
  proposal,
  canEdit,
  reasonId,
  onChanged,
}: {
  projectId: string;
  proposal: DesignProposal;
  canEdit: boolean;
  /** Id of the visible sentence that says why the buttons are disabled. */
  reasonId: string;
  onChanged: () => void;
}) {
  const tr = useT();
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);

  const diff = proposal.diff;
  const counts = diff?.counts ?? { added: 0, removed: 0, changed: 0 };
  const rows = [...(diff?.changed ?? []), ...(diff?.added ?? []), ...(diff?.removed ?? [])];

  const act = async (action: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await action();
      onChanged();
    } catch (e) {
      notifications.show({
        color: 'red',
        message: describeError(tr, e, tr.msg(PROPOSAL_ERROR)),
      });
    } finally {
      setBusy(false);
    }
  };

  const disabled = !canEdit || busy;

  return (
    <Paper withBorder radius="lg" p="md" data-testid={`proposal-${proposal.id}`}>
      <Stack gap="sm">
        <Group justify="space-between" align="center" wrap="nowrap" className={mobile.wrapRow}>
          <Text fw={600}>
            {(PROPOSAL_TRIGGER_LABELS[proposal.trigger] ? tr.msg(PROPOSAL_TRIGGER_LABELS[proposal.trigger]) : proposal.trigger)}
          </Text>
          <Group gap="xs" wrap="nowrap">
            <Text size="xs" c="dimmed">{tr.msg(PROPOSAL_CHANGE_SCORE_LABEL)}</Text>
            <Text size="sm" fw={600} data-testid={`proposal-score-${proposal.id}`}>
              {proposalScore(proposal.driftScore)}
            </Text>
          </Group>
        </Group>

        <Group gap="xs">
          <Badge variant="light" color="green" data-testid={`proposal-added-${proposal.id}`}>
            {tr.msg(DIFF_ADDED_LABEL)} {counts.added}
          </Badge>
          <Badge variant="light" color="red" data-testid={`proposal-removed-${proposal.id}`}>
            {tr.msg(DIFF_REMOVED_LABEL)} {counts.removed}
          </Badge>
          <Badge variant="light" color="blue" data-testid={`proposal-changed-${proposal.id}`}>
            {tr.msg(DIFF_CHANGED_LABEL)} {counts.changed}
          </Badge>
        </Group>

        {rows.length > 0 && (
          <Stack gap="xs">
            {rows.map((entry) => (
              <DiffRow key={`${entry.path}`} entry={entry} />
            ))}
          </Stack>
        )}

        <Group gap="xs" align="flex-end" wrap="nowrap" className={mobile.wrapRow}>
          <TextInput
            label={tr.msg(REJECT_NOTE_LABEL)}
            placeholder={tr.msg(REJECT_NOTE_PLACEHOLDER)}
            value={note}
            disabled={disabled}
            onChange={(event) => setNote(event.currentTarget.value)}
            data-testid={`proposal-note-${proposal.id}`}
            style={{ flex: 1 }}
          />
          <Button
            size="sm"
            disabled={disabled}
            aria-describedby={canEdit ? undefined : reasonId}
            onClick={() =>
              void act(() =>
                note ? approveProposal(projectId, proposal.id, note) : approveProposal(projectId, proposal.id),
              )
            }
          >
            {tr.msg(APPROVE_BUTTON_LABEL)}
          </Button>
          <Button
            size="sm"
            variant="light"
            disabled={disabled}
            aria-describedby={canEdit ? undefined : reasonId}
            onClick={() =>
              void act(() => rejectProposal(projectId, proposal.id, note || undefined))
            }
          >
            {tr.msg(REJECT_BUTTON_LABEL)}
          </Button>
        </Group>
      </Stack>
    </Paper>
  );
}

/**
 * The Proposals sub-tab (P3-04, SPEC §9/§13.4): every Pending proposal of one system, with its
 * counts, before/after rows (colour swatches with the hex beside them), provenance `file:line`,
 * conflict/locked badges and the Approve / Reject-with-note pair. Edit authority is the API's 403;
 * this gate only avoids buttons that could not work.
 */
export function ProposalReview({ projectId, kind, canEdit, onChanged }: ProposalReviewProps) {
  const tr = useT();
  const { data, error, isLoading, mutate } = useDesignProposals(projectId, kind);
  const revalidate = useRevalidateDesignSystem(projectId);
  // Two reviews (brand, product) can sit on one page, so the id is per kind.
  const reasonId = `proposals-edit-reason-${kind}`;
  const proposals = (data ?? []).filter((proposal) => proposal.status === 'Pending');

  if (isLoading && !data) {
    return <Center h={80}><Loader size="sm" /></Center>;
  }

  if (error && !data) {
    return <PanelError onRetry={() => void mutate()} />;
  }

  if (proposals.length === 0) {
    return <Text size="sm" c="dimmed" data-testid="proposals-empty">{tr.msg(PROPOSALS_EMPTY)}</Text>;
  }

  return (
    <Stack gap="md" data-testid="proposal-review">
      {!canEdit && <EditReasonText id={reasonId} />}
      {proposals.map((proposal) => (
        <ProposalCard
          key={proposal.id}
          projectId={projectId}
          proposal={proposal}
          canEdit={canEdit}
          reasonId={reasonId}
          onChanged={() => {
            void mutate();
            // Approving a proposal makes its version current: History, tokens and editor defaults follow.
            void revalidate();
            onChanged();
          }}
        />
      ))}
    </Stack>
  );
}
