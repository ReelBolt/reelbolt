'use client';

import { Stack, Text, Fieldset, Select, Group } from '@mantine/core';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import { ProviderSelect, AskAssistantButton, ModeHint, useFdRaw, assistantPrompt, AdvancedSection } from './config-kit';
import type { ExtractInputRef, RoomConveneConfig, StepType } from '@/lib/types/workflow';
import { InputRefPicker } from './ExtractStepConfig';
import { SchemaDrivenStepConfigForm } from './SchemaDrivenStepConfigForm';
import type { StepData } from './WorkflowStepList';

/**
 * The three "room" step types — `EditRoom`, `GraphicsRoom` and `ColorGradeRoom` — are bindings of
 * ONE shared backend infrastructure (`IRoomStepConfig`/`RoomGroupChatManager`/
 * `RoomStepExecutorBase<TDecision>`): several persona seats plus a director deliberate in a live
 * group chat over a bounded `VideoAnalyze` view, then the director synthesizes ONE schema-validated
 * output identical in shape to what the equivalent SOLO agent step would emit. Their config records
 * are field-for-field identical apart from one differently-named solo-fallback flag, so the builder
 * mirrors that: one editor, one canvas node, one metadata table keyed by step type.
 */
export type RoomStepType = Extract<StepType, 'EditRoom' | 'GraphicsRoom' | 'ColorGradeRoom' | 'DailiesRoom'>;

/** Which `StepData` field carries a given room's raw config JSON. */
export type RoomConfigField = 'editRoomConfigJson' | 'graphicsRoomConfigJson' | 'colorGradeRoomConfigJson' | 'dailiesRoomConfigJson';

export interface RoomStepKindMeta {
  /** `StepData`/`CreateWorkflowStepRequest` field holding this room's raw config JSON. */
  configField: RoomConfigField;
  /** The room-specific JSON property name for "degrade to one ordinary solo agent call". */
  fallbackField: 'fallbackToSoloEditor' | 'fallbackToSoloPlanner' | 'fallbackToSoloColorist' | 'fallbackToSoloSelector';
  /** Badge text on the canvas node. */
  badge: string;
  /** Accent colour (a Mantine colour name) used by the node border/handles and the badge gradient. */
  accent: string;
  /** Hex accent for the React Flow handles/border, which are raw DOM nodes outside Mantine. */
  accentHex: string;
  /** One-line summary shown on the canvas node. */
  summary: string;
  /** What the room's seats are called, for the fallback switch's description. */
  soloAgentName: string;
  /** Helper copy under the analysis-view picker. */
  viewHelp: string;
  /** Placeholder for the node's step-label input. */
  labelPlaceholder: string;
  /**
   * Catalogue messages for the four texts above. `nodes/RoomNode.tsx` still reads the plain strings; once it resolves
   * these with `useT().msg`, the English strings above can go.
   */
  badgeMsg: Msg;
  summaryMsg: Msg;
  viewHelpMsg: Msg;
  labelPlaceholderMsg: Msg;
}

