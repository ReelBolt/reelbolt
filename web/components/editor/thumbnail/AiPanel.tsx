'use client';

import React, { useEffect, useState } from 'react';
import Link from 'next/link';
import {
  Alert,
  Anchor,
  Button,
  Divider,
  Group,
  NumberInput,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  ThemeIcon,
  UnstyledButton,
} from '@mantine/core';
import { IconChevronRight, IconWand } from '@tabler/icons-react';
import {
  EXPAND_SHAPES,
  EXPAND_SHAPE_LABELS,
  OPERATION_INFO,
  aspectOf,
  defaultExpandAspect,
  defaultParams,
  expandMarginMax,
  expandShapeSummary,
  hasShape,
  promptRuleFor,
  subjectSupport,
  subjectsChangeRoute,
  takesReferencePicture,
  takesSubjects,
  type OperationInput,
} from '@/lib/editor/ai-operations';
import { hasUnusableChosen, sendableSubjectIds } from '@/lib/avatars/subjects';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { SubjectPicker } from '@/components/avatars/SubjectPicker';
import { aiToolInfo, type AiToolId } from '@/lib/editor/ai-tools';
import type { EditorLayer } from '@/lib/editor/model';
import type {
  EditImageOperation,
  ImageOperationCapabilities,
  ImageOperationResult,
} from '@/lib/types/image-document';
import { AI_TOOL_ICONS, AiToolPicker } from './AiToolPicker';
import {
  estimateLine,
  isUnaffordable,
  unaffordableText,
  usedLine,
  runLabel,
  type PictureEstimate,
} from '@/lib/editor/credit-estimate';
import { PinnedRun } from './PinnedRun';
import { useCoarsePointer } from './useCoarsePointer';
import { ReferencePictureControl, type ReferencePictureOption } from './ReferencePictureControl';
import { useFormat, useT } from '@/i18n';
import { expandShapeSummaryMsg, operationBlockerMsg } from '@/lib/editor/labels-msg';

/** The words under every paid tool's Run button, from one source (the capabilities estimate). */
export function CreditNote({
  estimate,
  charged,
  testId,
}: {
  estimate: PictureEstimate | null | undefined;
  /** What the last run cost; shown as "Used N credits". */
  charged?: number | null;
  testId: string;
}) {
  const t = useT('editor-thumbnail-b.ai.credit');
  const line = estimateLine(estimate);
  const unaffordable = unaffordableText(estimate);
  const used = usedLine(charged);
  if (!line && !used) return null;
  return (
    <Stack gap={2} data-testid={testId}>
      {line && (
        <Text size="xs" c={unaffordable ? 'red' : 'dimmed'} data-testid={`${testId}-estimate`}>
          {unaffordable ?? t('costs', { line })}{' '}
          {unaffordable && (
            <Anchor component={Link} href="/settings/billing" size="xs">
              {t('addCredits')}
            </Anchor>
          )}
        </Text>
      )}
      {used && (
        <Text size="xs" c="dimmed" data-testid={`${testId}-used`}>
          {used}
        </Text>
      )}
    </Stack>
  );
}

/** The "not available here" card: members are told who to ask, admins get the link to set it up. */
export function NoProviderCard({ reason, canConfigure }: { reason: string; canConfigure: boolean }) {
  const t = useT('editor-thumbnail-b.ai.unavailable');
  return (
    <Alert color="gray" variant="light" p="xs" title={t('title')} data-testid="picture-tool-unavailable">
      <Text size="xs">{reason}</Text>
      {canConfigure && (
        <Anchor component={Link} href="/admin/inference-providers" size="xs" fw={500} mt={4} display="block">
          {t('setUp')}
        </Anchor>
      )}
    </Alert>
  );
}

