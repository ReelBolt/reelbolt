'use client';

import { notifications } from '@mantine/notifications';
import { newMarkerId } from '@/lib/editing/markers/markers';
import { newGuid } from '@/lib/editing/ids';
import { LiveStatusBar } from '../../shell/LiveStatusBar';
import { WorkspaceHeader } from '../../shell/WorkspaceHeader';
import { PendingResults } from '../../background/PendingResults';
import { AiToolsLauncher } from '../../shell/AiToolsLauncher';
import { editorHubHref, timelineReviewHref } from '@/lib/editing/open-timeline';
import { SyncNotices } from '../../sync/SyncNotices';
import { ClipContextMenu } from '../../timeline/ClipContextMenu';
import { OfflineRelink } from '../../interchange/OfflineRelink';
import { ImportResultSheet } from '../../interchange/ImportResultSheet';
import { clearImportResult } from '@/lib/editing/interchange/handoff-ui';
import { buildRelinkOps } from '@/lib/editing/interchange/relink';
import { ReplaceFromPoolModal } from '../../timeline/ReplaceFromPoolModal';
import { RemoveSilenceDialog } from '../../panels/RemoveSilenceDialog';
import { CutToBeatDialog } from '../../panels/CutToBeatDialog';
import { replaceSourceCheck } from '@/lib/editing/timeline-edit/deferred-ops';
import { toFractions, isFractions } from '@/lib/editing/layout-fractions';
import { writeLayoutRecord, resetPage, readLayoutRecord, resetAllPages } from '@/lib/editing/layout/editor-layout';
import { readHistory } from '@/lib/editing/prefs/safe-save';
import { KeymapEditor } from '../../prefs/KeymapEditor';
import { ShortcutsCheatsheet } from '../../prefs/ShortcutsCheatsheet';
import { CommandPalette } from '../../prefs/CommandPalette';
import { LayoutMenu } from '../../prefs/LayoutMenu';
import { ShellSwitch } from '../../simple/ShellSwitch';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';
import type { PagesViewCtx } from './usePagesView';

