'use client';

import React, { useMemo } from 'react';
import { Stack, Button, Text } from '@mantine/core';
import { IconWaveSine } from '@tabler/icons-react';
import { withLivePlayhead } from '../shell/LivePlayhead';
import { PAIRED_AUDIO_TRACK_NAME } from '@/lib/editing/media/pool-insert';
import { useFrameDraft, applyFrameDraft } from '@/lib/editing/viewer-transform';
import { MarkersPanel } from '../panels/MarkersPanel';
import { InspectorPanel } from '../panels/InspectorPanel';
import { useTransformMirror } from '@/lib/editing/viewer-transform';
import { ClipActionMenu } from '../timeline/ClipActionMenu';
import { MixerPanel } from '../audio/MixerPanel';
import { CaptionsSheet } from '../phone/CaptionsSheet';
import { SubtitlesPanel } from '../panels/SubtitlesPanel';
import { CutWorkspace } from '../cut/CutWorkspace';
import { ColorPageProvider } from '../color/ColorWorkspace';
import { ComposeList } from '../compose/ComposeList';
import { ComposeProperties } from '../compose/ComposeProperties';
import { BusesPanel } from '../audio/BusesPanel';
import { VoRecorderPanel } from '../audio/VoRecorderPanel';
import { StructurePanel } from '../panels/StructurePanel';
import { ReviewPanel } from '../review/ReviewPanel';
import { useT } from '@/i18n';

/** Which pane the phone layout shows under the viewer. */

/**
 * The P3 editable workspace — everything wired to one store and one sync client:
 * shell (workspace bar, resizable panels, status bar), canvas timeline with track headers, the
 * program viewer that plays THE TIMELINE, the transport, markers, edit index, history, inspector,
 * revision history, and the read-only banner.
 */
/* Panels that show the playhead refresh themselves (4 Hz while playing); the shell does not render per tick. */
export const LiveMarkersPanel = withLivePlayhead(MarkersPanel);

export const LiveSubtitlesPanel = withLivePlayhead(SubtitlesPanel);

export const LiveStructurePanel = withLivePlayhead(StructurePanel);

export const LiveReviewPanel = withLivePlayhead(ReviewPanel);

export const PlayheadInspectorPanel = withLivePlayhead(InspectorPanel);

/** The inspector with the viewer-drag mirror (R07.2): the transform numbers follow a drag before release. */
export function LiveInspectorPanel(props: React.ComponentProps<typeof PlayheadInspectorPanel>) {
  const mirror = useTransformMirror(props.store, props.selection[0] ?? null);
  return <PlayheadInspectorPanel {...props} transformMirror={mirror} />;
}

export const LiveClipActionMenu = withLivePlayhead(ClipActionMenu);

export const LiveMixerPanel = withLivePlayhead(MixerPanel);

export const LiveBusesPanel = withLivePlayhead(BusesPanel);

export const LiveVoRecorderPanel = withLivePlayhead(VoRecorderPanel);

export const LiveCaptionsSheet = withLivePlayhead(CaptionsSheet);

export const PlayheadColorPage = withLivePlayhead(ColorPageProvider);

export const PlayheadComposeList = withLivePlayhead(ComposeList);

export const PlayheadComposeProperties = withLivePlayhead(ComposeProperties);

/** R10.3: the Colour page shows the document as the on-canvas window drag drafts it, so its fields follow live. */
export function LiveColorPage(props: React.ComponentProps<typeof PlayheadColorPage>) {
  const live = useFrameDraft(props.store);
  const doc = useMemo(() => applyFrameDraft(props.doc, live), [props.doc, live]);
  return <PlayheadColorPage {...props} doc={doc} />;
}

/** R10.3: the Compose sliders follow a mask or reframe drag live (the sliders stay as fine-tune). */
export function LiveComposeProperties(props: React.ComponentProps<typeof PlayheadComposeProperties>) {
  const live = useFrameDraft(props.store);
  const doc = useMemo(() => applyFrameDraft(props.doc, live), [props.doc, live]);
  return <PlayheadComposeProperties {...props} doc={doc} />;
}

export const LiveCutWorkspace = withLivePlayhead(CutWorkspace);

/** "Extract audio" for a footage clip: its sound onto the paired audio track (one undo step). */
export function ExtractAudioButton({ silent, disabled, onExtract }: { silent: boolean; disabled: boolean; onExtract: () => void }) {
  const tx = useT('editor.editorWorkspace');
  return (
    <Stack gap={2}>
      <Button size="xs" fz="xs" variant="light" leftSection={<IconWaveSine size={14} />} disabled={disabled || silent} onClick={onExtract} data-testid="inspector-extract-audio">
        {tx('extractAudio')}
      </Button>
      <Text size="xs" c="dimmed" data-testid="inspector-extract-audio-note">
        {silent ? tx('thisFileHasNo') : tx('putsThisClipS', { PAIRED_AUDIO_TRACK_NAME })}
      </Text>
    </Stack>
  );
}
