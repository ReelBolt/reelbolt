'use client';

import { useCallback, useState } from 'react';
import useSWR from 'swr';
import { useEditingStyles, useStylePresets } from '@/lib/hooks/use-styles';
import { useOutputHeightCap } from '@/lib/hooks/use-billing';
import { getProviderStatus } from '@/lib/api/inference-providers';
import type { InferenceProviderStatus } from '@/lib/types/inference-provider';
import type { RunChoices, RunOutputFormat, RunOutputResolution, RunResolutionChoice } from '@/lib/types/styles';
import type { RunBrief } from '@/lib/types/workflow';
import { NO_STYLE, RUN_RESOLUTION_OPTIONS, choiceFor, fitsOutputCap, suggestedFormatFor } from '@/components/styles/style-options';
import { describeVoiceProviderStatus } from '@/components/workflows/VoiceProviderStatusNotice';
import { workflowHasVoiceover, workflowProducesVideo, type RunStepKind } from '@/components/workflows/SilentRunNotice';

export interface UseRunFormOptions {
  /** Whether the form is on screen: gates the plan and provider requests (a closed modal fetches nothing). */
  enabled?: boolean;
  /** The project, so its project-only saved styles are offered too. */
  projectId?: string;
  /** The workflow's "Require a brief" setting: the brief must be filled in before running. */
  requiresBrief?: boolean;
  /** The type (and agent type) of each step: tells a silent video run from a narrated one. */
  steps?: RunStepKind[];
  /**
   * The chosen clips are mostly not vertical (from their probes). Until the person picks a shape, a
   * style's vertical suggestion then gives way to "As set up", so landscape clips are not boxed into 9:16.
   */
  clipsNotVertical?: boolean;
  /**
   * How many of the project's voices can be used right now. With one, a missing default speech
   * service is not worth a notice: the run has a voice to use.
   */
  projectVoiceCount?: number;
}

/**
 * The run choices shared by the run confirmation modal (Advanced view) and the app run page: the
 * brief text, the style, the video shape and the resolution, plus everything derived from them. The
 * shape follows a preset's suggestion until the user picks one; a resolution the plan no longer
 * covers falls back to Auto; a narrated run with no usable speech provider, or a video with no
 * narration, produces an advisory notice (never a block).
 */
export function useRunForm({ enabled = true, projectId, requiresBrief = false, steps = [], clipsNotVertical = false, projectVoiceCount = 0 }: UseRunFormOptions = {}) {
  const [notes, setNotes] = useState('');
  const [style, setStyle] = useState(NO_STYLE);
  const [suggestedShape, setFormat] = useState<RunOutputFormat>('Original');
  // A preset suggests a shape until the user picks one themselves; after that their choice stands.
  const [formatTouched, setFormatTouched] = useState(false);
  const formatFollowsClips = !formatTouched && clipsNotVertical && (suggestedShape === 'Vertical' || suggestedShape === 'Portrait');
  const format: RunOutputFormat = formatFollowsClips ? 'Original' : suggestedShape;
  const [pickedResolution, setPickedResolution] = useState<RunResolutionChoice>('Auto');
  const maxOutputHeight = useOutputHeightCap(enabled);
  // A choice the plan no longer covers (the summary arrived after the pick) falls back to Auto
  // rather than sending something the server will refuse.
  const resolution: RunResolutionChoice = fitsOutputCap(
    RUN_RESOLUTION_OPTIONS.find((o) => o.value === pickedResolution)?.shortSide,
    maxOutputHeight,
  )
    ? pickedResolution
    : 'Auto';
  const outputResolution: RunOutputResolution | undefined = resolution === 'Auto' ? undefined : resolution;
  const { data: presets } = useStylePresets();
  const { data: styles } = useEditingStyles(projectId);
  const trimmed = notes.trim();
  const missingBrief = requiresBrief && trimmed.length === 0;
  // Only a workflow with a Voiceover step needs the speech provider, so only then is its status fetched
  // (the same key as VoiceProviderStatusNotice, so the two share one request).
  const hasVoiceover = workflowHasVoiceover(steps);
  const { data: speechStatus } = useSWR<InferenceProviderStatus>(
    enabled && hasVoiceover ? 'inference-provider-status:SpeechSynthesis' : null,
    () => getProviderStatus('SpeechSynthesis'),
    { revalidateOnFocus: false },
  );
  const speechNotice = hasVoiceover ? describeVoiceProviderStatus(speechStatus, undefined, { projectVoiceCount }) : null;
  const silentVideo = !hasVoiceover && workflowProducesVideo(steps);

  const changeStyle = useCallback(
    (value: string) => {
      setStyle(value);
      const suggested = suggestedFormatFor(value, presets);
      if (!formatTouched && suggested) setFormat(suggested);
    },
    [formatTouched, presets],
  );

  const changeFormat = useCallback((f: RunOutputFormat) => {
    setFormat(f);
    setFormatTouched(true);
  }, []);

  /**
   * LEGACY output, for the run confirmation modal: style, shape and resolution as the legacy request
   * members. Use with `trimmedNotes` as `userRequest`, and NEVER together with `buildBrief`: the API
   * answers 400 when a legacy member and the same brief field are both sent.
   */
  const buildChoices = (): RunChoices => {
    const choices: RunChoices = { ...choiceFor(style) };
    if (format !== 'Original') choices.outputFormat = format;
    if (outputResolution) choices.outputResolution = outputResolution;
    return choices;
  };

  /**
   * BRIEF output, for the app run page: the form's choices as a typed run brief (docs/run-brief.md).
   * Send it as `brief` alone, without `buildChoices()` or `trimmedNotes`. `extra` carries the page's
   * other fields; where the form holds a value it wins over `extra`, and the result never carries two
   * style choices (the form's style replaces any in `extra`; otherwise at most one of `extra`'s is kept).
   */
  const buildBrief = (extra: RunBrief = {}): RunBrief => {
    const brief: RunBrief = { ...extra };
    const { styleId, stylePresetKey } = choiceFor(style);
    if (styleId || stylePresetKey) {
      delete brief.stylePreset;
      delete brief.styleId;
      delete brief.style;
      if (stylePresetKey) brief.stylePreset = stylePresetKey;
      else brief.styleId = styleId;
    } else if (brief.styleId) {
      delete brief.stylePreset;
      delete brief.style;
    } else if (brief.stylePreset) {
      delete brief.style;
    }
    if (format !== 'Original') brief.format = format;
    if (outputResolution) brief.resolution = outputResolution;
    if (trimmed) brief.notes = trimmed;
    return brief;
  };

  const reset = useCallback(() => {
    setNotes('');
    setStyle(NO_STYLE);
    setFormat('Original');
    setFormatTouched(false);
    setPickedResolution('Auto');
  }, []);

  return {
    notes,
    setNotes,
    /** The brief text, trimmed; null when empty. */
    trimmedNotes: trimmed.length > 0 ? trimmed : null,
    missingBrief,
    style,
    changeStyle,
    format,
    changeFormat,
    /** True while the shape is "As set up" only because the chosen clips are not vertical. */
    formatFollowsClips,
    resolution,
    setResolution: setPickedResolution,
    outputResolution,
    maxOutputHeight,
    presets,
    styles,
    speechNotice,
    silentVideo,
    buildChoices,
    buildBrief,
    reset,
  };
}

export type RunForm = ReturnType<typeof useRunForm>;