export const ROOM_STEP_KINDS: Record<RoomStepType, RoomStepKindMeta> = {
  EditRoom: {
    configField: 'editRoomConfigJson',
    fallbackField: 'fallbackToSoloEditor',
    badge: 'Editing team',
    accent: 'pink',
    accentHex: '#ec4899',
    summary:
      'Several AI editors discuss your footage and a lead editor settles on one cut, usually a better pick than a single editor working alone.',
    soloAgentName: 'VideoStoryEditor',
    viewHelp:
      'Which footage the team discusses: normally the step that watched your footage. The final-video step can use this team\'s choice exactly as it would a single editor\'s.',
    labelPlaceholder: 'Step label (e.g. Edit room deliberation)',
    badgeMsg: msg('wfSteps.room.EditRoom.badge'),
    summaryMsg: msg('wfSteps.room.EditRoom.summary'),
    viewHelpMsg: msg('wfSteps.room.EditRoom.viewHelp'),
    labelPlaceholderMsg: msg('wfSteps.room.EditRoom.labelPlaceholder'),
  },
  GraphicsRoom: {
    configField: 'graphicsRoomConfigJson',
    fallbackField: 'fallbackToSoloPlanner',
    badge: 'Graphics team',
    accent: 'lime',
    accentHex: '#65a30d',
    summary:
      'AI designers for layout, timing and wording discuss where titles and callouts go, and a lead designer settles on one plan. No titles at all is a valid outcome.',
    soloAgentName: 'MotionGraphicsPlanner',
    viewHelp:
      'Which footage the team discusses: pick the step that watched your footage, with "Find spots for titles" switched on. Choose it by name: "previous step" is usually the editor\'s choice instead.',
    labelPlaceholder: 'Step label (e.g. Graphics room deliberation)',
    badgeMsg: msg('wfSteps.room.GraphicsRoom.badge'),
    summaryMsg: msg('wfSteps.room.GraphicsRoom.summary'),
    viewHelpMsg: msg('wfSteps.room.GraphicsRoom.viewHelp'),
    labelPlaceholderMsg: msg('wfSteps.room.GraphicsRoom.labelPlaceholder'),
  },
  ColorGradeRoom: {
    configField: 'colorGradeRoomConfigJson',
    fallbackField: 'fallbackToSoloColorist',
    badge: 'Colour team',
    // Matches STEP_TYPE_COLORS.ColorGradeRoom — `orange` is Conditional's, and the badge palette
    // is deliberately collision-free. The hex is Mantine yellow-7, dark enough to read as a border.
    accent: 'yellow',
    accentHex: '#f08c00',
    summary:
      'AI colourists discuss the look of your footage and a lead colourist settles on one colour look for the whole video. Keeping the original look is a valid outcome.',
    soloAgentName: 'Colorist',
    viewHelp:
      'Which footage the team discusses: pick the step that watched your footage by name; "previous step" is usually the editor\'s choice instead.',
    labelPlaceholder: 'Step label (e.g. Color grade room deliberation)',
    badgeMsg: msg('wfSteps.room.ColorGradeRoom.badge'),
    summaryMsg: msg('wfSteps.room.ColorGradeRoom.summary'),
    viewHelpMsg: msg('wfSteps.room.ColorGradeRoom.viewHelp'),
    labelPlaceholderMsg: msg('wfSteps.room.ColorGradeRoom.labelPlaceholder'),
  },
  DailiesRoom: {
    configField: 'dailiesRoomConfigJson',
    fallbackField: 'fallbackToSoloSelector',
    badge: 'DAILIES ROOM',
    // Matches STEP_TYPE_COLORS.DailiesRoom. Mantine gray-7.
    accent: 'gray',
    accentHex: '#495057',
    summary:
      'Continuity, motion and image judges + a director compare the generated takes of each planned shot, then pick ONE take per shot by its offered take id — never a score or a number.',
    soloAgentName: 'TakeSelector',
    viewHelp:
      "Which step's bounded {view, meta} envelope the judges see — the VideoAnalyze step whose source is the GeneratedClips of the generation step (one analyzed source per delivered take, each with its take id). An empty selection is a valid outcome: every shot then keeps its first delivered take.",
    labelPlaceholder: 'Step label (e.g. Dailies room: pick one take per shot)',
    badgeMsg: msg('wfSteps.room.DailiesRoom.badge'),
    summaryMsg: msg('wfSteps.room.DailiesRoom.summary'),
    viewHelpMsg: msg('wfSteps.room.DailiesRoom.viewHelp'),
    labelPlaceholderMsg: msg('wfSteps.room.DailiesRoom.labelPlaceholder'),
  },
};

/** Narrows an arbitrary `StepType` to one of the three room types. */
export function isRoomStepType(stepType: StepType): stepType is RoomStepType {
  return stepType === 'EditRoom' || stepType === 'GraphicsRoom' || stepType === 'ColorGradeRoom' || stepType === 'DailiesRoom';
}

/** The raw config JSON a step of a room type currently carries, or null/undefined for a non-room step. */
export function getRoomConfigJson(step: StepData): string | null | undefined {
  if (!isRoomStepType(step.stepType)) return null;
  return step[ROOM_STEP_KINDS[step.stepType].configField];
}

/**
 * The default config a freshly-added room step gets — mirrors the C# record defaults for the
 * fields it names. Written IMMEDIATELY on add (not lazily on first edit): the executor hard-fails
 * a room step whose config JSON is null/empty, so an untouched step must still carry a runnable
 * default.
 */
