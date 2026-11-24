'use client';

import React, { useState } from 'react';
import Link from 'next/link';
import {
  Anchor,
  Alert,
  Badge,
  Button,
  Group,
  Loader,
  NativeSelect,
  NumberInput,
  SegmentedControl,
  SimpleGrid,
  Stack,
  Text,
  Textarea,
} from '@mantine/core';
import { IconPencil, IconShape } from '@tabler/icons-react';
import { graphicErrorOf } from '@/lib/api/image-documents';
import { PinnedRun } from './PinnedRun';
import { graphicCostLineMsg, graphicPlacementLabelMsg, graphicStatusLabelMsg, graphicFormBlockerMsg, noProviderMsg, usedLineMsg } from '@/lib/editor/labels-msg';
import { creditsNeededLine } from '@/lib/utils/billing';
import { NoProviderCard } from './AiPanel';
import {
  GRAPHIC_DESCRIPTION_MAX,
  GRAPHIC_FRAME_MAX,
  GRAPHIC_FRAME_MIN,
  GRAPHIC_PLACEMENT_OPTIONS,
  activeGraphicJob,
  buildGraphicRequest,
  defaultGraphicForm,
  isGraphicJobActive,
  latestProgressStage,
  plainGraphicError,
  type GraphicForm,
  type RevisableGraphic,
} from '@/lib/editor/graphics';
import { useExecutionStream } from '@/lib/hooks/use-execution-stream';
import { useT } from '@/i18n';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import { InlineJobStatus } from '@/components/activity';
import type {
  CreateImageGraphicRequest,
  GraphicJobStatus,
  GraphicPlacement,
  ImageGraphicJob,
  ImageGraphicsResponse,
} from '@/lib/types/image-document';
import { plainGraphicErrorMsg } from '@/lib/editor/reason-msg';

/** How many past designs the list shows. */
const LIST_LIMIT = 8;

const STATUS_COLOR: Record<GraphicJobStatus, string> = {
  Queued: 'gray',
  Running: 'blue',
  Passed: 'green',
  Failed: 'red',
  Cancelled: 'gray',
};

export interface DesignGraphicPanelProps {
  /** The document's design jobs, cost and availability; undefined while loading. */
  data: ImageGraphicsResponse | undefined;
  /** Why the list could not be read, if it could not. */
  loadError?: string | null;
  canvas: { width: number; height: number };
  /** The selected layer when it is a designed graphic that can be changed, else null. */
  revisable: RevisableGraphic | null;
  /** The label of the layer being changed, for the heading. */
  revisableLabel?: string | null;
  /**
   * Starts a design (or a change). Resolves once the job is queued; a refusal rejects with the
   * server's error, which the panel shows verbatim.
   */
  onDesign(request: CreateImageGraphicRequest): Promise<void>;
}

/**
 * "Design a graphic": describe a title, a badge, a price tag or a shape, and an AI designer draws it
 * as a new transparent layer. A design takes a few minutes, so it runs in the background: the panel
 * lists this picture's designs with what each is doing, the canvas shows the working effect where
 * the graphic will land, and the result arrives as a layer on its own. With a designed layer
 * selected, the panel changes that graphic instead (it can be switched back to a new one).
 */
