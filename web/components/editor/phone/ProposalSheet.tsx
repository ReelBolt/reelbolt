'use client';

import React, { useState } from 'react';
import { Alert, Box, Button, Checkbox, Group, Select, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconWand } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { PROPOSAL_AGENTS, type EditProposalRequest, type EditProposalResponse, type ProposalAgent, type ProposalEstimate } from '@/lib/editing/sync/api';
import { userOwnedTouchedBy, type ProposalGhost } from '@/lib/editing/proposals/apply-proposal';
import { framesToTimecode } from '@/lib/editing/time';
import { proposalCapReached } from '@/lib/editing/proposals/assist-text';
import { ProposalBusy } from '../panels/ProposalBusy';
import { AGENT_LABELS, useProposalFlow, type ProposalFlowOptions } from '../panels/useProposalFlow';
import { showUndoSnackbar } from './UndoSnackbar';
import { HelperAvailability } from '../panels/HelperAvailability';
import { AnalyseFirst } from '../panels/AnalyseFirst';
import { useT } from '@/i18n';
import { msg } from '@/i18n/msg';

export interface ProposalSheetProps {
  doc: EditTimeline;
  projectId: string;
  timelineId: string;
  readOnly: boolean;
  inOut: { inFrame: number | null; outFrame: number | null };
  onGhosts: (ghosts: { ghosts: ProposalGhost[]; newTracks: { id: string; name: string; kind: 'video' | 'audio' }[] } | null) => void;
  /** Applies the accepted program as ONE batch (one undo step). */
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSeek: (frame: number) => void;
  onUndo: () => void;
  request?: (projectId: string, timelineId: string, body: EditProposalRequest) => Promise<EditProposalResponse>;
  estimate?: (projectId: string, timelineId: string, body: EditProposalRequest) => Promise<ProposalEstimate>;
  availability?: ProposalFlowOptions['availability'];
  /** The helper selected on mount. */
  initialAgent?: ProposalFlowOptions['initialAgent'];
}

/**
 * RM.13: the phone review sheet for AI suggestions. The price is shown before asking; each suggested
 * change is a 44 px row that moves the playhead and highlights only its own ghost in the strip; the
 * footer keeps Accept and Reject in reach; a change to a clip you made needs its tick inside the group.
 */
