'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import { Alert, Button, Checkbox, Group, Modal, MultiSelect, SegmentedControl, Stack, Switch, Text, TextInput } from '@mantine/core';
import { ApiError } from '@/lib/api/client';
import { createWebhook, updateWebhook } from '@/lib/api/webhooks';
import { useProjects } from '@/lib/hooks/use-projects';
import { WEBHOOK_EVENTS, type WebhookEndpoint, type WebhookEvent } from '@/lib/types/webhook';
import { SecretOnceStep } from './SecretOnceStep';
import { useT } from '@/i18n';

interface WebhookFormModalProps {
  opened: boolean;
  /** The endpoint being edited; null creates a new one. */
  endpoint: WebhookEndpoint | null;
  onClose: () => void;
  /** After a successful save, before the secret step (create only). */
  onSaved: () => void | Promise<void>;
}

const DEFAULT_EVENTS: string[] = ['run.succeeded', 'run.failed', 'run.cancelled'];

/**
 * Create or edit a webhook endpoint. A create ends on the secret-shown-once step; the secret lives
 * in this component's state only and `reset()` clears it on close.
 */
export function WebhookFormModal({ opened, endpoint, onClose, onSaved }: WebhookFormModalProps) {
  const t = useT('org.apiKeys');
  const tAll = useT();
  const { data: projects } = useProjects();
  const editing = endpoint !== null;
  const [step, setStep] = useState<'form' | 'secret'>('form');
  const [url, setUrl] = useState(endpoint?.url ?? '');
  const [description, setDescription] = useState(endpoint?.description ?? '');
  const [events, setEvents] = useState<string[]>(endpoint?.events ?? DEFAULT_EVENTS);
  const [projectMode, setProjectMode] = useState<'all' | 'selected'>(endpoint?.projectIds?.length ? 'selected' : 'all');
  const [projectIds, setProjectIds] = useState<string[]>(endpoint?.projectIds ?? []);
  const [enabled, setEnabled] = useState(endpoint?.enabled ?? true);
  const [secret, setSecret] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const projectsModeLabelId = 'webhook-form-projects-mode';

  function reset() {
    setStep('form');
    setUrl('');
    setDescription('');
    setEvents(DEFAULT_EVENTS);
    setProjectMode('all');
    setProjectIds([]);
    setEnabled(true);
    setSecret('');
    setError(null);
  }

  function handleClose() {
    reset();
    onClose();
  }

  function eventLabel(e: WebhookEvent) {
    switch (e) {
      case 'run.succeeded': return t('webhooks.events.runSucceeded');
      case 'run.failed': return t('webhooks.events.runFailed');
      case 'run.cancelled': return t('webhooks.events.runCancelled');
      case 'run.step_completed': return t('webhooks.events.runStepCompleted');
      case 'run_request.started': return t('webhooks.events.runRequestStarted');
      case 'run_request.refused': return t('webhooks.events.runRequestRefused');
    }
  }

  function eventDescription(e: WebhookEvent) {
    switch (e) {
      case 'run.succeeded': return t('webhooks.events.runSucceededHelp');
      case 'run.failed': return t('webhooks.events.runFailedHelp');
      case 'run.cancelled': return t('webhooks.events.runCancelledHelp');
      case 'run.step_completed': return t('webhooks.events.runStepCompletedHelp');
      case 'run_request.started': return t('webhooks.events.runRequestStartedHelp');
      case 'run_request.refused': return t('webhooks.events.runRequestRefusedHelp');
    }
  }

  async function submit() {
    const trimmed = url.trim();
    if (!/^https:\/\/\S+$/i.test(trimmed)) return setError(t('webhooks.form.urlInvalid'));
    if (events.length === 0) return setError(t('webhooks.form.eventsRequired'));
    if (projectMode === 'selected' && projectIds.length === 0) return setError(t('webhooks.form.projectsRequired'));
    setError(null);
    setSubmitting(true);
    try {
      if (editing && endpoint) {
        await updateWebhook(endpoint.id, {
          url: trimmed,
          description: description.trim(),
          events,
          projectIds: projectMode === 'selected' ? projectIds : [],
          enabled,
        });
        await onSaved();
        handleClose();
      } else {
        const created = await createWebhook({
          url: trimmed,
          ...(description.trim() ? { description: description.trim() } : {}),
          events,
          ...(projectMode === 'selected' ? { projectIds } : {}),
        });
        setSecret(created.secret);
        setStep('secret');
        await onSaved();
      }
    } catch (err: unknown) {
      if (err instanceof ApiError && err.status === 409) setError(t('webhooks.form.limit'));
      else setError(describeError(tAll, err, t('webhooks.form.saveFailed')));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={step === 'secret' ? t('webhooks.secret.title') : editing ? t('webhooks.form.editTitle') : t('webhooks.form.createTitle')}
      centered
      size="lg"
      closeOnClickOutside={step === 'form'}
      closeOnEscape={step === 'form'}
      withCloseButton={step === 'form'}
    >
      {step === 'secret' ? (
        <SecretOnceStep body={t('webhooks.secret.body')} secret={secret} onDone={handleClose} />
      ) : (
        <Stack gap="md">
          <TextInput
            label={t('webhooks.form.urlLabel')}
            description={t('webhooks.form.urlHelp')}
            value={url}
            required
            onChange={(event) => setUrl(event.currentTarget.value)}
          />
          <TextInput
            label={t('webhooks.form.descriptionLabel')}
            value={description}
            maxLength={200}
            onChange={(event) => setDescription(event.currentTarget.value)}
          />
          <Checkbox.Group label={t('webhooks.form.eventsLabel')} value={events} onChange={setEvents}>
            <Stack gap="xs" mt="xs">
              {WEBHOOK_EVENTS.map((e) => (
                <Checkbox key={e} value={e} label={eventLabel(e)} description={eventDescription(e)} />
              ))}
            </Stack>
          </Checkbox.Group>
          <Stack gap="xs">
            <Text size="sm" fw={600} id={projectsModeLabelId}>
              {t('webhooks.form.projectsLabel')}
            </Text>
            <SegmentedControl
              aria-labelledby={projectsModeLabelId}
              value={projectMode}
              onChange={(value) => setProjectMode(value === 'selected' ? 'selected' : 'all')}
              data={[
                { value: 'all', label: t('webhooks.form.projectsAll') },
                { value: 'selected', label: t('webhooks.form.projectsSelected') },
              ]}
            />
            {projectMode === 'selected' && (
              <MultiSelect
                aria-label={t('webhooks.form.projectsLabel')}
                placeholder={t('webhooks.form.projectsPlaceholder')}
                data={(projects ?? []).map((project) => ({ value: project.id, label: project.name }))}
                value={projectIds}
                searchable
                onChange={setProjectIds}
              />
            )}
          </Stack>
          {editing && <Switch label={t('webhooks.form.enabledLabel')} checked={enabled} onChange={(e) => setEnabled(e.currentTarget.checked)} />}
          {error && (
            <Alert color="red" variant="light">
              {error}
            </Alert>
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={handleClose}>
              {t('webhooks.form.cancel')}
            </Button>
            <Button loading={submitting} onClick={() => void submit()}>
              {editing ? t('webhooks.form.submitEdit') : t('webhooks.form.submitCreate')}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
