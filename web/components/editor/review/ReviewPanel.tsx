'use client';

import React, { useState } from 'react';
import { Badge, Button, Checkbox, Group, Stack, Text, Textarea } from '@mantine/core';
import { IconCheck, IconClockPlay, IconMessageCircle, IconPencil, IconTrash } from '@tabler/icons-react';
import type { Rational } from '@/lib/editing/types';
import { framesToTimecode } from '@/lib/editing/time';
import { createReviewComment, deleteReviewComment, threadComments, updateReviewComment, type ReviewComment } from '@/lib/editing/api/editor-p9';
import { useReviewComments } from '@/lib/hooks/use-editor-p9';
import { useCoarsePointer } from '@/components/editor/thumbnail/useCoarsePointer';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { EditorIcon } from '../ui';
import { useT } from '@/i18n';

export interface ReviewPanelProps {
  projectId: string;
  timelineId: string;
  rate: Rational;
  playheadFrame: number;
  /** In/out marks: a comment can cover the marked range instead of one frame. */
  inOut?: { inFrame: number | null; outFrame: number | null };
  onSeek: (frame: number) => void;
  /** Reads the playhead at the moment of posting (the paused video's own time); falls back to `playheadFrame`. */
  readPlayheadFrame?: () => number;
  /** Compact layout for the mobile review page. */
  compact?: boolean;
}

/**
 * P9: timecoded review comments — threads pinned to a frame (or the marked range), replies, resolve,
 * delete. The list refreshes every 15 s so a reviewer on another device shows up. Comments are NOT
 * part of the document: adding one never creates a revision or an undo step.
 */