export function ProposalSheet({ doc, projectId, timelineId, readOnly, inOut, onGhosts, onApply, onSeek, onUndo, request, estimate, availability, initialAgent }: ProposalSheetProps) {
  const t = useT('editor.phone.proposalSheet');
  const [focus, setFocus] = useState<number | null>(null);
  const flow = useProposalFlow({ doc, projectId, timelineId, readOnly, inOut, onGhosts, onApply, request, estimate, availability, focusGroup: focus, initialAgent });
  const { proposal, selected, setSelected, acknowledged, setAcknowledged, items, diff, quote, error } = flow;
  const capped = proposalCapReached(quote);

  const pending = proposal ? [...selected].flatMap((i) => (proposal.groups[i] ? userOwnedTouchedBy(doc, proposal.groups[i]) : [])).filter((id) => !acknowledged.has(id)) : [];
  const canAccept = !readOnly && selected.size > 0 && pending.length === 0;

  const accept = async () => {
    const count = selected.size;
    const ok = await flow.accept(selected);
    if (ok) {
      setFocus(null);
      showUndoSnackbar({ message: t.msg(msg('editor-helpers-2.fmt.changesApplied', { count })), onUndo });
    }
  };
  const reject = () => {
    flow.setProposal(null);
    setFocus(null);
  };

  return (
    <Stack gap="sm" data-testid="proposal-sheet">
      {!proposal && (
        <>
          <Text size="sm">{t('pickAnAssistantAnd')}</Text>
          <Select
            size="md"
            label={t('assistant')}
            data={PROPOSAL_AGENTS.map((a) => ({ value: a, label: t.msg(AGENT_LABELS[a].label), disabled: flow.unavailable.some((u) => u.agent === a) }))}
            value={flow.agent}
            allowDeselect={false}
            onChange={(v) => v && flow.setAgent(v as ProposalAgent)}
            comboboxProps={{ withinPortal: true }}
            data-testid="proposal-agent"
          />
          <Text size="xs" c="dimmed">{t.msg(AGENT_LABELS[flow.agent].hint)}</Text>
          <HelperAvailability unavailable={flow.unavailable} grounding={flow.grounding} size="sm" />
          {flow.priceLine && <Text size="sm" data-testid="proposal-estimate">{flow.priceLine}</Text>}
          {flow.allowanceLine && <Text size="sm" c={capped ? 'red' : 'dimmed'} data-testid="proposal-allowance">{flow.allowanceLine}</Text>}
          {flow.unaffordableLine && !capped && <Text size="sm" c="red" data-testid="proposal-unaffordable">{flow.unaffordableLine}</Text>}
          <Button size="md" mih={44} leftSection={<IconWand size={16} />} loading={flow.loading} disabled={readOnly || !!flow.blockedLine} onClick={() => void flow.ask()} data-testid="proposal-ask">
            {t('askForSuggestions')}
          </Button>
          <ProposalBusy projectId={projectId} />
        </>
      )}
      {error && <Alert color="red" variant="light" data-testid="proposal-error">{error}</Alert>}
      {flow.analysisNeed && (
        <Stack gap={4} data-testid="proposal-needs-analysis">
          <Text size="sm" fw={600}>{flow.waitingForAnalysis ? t('findingWhatIsSaid') : t('cantSeeWhatIsSaid')}</Text>
          {flow.analysisFailed && <Text size="sm" c="red" role="alert">{t('analysisFailedTryAgain')}</Text>}
          {flow.analysisUnreadable && (
            <>
              <Text size="sm" c="red" role="alert">{t('couldNotCheckTheWords')}</Text>
              <Button size="sm" fz="sm" variant="light" style={{ minHeight: 44, minWidth: 44 }} onClick={flow.retryAnalysisCheck}>{t('tryAgain')}</Button>
            </>
          )}
          {!flow.waitingForAnalysis && <AnalyseFirst doc={doc} projectId={projectId} range={flow.range} readOnly={readOnly} tapStyle={{ minHeight: 44, minWidth: 44 }} onStarted={flow.onAnalysisStarted} kinds={flow.analysisNeed.kinds} mediaRefs={flow.analysisNeed.mediaRefs} />}
        </Stack>
      )}
      {proposal && (
        <>
          <Text size="sm" fw={600}>{proposal.summary}</Text>
          {flow.didLine && <Text size="sm" data-testid="proposal-did">{flow.didLine}</Text>}
          {flow.swapsLine && <Text size="xs" c="dimmed" data-testid="proposal-swaps-refused">{flow.swapsLine}</Text>}
          <HelperAvailability unavailable={[]} grounding={flow.grounding} size="sm" />
          {proposal.disclosure?.summary && <Text size="xs" c="dimmed" data-testid="proposal-sent">{proposal.disclosure.summary}</Text>}
          {proposal.groups.length === 0 && <Text size="sm" c="dimmed">{t('nothingToChange')}</Text>}
          {proposal.groups.map((group, index) => {
            const owned = userOwnedTouchedBy(doc, group);
            const unavailable = diff?.failed.includes(index) ?? false;
            const anchor = group.touchedItemIds.map((id) => items.get(id)).find((i) => i !== undefined);
            const focused = focus === index;
            return (
              <Box
                key={index}
                p={4}
                style={{ border: `1px solid ${focused ? 'var(--mantine-primary-color-filled)' : 'var(--mantine-color-default-border)'}`, borderRadius: 6, opacity: unavailable ? 0.6 : 1 }}
                data-testid={`proposal-group-${index}`}
                data-focused={focused ? 'true' : undefined}
              >
                <Group gap={4} wrap="nowrap" align="center">
                  <Box style={{ width: 44, height: 44, display: 'flex', alignItems: 'center', justifyContent: 'center', flex: '0 0 44px' }}>
                    <Checkbox
                      aria-label={t('include', { label: group.label })}
                      checked={selected.has(index)}
                      disabled={readOnly || unavailable}
                      onChange={(e) => {
                        const next = new Set(selected);
                        if (e.currentTarget.checked) next.add(index);
                        else next.delete(index);
                        setSelected(next);
                      }}
                      data-testid={`proposal-group-check-${index}`}
                    />
                  </Box>
                  <UnstyledButton
                    onClick={() => {
                      setFocus(focused ? null : index);
                      if (!focused && anchor) onSeek(anchor.recordIn);
                    }}
                    style={{ flex: 1, minHeight: 44, minWidth: 0, display: 'flex', flexDirection: 'column', justifyContent: 'center' }}
                    aria-pressed={focused}
                    data-testid={`proposal-group-focus-${index}`}
                  >
                    <Text size="sm" fw={500} lineClamp={2}>{group.label}</Text>
                    {anchor && <Text size="xs" c="dimmed" ff="monospace">{framesToTimecode(anchor.recordIn, doc.settings.rate)}</Text>}
                  </UnstyledButton>
                </Group>
                {focused && group.reason && <Text size="xs" c="dimmed" px={8} pb={4}>{group.reason}</Text>}
                {unavailable && <Text size="xs" c="dimmed" px={8} pb={4}>{t('noLongerFitsThe')}</Text>}
                {owned.map((id) => (
                  <Box key={id} px={8} py={4} style={{ minHeight: 44, display: 'flex', alignItems: 'center' }}>
                    <Checkbox
                      color="orange"
                      label={t.msg(msg('editor-helpers-2.fmt.allowChanging', { name: items.get(id)?.name ?? t('clipFallback') }))}
                      checked={acknowledged.has(id)}
                      disabled={readOnly}
                      onChange={(e) => {
                        const next = new Set(acknowledged);
                        if (e.currentTarget.checked) next.add(id);
                        else next.delete(id);
                        setAcknowledged(next);
                      }}
                      data-testid={`proposal-ack-${id}`}
                    />
                  </Box>
                ))}
              </Box>
            );
          })}
          {proposal.rejected.length > 0 && (
            <Text size="xs" c="dimmed">{t('rejectedCount', { count: proposal.rejected.length })}</Text>
          )}
          <Group
            gap={8}
            wrap="nowrap"
            pt="xs"
            pb="xs"
            style={{ position: 'sticky', bottom: 0, background: 'var(--mantine-color-body)', zIndex: 1 }}
            data-testid="proposal-footer"
          >
            <Button flex={1} size="md" mih={44} disabled={!canAccept} onClick={() => void accept()} data-testid="proposal-accept-selected">
              {t('accept')}{selected.size > 0 ? ` (${selected.size})` : ''}
            </Button>
            <Button flex={1} size="md" mih={44} variant="default" onClick={reject} data-testid="proposal-reject">
              {t('reject')}
            </Button>
          </Group>
          {pending.length > 0 && selected.size > 0 && <Text size="xs" c="dimmed" data-testid="proposal-ack-needed">{t('tickTheBoxUnder')}</Text>}
        </>
      )}
    </Stack>
  );
}