export function DesignGraphicPanel({
  data,
  loadError = null,
  canvas,
  revisable,
  revisableLabel = null,
  onDesign,
}: DesignGraphicPanelProps) {
  const t = useT('editor-thumbnail-b.graphic');
  const [form, setForm] = useState<GraphicForm>(() => defaultGraphicForm(canvas));
  const [change, setChange] = useState('');
  // The layer the user chose to design a NEW graphic beside rather than change. Keyed by layer, so
  // selecting another designed layer offers to change that one.
  const [newInsteadOf, setNewInsteadOf] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [queued, setQueued] = useState<string | null>(null);

  const revising = revisable && newInsteadOf !== revisable.layerId ? revisable : null;
  const unavailable = data ? !data.available : false;
  const costMsg = graphicCostLineMsg(data?.estimate, data?.estimate ? creditsNeededLine(data.estimate.requiredCredits) : null);
  const cost = costMsg ? { ...costMsg, text: t.msg(costMsg.text) } : null;
  const unaffordable = cost ? !cost.affordable : false;
  // "Design it, about 3 credits": the price on the button (only when billing is on and the run is affordable).
  const priceSuffix = cost && data?.estimate?.billingEnabled && cost.affordable ? t('priceSuffix', { text: `${cost.text.charAt(0).toLowerCase()}${cost.text.slice(1)}` }) : '';
  const effective: GraphicForm = revising ? { ...form, description: change } : form;
  const blockerMsg = graphicFormBlockerMsg(effective, !!revising);
  const blocker = blockerMsg ? t.msg(blockerMsg) : null;
  const disabled = !data || unavailable || busy;

  const submit = async () => {
    if (blocker || disabled || unaffordable) return;
    setBusy(true);
    setError(null);
    setQueued(null);
    try {
      await onDesign(buildGraphicRequest(effective, canvas, revising?.layerId ?? null));
      if (revising) setChange('');
      else setForm((cur) => ({ ...cur, description: '' }));
      setQueued(
        revising
          ? t('queuedChange')
          : t('queuedNew'),
      );
    } catch (cause: unknown) {
      setError(graphicErrorOf(cause).message);
    } finally {
      setBusy(false);
    }
  };

  const jobs = (data?.graphics ?? []).slice(0, LIST_LIMIT);
  const active = activeGraphicJob(data?.graphics);

  return (
    <Stack gap="sm" data-testid="design-graphic-panel">
      <Text size="xs" c="dimmed">
        {t('intro')}
      </Text>

      {loadError && (
        <Alert color="red" variant="light" p="xs">
          <Text size="xs">{loadError}</Text>
        </Alert>
      )}

      {unavailable && (
        <NoProviderCard
          reason={
            data?.unavailableCode === 'NO_PROVIDER' || data?.canConfigure !== undefined
              ? t.msg(noProviderMsg(data?.canConfigure === true))
              : data?.unavailableReason || t('cannotDesign')
          }
          canConfigure={data?.canConfigure === true}
        />
      )}

      {revising ? (
        <Stack gap="xs" data-testid="design-graphic-revise">
          <Group gap={6} wrap="nowrap">
            <IconPencil size={16} aria-hidden />
            <Text size="sm" fw={600}>
              {t('changeTitle')}
            </Text>
          </Group>
          {revisableLabel && (
            <Text size="xs">
              {t.rich('changing', { label: revisableLabel, b: (c) => <b>{c}</b> })}
            </Text>
          )}
          {revising.prompt && (
            <Text size="xs" c="dimmed">
              {t('designedFrom', { prompt: revising.prompt })}
            </Text>
          )}
          <Textarea
            size="xs"
            label={t('whatChange')}
            placeholder={t('whatChangePlaceholder')}
            minRows={3}
            maxLength={GRAPHIC_DESCRIPTION_MAX}
            value={change}
            disabled={disabled}
            onChange={(e) => setChange(e.currentTarget.value)}
          />
          <Button
            size="compact-xs" fz="xs"
            variant="subtle"
            onClick={() => setNewInsteadOf(revising.layerId)}
            style={{ alignSelf: 'flex-start' }}
          >
            {t('newInstead')}
          </Button>
        </Stack>
      ) : (
        <Stack gap="xs">
          {revisable && (
            <Button
              size="compact-xs" fz="xs"
              variant="subtle"
              leftSection={<IconPencil size={14} />}
              onClick={() => setNewInsteadOf(null)}
              style={{ alignSelf: 'flex-start' }}
            >
              {t('changeInstead')}
            </Button>
          )}
          <Textarea
            size="xs"
            label={t('describe')}
            placeholder={t('describePlaceholder')}
            minRows={3}
            maxLength={GRAPHIC_DESCRIPTION_MAX}
            value={form.description}
            disabled={disabled}
            onChange={(e) => {
              const description = e.currentTarget.value;
              setForm((cur) => ({ ...cur, description }));
            }}
          />
          <NativeSelect
            size="xs"
            label={t('where')}
            data={GRAPHIC_PLACEMENT_OPTIONS.map((o) => ({ value: o.value, label: t.msg(graphicPlacementLabelMsg(o.value)) }))}
            value={form.placement}
            disabled={disabled}
            onChange={(e) => {
              const placement = e.currentTarget.value as GraphicPlacement;
              setForm((cur) => ({ ...cur, placement }));
            }}
          />
          <div>
            <Text size="xs" mb={2} id="design-graphic-size">
              {t('size')}
            </Text>
            <SegmentedControl
              size="xs"
              fullWidth
              aria-labelledby="design-graphic-size"
              disabled={disabled}
              data={[
                { value: 'fit', label: t('sizeFit') },
                { value: 'custom', label: t('sizeCustom') },
              ]}
              value={form.size}
              onChange={(v) => setForm((cur) => ({ ...cur, size: v === 'custom' ? 'custom' : 'fit' }))}
            />
          </div>
          {form.size === 'custom' && (
            <SimpleGrid cols={2} spacing="xs">
              <NumberInput
                size="xs"
                label={t('width')}
                suffix=" px"
                min={GRAPHIC_FRAME_MIN}
                max={GRAPHIC_FRAME_MAX}
                value={form.width}
                disabled={disabled}
                onChange={(v) => setForm((cur) => ({ ...cur, width: typeof v === 'number' ? v : Number(v) }))}
              />
              <NumberInput
                size="xs"
                label={t('height')}
                suffix=" px"
                min={GRAPHIC_FRAME_MIN}
                max={GRAPHIC_FRAME_MAX}
                value={form.height}
                disabled={disabled}
                onChange={(v) => setForm((cur) => ({ ...cur, height: typeof v === 'number' ? v : Number(v) }))}
              />
            </SimpleGrid>
          )}
        </Stack>
      )}

      {cost && (
        <Text size="xs" c={cost.affordable ? 'dimmed' : 'red'} data-testid="design-graphic-cost">
          {cost.text}
          {!cost.affordable && (
            <>
              {' '}
              <Anchor component={Link} href="/settings/billing" size="xs">
                {t('addCredits')}
              </Anchor>
            </>
          )}
        </Text>
      )}
      {cost?.needs && (
        <Text size="xs" c="dimmed" data-testid="design-graphic-needs">
          {cost.needs}
        </Text>
      )}

      {!unavailable && (
        <PinnedRun>
          <Button
            size="sm" fz="xs"
            fullWidth
            leftSection={<IconShape size={16} />}
            loading={busy}
            disabled={disabled || unaffordable || blocker !== null}
            onClick={() => void submit()}
          >
            {revising ? t('changeIt') : t('designIt')}
            {priceSuffix}
          </Button>
          {blocker && data && (
            <Text size="xs" c="dimmed">
              {blocker}
            </Text>
          )}
        </PinnedRun>
      )}

      {error && (
        <Alert color="red" variant="light" p="xs" withCloseButton onClose={() => setError(null)}>
          <Text size="xs">{error}</Text>
        </Alert>
      )}
      {queued && !error && (
        <Text size="xs" c="blue" role="status">
          {queued}
        </Text>
      )}

      {!data && !loadError && (
        <Group gap="xs">
          <Loader size="xs" />
          <Text size="xs" c="dimmed">
            {t('loading')}
          </Text>
        </Group>
      )}

      {jobs.length > 0 && (
        <Stack gap={6} role="list" aria-label={t('listTitle')}>
          <Text size="xs" fw={700} tt="uppercase" c="dimmed" style={{ letterSpacing: 0.6 }}>
            {t('listTitle')}
          </Text>
          {jobs.map((job) => (
            <GraphicJobRow key={job.id} job={job} live={active?.id === job.id} />
          ))}
        </Stack>
      )}
    </Stack>
  );
}

