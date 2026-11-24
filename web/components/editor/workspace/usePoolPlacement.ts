import { useCallback, useEffect, useRef } from 'react';
import { notifications } from '@mantine/notifications';
import { newGuid } from '@/lib/editing/ids';
import { type PlaceMode, buildPlaceOps } from '@/lib/editing/media/place';
import type { PlaceChoice } from '../place/placeChoices';
import { buildPlaceOverOps, coversLowerPicture, pipOps, pipValues } from '@/lib/editing/media/place-over';
import type { Track, MediaClip } from '@/lib/editing/types';
import { compileExtractAudio } from '@/lib/editing/media/pool-insert';
import type { Marks } from '@/lib/editing/media/in-out';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { displayChord } from '@/lib/editing/keymap';
import { useT } from '@/i18n';
import { IS_MAC } from './helpers';
import type { ClipAndViewerActionsCtx } from './useClipAndViewerActions';

/** Putting media from the pool on the timeline: insert, overwrite, add to end, place on top, place over, drop, extract audio. */
export function usePoolPlacement(ctx: ClipAndViewerActionsCtx) {
  const { store, ui, doc, readOnly, readOnlyMessage, rate, showRefusal, showLandedToast, chords, pasteChordRef, placeRef, viewerSwitchOn, sourceItem, monitor, placeOverHandlerRef, pool, showPipToast, sourceSizeFor, applyBatch } = ctx;
  const tx = useT('editor.editorWorkspace');
  // ── clips from the media pool (Media workspace) ───────────────────────────
  // The clip takes the probe's duration and the source viewer's marks; a video with sound brings
  // its sound onto the paired audio track in the same op batch (lib/editing/media/pool-insert.ts).
  const trackForPoolItem = useCallback(
    (item: MediaPoolItem): Track | null => {
      const kind = item.kind === 'audio' ? 'audio' : 'video';
      const targeted = ui.targetedTrackIds.map((id) => doc.tracks.find((t) => t.id === id)).find((t) => t?.kind === kind);
      return targeted ?? doc.tracks.find((t) => t.kind === kind && !store.effectiveTrackFlags(t).locked) ?? null;
    },
    [doc, store, ui.targetedTrackIds],
  );
  const placePoolItem = useCallback(
    (item: MediaPoolItem, mode: PlaceMode, marks?: Marks | null, name?: string, drop?: { trackId: string; frame: number }): string | null => {
      if (readOnly) {
        notifications.show({ color: 'orange', title: tx('readOnly2'), message: readOnlyMessage });
        return readOnlyMessage;
      }
      const track = drop ? doc.tracks.find((t) => t.id === drop.trackId) ?? null : trackForPoolItem(item);
      if (!track && mode !== 'placeOnTop') {
        notifications.show({ color: 'yellow', title: tx('nowhereToPutIt'), message: item.kind === 'audio' ? tx('addUnlockedAudioTrack') : tx('addUnlockedVideoTrack') });
        return `Add an unlocked ${item.kind === 'audio' ? 'audio' : 'video'} track first.`;
      }
      const result = buildPlaceOps(doc, {
        item,
        mode,
        at: drop ? drop.frame : store.getState().ui.playheadFrame,
        trackId: track?.id ?? null,
        marks,
        name,
        rate,
        newId: newGuid,
        isLocked: (t) => store.effectiveTrackFlags(t).locked,
      });
      if (!result.ok) {
        notifications.show({ color: 'yellow', title: tx('couldNotPlaceIt'), message: result.reason });
        return result.reason;
      }
      try {
        store.dispatchBatch(result.ops, result.label);
      } catch (error) {
        showRefusal(error, store.getState().doc);
        return tx('thatCouldNotBe');
      }
      store.setUi(mode === 'append' || drop ? { selection: [result.clipId] } : { selection: [result.clipId], playheadFrame: result.landedAt.frame });
      showLandedToast(store, result.landedAt, tx('placed'), result.lengthKnown ? undefined : tx('lengthUnknownNote'));
      return null;
    },
    [trackForPoolItem, store, doc, rate, readOnly, readOnlyMessage, tx],
  );
  // T-018: what the menus and chords read lazily: the live Paste chord, and the loaded source's placement (its marks, the timeline playhead).
  useEffect(() => {
    const paste = chords.pasteClips[0];
    pasteChordRef.current = paste ? displayChord(paste, IS_MAC) : undefined;
    placeRef.current = viewerSwitchOn && sourceItem ? (mode) => void placePoolItem(sourceItem, mode, monitor.marks) : null;
  });
  // T-012 "Place over": the source at the playhead on the first video track above every picture there (a new track when none
  // above is free), small in the bottom-right corner, selected so the canvas handles appear. One undo step.
  const placeOverPoolItem = useCallback(
    (item: MediaPoolItem, marks?: Marks | null): string | null => {
      if (readOnly) {
        notifications.show({ color: 'orange', title: tx('readOnly2'), message: readOnlyMessage });
        return readOnlyMessage;
      }
      const result = buildPlaceOverOps(doc, {
        item,
        at: store.getState().ui.playheadFrame,
        marks,
        rate,
        newId: newGuid,
        isLocked: (t) => store.effectiveTrackFlags(t).locked,
        canvas: { width: doc.settings.width, height: doc.settings.height },
        aspectPolicy: doc.settings.aspectPolicy ?? 'fit',
      });
      if (!result.ok) {
        notifications.show({ color: 'yellow', title: tx('couldNotPlaceIt'), message: result.reason });
        return result.reason;
      }
      try {
        store.dispatchBatch(result.ops, result.label);
      } catch (error) {
        showRefusal(error, store.getState().doc);
        return tx('thatCouldNotBe');
      }
      store.setUi({ selection: [result.clipId] });
      showLandedToast(store, result.landedAt, tx('placed'), result.lengthKnown ? undefined : tx('lengthUnknownNote'));
      return null;
    },
    [store, doc, rate, readOnly, readOnlyMessage, tx, showLandedToast],
  );
  const placeChosen = useCallback(
    (item: MediaPoolItem, choice: PlaceChoice, marks?: Marks | null) => (choice === 'placeOver' ? placeOverPoolItem(item, marks) : placePoolItem(item, choice, marks)),
    [placeOverPoolItem, placePoolItem],
  );
  // The source the Media workspace last offered placements for: the "Place over" chord puts this one.
  const offeredSourceRef = useRef<{ item: MediaPoolItem; marks: Marks } | null>(null);
  useEffect(() => {
    placeOverHandlerRef.current = () => {
      // On Cut and Edit the Source monitor's loaded source wins; on Media the last offered one.
      if (viewerSwitchOn && sourceItem) {
        placeOverPoolItem(sourceItem, monitor.marks);
        return;
      }
      const offered = offeredSourceRef.current;
      if (offered) placeOverPoolItem(offered.item, offered.marks);
    };
  }, [placeOverPoolItem, viewerSwitchOn, sourceItem, monitor.marks, placeOverHandlerRef]);
  // R05.4: the source viewer's marks per source, so a tile dropped on the timeline places the marked range.
  const poolMarksRef = useRef(new Map<string, Marks>());
  const dropPoolItem = useCallback(
    (mediaRef: string, trackId: string, frame: number) => {
      const item = pool?.find((p) => p.mediaRef === mediaRef);
      if (!item) return;
      // A drop lands where the indicator showed it and covers what is under it (overwrite): nothing else moves.
      const refused = placePoolItem(item, 'overwrite', poolMarksRef.current.get(mediaRef) ?? null, undefined, { trackId, frame });
      if (refused !== null) return;
      // T-012: dropped on an upper track over a picture below, the clip is full-frame and hides it: offer the corner preset.
      const clipId = store.getState().ui.selection[0];
      const placedDoc = store.getState().doc;
      if (!clipId || !coversLowerPicture(placedDoc, clipId)) return;
      showPipToast(() => {
        const current = store.getState().doc;
        const clip = current.tracks.flatMap((t) => t.items).find((i): i is MediaClip => i.id === clipId && i.type === 'mediaClip');
        if (!clip || readOnly) return;
        const ops = pipOps(clip, pipValues({ width: current.settings.width, height: current.settings.height }, sourceSizeFor(clip), current.settings.aspectPolicy ?? 'fit', clip.crop));
        try {
          store.dispatchBatch(ops, tx('makeItPictureInPicture'));
        } catch (error) {
          showRefusal(error, store.getState().doc);
        }
      });
    },
    [pool, placePoolItem, store, showPipToast, sourceSizeFor, readOnly, tx],
  );
  const extractAudio = useCallback(
    (itemId: string) => {
      if (readOnly) return;
      const plan = compileExtractAudio(doc, itemId, newGuid, (t) => store.effectiveTrackFlags(t).locked);
      if (typeof plan === 'string') {
        notifications.show({ color: 'yellow', title: tx('noSoundToExtract'), message: plan });
        return;
      }
      applyBatch(plan.ops, tx('extractAudio2'));
    },
    [doc, store, readOnly, applyBatch, tx],
  );

  return { trackForPoolItem, placePoolItem, placeChosen, offeredSourceRef, poolMarksRef, dropPoolItem, extractAudio };
}

export type PoolPlacementCtx = ClipAndViewerActionsCtx & ReturnType<typeof usePoolPlacement>;
