'use client';

import { useState } from 'react';
import { Select, Text, Group, Loader, Button, Stack } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useAuth } from '@/lib/hooks/use-auth';
import { useOrgAgentModels, useOrgAgentOverrides } from '@/lib/hooks/use-agents';
import { useSelectableProviders } from '@/lib/hooks/use-inference-providers';
import { selectableLabel } from '@/lib/types/inference-provider';
import { clearOrgAgentOverride, setAgentInferenceProvider, setOrgAgentOverride } from '@/lib/api/agents';
import { msg, useT, type Msg } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface AgentInferenceProviderSelectProps {
  agentId: string;
  inferenceProviderId: string | null;
  inferenceProviderName: string | null;
  onUpdated: () => void;
  /** The agent's `AgentType` name; the workspace override is keyed by it. */
  agentType?: string | null;
  /** Built-in agents are shared rows, so a workspace's choice for them lives on the org override. */
  isBuiltIn?: boolean;
}

const DEFAULT_VALUE = '__default__';

/** A model must differ from the recommended one by at least this factor before a cost note is shown. */
const COST_NOTE_RATIO = 1.5;

function formatTimes(n: number): string {
  return n.toFixed(1).replace(/\.0$/, '');
}

/** Relative cost of an option against the recommended model, or null when there is nothing to say. */
export function costNote(optionCost: number | null, refCost: number | null | undefined): Msg | null {
  if (optionCost === null) return msg('agents.provider.ownAccount');
  if (!refCost || !optionCost || refCost <= 0 || optionCost <= 0) return null;
  const cheaper = refCost / optionCost;
  if (cheaper >= COST_NOTE_RATIO) return msg('agents.provider.cheaper', { times: formatTimes(cheaper) });
  const pricier = optionCost / refCost;
  if (pricier >= COST_NOTE_RATIO) return msg('agents.provider.pricier', { times: formatTimes(pricier) });
  return null;
}

/**
 * Which model one agent runs on (a Chat model capability; the stored value is its id).
 *
 * A17-UI splits this by authority, because there are now two different levers:
 *   - A PLATFORM admin edits the agent definition itself (`PUT /api/v1/agents/{id}/inference-provider`),
 *     which is the platform-wide choice.
 *   - An OWNER/ADMIN of a workspace cannot edit a built-in definition — it is a shared row — so for a
 *     built-in agent they get the workspace's own override instead
 *     (`PUT /api/v1/org/agent-overrides/{agentType}`). It takes precedence over the platform's
 *     built-in setting for every run in that workspace (see InferenceProviderResolver.ResolveAsync).
 *   - Everyone else, and every custom agent, sees the current choice read-only.
 *
 * Both levers read the member-facing selectable list for `Chat` — never the full provider tree —
 * and label each option `{model} · {provider}`. A platform admin is offered the platform-managed
 * models only, since the definition is shared by every workspace. The filter is an allowlist
 * ("capability === Chat"), never merely "not Transcription": a denylist silently admits every
 * future capability.
 */