export interface AiPanelProps {
  /** The tool the user picked, or null before the first pick. */
  tool: AiToolId | null;
  onPickTool(tool: AiToolId): void;
  /** The selected layer, or null. */
  layer: EditorLayer | null;
  /** The selected layer's own pixel size, when known (from the server or the decoded picture). */
  layerSize?: { width: number; height: number } | null;
  hasMask: boolean;
  canvas: { width: number; height: number };
  /**
   * What the AI service set up for each tool can take (prompt or not; expand's geometry and limits), as the
   * server described it. Absent while loading: the panel then shows the tool's default controls.
   */
  capabilities?: Partial<Record<EditImageOperation, ImageOperationCapabilities>>;
  /** Operations the server said this workspace cannot run, each with the server's own reason. */
  unavailable: Partial<Record<EditImageOperation, string>>;
  /** What served the last run of each operation, and what it cost. */
  lastRun: Partial<Record<EditImageOperation, Pick<ImageOperationResult, 'provider' | 'usage' | 'chargedCredits'>>>;
  /** True when the caller may set up the workspace's AI services. */
  canConfigure?: boolean;
  /** Why "Design a graphic" cannot run (no service), or null. */
  graphicUnavailable?: { reason: string; canConfigure: boolean } | null;
  /** The operation in flight, if any. Only one runs at a time. */
  running: EditImageOperation | null;
  onRun(op: EditImageOperation, input: OperationInput): void;
  /** The cut-out controls, shown when the subject tool is picked. */
  subjectControls: React.ReactNode;
  /** The "Design a graphic" controls, shown when that tool is picked. */
  designControls?: React.ReactNode;
  /**
   * The project's own pictures, offered as references to the one tool that takes them ("edit with
   * words"). Ignored for every other tool.
   */
  referencePictures?: readonly ReferencePictureOption[];
  /**
   * Uploads a picture from the device and answers it as an ordinary project file, so the panel can use
   * it as a reference. Absent: only the project's existing pictures are offered.
   */
  onUploadReference?(file: File): Promise<ReferencePictureOption>;
  /** The brush controls, shown above erase and fill (they act on a painted area). */
  brushControls: React.ReactNode;
  /**
   * The project's avatars and objects, offered to the two tools that can show them ("create a picture" and
   * "edit with words"). Null or absent while the project's avatars do not answer (the feature is off here):
   * no picker is shown at all.
   */
  subjects?: AiPanelSubjects | null;
  /**
   * What the server said about a tool's run WITH avatars or objects, for the tool whose run then goes to another AI
   * service ("create a picture", see {@link subjectsChangeRoute}): asked with `subjects=true` once the person has
   * chosen some. Absent while it was not asked or has not answered.
   */
  subjectRoute?: Partial<Record<EditImageOperation, SubjectRouteAnswer>>;
  /**
   * Told whether the person has chosen avatars or objects for such a tool, so the caller can ask the server about
   * that route. Called again whenever the answer changes.
   */
  onSubjectsChosen?(op: EditImageOperation, chosen: boolean): void;
}

/** The capabilities route's answer for a run with subjects: what it can take and costs, or why it cannot run. */
export interface SubjectRouteAnswer {
  capabilities?: ImageOperationCapabilities;
  /** The server's own reason the run with subjects cannot happen here. */
  unavailable?: string;
}

export interface AiPanelSubjects {
  projectId: string;
  avatars: readonly ProjectAvatar[];
}

interface FormState {
  prompt: string;
  params: Record<string, number>;
  /** Expand: grow to a shape (`aspect`) or by pixels per side. */
  expandMode?: 'shape' | 'margins';
  /** Expand: the chosen shape; null until the user picks one (the default follows the canvas). */
  aspect?: string | null;
  /** "Edit with words" only: the reference pictures' project file ids, in the order the AI sees them. */
  referenceProjectFileIds?: string[];
  /** "Create a picture" and "edit with words": the avatars and objects to show, in the order chosen. */
  subjectIds?: string[];
}

