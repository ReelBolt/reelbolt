'use client';

import { ProposalBusy } from './ProposalBusy';
import React from 'react';
import { Alert, Badge, Button, Checkbox, Group, Select, Stack, Text, Textarea, Tooltip } from '@mantine/core';
import { IconWand, IconUserExclamation } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline, Item } from '@/lib/editing/types';
import { PROPOSAL_AGENTS, estimateEditProposal, requestEditProposal, type ProposalEstimate, type EditProposalRequest, type EditProposalResponse, type ProposalAgent } from '@/lib/editing/sync/api';
import { userOwnedTouchedBy, type ProposalGhost } from '@/lib/editing/proposals/apply-proposal';
import { framesToTimecode } from '@/lib/editing/time';
import { useCoarsePointer } from '@/components/editor/thumbnail/useCoarsePointer';
import { useProposalFlow, AGENT_LABELS, type ProposalFlowOptions } from './useProposalFlow';
import { HelperAvailability } from './HelperAvailability';
import { AnalyseFirst } from './AnalyseFirst';
import { proposalCapReached } from '@/lib/editing/proposals/assist-text';
import { useT } from '@/i18n';
import { msg } from '@/i18n/msg';

export interface ProposalPanelProps {
  doc: EditTimeline;
  projectId: string;
  timelineId: string;
  readOnly: boolean;
  inOut: { inFrame: number | null; outFrame: number | null };
  /** Shows (or clears with null) the ghost diff on the timeline. */
  onGhosts: (ghosts: { ghosts: ProposalGhost[]; newTracks: { id: string; name: string; kind: 'video' | 'audio' }[] } | null) => void;
  /** Applies the accepted program as ONE batch (one undo step). */
  onApply: (ops: readonly EditOp[], label: string) => void;
  onSeek: (frame: number) => void;
  /** Injectable for tests. */
  request?: (projectId: string, timelineId: string, body: EditProposalRequest) => Promise<EditProposalResponse>;
  estimate?: (projectId: string, timelineId: string, body: EditProposalRequest) => Promise<ProposalEstimate>;
  availability?: ProposalFlowOptions['availability'];
  /** The helper selected on mount. */
  initialAgent?: ProposalAgent;
  /** The brief the panel opens with. */
  initialBrief?: string;
}

/**
 * AI assist (P8): ask one of five agents for a proposal over the in/out range (or the whole
 * timeline), review it as a GHOST DIFF on the timeline — additions outlined, removals hatched,
 * changes showing old and new spans — and accept all, some or none of it.
 *
 * Provenance is recomputed from the current document: a group that would change a clip you made
 * yourself is marked, and accepting it requires ticking the acknowledgement for that clip after the
 * ghost diff has shown it. Accepting runs the chosen groups as ONE atomic batch; a group that no
 * longer fits the timeline refuses the whole accept and is named.
 */
