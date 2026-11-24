'use client';

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Button, Group, Modal, Select, Stack, Switch, Text, TextInput } from '@mantine/core';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { IconCopy, IconShare } from '@tabler/icons-react';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import { useEditTimelineRenders } from '@/lib/hooks/use-edit-timeline-renders';
import { useReviewLinks } from '@/lib/hooks/use-review-links';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { formatLength } from '../deliver/deliver-model';
import { pickerRenders } from '@/lib/editing/review/review-state';
import {
  DEFAULT_EXPIRY_DAYS,
  EXPIRY_CHOICES,
  createReviewLink,
  decisionSentence,
  getReviewLink,
  revokeReviewLink,
  unreadLinkCount,
  type CreatedReviewLink,
  type ReviewLinkSummary,
} from '@/lib/editing/review/guest-links';
import { useFormat, useT } from '@/i18n';
import { decisionSentenceMsg } from '@/lib/editing/summary-msg';

/** Passed full-timeline picture renders a link can pin (the server checks again). */
export function shareableRenders(renders: readonly EditTimelineRenderSummary[] | undefined): EditTimelineRenderSummary[] {
  return pickerRenders(renders).filter((r) => !/preview/i.test(r.preset));
}

/** "clip.mp4, Oct 26, 10:30, 0:12": name, date and length, never an id. */
export function shareRenderLabel(r: EditTimelineRenderSummary): string {
  const when = r.completedAt ? new Date(r.completedAt).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : null;
  return [r.outputFileName, when, formatLength(r.durationSec)].filter((p): p is string => !!p).join(', ');
}

export const EXPIRY_LABELS: Record<number, string> = { 1: '1 day', 7: '7 days', 14: '14 days', 30: '30 days', 90: '90 days' };

const STATUS_COLOR: Record<ReviewLinkSummary['status'], string> = { active: 'green', expired: 'gray', revoked: 'gray' };

export interface ShareForReviewProps {
  projectId: string;
  timelineId: string;
  /** Pre-select this render (Deliver passes the film on screen). */
  defaultExecutionId?: string | null;
  /** The button label; the unread badge shows on it when guests left something. */
  buttonLabel?: string;
  buttonVariant?: 'default' | 'light' | 'subtle';
}

/**
 * "Share for review": the button, the dialog (create a link, see the URL ONCE, copy it) and the list of
 * existing links (status, views, latest decision, revoke). The list never contains a URL: the server only
 * returns it in the create response, which this component holds in state until the dialog closes.
 */
export function ShareForReview({ projectId, timelineId, defaultExecutionId = null, buttonLabel: buttonLabelProp, buttonVariant = 'default' }: ShareForReviewProps) {
  const t = useT('editor.review.shareForReview');
  const buttonLabel = buttonLabelProp ?? t('shareForReview2');
  const [opened, setOpened] = useState(false);
  const { data: links } = useReviewLinks(projectId, timelineId);
  const unread = unreadLinkCount(links);
  return (
    <>
      <Button size="md" mih={44} variant={buttonVariant} leftSection={<IconShare size={16} />} onClick={() => setOpened(true)} data-testid="share-for-review">
        {buttonLabel}
        {unread > 0 && (
          <Badge ml={8} fz="xs" size="sm" color="red" variant="filled" data-testid="share-unread-badge" aria-label={t('withNewGuestActivity', { unread })}>
            {unread}
          </Badge>
        )}
      </Button>
      {opened && <ShareDialog projectId={projectId} timelineId={timelineId} defaultExecutionId={defaultExecutionId} onClose={() => setOpened(false)} />}
    </>
  );
}

function ShareDialog({ projectId, timelineId, defaultExecutionId, onClose }: { projectId: string; timelineId: string; defaultExecutionId: string | null; onClose: () => void }) {
  const t = useT('editor.review.shareForReview');
  const phone = useDeviceClass() === 'phone';
  return (
    <Modal opened onClose={onClose} title={t('shareForReview')} closeButtonProps={{ 'aria-label': 'Close', style: { minWidth: 44, minHeight: 44 } }} fullScreen={phone} size="lg" centered={!phone} data-testid="share-dialog">
      <ReviewLinksBody projectId={projectId} timelineId={timelineId} defaultExecutionId={defaultExecutionId} />
    </Modal>
  );
}

