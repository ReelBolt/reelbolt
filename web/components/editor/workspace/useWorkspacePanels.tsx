'use client';

import { useMemo, useCallback, useEffect } from 'react';
import { Group, Text, Button } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { BADGE_PHONE_SECTION, BADGE_SECTION } from '@/lib/editing/display/clip-badges';
import { NeedsLargerScreen } from '../shell/LargerScreen';
import { revealElement } from '@/lib/editing/display/reveal-section';
import { requestClipSheet } from '../phone/clip-sheet-bus';
import { onProposalReviewRequested } from '@/lib/editing/background/proposal';
import { StabilizeJobs } from '@/lib/editing/background/stabilize-job';
import { marksByClip, marksFromEntries, requestReview } from '@/lib/editing/background/job-marks';
import { useBackgroundActivityBridge } from '../background/useBackgroundActivityBridge';
import type { EditorAiToolContext } from '@/lib/editing/ai-tools';
import { useBackgroundResultWatcher } from '../background/useBackgroundResultWatcher';
import { useBusyEditGuard } from '../background/useBusyEditGuard';
import { useBackgroundResults } from '@/lib/editing/background/results-store';
import { assetRefFromMediaRef } from '@/lib/editing/api/media';
import { singleSelectedItem } from '@/lib/editing/stills/stills';
import { useT } from '@/i18n';
import type { TimelineZoomCtx } from './useTimelineZoom';