/** Around the pages: the header, the notices, the dialogs and the status bar. A view hook: it returns elements and keeps no state. */
export function useChromeView(ctx: PagesViewCtx) {
  const { store, sync, onReload, seedStepResultId, doc, revision, projectId, timelineId, ui, setWorkspace, isPhone, isTablet, setCheatsheetOpen, pageChords, pageHeadingRef, aiToolsCtx, busyGuard, preferences, panelSizes, panelBounds, leftTab, rightTab, editorPage, setLayoutReset, setAppliedSizes, setAppliedFractions, setLeftTab, setRightTab, savePreferences, pool, trackForPoolItem, rate, showRefusal, setImportResult, save, relinkOptions, readOnly, seek, importHints, relinkRequest, setRelinkRequest, importResult, editing, pointMenuLabel, badgeMenu, setBadgeMenu, replaceFor, setReplaceFor, dispatch, keymapOpen, setKeymapOpen, keymapOverrides, restorePreferences, cheatsheetOpen, paletteOpen, setPaletteOpen, paletteUnavailable, runPaletteCommand, beatsOpen, setBeatsOpen, beatFeeds, startAudition, silenceOpen, setSilenceOpen, statsStore, sessionKind, renderCache, shell, simple, setShell } = ctx;
  const tx = useT('editor.editorWorkspace');
  const header = (
    <WorkspaceHeader
      name={doc.name}
      revision={revision}
      backHref={editorHubHref(projectId)}
      reviewHref={timelineReviewHref(projectId, timelineId)}
      workspace={ui.workspace}
      onWorkspace={setWorkspace}
      compact={isPhone}
      tablet={isTablet}
      onShortcuts={() => setCheatsheetOpen(true)}
      chords={pageChords}
      headingRef={pageHeadingRef}
      aiTools={isPhone ? undefined : <AiToolsLauncher context={aiToolsCtx} projectId={projectId} />}
      shellSwitch={shell ? <ShellSwitch shell={shell} onChange={setShell} /> : undefined}
      pending={
        <>
          <PendingResults store={store} projectId={projectId} compact={isPhone} />
          {busyGuard}
        </>
      }
      onExport={() => {
        if (ui.workspace !== 'deliver') {
          setWorkspace('deliver');
          return;
        }
        // Already on Deliver: take the user to the Render button instead of doing nothing.
        const render = document.querySelector<HTMLElement>('[data-testid="render-start"]');
        render?.scrollIntoView({ block: 'center' });
        render?.focus();
      }}
      layoutMenu={
        <LayoutMenu
          workspace={ui.workspace}
          layouts={preferences?.layouts ?? []}
          current={() => ({
            workspace: ui.workspace,
            sizes: { left: panelSizes.current.left, right: panelSizes.current.right, timeline: panelSizes.current.timeline },
            fractions: panelBounds.current.width > 0 ? toFractions(panelSizes.current, panelBounds.current) : undefined,
            leftTab,
            rightTab,
          })}
          onReset={() => {
            const storage = typeof window === 'undefined' ? null : window.localStorage;
            writeLayoutRecord(storage, resetPage(readLayoutRecord(storage), editorPage));
            setLayoutReset((n) => n + 1);
            setAppliedSizes(null);
            setAppliedFractions(null);
          }}
          onResetAll={() => {
            const storage = typeof window === 'undefined' ? null : window.localStorage;
            writeLayoutRecord(storage, resetAllPages());
            setLayoutReset((n) => n + 1);
            setAppliedSizes(null);
            setAppliedFractions(null);
          }}
          onApply={(layout) => {
            if (isFractions(layout.fractions)) setAppliedFractions({ ...layout.fractions });
            else setAppliedSizes({ ...layout.sizes });
            if (layout.leftTab) setLeftTab(layout.leftTab);
            if (layout.rightTab) setRightTab(layout.rightTab);
          }}
          onSaveLayouts={(layouts) =>
            void savePreferences({ layouts }).catch((error: unknown) => notifications.show({ color: 'red', title: tx('layoutNotSaved'), message: humanizeErrorT(tx, error, tx('yourLayoutCouldNot')) }))
          }
        />
      }
    />
  );

  const relinkOffline = (markerId: string, mediaRef: string): string | null => {
    const marker = doc.markers.find((m) => m.id === markerId);
    const item = pool?.find((p) => p.mediaRef === mediaRef);
    if (!marker || !item) return tx('thatMarkerOrFile');
    const result = buildRelinkOps(doc, { marker, item, trackId: trackForPoolItem(item)?.id ?? null, rate, newId: newGuid, isLocked: (t) => store.effectiveTrackFlags(t).locked });
    if (!result.ok) return result.reason;
    try {
      store.dispatchBatch(result.ops, result.label);
    } catch (error) {
      showRefusal(error, store.getState().doc);
      return tx('theFileCouldNot');
    }
    store.setUi({ selection: [result.place.clipId] });
    return null;
  };
  const closeImportResult = () => {
    clearImportResult(timelineId);
    setImportResult(null);
  };
  const banner = (
    <>
      <SyncNotices sync={sync} save={save} isPhone={isPhone} projectId={projectId} docName={doc.name} onReload={onReload} />
      <OfflineRelink
        doc={doc}
        options={relinkOptions.map((o) => ({ value: o.value, label: o.label }))}
        readOnly={readOnly}
        onSeek={seek}
        onRelink={relinkOffline}
        hints={importHints}
        requestedMarkerId={relinkRequest}
        onRequestHandled={() => setRelinkRequest(null)}
      />
      <ImportResultSheet model={importResult} readOnly={readOnly} onClose={closeImportResult} onRelink={(markerId) => { closeImportResult(); setRelinkRequest(markerId); }} />
    </>
  );

  const dialogs = (
    <>
      <ClipContextMenu anchor={editing.anchor} items={editing.anchor ? (editing.menuTarget.area === 'clip' ? editing.items() : editing.pointItems()) : []} label={pointMenuLabel} onClose={editing.closeMenu} />
      <ClipContextMenu
        anchor={badgeMenu ? { x: badgeMenu.x, y: badgeMenu.y } : null}
        items={badgeMenu?.items ?? []}
        onClose={() => setBadgeMenu(null)}
      />
      <ReplaceFromPoolModal
        opened={replaceFor !== null}
        options={relinkOptions}
        onClose={() => setReplaceFor(null)}
        onReplace={(option) => {
          const clip = doc.tracks.flatMap((t) => t.items).find((i) => i.id === replaceFor);
          if (!clip || clip.type !== 'mediaClip') return tx('onlyAMediaClip');
          const source = pool?.find((p) => p.mediaRef === option.value);
          const frames = source?.probe?.durationSec ? Math.floor((source.probe.durationSec * rate.num) / Math.max(1, rate.den)) : null;
          const check = replaceSourceCheck(clip, { frames, isStill: source?.kind === 'image' });
          if (!check.ok) return check.reason;
          const ok = dispatch({ type: 'replace', itemId: clip.id, item: { ...clip, assetRef: option.assetRef, sourceIn: 0 } }, tx('replaceSource'));
          return ok ? null : tx('theReplacementWasRefused');
        }}
      />
      <KeymapEditor opened={keymapOpen} onClose={() => setKeymapOpen(false)} overrides={keymapOverrides} onSave={(overrides) => savePreferences({ keymap: overrides })} onRestore={restorePreferences} canRestore={readHistory().length > 0} />
      <ShortcutsCheatsheet
        opened={cheatsheetOpen}
        onClose={() => setCheatsheetOpen(false)}
        overrides={keymapOverrides}
        onEditShortcuts={() => {
          setCheatsheetOpen(false);
          setKeymapOpen(true);
        }}
      />
      <CommandPalette opened={paletteOpen} onClose={() => setPaletteOpen(false)} overrides={keymapOverrides} unavailable={paletteUnavailable} onRun={runPaletteCommand} simple={simple} />
      <CutToBeatDialog
        opened={beatsOpen}
        onClose={() => setBeatsOpen(false)}
        doc={doc}
        projectId={projectId}
        newItemId={newMarkerId}
        selectedItemIds={ui.selection}
        readOnly={readOnly}
        probeDurations={beatFeeds.probeDurations}
        motion={beatFeeds.motion}
        onAudition={(ops, label) => {
          setBeatsOpen(false);
          startAudition(ops, label);
        }}
        onApply={(ops, label) => {
          try {
            store.dispatchBatch(ops, label);
          } catch (error) {
            showRefusal(error, store.getState().doc);
          }
        }}
      />
      <RemoveSilenceDialog
        opened={silenceOpen}
        onClose={() => setSilenceOpen(false)}
        doc={doc}
        projectId={projectId}
        seedStepResultId={seedStepResultId}
        newItemId={newMarkerId}
        onAudition={(ops, label) => {
          setSilenceOpen(false);
          startAudition(ops, label);
        }}
        onApply={(ops, label) => {
          try {
            store.dispatchBatch(ops, label);
          } catch (error) {
            showRefusal(error, store.getState().doc);
          }
        }}
      />
    </>
  );

  const statusBar = (
    <LiveStatusBar editorStore={store} statsStore={statsStore} rate={rate} save={save} sessionKind={sessionKind} renderCache={renderCache} readOnly={readOnly} compact={isPhone} hideSave={isPhone} hideTimecode={isPhone} onRetry={() => void sync.retry()} onSignIn={() => sync.requestSignIn()} />
  );

  return { header, banner, dialogs, statusBar };
}

export type ChromeViewCtx = PagesViewCtx & ReturnType<typeof useChromeView>;