export function ProposalPanel({ doc, projectId, timelineId, readOnly, inOut, onGhosts, onApply, onSeek, request = requestEditProposal, estimate = estimateEditProposal, availability, initialAgent, initialBrief }: ProposalPanelProps) {
  const t = useT('editor.panels.proposalPanel');
  const coarse = useCoarsePointer();
  const tap = coarse ? { minHeight: 44, minWidth: 44 } : undefined;
  const { agent, setAgent, brief, setBrief, loading, proposal, setProposal, selected, setSelected, acknowledged, setAcknowledged, error, showGhosts, setShowGhosts, items, diff, range, quote, unaffordableLine, priceLine, allowanceLine, blockedLine, skip, ask, accept, unavailable, agentReason, analysisAvailable, grounding, refreshAvailability, didLine, swapsLine, analysisNeed, waitingForAnalysis, analysisFailed, analysisUnreadable, retryAnalysisCheck, onAnalysisStarted } = useProposalFlow({ doc, projectId, timelineId, readOnly, inOut, onGhosts, onApply, request, estimate, availability, initialAgent, initialBrief });

  return (
    <Stack gap="xs" data-testid="proposal-panel">
      <Text size="12px" c="dimmed">
        {t('pickAHelperSay')}
      </Text>
      <Group gap={4} align="end">
        <Select size="xs" label={t('helper')} data={PROPOSAL_AGENTS.map((a) => ({ value: a, label: t.msg(AGENT_LABELS[a].label), disabled: unavailable.some((u) => u.agent === a) }))} value={agent} allowDeselect={false} onChange={(v) => v && setAgent(v as ProposalAgent)} w={140} data-testid="proposal-agent" />
        <Button size="compact-sm" fz="xs" style={tap} leftSection={<IconWand size={14} />} loading={loading} disabled={readOnly || !!blockedLine} onClick={() => void ask()} data-testid="proposal-ask">
          {t('askForSuggestions')}
        </Button>
      </Group>
      <ProposalBusy projectId={projectId} />
      <Text size="12px" c="dimmed" data-testid="proposal-hint">
        {t('rangeLine', { hint: t.msg(AGENT_LABELS[agent].hint), range: range ? t('rangeBetween', { from: framesToTimecode(range.inFrame, doc.settings.rate), to: framesToTimecode(range.outFrame, doc.settings.rate) }) : t('theWholeTimelineMark') })}
      </Text>
      <HelperAvailability unavailable={unavailable} grounding={grounding} />
      {analysisNeed ? (
        <Stack gap={4} data-testid="proposal-needs-analysis">
          <Text size="12px" fw={600}>{waitingForAnalysis ? t('findingWhatIsSaid') : t('cantSeeWhatIsSaid')}</Text>
          {analysisFailed && <Text size="12px" c="red" role="alert">{t('analysisFailedTryAgain')}</Text>}
          {analysisUnreadable && (
            <>
              <Text size="12px" c="red" role="alert">{t('couldNotCheckTheWords')}</Text>
              <Button size="compact-sm" fz="xs" variant="light" style={tap} onClick={retryAnalysisCheck}>{t('tryAgain')}</Button>
            </>
          )}
          {!waitingForAnalysis && <AnalyseFirst doc={doc} projectId={projectId} range={range} readOnly={readOnly} tapStyle={tap} onStarted={onAnalysisStarted} kinds={analysisNeed.kinds} mediaRefs={analysisNeed.mediaRefs} />}
        </Stack>
      ) : (
        (analysisAvailable || agentReason !== null) && <AnalyseFirst doc={doc} projectId={projectId} range={range} readOnly={readOnly} tapStyle={tap} onStarted={refreshAvailability} />
      )}
      {priceLine && <Text size="12px" data-testid="proposal-estimate">{priceLine}</Text>}
      {allowanceLine && <Text size="12px" c={proposalCapReached(quote) ? 'red' : 'dimmed'} data-testid="proposal-allowance">{allowanceLine}</Text>}
      {unaffordableLine && !proposalCapReached(quote) && <Text size="12px" c="red" data-testid="proposal-unaffordable">{unaffordableLine}</Text>}
      {quote?.disclosure?.summary && <Text size="12px" c="dimmed" data-testid="proposal-disclosure">{quote.disclosure.summary}{quote.modelLabel ? t('model', { modelLabel: quote.modelLabel }) : ''}</Text>}
      <Textarea size="xs" aria-label={t('brief')} placeholder={t('optionalBriefEG')} value={brief} onChange={(e) => setBrief(e.currentTarget.value)} minRows={1} maxLength={1000} data-testid="proposal-brief" />
      {error && (
        <Alert color="red" variant="light" data-testid="proposal-error">
          {error}
        </Alert>
      )}
      {proposal && (
        <Stack gap={6}>
          <Group justify="space-between">
            <Text size="xs" fw={600}>
              {proposal.summary}
            </Text>
            <Checkbox size="xs" label={t('ghostDiff')} checked={showGhosts} onChange={(e) => setShowGhosts(e.currentTarget.checked)} data-testid="proposal-ghost-toggle" />
          </Group>
          {didLine && <Text size="12px" data-testid="proposal-did">{didLine}</Text>}
          {swapsLine && <Text size="12px" c="dimmed" data-testid="proposal-swaps-refused">{swapsLine}</Text>}
          {proposal.disclosure?.summary && (
            <Text size="12px" c="dimmed" data-testid="proposal-sent">{proposal.disclosure.summary}</Text>
          )}
          {proposal.groups.length === 0 && (
            <Text size="xs" c="dimmed">
              {t('nothingToChange')}
            </Text>
          )}
          {proposal.groups.map((group, index) => {
            const owned = userOwnedTouchedBy(doc, group);
            const unavailable = diff?.failed.includes(index) ?? false;
            const anchor = group.touchedItemIds.map((id) => items.get(id)).find((i): i is Item => i !== undefined);
            return (
              <Stack key={index} gap={2} p={6} style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 4, opacity: unavailable ? 0.6 : 1 }} data-testid={`proposal-group-${index}`}>
                <Group justify="space-between" wrap="nowrap">
                  <Checkbox
                    size="xs"
                    label={group.label}
                    checked={selected.has(index)}
                    disabled={readOnly}
                    onChange={(e) => {
                      const next = new Set(selected);
                      if (e.currentTarget.checked) next.add(index);
                      else next.delete(index);
                      setSelected(next);
                    }}
                    data-testid={`proposal-group-check-${index}`}
                  />
                  <Button size="compact-xs" fz="xs" variant="light" disabled={readOnly || unavailable} onClick={() => void accept(new Set([index]))} style={tap} data-testid={`proposal-accept-${index}`}>
                    {t('accept')}
                  </Button>
                  <Button size="compact-xs" fz="xs" variant="subtle" color="gray" onClick={() => skip(index)} style={tap} data-testid={`proposal-skip-${index}`}>
                    {t('skip')}
                  </Button>
                  {anchor && (
                    <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => onSeek(anchor.recordIn)}>
                      {framesToTimecode(anchor.recordIn, doc.settings.rate)}
                    </Button>
                  )}
                </Group>
                {group.reason && (
                  <Text size="12px" c="dimmed">
                    {group.reason}
                  </Text>
                )}
                {unavailable && (
                  <Badge size="xs" fz="xs" color="gray">{t('noLongerFitsThe')}</Badge>
                )}
                {owned.length > 0 && (
                  <Badge size="xs" fz="xs" color="orange" leftSection={<IconUserExclamation size={10} />} data-testid={`proposal-user-owned-${index}`}>
                    {t('changesClips', { count: owned.length })}
                  </Badge>
                )}
                {owned.map((id) => (
                  <Tooltip key={id} label={t('youMadeOrEdited')}>
                    <Checkbox
                      size="xs"
                      color="orange"
                      label={t.msg(msg('editor-helpers-2.fmt.allowChanging', { name: items.get(id)?.name ?? id }))}
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
                  </Tooltip>
                ))}
              </Stack>
            );
          })}
          {proposal.rejected.length > 0 && (
            <Alert color="gray" variant="light" title={t('otherCouldNotBe', { length: proposal.rejected.length })}>
              {proposal.rejected.slice(0, 5).map((r) => (
                <Text key={r} size="12px">
                  {r}
                </Text>
              ))}
            </Alert>
          )}
          <Group gap={4}>
            <Button size="compact-xs" fz="xs" disabled={readOnly || selected.size === 0} onClick={() => void accept(selected)} style={tap} data-testid="proposal-accept-selected">
              {t('acceptCheckedChanges', { size: selected.size })}
            </Button>
            <Button size="compact-xs" fz="xs" variant="subtle" color="red" onClick={() => setProposal(null)} style={tap} data-testid="proposal-reject">
              {t('skipAll')}
            </Button>
          </Group>
        </Stack>
      )}
    </Stack>
  );
}