export function createDefaultRoomConfigJson(): string {
  return JSON.stringify({ version: 1, view: { from: 'Previous' } });
}

/** A `Partial<StepData>` that seeds the given room type's default config, for use on add/type-switch. */
export function defaultRoomConfigPatch(stepType: RoomStepType): Partial<StepData> {
  return { [ROOM_STEP_KINDS[stepType].configField]: createDefaultRoomConfigJson() };
}

/**
 * Default convening-gate config for a room step. Mirrors the C# `ConveneGate*` member defaults on
 * all three room records — and on `IRoomStepConfig` — exactly
 * (`inference/src/ReelBolt.Shared/Workflows/RoomStepConfig.cs`): `ConveneGate = Off`,
 * `ConveneAcceptAt = 0.85`, `ConveneMinMargin = 0.25`, `ConveneProviderId = null`. `Off` is the
 * default everywhere, so nothing here turns the gate on.
 *
 * This is the TypeScript source of truth the `default`s in `ROOM_SCHEMA` are cross-checked against,
 * the same relationship `createDefaultVideoGenerateStepConfig` has with `VIDEO_GENERATE_SCHEMA`.
 * It is deliberately separate from `createDefaultRoomConfigJson()`: that one is what an untouched
 * room step is SEEDED with, and it deliberately writes only what the executor requires.
 */
export function createDefaultRoomConveneConfig(): RoomConveneConfig {
  return {
    conveneGate: 'Off',
    conveneAcceptAt: 0.85,
    conveneMinMargin: 0.25,
    conveneProviderId: null,
  };
}

/**
 * The known fields of the backend room config records this minimal editor exposes. The config is
 * carried on `StepData` as a RAW JSON string, and this editor parses/edits/re-serializes it while
 * spread-preserving any field it does not know about — so a template-provisioned config with
 * fields beyond these (seats, temperatures, termination mode, timeouts…) round-trips losslessly
 * through the builder, exactly like the pre-editor opaque passthrough did.
 */
interface RoomKnownFields extends Partial<RoomConveneConfig> {
  version?: number;
  view?: ExtractInputRef | null;
  rounds?: number;
  maxTurns?: number;
  persistTranscript?: boolean;
  fallbackToSoloEditor?: boolean;
  fallbackToSoloPlanner?: boolean;
  fallbackToSoloColorist?: boolean;
  fallbackToSoloSelector?: boolean;
}

type RoomConfigObject = RoomKnownFields & Record<string, unknown>;

function parseConfig(json: string | null | undefined): RoomConfigObject {
  if (!json) return { version: 1, view: { from: 'Previous' } };
  try {
    const parsed = JSON.parse(json);
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) return parsed as RoomConfigObject;
  } catch {
    // Malformed JSON (hand-edited?) — fall through to the default rather than crashing the builder.
  }
  return { version: 1, view: { from: 'Previous' } };
}

export interface RoomStepConfigEditorProps {
  /** Which room this is — selects the labels, the accent, and the solo-fallback JSON property name. */
  stepType: RoomStepType;
  /** The raw JSON string from the matching `StepData` field. */
  configJson: string | null | undefined;
  onChange: (configJson: string) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
}

/**
 * Minimal configuration form shared by all three room step types. Exposes the fields a workflow
 * author composing pipelines actually needs — which step's bounded view the room deliberates over,
 * its size/ceiling, and the degrade toggles — while preserving every other (defaulted or
 * template-provisioned) field untouched.
 */