/**
 * The AI tab: one "Choose an AI tool" entry that opens the picker (every tool, with a sentence about
 * what it does), and then only the chosen tool's controls. When a tool cannot run (no layer, no mask,
 * no prompt) the reason is next to the disabled button; when the server answers that this workspace
 * has no provider for it, the tool stays disabled with the server's own reason instead of failing
 * again on every press.
 *
 * Which AI service and model answered is the server's choice; after a run the tool shows what
 * served it, so the cost of a repeat is not a surprise.
 */
export function AiPanel({
  tool,
  onPickTool,
  layer,
  layerSize = null,
  hasMask,
  canvas,
  capabilities = {},
  unavailable,
  lastRun,
  canConfigure = false,
  graphicUnavailable = null,
  running,
  onRun,
  subjectControls,
  designControls,
  referencePictures = [],
  onUploadReference,
  brushControls,
  subjects = null,
  subjectRoute,
  onSubjectsChosen,
}: AiPanelProps) {
  const t = useT('editor-thumbnail-b.ai');
  const [pickerOpen, setPickerOpen] = useState(false);
  const [forms, setForms] = useState<Partial<Record<EditImageOperation, FormState>>>({});
  // Pictures uploaded from here but not yet in the caller's list, so a chosen reference keeps its name.
  const [uploadedPictures, setUploadedPictures] = useState<ReferencePictureOption[]>([]);

  const referenceOptions: ReferencePictureOption[] = [
    ...referencePictures,
    ...uploadedPictures.filter((uploaded) => !referencePictures.some((picture) => picture.value === uploaded.value)),
  ];
  const uploadReference = onUploadReference
    ? async (file: File): Promise<ReferencePictureOption> => {
        const uploaded = await onUploadReference(file);
        setUploadedPictures((cur) => (cur.some((picture) => picture.value === uploaded.value) ? cur : [...cur, uploaded]));
        return uploaded;
      }
    : undefined;

  const formOf = (op: EditImageOperation): FormState => forms[op] ?? { prompt: '', params: defaultParams(op, canvas) };
  const patchForm = (op: EditImageOperation, patch: Partial<FormState>) =>
    setForms((prev) => ({ ...prev, [op]: { ...formOf(op), ...patch } }));

  const picker = (
    <AiToolPicker
      graphicUnavailable={graphicUnavailable?.reason ?? null}
      opened={pickerOpen}
      onClose={() => setPickerOpen(false)}
      current={tool}
      unavailable={unavailable}
      onPick={(id) => {
        setPickerOpen(false);
        onPickTool(id);
      }}
    />
  );

  if (!tool) {
    return (
      <Stack gap="sm">
        <UnstyledButton
          onClick={() => setPickerOpen(true)}
          aria-haspopup="dialog"
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 12,
            padding: 14,
            borderRadius: 10,
            border: '1px dashed var(--mantine-color-blue-5)',
            background: 'rgba(77,171,247,0.06)',
          }}
        >
          <ThemeIcon size={40} radius="md" variant="light">
            <IconWand size={22} />
          </ThemeIcon>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Text size="sm" fw={600}>
              {t('chooseTool')}
            </Text>
            <Text size="xs" c="dimmed">
              {t('chooseToolHint')}
            </Text>
          </div>
          <IconChevronRight size={18} aria-hidden />
        </UnstyledButton>
        <Text size="xs" c="dimmed">
          {t('paidNote')}
        </Text>
        {picker}
      </Stack>
    );
  }

  const info = aiToolInfo(tool);
  const Icon = AI_TOOL_ICONS[tool];

  return (
    <Stack gap="sm" data-testid={`ai-tool-${tool}`}>
      <Group gap="sm" wrap="nowrap" align="flex-start">
        <ThemeIcon size={36} radius="md" variant="light">
          <Icon size={20} />
        </ThemeIcon>
        <div style={{ flex: 1, minWidth: 0 }}>
          <Text size="sm" fw={600}>
            {info.title}
          </Text>
          <Text size="xs" c="dimmed">
            {info.description}
          </Text>
        </div>
        <Button size="compact-xs" fz="xs" variant="light" onClick={() => setPickerOpen(true)} aria-haspopup="dialog">
          {t('changeTool')}
        </Button>
      </Group>
      <Divider />
      {tool === 'subject' ? (
        subjectControls
      ) : tool === 'designGraphic' ? (
        designControls
      ) : (
        <>
          {info.canvasTool === 'brush' && brushControls}
          <OperationForm
            op={tool}
            form={formOf(tool)}
            onForm={(patch) => patchForm(tool, patch)}
            layer={layer}
            layerSize={layerSize}
            canvas={canvas}
            caps={capabilities[tool] ?? null}
            hasMask={hasMask}
            reason={unavailable[tool]}
            canConfigure={canConfigure}
            last={lastRun[tool]}
            running={running}
            referencePictures={referenceOptions}
            onUploadReference={uploadReference}
            subjects={subjects}
            subjectRoute={subjectRoute?.[tool]}
            onSubjectsChosen={onSubjectsChosen}
            onRun={onRun}
          />
        </>
      )}
      {picker}
    </Stack>
  );
}

