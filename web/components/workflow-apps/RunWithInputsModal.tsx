'use client';

import { useMemo, useState } from 'react';
import { useDebouncedValue } from '@mantine/hooks';
import { Modal, Textarea, Group, Button, Text, Stack, List, Alert, ScrollArea, Collapse } from '@mantine/core';
import { IconAlertTriangle, IconChevronDown, IconClock, IconPlayerPlay } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { useWorkflowCostEstimateWithBrief } from '@/lib/hooks/use-billing';
import { useRunForm } from '@/lib/hooks/use-run-form';
import type { AppManifest, RunBrief, AppMediaEgress } from '@/lib/types/workflow';
import { RunStylePicker } from '@/components/styles/RunStylePicker';
import { RunCostEstimate } from '@/components/billing/RunCostEstimate';
import { SilentRunNotice, type RunStepKind } from '@/components/workflows/SilentRunNotice';
import { AppInputField } from './AppInputField';
import { PermissionBlock } from './PermissionBlock';
import { briefInputs, firstMissing, initialValues, isAppInput, isVisible, pageLayout, pricingValues, type InputValue, type InputValues, isPermissionInput } from './app-input-model';

interface RunWithInputsModalProps {
  opened: boolean;
  onClose: () => void;
  /** The typed run brief to start with: the app's inputs in `inputs`, or the notes when the app has none. */
  onConfirm: (brief: RunBrief) => void;
  executing?: boolean;
  /** The app's page (the effective manifest). Absent when the apps switch is off: the notes box only. */
  manifest?: AppManifest | null;
  /** The workflow's "Require a brief" setting: the notes box (shown only without typed inputs) must be filled in. */
  requiresBrief?: boolean;
  stepSummaries?: string[];
  steps?: RunStepKind[];
  estimate?: string | null;
  workflowName?: string;
  projectId: string;
  workflowId: string;
  /** The steps the permission to send media is for (from the app view), so the dialog names them like the app page. */
  mediaEgress?: AppMediaEgress | null;
}

/**
 * Confirmation before a run starts, from the workflow page. An app with typed inputs shows exactly those inputs (the
 * same fields and value model as the app run page, sent as `brief.inputs`) and never asks for a free brief. An app
 * without any (a derived page) keeps the one notes box. Style, shape and resolution ride in the same brief. The
 * permission to send media is the app's own field here too, in the same block as on the app page: it applies to the run
 * being started and never changes a saved step.
 */