export function ReviewPanel({ projectId, timelineId, rate, playheadFrame, inOut, onSeek, readPlayheadFrame, compact = false }: ReviewPanelProps) {
  const tx = useT('editor.review.reviewPanel');
  const coarse = useCoarsePointer();
  const [editing, setEditing] = useState<{ id: string; body: string } | null>(null);
  const { data, mutate, isLoading, error } = useReviewComments(projectId, timelineId);
  const [body, setBody] = useState('');
  const [useRange, setUseRange] = useState(false);
  const [showResolved, setShowResolved] = useState(false);
  const [replyTo, setReplyTo] = useState<string | null>(null);
  const [reply, setReply] = useState('');
  const [failure, setFailure] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);
  const rangeOk = !!inOut && inOut.inFrame !== null && inOut.outFrame !== null && inOut.outFrame > inOut.inFrame;
  const threads = threadComments(data ?? []).filter((t) => showResolved || !t.root.resolved);
  const run = async (fn: () => Promise<unknown>, fallback = tx('theCommentCouldNot')) => {
    setFailure(null);
    try {
      await fn();
      await mutate();
    } catch (e) {
      setFailure(humanizeErrorT(tx, e, fallback));
    }
  };
  const btn = compact || coarse ? { size: 'md', mih: 44, miw: 44 } : { size: 'compact-xs' };

  // A guest's words are theirs: shown as typed, always marked "(guest)", and never editable by a member.
  const who = (c: ReviewComment) =>
    c.guest ? `${c.guestName || c.authorName} (guest)` : c.actorKind === 'apiKey' ? `${tx('apiKeyPrefix')} ${c.authorName}` : c.authorName;
  const tc = (c: ReviewComment) => framesToTimecode(c.frame, rate) + (c.durationFrames ? ` to ${framesToTimecode(c.frame + c.durationFrames, rate)}` : '');

  return (
    <Stack gap="xs" data-testid="review-panel">
      <Badge size="lg" fz="xs" variant="light" radius="sm" style={{ alignSelf: 'flex-start', textTransform: 'none' }} data-testid="review-pinned-chip">
        {tx('pinnedTo')} {framesToTimecode(playheadFrame, rate)}
      </Badge>
      <Textarea size={compact ? 'md' : 'xs'} placeholder={tx('commentAt', { v1: framesToTimecode(playheadFrame, rate) })} value={body} onChange={(e) => setBody(e.currentTarget.value)} minRows={2} maxLength={4000} aria-label={tx('comment')} data-testid="review-body" />
      <Group justify="space-between">
        {inOut && <Checkbox size="xs" label={tx('coverTheMarkedRange')} disabled={!rangeOk} checked={useRange && rangeOk} onChange={(e) => setUseRange(e.currentTarget.checked)} />}
        <Button
          size={compact ? 'md' : 'compact-xs'}
          mih={compact ? 44 : undefined}
          leftSection={<IconMessageCircle size={12} />}
          disabled={body.trim().length === 0}
          onClick={() =>
            run(async () => {
              const range = useRange && rangeOk && inOut ? { frame: inOut.inFrame as number, durationFrames: (inOut.outFrame as number) - (inOut.inFrame as number) } : { frame: readPlayheadFrame ? readPlayheadFrame() : playheadFrame, durationFrames: null };
              await createReviewComment(projectId, timelineId, { body: body.trim(), ...range });
              setBody('');
            })
          }
          data-testid="review-add"
        >
          {tx('comment2')}
        </Button>
      </Group>
      {failure && <Text size="xs" c="red">{failure}</Text>}
      <Group justify="space-between">
        <Text size="xs" fw={600}>{tx('threadCount', { count: threads.length })}</Text>
        <Checkbox size="xs" styles={compact ? { root: { minHeight: 44, display: 'flex', alignItems: 'center' } } : undefined} label={tx('showResolved')} checked={showResolved} onChange={(e) => setShowResolved(e.currentTarget.checked)} />
      </Group>
      {isLoading && <Text size="xs" c="dimmed">{tx('loadingComments')}</Text>}
      {error && <Text size="xs" c="red">{tx('commentsCouldNotBe')}</Text>}
      {threads.map(({ root, replies }) => (
        <Stack key={root.id} gap={4} p={6} style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 4, opacity: root.resolved ? 0.6 : 1 }} data-testid={`review-thread-${root.id}`}>
          <Group justify="space-between" wrap="nowrap">
            <Button {...btn} fz="xs" variant="subtle" onClick={() => onSeek(root.frame)} data-testid={`review-seek-${root.id}`}>{tc(root)}</Button>
            <Group gap={4}>
              {root.resolved && <Badge size="xs" fz="xs" color="green">{tx('resolved')}</Badge>}
              {compact ? null : <EditorIcon icon={IconClockPlay} label={tx('jumpToThisTime')} onClick={() => onSeek(root.frame)} data-testid={`review-jump-${root.id}`} />}
              {compact || root.guest ? null : <EditorIcon icon={IconPencil} label={tx('editComment')} onClick={() => setEditing({ id: root.id, body: root.body })} data-testid={`review-edit-${root.id}`} />}
              {compact ? null : <EditorIcon icon={IconCheck} label={root.resolved ? tx('reopen') : tx('resolve')} onClick={() => run(() => updateReviewComment(projectId, timelineId, root.id, { resolved: !root.resolved }), tx('theCommentCouldNot2'))} data-testid={`review-resolve-${root.id}`} />}
              {compact ? null : <EditorIcon icon={IconTrash} color="red" label={tx('deleteComment')} onClick={() => run(() => deleteReviewComment(projectId, timelineId, root.id), tx('theCommentCouldNot3'))} data-testid={`review-delete-${root.id}`} />}
            </Group>
          </Group>
          {editing?.id === root.id ? (
            <Group gap={4} align="end" wrap="nowrap">
              <Textarea size="xs" style={{ flex: 1 }} value={editing.body} onChange={(e) => setEditing({ id: root.id, body: e.currentTarget.value })} minRows={1} maxLength={4000} aria-label={tx('editYourComment')} data-testid={`review-edit-body-${root.id}`} />
              <Button {...btn} fz="xs" disabled={editing.body.trim().length === 0} onClick={() => run(async () => { await updateReviewComment(projectId, timelineId, root.id, { body: editing.body.trim() }); setEditing(null); })} data-testid={`review-edit-save-${root.id}`}>{tx('save')}</Button>
              <Button {...btn} fz="xs" variant="subtle" onClick={() => setEditing(null)}>{tx('cancel')}</Button>
            </Group>
          ) : (
          <Text size="xs" style={{ whiteSpace: 'pre-wrap' }}>{root.body}</Text>
          )}
          <Text size="12px" c="dimmed" data-testid={`review-author-${root.id}`}>{who(root)}</Text>
          {replies.map((r) => (
            <Stack key={r.id} gap={0} pl="sm" style={{ borderLeft: '2px solid var(--mantine-color-default-border)' }}>
              <Text size="xs" style={{ whiteSpace: 'pre-wrap' }}>{r.body}</Text>
              <Text size="12px" c="dimmed" data-testid={`review-author-${r.id}`}>{who(r)}</Text>
              {compact && <Button {...btn} fz="xs" variant="subtle" color="red" style={{ alignSelf: 'flex-start' }} onClick={() => run(() => deleteReviewComment(projectId, timelineId, r.id), tx('theReplyCouldNot'))} data-testid={`review-delete-${r.id}`}>{tx('deleteReply')}</Button>}
            </Stack>
          ))}
          {replyTo === root.id ? (
            <Group gap={8} align="end" wrap="nowrap">
              <Textarea size={compact ? 'md' : 'xs'} style={{ flex: 1 }} value={reply} onChange={(e) => setReply(e.currentTarget.value)} minRows={1} maxLength={4000} aria-label={tx('reply')} data-testid={`review-reply-body-${root.id}`} />
              <Button {...btn} fz="xs" disabled={reply.trim().length === 0} onClick={() => run(async () => { await createReviewComment(projectId, timelineId, { body: reply.trim(), frame: root.frame, parentId: root.id }); setReply(''); setReplyTo(null); }, tx('theReplyCouldNot2'))} data-testid={`review-reply-send-${root.id}`}>
                {tx('reply2')}
              </Button>
            </Group>
          ) : (
            <Group gap={8}>
              <Button {...btn} fz="xs" variant="subtle" onClick={() => setReplyTo(root.id)} data-testid={`review-reply-${root.id}`}>{tx('reply3')}</Button>
              {compact && !root.guest && <Button {...btn} fz="xs" variant="subtle" leftSection={<IconPencil size={14} />} onClick={() => setEditing({ id: root.id, body: root.body })} data-testid={`review-edit-${root.id}`}>{tx('edit')}</Button>}
              {compact && <Button {...btn} fz="xs" variant="subtle" leftSection={<IconCheck size={14} />} onClick={() => run(() => updateReviewComment(projectId, timelineId, root.id, { resolved: !root.resolved }), tx('theCommentCouldNot4'))} data-testid={`review-resolve-${root.id}`}>{root.resolved ? tx('reopen2') : tx('resolve2')}</Button>}
              {compact && (confirmDelete === root.id ? (
                <>
                  <Button {...btn} fz="xs" color="red" onClick={() => { setConfirmDelete(null); void run(() => deleteReviewComment(projectId, timelineId, root.id), tx('theCommentCouldNot5')); }} data-testid={`review-delete-confirm-${root.id}`}>{tx('delete')}</Button>
                  <Button {...btn} fz="xs" variant="default" onClick={() => setConfirmDelete(null)}>{tx('keep')}</Button>
                </>
              ) : (
                <Button {...btn} fz="xs" variant="subtle" color="red" leftSection={<IconTrash size={14} />} onClick={() => setConfirmDelete(root.id)} data-testid={`review-delete-${root.id}`}>{tx('delete2')}</Button>
              ))}
            </Group>
          )}
        </Stack>
      ))}
    </Stack>
  );
}