export interface ReviewLinksBodyProps {
  projectId: string;
  timelineId: string;
  /** Pre-select this render (Deliver passes the film on screen). */
  defaultExecutionId?: string | null;
  /** The render queue when the caller already holds it (Deliver); absent = read here. */
  renders?: readonly EditTimelineRenderSummary[];
  /**
   * On the Deliver page, beside the film: the form opens from a labelled button, the list says when it is loading
   * or could not be read, and a link is marked as seen only when the person says so (the list is always on screen
   * there, so being listed is not the same as being looked at).
   */
  inline?: boolean;
  /** No right to change this timeline: the links are listed, creating one and turning one off are not offered. */
  readOnly?: boolean;
}

/**
 * The owner side of guest review links: create one for a finished film (the URL is shown ONCE, here, and kept only
 * in this component's state), and the list of existing links with their status, views, latest decision and
 * "turn off". The list never contains a URL or a token: the server returns the URL only when a link is created.
 */
export function ReviewLinksBody({ projectId, timelineId, defaultExecutionId = null, renders: givenRenders, inline = false, readOnly = false }: ReviewLinksBodyProps) {
  const t = useT('editor.review.shareForReview');
  const fmt = useFormat();
  const { data: fetchedRenders } = useEditTimelineRenders(givenRenders ? null : projectId, givenRenders ? null : timelineId);
  const renders = givenRenders ?? fetchedRenders;
  const { data: links, mutate, error: linksError, isLoading: linksLoading } = useReviewLinks(projectId, timelineId);
  const options = useMemo(() => shareableRenders(renders), [renders]);
  const [formOpen, setFormOpen] = useState(!inline);
  const [label, setLabel] = useState('');
  // The film the person picked; until they pick, the one the caller points at (on Deliver, the film on screen).
  const [pickedByUser, setPicked] = useState<string | null>(null);
  const picked = pickedByUser ?? defaultExecutionId;
  const [days, setDays] = useState<string>(String(DEFAULT_EXPIRY_DAYS));
  const [comments, setComments] = useState(true);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [created, setCreated] = useState<CreatedReviewLink | null>(null);
  const [copyNote, setCopyNote] = useState<string | null>(null);
  const [confirmRevoke, setConfirmRevoke] = useState<string | null>(null);
  const urlRef = useRef<HTMLInputElement | null>(null);
  const executionId = picked && options.some((r) => r.executionId === picked) ? picked : (options[0]?.executionId ?? null);

  const markSeen = (unread: readonly ReviewLinkSummary[]) => Promise.allSettled(unread.map((l) => getReviewLink(projectId, timelineId, l.id))).then(() => mutate());
  const unreadLinks = (links ?? []).filter((l) => l.unreadGuestActivity);

  // Opening the dialog is looking at the list: mark every link with unread guest activity as seen.
  const markedRef = useRef(false);
  useEffect(() => {
    if (inline || markedRef.current || !links) return;
    const unread = links.filter((l) => l.unreadGuestActivity);
    if (unread.length === 0) return;
    markedRef.current = true;
    void Promise.allSettled(unread.map((l) => getReviewLink(projectId, timelineId, l.id))).then(() => mutate());
  }, [inline, links, projectId, timelineId, mutate]);

  const create = async () => {
    setProblem(null);
    setBusy(true);
    try {
      const result = await createReviewLink(projectId, timelineId, {
        ...(label.trim() ? { label: label.trim() } : {}),
        ...(executionId ? { renderExecutionId: executionId } : {}),
        expiresInDays: Number(days),
        allowComments: comments,
      });
      setCreated(result);
      await mutate();
    } catch (e) {
      setProblem(humanizeErrorT(t, e, t('theLinkCouldNot')));
    } finally {
      setBusy(false);
    }
  };

  const copy = async () => {
    if (!created) return;
    try {
      await navigator.clipboard.writeText(created.url);
      setCopyNote(t('linkCopied'));
    } catch {
      const input = urlRef.current;
      if (input) {
        input.focus();
        input.select();
      }
      setCopyNote('Press and hold, or use Ctrl+C, to copy the selected link.');
    }
  };

  const revoke = async (id: string) => {
    setProblem(null);
    try {
      await revokeReviewLink(projectId, timelineId, id);
      setConfirmRevoke(null);
      await mutate();
    } catch (e) {
      setProblem(humanizeErrorT(t, e, t('theLinkCouldNot2')));
    }
  };

  return (
      <Stack gap="md" data-testid={inline ? 'review-links' : undefined}>
        {readOnly ? (
          <Text size="xs" c="dimmed" data-testid="share-read-only">
            {t('readOnlyNote')}
          </Text>
        ) : !formOpen && !created ? (
          <Stack gap={4} align="flex-start">
            <Button size="md" mih={44} leftSection={<IconShare size={16} />} disabled={options.length === 0} onClick={() => setFormOpen(true)} aria-expanded={false} data-testid="share-open-form">
              {t('createALinkForThisFilm')}
            </Button>
            {options.length === 0 && (
              <Text size="xs" c="dimmed" data-testid="share-no-render">
                {t('makeAFullRender')}
              </Text>
            )}
          </Stack>
        ) : created ? (
          <Stack gap="xs" data-testid="share-created">
            <Text size="sm" fw={600}>{t('yourLinkIsReady')}</Text>
            <TextInput size="md" readOnly value={created.url} ref={urlRef} onFocus={(e) => e.currentTarget.select()} aria-label={t('reviewLink')} data-testid="share-url" />
            <Group gap="sm">
              <Button size="md" mih={44} leftSection={<IconCopy size={16} />} onClick={() => void copy()} data-testid="share-copy">
                {t('copyLink')}
              </Button>
              <Button size="md" mih={44} variant="default" onClick={() => setCreated(null)}>
                {t('makeAnother')}
              </Button>
              {inline && (
                <Button
                  size="md"
                  mih={44}
                  variant="subtle"
                  onClick={() => {
                    setCreated(null);
                    setCopyNote(null);
                    setFormOpen(false);
                  }}
                  data-testid="share-done"
                >
                  {t('done')}
                </Button>
              )}
            </Group>
            {copyNote && <Text size="xs" c="dimmed" role="status">{copyNote}</Text>}
          </Stack>
        ) : (
          <Stack gap="xs" data-testid="share-form">
            <TextInput size="md" label={t('nameForThisLink')} placeholder={t('clientCutV2')} maxLength={80} value={label} onChange={(e) => setLabel(e.currentTarget.value)} data-testid="share-label" />
            {options.length === 0 ? (
              <Alert color="gray" variant="light" data-testid="share-no-render">
                {t('makeAFullRender')}
              </Alert>
            ) : (
              <Select
                size="md"
                label={t('whichFilmToShare')}
                allowDeselect={false}
                data={options.map((r) => ({ value: r.executionId, label: shareRenderLabel(r) }))}
                value={executionId}
                onChange={(v) => setPicked(v)}
                data-testid="share-render"
              />
            )}
            <Select size="md" label={t('linkWorksFor')} allowDeselect={false} data={EXPIRY_CHOICES.map((d) => ({ value: String(d), label: EXPIRY_LABELS[d] }))} value={days} onChange={(v) => setDays(v ?? String(DEFAULT_EXPIRY_DAYS))} data-testid="share-expiry" />
            <Switch size="md" label={t('guestsCanLeaveNotes')} checked={comments} onChange={(e) => setComments(e.currentTarget.checked)} styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }} data-testid="share-comments" />
            <Text size="xs" c="dimmed">{t('thisLinkShowsOnly')}</Text>
            <Group gap="sm">
              <Button size="md" mih={44} loading={busy} disabled={options.length === 0} onClick={() => void create()} data-testid="share-create">
                {t('createLink')}
              </Button>
              {inline && (
                <Button size="md" mih={44} variant="default" onClick={() => setFormOpen(false)} data-testid="share-cancel-form">
                  {t('cancel')}
                </Button>
              )}
            </Group>
          </Stack>
        )}
        {problem && <Text size="sm" c="red" role="alert" data-testid="share-problem">{problem}</Text>}

        <Stack gap="xs" data-testid="share-list">
          <Group gap="sm" justify="space-between" wrap="wrap">
            <Text size="xs" fw={600}>{t('yourLinks')}</Text>
            {inline && unreadLinks.length > 0 && (
              <Button size="md" mih={44} variant="subtle" onClick={() => void markSeen(unreadLinks)} data-testid="share-mark-seen">
                {t('markAsSeen')}
              </Button>
            )}
          </Group>
          {inline && linksLoading && !links ? (
            <Text size="sm" c="dimmed" role="status" data-testid="share-list-loading">{t('loadingLinks')}</Text>
          ) : inline && linksError && !links ? (
            <Text size="sm" c="red" role="alert" data-testid="share-list-error">{t('linksCouldNotLoad')}</Text>
          ) : (
            (links ?? []).length === 0 && <Text size="sm" c="dimmed" data-testid="share-list-empty">{inline ? t('noLinksYetInline') : t('noLinksYet')}</Text>
          )}
          {(links ?? []).map((l) => (
            <Stack key={l.id} gap={4} p="xs" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 6 }} data-testid={`share-link-${l.id}`}>
              <Group gap="xs" wrap="wrap">
                <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>{l.label || t('untitledLink')}</Text>
                <Badge fz="xs" size="sm" color={STATUS_COLOR[l.status]} variant="light" data-testid={`share-status-${l.id}`}>{t(`status.${l.status}`)}</Badge>
                {l.stale && <Badge fz="xs" size="sm" color="yellow" variant="light">{t('newerEditsExist')}</Badge>}
                {l.unreadGuestActivity && <Badge fz="xs" size="sm" color="red" variant="filled">{t('new')}</Badge>}
              </Group>
              <Text size="xs" c="dimmed">
                {l.status === 'active' ? t('expires', { when: fmt.relative(l.expiresAt) }) : t(`status.${l.status}`)}{', '}{t('viewsAndNotes', { viewCount: l.viewCount, guestComments: l.guestComments })}
              </Text>
              {l.decision && <Text size="xs" style={{ overflowWrap: 'anywhere' }}>{t.msg(decisionSentenceMsg(l.decision))}</Text>}
              {l.status === 'active' && !readOnly &&
                (confirmRevoke === l.id ? (
                  <Stack gap="xs">
                    <Text size="sm">{t('turnThisLinkOff')}</Text>
                    <Group gap="sm">
                      <Button size="md" mih={44} color="red" onClick={() => void revoke(l.id)} data-testid={`share-revoke-confirm-${l.id}`}>{t('turnOff')}</Button>
                      <Button size="md" mih={44} variant="default" onClick={() => setConfirmRevoke(null)}>{t('keep')}</Button>
                    </Group>
                  </Stack>
                ) : (
                  <Button size="md" mih={44} variant="subtle" color="red" style={{ alignSelf: 'flex-start' }} onClick={() => setConfirmRevoke(l.id)} data-testid={`share-revoke-${l.id}`}>
                    {t('revoke')}
                  </Button>
                ))}
            </Stack>
          ))}
        </Stack>
      </Stack>
  );
}

/** Member review page: one banner per active link that has a decision. Plain text only. */
export function GuestDecisionBanners({ links }: { links: readonly ReviewLinkSummary[] | undefined }) {
  const t = useT('editor.review.shareForReview');
  const decided = (links ?? []).filter((l) => l.status === 'active' && l.decision);
  if (decided.length === 0) return null;
  return (
    <Stack gap={6} data-testid="guest-decision-banners">
      {decided.map((l) => (
        <Alert key={l.id} color={l.decision?.decision === 'approved' ? 'green' : 'orange'} variant="light" data-testid={`guest-decision-${l.id}`}>
          <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
            {l.decision ? t.msg(decisionSentenceMsg(l.decision)) : ''}
            {l.label ? ` (${l.label})` : ''}
          </Text>
        </Alert>
      ))}
    </Stack>
  );
}