export function RunWithInputsModal({
  opened, onClose, onConfirm, executing, manifest = null, requiresBrief = false, stepSummaries = [], steps = [], estimate = null, workflowName, projectId, workflowId, mediaEgress = null,
}: RunWithInputsModalProps) {
  const t = useT('workflows.run');
  const tr = useT();
  const layout = useMemo(() => pageLayout(manifest), [manifest]);
  const inputs = layout.all;
  // The app's own permission field does not make a page "typed": a page with only that field keeps its notes box.
  const typed = inputs.some((i) => isAppInput(i) && !isPermissionInput(i));
  const sendsInputs = typed || layout.permission !== null;
  // What the person changed, over the manifest's defaults; cleared on close so each opening starts from the defaults.
  const [edits, setEdits] = useState<InputValues>({});
  const values = useMemo<InputValues>(() => ({ ...initialValues(inputs), ...edits }), [inputs, edits]);
  const [moreOpen, setMoreOpen] = useState(false);
  const form = useRunForm({ enabled: opened, projectId, requiresBrief: requiresBrief && !typed, steps });
  const { notes, setNotes, missingBrief, outputResolution, speechNotice, silentVideo } = form;
  // The dialog has no pre-run check of its own: it asks for the permission only when the app needs it whatever is chosen
  // (the run gate has the last word when a field picks the branch).
  const permissionNeeded = layout.permission !== null && mediaEgress?.alreadyAllowed !== true
    && (mediaEgress ? mediaEgress.required && mediaEgress.dependsOnChoices !== true : layout.permission.required === true);
  const missing = (typed ? firstMissing(inputs.filter((i) => !isPermissionInput(i)), values) : null)
    ?? (layout.permission && permissionNeeded && values[layout.permission.id] !== true ? layout.permission : null);
  const blocked = missing !== null || (!typed && missingBrief);

  // The price follows the chosen inputs (a lite switch, a model, a length), as on the app run page; typing does not re-price.
  const pricingInputs = typed ? pricingValues(inputs, values) : undefined;
  const pricingKey = opened && typed
    ? JSON.stringify({
        brief: { ...(pricingInputs ? { inputs: pricingInputs } : {}), ...(form.format !== 'Original' ? { format: form.format } : {}) },
        ...(outputResolution ? { outputResolution } : {}),
      })
    : null;
  const [debouncedPricingKey] = useDebouncedValue(pricingKey, 300);
  const { data: briefEstimate } = useWorkflowCostEstimateWithBrief(projectId, workflowId, debouncedPricingKey ? JSON.parse(debouncedPricingKey) : null);

  const setValue = (id: string, value: InputValue) => setEdits((current) => ({ ...current, [id]: value }));

  const renderInput = (input: (typeof inputs)[number]) => {
    if (!isAppInput(input) || !isVisible(input, values)) return null;
    return (
      <AppInputField
        key={input.id}
        input={input}
        value={values[input.id] ?? null}
        onChange={(value) => setValue(input.id, value)}
        projectId={projectId}
        workflowId={workflowId}
        phone={false}
      />
    );
  };

  const handleConfirm = () => {
    if (blocked) return;
    const brief = form.buildBrief();
    if (sendsInputs) {
      const sent = briefInputs(inputs, values);
      if (sent) brief.inputs = sent;
    }
    onConfirm(brief);
  };

  const handleClose = () => {
    form.reset();
    setEdits({});
    onClose();
  };

  return (
    <Modal opened={opened} onClose={handleClose} title={workflowName ? t('titleNamed', { name: workflowName }) : t('title')} centered size="lg">
      <Stack gap="md">
        {typed ? (
          <Stack gap="sm" data-testid="run-modal-inputs">
            {layout.shown.map(renderInput)}
            {layout.more.length > 0 && (
              <>
                <Button
                  variant="subtle"
                  onClick={() => setMoreOpen((open) => !open)}
                  aria-expanded={moreOpen}
                  rightSection={<IconChevronDown size={16} style={{ transform: moreOpen ? 'rotate(180deg)' : undefined }} aria-hidden />}
                  data-testid="run-modal-more-toggle"
                >
                  {tr('apps.run.inputs.moreOptions', { count: layout.more.length })}
                </Button>
                <Collapse expanded={moreOpen}>
                  <Stack gap="sm">{moreOpen && layout.more.map(renderInput)}</Stack>
                </Collapse>
              </>
            )}
          </Stack>
        ) : (
          <Textarea
            label={t('briefLabel')}
            description={requiresBrief ? t('briefRequired') : t('briefOptional')}
            placeholder={t('briefPlaceholder')}
            minRows={3}
            autosize
            maxRows={10}
            value={notes}
            onChange={(e) => setNotes(e.currentTarget.value)}
            required={requiresBrief}
            error={missingBrief && notes.length > 0 ? t('briefMissing') : undefined}
            data-autofocus
          />
        )}

        <RunStylePicker
          presets={form.presets}
          styles={form.styles}
          style={form.style}
          onStyleChange={form.changeStyle}
          format={form.format}
          onFormatChange={form.changeFormat}
          resolution={form.resolution}
          onResolutionChange={form.setResolution}
          maxOutputHeight={form.maxOutputHeight}
        />

        {stepSummaries.length > 0 && (
          <div>
            <Text size="sm" fw={500} mb={4}>
              {t('whatWillHappen', { count: stepSummaries.length })}
            </Text>
            <ScrollArea.Autosize mah={200}>
              <List type="ordered" size="sm" spacing={2}>
                {stepSummaries.map((s, i) => (
                  <List.Item key={i}>{s}</List.Item>
                ))}
              </List>
            </ScrollArea.Autosize>
          </div>
        )}

        {speechNotice && (
          <Alert color="yellow" variant="light" p="xs" icon={<IconAlertTriangle size={16} />} title={speechNotice.title} role="alert">
            <Text size="sm">{speechNotice.body}</Text>
          </Alert>
        )}

        {silentVideo && <SilentRunNotice />}

        {typed ? (
          <RunCostEstimate projectId={briefEstimate ? projectId : undefined} workflowId={workflowId} outputResolution={outputResolution} estimate={briefEstimate} />
        ) : (
          <RunCostEstimate projectId={projectId} workflowId={workflowId} outputResolution={outputResolution} />
        )}

        {layout.permission && (
          <PermissionBlock
            input={layout.permission}
            egress={mediaEgress}
            required={permissionNeeded}
            checked={values[layout.permission.id] === true}
            onChange={(checked) => setValue(layout.permission!.id, checked)}
            error={missing?.id === layout.permission.id ? tr('apps.permission.mediaEgress.required') : undefined}
          />
        )}

        {estimate && (
          <Alert color="blue" variant="light" p="xs" icon={<IconClock size={16} />}>
            <Text size="sm">{estimate}</Text>
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={handleClose} disabled={executing}>
            {t('cancel')}
          </Button>
          <Button
            onClick={handleConfirm}
            loading={executing}
            disabled={blocked}
            leftSection={<IconPlayerPlay size={16} />}
            variant="gradient"
            gradient={{ from: 'violet', to: 'purple' }}
          >
            {t('start')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