function GraphicJobRow({ job, live }: { job: ImageGraphicJob; live: boolean }) {
  const t = useT('editor-thumbnail-b.graphic');
  const usedMsg = usedLineMsg(job.chargedCredits);
  // The same run as the tray shows it, in place (matched by its execution).
  const tracked = useJobFor((j) => !!job.executionId && j.executionId === job.executionId);
  return (
    <Stack
      gap={2}
      role="listitem"
      data-testid={`graphic-job-${job.id}`}
      style={{ padding: 8, borderRadius: 8, border: '1px solid var(--mantine-color-default-border)' }}
    >
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Text size="xs" lineClamp={2} style={{ minWidth: 0 }}>
          {job.revisesLayerId ? t('changeOf', { description: job.description }) : job.description}
        </Text>
        <Badge size="xs" fz="xs" variant="light" color={STATUS_COLOR[job.status] ?? 'gray'} style={{ flexShrink: 0 }}>
          {job.status in STATUS_COLOR ? t.msg(graphicStatusLabelMsg(job.status)) : job.status}
        </Badge>
      </Group>
      {isGraphicJobActive(job) &&
        (tracked ? (
          <InlineJobStatus job={tracked} compact />
        ) : live && job.executionId ? (
          <GraphicJobProgress executionId={job.executionId} workflowId={job.workflowId} />
        ) : (
          <Text size="xs" c="dimmed">
            {job.status === 'Queued' ? t('waiting') : t('designing')}
          </Text>
        ))}
      {job.status === 'Failed' && job.error && (
        <Text size="xs" c="red">
          {t.msg(plainGraphicErrorMsg(job.error))}
        </Text>
      )}
      {job.status === 'Passed' && usedMsg && (
        <Text size="xs" c="dimmed" data-testid="design-graphic-used">
          {t.msg(usedMsg)}
        </Text>
      )}
      {job.status === 'Passed' && job.summary && (
        <Text size="xs" c="dimmed">
          {job.summary}
        </Text>
      )}
    </Stack>
  );
}

/** The latest stage of the running design, from its live progress feed. */
function GraphicJobProgress({ executionId, workflowId }: { executionId: string; workflowId: string }) {
  const t = useT('editor-thumbnail-b.graphic');
  const { events } = useExecutionStream({ projectId: '', workflowId, executionId, enabled: true });
  const caption = latestProgressStage(events);
  return (
    <Group gap={6} wrap="nowrap" role="status" aria-live="polite">
      <Loader size={10} />
      <Text size="xs" c="dimmed">
        {caption ? `${caption.replace(/…$/, '')}…` : t('designing')}
      </Text>
    </Group>
  );
}