export function RoomStepConfigEditor({
  stepType, configJson, onChange, allSteps = [], currentStepIndex = 0,
}: RoomStepConfigEditorProps) {
  const t = useT('wfSteps.room');
  const meta = ROOM_STEP_KINDS[stepType];
  const badge = t.msg(meta.badgeMsg);
  const config = parseConfig(configJson);

  const priorStepOptions = allSteps
    .slice(0, currentStepIndex)
    .map((previousStep, index) => {
      const stepOrder = index + 1;
      const label = previousStep.label?.trim() || t('stepFallback', { n: stepOrder });
      return { value: String(stepOrder), label: `${stepOrder}. ${label}` };
    });

  const patch = (updates: Partial<RoomKnownFields>) =>
    onChange(JSON.stringify({ ...config, ...updates }));

  const fdr = useFdRaw();
  const viewDiff = fdr('config.view.from').className ? fdr('config.view.from') : fdr('config.view.stepOrder');
  const isGrade = stepType === 'ColorGradeRoom';
  const gateNote = isGrade
    ? null
    : t('gateNote', { badge: badge.toLowerCase() });

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">{t.msg(meta.summaryMsg)}</Text>
        <AskAssistantButton
          prompt={assistantPrompt(currentStepIndex, stepType, msg('wfSteps.prompts.room'))}
        />
      </Group>
      <div {...viewDiff}>
        <InputRefPicker
          label={t('footage')}
          value={config.view ?? { from: 'Previous' }}
          onChange={(view) => patch({ view })}
          priorStepOptions={priorStepOptions}
          allowedSources={['Previous', 'Step']}
        />
      </div>
      <Text size="xs" c="dimmed" mt={-8}>
        {t.msg(meta.viewHelpMsg)}
      </Text>

      <Fieldset legend={badge}>
        <SchemaDrivenStepConfigForm
          stepType={stepType}
          value={config}
          onChange={(next) => patch(next)}
          fields={['rounds', 'maxTurns', 'persistTranscript', meta.fallbackField]}
          labels={{
            rounds: t('labels.rounds'),
            maxTurns: t('labels.maxTurns'),
            persistTranscript: t('labels.persistTranscript'),
            fallbackToSoloEditor: t('labels.fallbackToSoloEditor'),
            fallbackToSoloPlanner: t('labels.fallbackToSoloPlanner'),
            fallbackToSoloColorist: t('labels.fallbackToSoloColorist'),
            decisionScheduling: t('labels.decisionScheduling'),
            conveneGate: t('labels.conveneGate'),
            conveneAcceptAt: t('labels.conveneAcceptAt'),
            conveneMinMargin: t('labels.conveneMinMargin'),
            conveneProviderId: t('labels.conveneProviderId'),
          }}
          placeholders={{ rounds: t('placeholders.rounds'), maxTurns: t('placeholders.maxTurns') }}
          hints={{
            rounds: t('hints.rounds'),
            maxTurns: t('hints.maxTurns'),
            persistTranscript: t('hints.persistTranscript'),
            conveneAcceptAt: t('hints.conveneAcceptAt'),
            conveneMinMargin: t('hints.conveneMinMargin'),
          }}
          renderers={{
            decisionScheduling: ({ value, onChange, label, wrapperProps }) => (
              <Stack gap={6}>
                <Select
                  label={label}
                  size="sm"
                  allowDeselect={false}
                  value={(value as string) || 'Off'}
                  onChange={(v) => v && onChange(v)}
                  wrapperProps={wrapperProps}
                  data={[
                    { value: 'Off', label: t('scheduling.Off') },
                    { value: 'Questions', label: t('scheduling.Questions') },
                    { value: 'Adaptive', label: t('scheduling.Adaptive') },
                  ]}
                />
                <ModeHint>
                  {value === 'Questions' || value === 'Adaptive'
                    ? t('scheduling.questionsHint')
                    : t('scheduling.fixedHint')}
                </ModeHint>
              </Stack>
            ),
            conveneGate: ({ value, onChange, label, wrapperProps }) => (
              <Stack gap={6}>
                <Select
                  label={label}
                  size="sm"
                  allowDeselect={false}
                  value={(value as string) || 'Off'}
                  onChange={(v) => v && onChange(v)}
                  wrapperProps={wrapperProps}
                  disabled={!isGrade}
                  data={[
                    { value: 'Off', label: t('gate.Off') },
                    { value: 'Shadow', label: t('gate.Shadow') },
                    { value: 'Gate', label: t('gate.Gate') },
                  ]}
                />
                <ModeHint>
                  {gateNote ??
                    (value === 'Gate'
                      ? t('gate.gateHint')
                      : value === 'Shadow'
                        ? t('gate.shadowHint')
                        : t('gate.offHint'))}
                </ModeHint>
              </Stack>
            ),
            conveneProviderId: ({ value, onChange, label, wrapperProps }) =>
              isGrade ? (
                <ProviderSelect
                  capability="Decision"
                  label={label}
                  size="sm"
                  description={t('gate.providerDescription')}
                  value={value as string | null}
                  onChange={onChange}
                  nullMeans={t('gate.providerNullMeans')}
                  wrapperProps={wrapperProps}
                />
              ) : null,
          }}
        />
      </Fieldset>
      <AdvancedSection description={t('advanced.description')}>
      <Fieldset legend={t('advanced.legend')}>
        <SchemaDrivenStepConfigForm
          stepType={stepType}
          value={config}
          onChange={(next) => patch(next)}
          fields={['decisionScheduling',
            ...(isGrade ? ['conveneGate', 'conveneAcceptAt', 'conveneMinMargin', 'conveneProviderId'] : ['conveneGate'])]}
          labels={{
            rounds: t('labels.rounds'),
            maxTurns: t('labels.maxTurns'),
            persistTranscript: t('labels.persistTranscript'),
            fallbackToSoloEditor: t('labels.fallbackToSoloEditor'),
            fallbackToSoloPlanner: t('labels.fallbackToSoloPlanner'),
            fallbackToSoloColorist: t('labels.fallbackToSoloColorist'),
            fallbackToSoloSelector: t('labels.fallbackToSoloSelector'),
            decisionScheduling: t('labels.decisionScheduling'),
            conveneGate: t('labels.conveneGate'),
            conveneAcceptAt: t('labels.conveneAcceptAt'),
            conveneMinMargin: t('labels.conveneMinMargin'),
            conveneProviderId: t('labels.conveneProviderId'),
          }}
          placeholders={{ rounds: t('placeholders.rounds'), maxTurns: t('placeholders.maxTurns') }}
          hints={{
            rounds: t('hints.rounds'),
            maxTurns: t('hints.maxTurns'),
            persistTranscript: t('hints.persistTranscript'),
            conveneAcceptAt: t('hints.conveneAcceptAt'),
            conveneMinMargin: t('hints.conveneMinMargin'),
          }}
          renderers={{
            decisionScheduling: ({ value, onChange, label, wrapperProps }) => (
              <Stack gap={6}>
                <Select
                  label={label}
                  size="sm"
                  allowDeselect={false}
                  value={(value as string) || 'Off'}
                  onChange={(v) => v && onChange(v)}
                  wrapperProps={wrapperProps}
                  data={[
                    { value: 'Off', label: t('scheduling.Off') },
                    { value: 'Questions', label: t('scheduling.Questions') },
                    { value: 'Adaptive', label: t('scheduling.Adaptive') },
                  ]}
                />
                <ModeHint>
                  {value === 'Questions' || value === 'Adaptive'
                    ? t('scheduling.questionsHint')
                    : t('scheduling.fixedHint')}
                </ModeHint>
              </Stack>
            ),
            conveneGate: ({ value, onChange, label, wrapperProps }) => (
              <Stack gap={6}>
                <Select
                  label={label}
                  size="sm"
                  allowDeselect={false}
                  value={(value as string) || 'Off'}
                  onChange={(v) => v && onChange(v)}
                  wrapperProps={wrapperProps}
                  disabled={!isGrade}
                  data={[
                    { value: 'Off', label: t('gate.Off') },
                    { value: 'Shadow', label: t('gate.Shadow') },
                    { value: 'Gate', label: t('gate.Gate') },
                  ]}
                />
                <ModeHint>
                  {gateNote ??
                    (value === 'Gate'
                      ? t('gate.gateHint')
                      : value === 'Shadow'
                        ? t('gate.shadowHint')
                        : t('gate.offHint'))}
                </ModeHint>
              </Stack>
            ),
            conveneProviderId: ({ value, onChange, label, wrapperProps }) =>
              isGrade ? (
                <ProviderSelect
                  capability="Decision"
                  label={label}
                  size="sm"
                  description={t('gate.providerDescription')}
                  value={value as string | null}
                  onChange={onChange}
                  nullMeans={t('gate.providerNullMeans')}
                  wrapperProps={wrapperProps}
                />
              ) : null,
          }}
        />
      </Fieldset>
      </AdvancedSection>
    </Stack>
  );
}