export function AgentInferenceProviderSelect({
  agentId,
  inferenceProviderId,
  inferenceProviderName,
  onUpdated,
  agentType = null,
  isBuiltIn = false,
}: AgentInferenceProviderSelectProps) {
  const t = useT('agents.provider');
  const tAll = useT();
  const { isPlatformAdmin, isOrgAdmin, user } = useAuth();
  const role = user?.orgRole ?? null;
  const maySelect = !!role && role !== 'Billing';

  // A workspace override only exists for a built-in agent type the API accepts (Custom is excluded).
  const canUseOrgOverride =
    !isPlatformAdmin && isOrgAdmin && isBuiltIn && !!agentType && agentType !== 'Custom';

  const { data: selectable, isLoading } = useSelectableProviders(
    'Chat',
    isPlatformAdmin || canUseOrgOverride,
  );
  const { data: orgOverrides, mutate: mutateOrgOverrides } = useOrgAgentOverrides(canUseOrgOverride);

  const { data: agentModels, mutate: mutateAgentModels } = useOrgAgentModels(canUseOrgOverride);
  const recommendation = canUseOrgOverride
    ? agentModels?.find((m) => m.agentType === agentType) ?? null
    : null;
  const recommendedName = recommendation?.recommendedModelName ?? null;

  const [saving, setSaving] = useState(false);

  const orgOverrideId =
    (canUseOrgOverride ? orgOverrides?.find((o) => o.agentType === agentType)?.inferenceProviderId : null) ?? null;

  if (!isPlatformAdmin && !canUseOrgOverride) {
    return (
      <Group gap="xs">
        <Text size="sm" fw={500}>{t('model')}</Text>
        <Text size="sm" c="dimmed">
          {isBuiltIn && !maySelect
            ? t('roleCannot')
            : inferenceProviderName ?? t('defaultModel')}
        </Text>
      </Group>
    );
  }

  if (isLoading) {
    return (
      <Group gap="xs">
        <Text size="sm" fw={500}>{t('model')}</Text>
        <Loader size="xs" />
      </Group>
    );
  }

  // The selectable list holds enabled models only, so a disabled one is never a NEW choice; the
  // currently-assigned one stays visible below even when it is no longer selectable.
  const chatRows = (selectable ?? []).filter(
    (p) => p.capability === 'Chat' && (!isPlatformAdmin || p.isPlatformManaged),
  );
  const refCost = recommendation?.recommendedModelId
    ? (chatRows.find((p) => p.id === recommendation.recommendedModelId)?.costIndex ?? null)
    : null;
  // Cost notes are a workspace-picker aid; the platform admin's list stays as it was.
  const notes = new Map<string, string>();
  if (canUseOrgOverride) {
    for (const p of chatRows) {
      const note = costNote(p.costIndex ?? null, refCost);
      if (note) notes.set(p.id, t.msg(note));
    }
  }
  const data = [
    {
      value: DEFAULT_VALUE,
      label: canUseOrgOverride && recommendedName ? t('recommendedWith', { name: recommendedName }) : t('defaultModel'),
    },
    ...chatRows.map((p) => ({ value: p.id, label: selectableLabel(p) })),
  ];

  const currentId = isPlatformAdmin ? inferenceProviderId : orgOverrideId;
  // A stored id that is no longer selectable must stay visible rather than silently blanking.
  if (currentId && !data.some((o) => o.value === currentId)) {
    const known = isPlatformAdmin ? inferenceProviderName : null;
    data.push({ value: currentId, label: known ?? t('unknown') });
  }

  const handleChange = async (value: string | null) => {
    const nextId = !value || value === DEFAULT_VALUE ? null : value;
    setSaving(true);
    try {
      if (isPlatformAdmin) {
        await setAgentInferenceProvider(agentId, nextId);
      } else if (nextId) {
        await setOrgAgentOverride(agentType!, nextId);
      } else {
        await clearOrgAgentOverride(agentType!);
      }
      await Promise.all([mutateOrgOverrides(), mutateAgentModels()]);
      notifications.show({ title: t('updatedTitle'), message: t('updatedBody'), color: 'green' });
      onUpdated();
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tAll, err, t('failed')),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    setSaving(true);
    try {
      await clearOrgAgentOverride(agentType!);
      await Promise.all([mutateOrgOverrides(), mutateAgentModels()]);
      notifications.show({ title: t('updatedTitle'), message: t('resetBody'), color: 'green' });
      onUpdated();
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tAll, err, t('resetFailed')),
        color: 'red',
      });
    } finally {
      setSaving(false);
    }
  };

  return (
    <Stack gap={4}>
      {canUseOrgOverride && recommendedName && (
        <div>
          <Text size="sm">{t('recommended', { name: recommendedName })}</Text>
          {recommendation?.why && <Text size="xs" c="dimmed">{recommendation.why}</Text>}
        </div>
      )}
    <Select
      label={t('model')}
      description={
        isPlatformAdmin
          ? t('descGlobal')
          : t('descOrg', { current: inferenceProviderName ?? t('globalDefault') })
      }
      data={data}
      value={currentId ?? DEFAULT_VALUE}
      onChange={handleChange}
      disabled={saving}
      rightSection={saving ? <Loader size="xs" /> : undefined}
      allowDeselect={false}
      maw={360}
      renderOption={({ option }) => {
        const note = notes.get(option.value);
        return (
          <Group gap="xs" wrap="nowrap" justify="space-between" style={{ width: '100%' }}>
            <span>{option.label}</span>
            {note && (
              <Text span size="xs" c="dimmed">
                {note}
              </Text>
            )}
          </Group>
        );
      }}
    />
      {canUseOrgOverride && orgOverrideId && (
        <div>
          <Button variant="subtle" size="compact-sm" onClick={handleReset} disabled={saving}>
            {t('reset')}
          </Button>
        </div>
      )}
    </Stack>
  );
}