/** What the side panels need: relink options, the selected clip, background jobs on clips, the AI tools and the busy-edit guard. */
export function useWorkspacePanels(ctx: TimelineZoomCtx) {
  const { store, pool, doc, ui, externalJobMarks, stabilizeJobs, isPhone, setRightTab, setWorkspace, setLeftTab, setSilenceOpen, setBeatsOpen, setAssistHelper, simple, setSimplePlace, setSimpleView, revealRef, showRefusal } = ctx;
  const tBadges = useT('editor-timeline.badges');
  const tBg = useT('editor-background');
  // ── panels ────────────────────────────────────────────────────────────────
  // P6: the pool's sources as relink options for the inspector, and the single selected clip.
  const relinkOptions = useMemo(
    () =>
      (pool ?? [])
        .map((item) => ({ value: item.mediaRef, label: item.fileName, assetRef: assetRefFromMediaRef(item.mediaRef) }))
        .filter((o): o is { value: string; label: string; assetRef: NonNullable<ReturnType<typeof assetRefFromMediaRef>> } => o.assetRef !== null),
    [pool],
  );
  const selectedClip = useMemo(() => singleSelectedItem(doc, ui.selection), [doc, ui.selection]);
  const targetedVideoTrackId = ui.targetedTrackIds.map((id) => doc.tracks.find((t) => t.id === id)).find((t) => t?.kind === 'video')?.id ?? null;
  const projectId = store.getState().projectId;
  const timelineId = store.getState().timelineId;
  // Background jobs (magic blur, ...): one watcher for the whole editor, a busy mark per clip, a warning before an edit that affects one.
  useBackgroundResultWatcher({ store, projectId, timelineId });
  useBackgroundActivityBridge({ store, projectId, timelineId });
  const { entries: backgroundEntries, store: backgroundStore } = useBackgroundResults(timelineId);
  const jobByClip = useMemo(
    () => marksByClip([...marksFromEntries(backgroundEntries, doc, (tool) => (tBg.has(`tool.${tool}`) ? tBg(`tool.${tool}`) : tBg('tool.other'))), ...externalJobMarks], doc),
    [backgroundEntries, doc, externalJobMarks, tBg],
  );
  // A click on a job chip: "Ready to review" opens the review; a failure says why (the stabilise job offers Retry).
  const onJobClick = useCallback(
    ({ itemId, job }: { itemId: string; job: { state: string; label: string; message?: string; markId: string; jobKind: string } }) => {
      store.setUi({ selection: [itemId] });
      if (job.state === 'needsReview') {
        requestReview(job.markId);
        return;
      }
      if (job.state !== 'failed') return;
      const source = job.jobKind === 'stabilize' ? StabilizeJobs.sourceOf(job.markId) : null;
      notifications.show({
        color: 'red',
        title: job.label,
        message: (
          <Group gap="xs" wrap="nowrap">
            <Text size="sm">{job.message ?? tBg('tool.other')}</Text>
            {source && (
              <Button size="compact-xs" fz="xs" variant="light" onClick={() => stabilizeJobs.retry(source)} data-testid="stabilize-retry">
                {tBg('retry')}
              </Button>
            )}
          </Group>
        ),
      });
    },
    [store, tBg, stabilizeJobs],
  );
  const jobOf = useCallback((itemId: string) => jobByClip.get(itemId), [jobByClip]);
  const jobsAnimating = useMemo(() => [...jobByClip.values()].some((j) => j.state === 'running' && j.progress === undefined), [jobByClip]);
  const busyClipIds = useMemo(() => (backgroundStore ? backgroundStore.busyClipIds(doc) : new Set<string>()), [backgroundStore, backgroundEntries, doc]);
  useEffect(
    () =>
      onProposalReviewRequested(() => {
        // The Simple shell reviews a suggestion in its own panel; the full editor in the Assist tab.
        if (simple) {
          setSimplePlace('edit');
          setSimpleView('ask');
        } else if (!isPhone) setRightTab('assist');
      }),
    [isPhone, setRightTab, simple, setSimplePlace, setSimpleView],
  );
  // The "AI tools" list: what a tool needs to open itself (lib/editing/ai-tools.ts).
  const aiToolsCtx = useMemo<EditorAiToolContext>(() => {
    const track = selectedClip ? doc.tracks.find((tr) => tr.items.some((i) => i.id === selectedClip.id)) : undefined;
    const openEditTab = (side: 'left' | 'right', value: string) => {
      if (ui.workspace !== 'edit') setWorkspace('edit');
      (side === 'left' ? setLeftTab : setRightTab)(value);
    };
    return {
      selectedClipId: selectedClip?.id ?? null,
      selectedClipKind: !selectedClip ? null : selectedClip.type === 'mediaClip' && track?.kind === 'video' ? 'video' : selectedClip.type === 'mediaClip' && track?.kind === 'audio' ? 'audio' : 'other',
      hasRange: ui.inOut.inFrame !== null && ui.inOut.outFrame !== null,
      openPanel: (value) => openEditTab(value === 'analyse' || value === 'transcript' ? 'left' : 'right', value),
      openWorkspace: setWorkspace,
      openRemoveSilence: () => setSilenceOpen(true),
      openCutToBeat: () => setBeatsOpen(true),
      openInspectorSection: (id) => {
        openEditTab('right', 'inspector');
        window.setTimeout(() => document.getElementById(id)?.scrollIntoView({ block: 'nearest' }), 200);
      },
      openAssist: (helper) => {
        setAssistHelper(helper);
        openEditTab('right', 'assist');
      },
      openDeliver: () => setWorkspace('deliver'),
    };
  }, [doc, selectedClip, ui.inOut, ui.workspace, setWorkspace, setLeftTab, setRightTab, setSilenceOpen, setBeatsOpen, setAssistHelper]);
  // A badge click: select the clip, open the inspector (the colour page for a colour badge) and scroll to its section.
  useEffect(() => {
    revealRef.current = (itemId, kind) => {
      if (isPhone) {
        store.setUi({ selection: [itemId] });
        // The sheet section that has the control, or - for a desktop and tablet tool - the plain "larger screen" note.
        const phoneSection = BADGE_PHONE_SECTION[kind];
        if (phoneSection) requestClipSheet(phoneSection);
        else notifications.show({ message: <NeedsLargerScreen feature={`badge-${kind}`} what={tBadges('needsLargerWhat', { name: tBadges(kind) })} /> });
        return;
      }
      store.setUi({ selection: [itemId], ...(BADGE_SECTION[kind].composeEntry ? { composeEntry: BADGE_SECTION[kind].composeEntry } : {}) });
      const section = BADGE_SECTION[kind];
      if (ui.workspace !== section.workspace) setWorkspace(section.workspace);
      if (section.workspace === 'edit') setRightTab('inspector');
      revealElement(section.anchor);
    };
  });
  const busyGuard = useBusyEditGuard({ store, projectId, timelineId, phone: isPhone, onRefusal: (error) => showRefusal(error, store.getState().doc) });

  return { relinkOptions, selectedClip, targetedVideoTrackId, projectId, timelineId, onJobClick, jobOf, jobsAnimating, busyClipIds, aiToolsCtx, busyGuard };
}

export type WorkspacePanelsCtx = TimelineZoomCtx & ReturnType<typeof useWorkspacePanels>;