function OperationForm({
  op,
  form,
  onForm,
  layer,
  layerSize,
  canvas,
  caps: plainCaps,
  hasMask,
  reason: plainReason,
  canConfigure,
  last,
  running,
  referencePictures,
  onUploadReference,
  subjects,
  subjectRoute,
  onSubjectsChosen,
  onRun,
}: {
  op: EditImageOperation;
  form: FormState;
  onForm(patch: Partial<FormState>): void;
  layer: EditorLayer | null;
  layerSize: { width: number; height: number } | null;
  canvas: { width: number; height: number };
  caps: ImageOperationCapabilities | null;
  hasMask: boolean;
  reason: string | undefined;
  canConfigure: boolean;
  last: Pick<ImageOperationResult, 'provider' | 'usage' | 'chargedCredits'> | undefined;
  running: EditImageOperation | null;
  referencePictures: readonly ReferencePictureOption[];
  onUploadReference: ((file: File) => Promise<ReferencePictureOption>) | undefined;
  subjects: AiPanelSubjects | null;
  subjectRoute: SubjectRouteAnswer | undefined;
  onSubjectsChosen: ((op: EditImageOperation, chosen: boolean) => void) | undefined;
  onRun(op: EditImageOperation, input: OperationInput): void;
}) {
  const t = useT('editor-thumbnail-b.ai.form');
  const tSub = useT('editor-thumbnail-b.ai.subjects');
  const coarse = useCoarsePointer();
  const f = useFormat();
  const info = OPERATION_INFO[op];
  const isExpand = op === 'expand';
  const expandMode = form.expandMode ?? 'shape';
  const canvasShape = aspectOf(canvas.width, canvas.height);
  const aspect = isExpand && expandMode === 'shape' ? (form.aspect ?? defaultExpandAspect(canvas, layerSize)) : null;
  // Avatars and objects: only for the two tools that take them, and only while the project's avatars answer.
  const offersSubjects = takesSubjects(op) && subjects !== null;
  const chosenSubjects = offersSubjects ? (form.subjectIds ?? []) : [];
  // "Create a picture" with subjects is made by another AI service (the picture-edit one, from the subjects' own
  // pictures): once some are chosen the caller asks the server about THAT run, and its answer is what is shown.
  const movesRoute = offersSubjects && subjectsChangeRoute(op);
  const routeAsked = movesRoute && sendableSubjectIds(chosenSubjects, subjects?.avatars, { forPictures: true }).length > 0;
  useEffect(() => {
    if (movesRoute) onSubjectsChosen?.(op, routeAsked);
  }, [movesRoute, onSubjectsChosen, op, routeAsked]);
  const route = routeAsked ? subjectRoute : undefined;
  const support = subjectSupport(route?.capabilities ?? plainCaps);
  const subjectRules = { forPictures: true, max: support.maxSubjects };
  // A model that cannot be shown them gets none: the note under the picker says so.
  const sentSubjectIds = offersSubjects && support.supported ? sendableSubjectIds(chosenSubjects, subjects?.avatars, subjectRules) : [];
  const staleSubject = offersSubjects && support.supported && hasUnusableChosen(chosenSubjects, subjects?.avatars, subjectRules);
  // The run really goes with subjects: show that route's price and availability (none yet while it is being asked).
  const routed = routeAsked && sentSubjectIds.length > 0;
  const caps = routed ? (route?.capabilities ?? null) : plainCaps;
  const reason = routed ? route?.unavailable : plainReason;
  const input: OperationInput = {
    layerId: layer?.id ?? null,
    hasMask,
    prompt: form.prompt,
    params: form.params,
    aspect,
    referenceProjectFileIds: form.referenceProjectFileIds ?? [],
    subjectIds: sentSubjectIds,
  };
  const shapeBlocker =
    aspect && layerSize && hasShape(layerSize, aspect) ? t('alreadyShape', { aspect }) : null;
  const blocker = reason ? null : (shapeBlocker ?? (() => { const m = operationBlockerMsg(op, input, layer ? layer.kind !== 'text' : false); return m ? t.msg(m) : null; })() ?? (staleSubject ? tSub('stale') : null));
  const promptRule = promptRuleFor(op, caps);
  const marginMax = expandMarginMax(caps);
  const numberFields = isExpand && expandMode === 'shape' ? [] : info.params.filter((f) => f.kind === 'number');
  const choiceFields = info.params.filter((f) => f.kind === 'choice');
  const shapeOptions = [
    ...(EXPAND_SHAPES.includes(canvasShape) ? [] : [canvasShape]),
    ...EXPAND_SHAPES,
  ].map((shape) => ({
    value: shape,
    label: shape === canvasShape ? t('shapeThisDesign', { shape }) : t('shapeLabeled', { shape, name: EXPAND_SHAPE_LABELS[shape] ?? t('shapeCustom') }),
  }));

  return (
    <Stack gap="sm">
      {info.needsLayer && layer && (
        <Text size="xs">
          {t.rich('worksOn', { layer: layer.label, b: (c) => <b>{c}</b> })}
        </Text>
      )}

      {isExpand && promptRule === 'none' && caps && (
        <Text size="xs" c="dimmed">
          {t('noDescription')}
        </Text>
      )}

      {isExpand && (
        <>
          <SegmentedControl
            size="xs"
            fullWidth
            aria-label={t('howToExpand')}
            disabled={!!reason}
            data={[
              { value: 'shape', label: t('fitShape') },
              { value: 'margins', label: t('pixelsPerSide') },
            ]}
            value={expandMode}
            onChange={(v) => onForm({ expandMode: v === 'margins' ? 'margins' : 'shape' })}
          />
          {expandMode === 'shape' ? (
            <Stack gap={4}>
              <Select
                size="xs"
                label={t('shape')}
                data={shapeOptions}
                value={aspect}
                allowDeselect={false}
                disabled={!!reason}
                onChange={(v) => onForm({ aspect: v ?? null })}
              />
              {aspect && (
                <Text size="xs" c="dimmed">
                  {t.msg(expandShapeSummaryMsg(aspect, layerSize))}
                </Text>
              )}
            </Stack>
          ) : (
            <Text size="xs" c="dimmed">
              {t('marginHelp', { max: f.number(marginMax), capped: caps?.expand?.maxMarginPx ? 'yes' : 'no' })}
            </Text>
          )}
        </>
      )}

      {promptRule !== 'none' && (
        <TextInput
          size="xs"
          label={info.promptLabel}
          placeholder={info.promptPlaceholder}
          value={form.prompt}
          disabled={!!reason}
          onChange={(e) => onForm({ prompt: e.currentTarget.value })}
        />
      )}

      {offersSubjects && subjects && (
        <Stack gap={4} data-testid={`ai-subjects-${op}`}>
          <SubjectPicker
            projectId={subjects.projectId}
            avatars={subjects.avatars}
            value={chosenSubjects}
            onChange={(ids) => onForm({ subjectIds: ids })}
            forPictures
            max={support.maxSubjects}
            label={tSub('label')}
            hint={tSub('hint', { max: support.maxSubjects })}
            disabled={(movesRoute ? false : !!reason) || !support.supported || running !== null}
            touch={coarse}
          />
          {movesRoute && !routed && plainReason && (
            <Text size="xs" c="dimmed" role="note" data-testid={`ai-subjects-other-service-${op}`}>
              {tSub('otherService')}
            </Text>
          )}
          {!support.supported && (
            <Text size="xs" c="orange" role="note" data-testid={`ai-subjects-unsupported-${op}`}>
              {support.models.length > 0 ? tSub('unsupportedModels', { models: f.list(support.models) }) : tSub('unsupported')}
            </Text>
          )}
        </Stack>
      )}

      {takesReferencePicture(op) && (
        <ReferencePictureControl
          pictures={referencePictures}
          value={form.referenceProjectFileIds ?? []}
          onChange={(ids) => onForm({ referenceProjectFileIds: ids })}
          onUpload={onUploadReference}
          disabled={!!reason}
        />
      )}

      {choiceFields.map((field) =>
        field.kind === 'choice' ? (
          <div key={field.key}>
            <Text size="xs" mb={2}>
              {field.label}
            </Text>
            <SegmentedControl
              size="xs"
              fullWidth
              aria-label={field.label}
              disabled={!!reason}
              data={field.options.map((o) => ({ value: String(o), label: t('multiplier', { n: o }) }))}
              value={String(form.params[field.key] ?? field.defaultValue)}
              onChange={(v) => onForm({ params: { ...form.params, [field.key]: Number(v) } })}
            />
          </div>
        ) : null,
      )}

      {numberFields.length > 0 && (
        <SimpleGrid cols={2} spacing="xs">
          {numberFields.map((field) =>
            field.kind === 'number' ? (
              <NumberInput
                key={field.key}
                size="xs"
                label={field.label}
                min={field.min}
                max={isExpand ? marginMax : field.max}
                step={field.step}
                disabled={!!reason}
                value={form.params[field.key] ?? field.defaultValue}
                onChange={(v) => {
                  const value = typeof v === 'number' ? v : field.defaultValue;
                  const clamped = isExpand ? Math.min(Math.max(0, value), marginMax) : value;
                  onForm({ params: { ...form.params, [field.key]: clamped } });
                }}
              />
            ) : null,
          )}
        </SimpleGrid>
      )}

      {reason ? (
        <NoProviderCard reason={reason} canConfigure={canConfigure} />
      ) : (
        <>
          <PinnedRun>
            <Button
              size="sm" fz="xs"
              fullWidth
              leftSection={<IconWand size={16} />}
              loading={running === op}
              disabled={blocker !== null || isUnaffordable(caps?.estimate) || (running !== null && running !== op)}
              onClick={() => onRun(op, input)}
            >
              {runLabel(info.action, caps?.estimate)}
            </Button>
            {running === op && (
              <Text size="xs" c="dimmed" data-testid="ai-wait-note">
                {t('waitNote')}
              </Text>
            )}
            {blocker && (
              <Text size="xs" c="dimmed">
                {blocker}
              </Text>
            )}
            <CreditNote estimate={caps?.estimate} charged={last?.chargedCredits} testId={`credits-${op}`} />
          </PinnedRun>
        </>
      )}

    </Stack>
  );
}
