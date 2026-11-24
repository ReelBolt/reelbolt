'use client';

import { useEffect, useMemo, useState } from 'react';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import {
  Accordion,
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  List,
  Loader,
  Select,
  SimpleGrid,
  Slider,
  Stack,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import { IconAlertTriangle, IconInfoCircle, IconWand } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { useDecisionCalibration } from '@/lib/hooks/use-decision-calibration';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import { ReliabilityChart } from '@/components/calibration/ReliabilityChart';
import { SweepChart } from '@/components/calibration/SweepChart';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import {
  VERDICT_COLOR,
  VERDICT_LABEL,
  summarizeAgents,
  type AgentSummary,
} from '@/lib/calibration-summary';
import type { DecisionCalibrationProvider } from '@/lib/types/decision-calibration';

/**
 * Read-only calibration view for the decision models. Reports on `decision_observations`; writes
 * nothing. Never fabricates (no data -> an explanation, not an empty chart) and never overstates
 * ("tokens saved" is a projection at the selected thresholds, not a measurement).
 */
const ALL_PROVIDERS = '__all_providers__';
const BREADCRUMBS = [{ label: 'Admin', href: '/admin' }, { label: 'Calibration' }];

const formatPercent = (v: number) => `${(v * 100).toFixed(1)}%`;
const formatCount = (v: number) => Math.round(v).toLocaleString('en-US');
const providerLabel = (p: DecisionCalibrationProvider) => (p.model ? p.model : p.providerId);

const TOKENS_SAVED_TOOLTIP =
  'A projection, not a measurement: Shadow still pays for the agent it shadows. This is what Gate would have skipped at the selected thresholds, counted only on rows where the agent run was recorded.';

/** The assistant is optional (absent outside the dashboard shell, e.g. in tests). */
function useAskAssistant(): ((prompt: string) => void) | null {
  try {
    return useAssistantShell().openWithPrompt;
  } catch {
    return null;
  }
}

function AskButton({ ask, prompt, label }: { ask: ((p: string) => void) | null; prompt: string; label: string }) {
  if (!ask) return null;
  return (
    <Button variant="light" color="violet" size="xs" leftSection={<IconWand size={14} />} onClick={() => ask(prompt)}>
      {label}
    </Button>
  );
}

export default function DecisionCalibrationPage() {
  const tAll = useT();
  const ask = useAskAssistant();
  const [providerId, setProviderId] = useState<string | undefined>(undefined);
  const [acceptAt, setAcceptAt] = useState(0.85);
  const [minMargin, setMinMargin] = useState(0.25);
  const [committed, setCommitted] = useState({ acceptAt: 0.85, minMargin: 0.25 });
  const [selectedKey, setSelectedKey] = useState<string | null>(null);

  const { data: report, isLoading, isValidating, error } = useDecisionCalibration({
    acceptAt: committed.acceptAt,
    minMargin: committed.minMargin,
    providerId,
  });

  useEffect(() => {
    if (!error) return;
    notifications.show({
      title: 'Error',
      message: describeError(tAll, error, 'Failed to load the decision calibration report'),
      color: 'red',
    });
  }, [error, tAll]);

  const agents = useMemo(() => summarizeAgents(report?.sites ?? [], committed.acceptAt), [report, committed.acceptAt]);
  const pairKey = (site: string, question: string) => `${site}·${question}`;
  const selected = useMemo(() => {
    const all = report?.sites ?? [];
    return all.find((s) => pairKey(s.site, s.question) === selectedKey) ?? all[0] ?? null;
  }, [report, selectedKey]);

  const providerOptions = [
    { value: ALL_PROVIDERS, label: 'All providers' },
    ...(report?.providers ?? []).map((p) => ({ value: p.providerId, label: providerLabel(p) })),
  ];

  const headerPrompt =
    'Look at my decision calibration data (use GetDecisionCalibration) and tell me, per agent, whether it is safe to move from Shadow to Gate, with what AcceptAt and MinMargin, and what evidence is still missing.';

  return (
    <>
      <PageHeader title="Decision Calibration" breadcrumbs={BREADCRUMBS}>
        <Group gap="xs">
          {isValidating && <Loader size="xs" />}
          <AskButton ask={ask} prompt={headerPrompt} label="Ask assistant for a recommendation" />
        </Group>
      </PageHeader>

      <Accordion variant="contained" radius="md" defaultValue="how" mb="md">
        <Accordion.Item value="how">
          <Accordion.Control icon={<IconInfoCircle size={18} />}>How this page works</Accordion.Control>
          <Accordion.Panel>
            <Stack gap="xs">
              <Text size="sm">
                Some workflow steps can ask a small, cheap <b>decision model</b> a question (for example
                &quot;which colour look fits?&quot;) instead of running a full agent. This page tells you whether
                that model is trustworthy enough to be allowed to skip the agent.
              </Text>
              <List size="sm" spacing={4}>
                <List.Item>
                  <b>Shadow</b> (what runs today): the agent still decides. The decision model answers too,
                  silently, and the pair is stored as an <b>observation</b>. Nothing changes for the user.
                </List.Item>
                <List.Item>
                  <b>Gate</b>: if the decision model is confident enough, its answer is used and the agent is
                  skipped, saving tokens. If it is not confident, the step <b>escalates</b> to the agent as usual.
                </List.Item>
                <List.Item>
                  <b>Agreement</b>: how often the decision model gave the same answer as the agent.{' '}
                  <b>Confidence</b>: how sure the model said it was. A good model agrees more often when it is
                  more confident.
                </List.Item>
                <List.Item>
                  <b>Accept-at</b> and <b>Minimum margin</b> (sliders below) define &quot;confident enough&quot;: top
                  probability at least Accept-at, and ahead of the runner-up by at least the margin. Raise them to
                  be safer (more escalations), lower them to save more tokens.
                </List.Item>
                <List.Item>
                  <b>Tokens saved</b> is a <b>projection</b> at the selected thresholds, not a measurement.
                </List.Item>
              </List>
              <Text size="sm" c="dimmed">
                Rule of thumb: consider Gate only when a site has at least 30 observations and, at the chosen
                threshold, the accepted decisions agree with the agent at least 95% of the time. The badges below
                apply exactly that rule. They are advice; nothing here changes any setting.
              </Text>
            </Stack>
          </Accordion.Panel>
        </Accordion.Item>
      </Accordion>

      <Card withBorder radius="md" padding="md" mb="md">
        <SimpleGrid cols={{ base: 1, md: 3 }} spacing="lg">
          <Stack gap={4}>
            <Text size="sm" fw={600}>
              Accept-at confidence: {committed.acceptAt === acceptAt ? acceptAt.toFixed(2) : `${acceptAt.toFixed(2)} (release to apply)`}
            </Text>
            <Slider
              aria-label="Accept-at confidence"
              thumbLabel="Accept-at confidence"
              min={0.5}
              max={1}
              step={0.05}
              value={acceptAt}
              onChange={setAcceptAt}
              onChangeEnd={(v) => setCommitted((c) => ({ ...c, acceptAt: v }))}
              marks={[{ value: 0.5, label: '0.5' }, { value: 0.75, label: '0.75' }, { value: 1, label: '1' }]}
              mb="md"
            />
            <Text size="xs" c="dimmed">The model must be at least this sure to be trusted without the agent.</Text>
          </Stack>
          <Stack gap={4}>
            <Text size="sm" fw={600}>
              Minimum margin: {committed.minMargin === minMargin ? minMargin.toFixed(2) : `${minMargin.toFixed(2)} (release to apply)`}
            </Text>
            <Slider
              aria-label="Minimum margin"
              thumbLabel="Minimum margin"
              min={0}
              max={1}
              step={0.05}
              value={minMargin}
              onChange={setMinMargin}
              onChangeEnd={(v) => setCommitted((c) => ({ ...c, minMargin: v }))}
              marks={[{ value: 0, label: '0' }, { value: 0.5, label: '0.5' }, { value: 1, label: '1' }]}
              mb="md"
            />
            <Text size="xs" c="dimmed">Its best answer must beat the runner-up by at least this much.</Text>
          </Stack>
          <Select
            label="Provider"
            description="Restrict every figure to one decision provider."
            data={providerOptions}
            value={providerId ?? ALL_PROVIDERS}
            onChange={(v) => setProviderId(v && v !== ALL_PROVIDERS ? v : undefined)}
            allowDeselect={false}
          />
        </SimpleGrid>
      </Card>

      {!report ? (
        <Center h={300}>{isLoading ? <Loader /> : <Text c="dimmed">Could not load the decision calibration report.</Text>}</Center>
      ) : report.totals.observationCount === 0 ? (
        <EmptyState
          title="No decision observations recorded"
          description="No decision observations have been recorded. A Decision provider must be configured and the decision gate set to Shadow mode before any can be collected. Until then there is nothing to report, and this page will not draw an empty chart."
        />
      ) : (
        <Stack gap="md">
          {report.sampleSizeWarning ? (
            <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title="Small sample">
              {report.sampleSizeWarning}
            </Alert>
          ) : null}

          <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md">
            <Stat label="Observations" value={formatCount(report.totals.observationCount)} hint="Shadow decisions recorded" />
            <Stat label="Agreement" value={formatPercent(report.totals.agreementRate)} hint="Model matched the agent" />
            <Stat
              label="Would escalate"
              value={formatPercent(report.totals.escalationRate)}
              hint={`At accept-at ${report.totals.acceptAt.toFixed(2)} / margin ${report.totals.minMargin.toFixed(2)}`}
            />
            <Stat
              label="Tokens saved"
              badge
              value={formatCount(report.totals.tokensSaved)}
              hint="Projected at these thresholds"
              tooltip={TOKENS_SAVED_TOOLTIP}
            />
          </SimpleGrid>

          <Title order={4}>Agents</Title>
          <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }} spacing="md">
            {agents.map((a) => (
              <AgentCard key={a.agent} agent={a} ask={ask} onInspect={(k) => setSelectedKey(k)} />
            ))}
          </SimpleGrid>

          {selected ? (
            <Card withBorder radius="md" padding="md">
              <Group justify="space-between" align="flex-start" mb="sm" wrap="wrap">
                <Stack gap={2}>
                  <Title order={4}>
                    {selected.site} <Text span c="dimmed" fw={400}>· {selected.question}</Text>
                  </Title>
                  <Text size="sm" c="dimmed">
                    {formatCount(selected.observationCount)} observations. {selected.recommendation.reason}
                  </Text>
                </Stack>
                <Select
                  aria-label="Question to chart"
                  data={(report.sites ?? []).map((s) => ({ value: pairKey(s.site, s.question), label: `${s.site} (${s.observationCount})` }))}
                  value={pairKey(selected.site, selected.question)}
                  onChange={setSelectedKey}
                  allowDeselect={false}
                  w={260}
                />
              </Group>
              <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="xl">
                <Stack gap={4}>
                  <Text fw={600} size="sm">Is the model honest about its confidence?</Text>
                  <Text size="xs" c="dimmed">Dots on the dashed line mean &quot;80% sure&quot; really is right 80% of the time.</Text>
                  <ReliabilityChart buckets={selected.reliabilityBuckets} />
                </Stack>
                <Stack gap={4}>
                  <Text fw={600} size="sm">What would a stricter or looser Accept-at do?</Text>
                  <Text size="xs" c="dimmed">Move right for safety (fewer decisions handled), left to save more.</Text>
                  <SweepChart sweep={selected.thresholdSweep} acceptAt={report.totals.acceptAt} />
                </Stack>
              </SimpleGrid>
            </Card>
          ) : null}

          {report.providers.length > 0 ? (
            <Accordion variant="contained" radius="md">
              <Accordion.Item value="providers">
                <Accordion.Control>Per provider</Accordion.Control>
                <Accordion.Panel>
                  <Table withTableBorder>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>Provider</Table.Th>
                        <Table.Th>Observations</Table.Th>
                        <Table.Th>Agreement</Table.Th>
                        <Table.Th>Mean confidence</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {report.providers.map((p) => (
                        <Table.Tr key={p.providerId}>
                          <Table.Td>{providerLabel(p)}</Table.Td>
                          <Table.Td>{formatCount(p.observationCount)}</Table.Td>
                          <Table.Td>{formatPercent(p.agreementRate)}</Table.Td>
                          <Table.Td>{formatPercent(p.meanConfidence)}</Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                  {report.providers.some((p) => p.sampleSizeWarning) ? (
                    <Stack gap={4} mt="sm">
                      {report.providers.filter((p) => p.sampleSizeWarning).map((p) => (
                        <Text size="xs" c="dimmed" key={p.providerId}>{p.sampleSizeWarning}</Text>
                      ))}
                    </Stack>
                  ) : null}
                </Accordion.Panel>
              </Accordion.Item>
            </Accordion>
          ) : null}
        </Stack>
      )}
    </>
  );
}

function Stat(props: { label: string; value: string; hint: string; badge?: boolean; tooltip?: string }) {
  return (
    <Card withBorder padding="md" radius="md">
      <Group gap={6} wrap="nowrap">
        <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{props.label}</Text>
        {props.badge ? <Badge size="xs" variant="light" color="violet">PROJECTED</Badge> : null}
        {props.tooltip ? (
          <Tooltip label={props.tooltip} multiline w={300} withArrow>
            <IconInfoCircle size={14} aria-label="About the tokens-saved projection" />
          </Tooltip>
        ) : null}
      </Group>
      <Text size="xl" fw={700}>{props.value}</Text>
      <Text size="xs" c="dimmed">{props.hint}</Text>
    </Card>
  );
}

function AgentCard({ agent, ask, onInspect }: { agent: AgentSummary; ask: ((p: string) => void) | null; onInspect: (key: string) => void }) {
  const prompt = `Should I enable Gate for ${agent.agent}? Use GetDecisionCalibration and explain the evidence, the AcceptAt/MinMargin you would pick, and the risk.`;
  return (
    <Card withBorder padding="md" radius="md">
      <Group justify="space-between" mb={4} wrap="nowrap">
        <Text fw={700}>{agent.agent}</Text>
        <Badge color={VERDICT_COLOR[agent.verdict]} variant="light">
          {VERDICT_LABEL[agent.verdict]}
          {agent.verdict === 'TryGate' && agent.suggestedAcceptAt ? ` at ${agent.suggestedAcceptAt.toFixed(2)}` : ''}
        </Badge>
      </Group>
      <Text size="xs" c="dimmed" mb="sm">{agent.reason}</Text>
      <SimpleGrid cols={2} spacing="xs" mb="sm">
        <MiniStat label="Observations" value={formatCount(agent.observationCount)} />
        <MiniStat label="Agreement" value={formatPercent(agent.agreementRate)} />
        <MiniStat label="Would handle alone" value={formatPercent(agent.acceptRate)} />
        <MiniStat label="Tokens saved (proj.)" value={formatCount(agent.tokensSaved)} />
      </SimpleGrid>
      <Group gap="xs" wrap="wrap">
        {agent.pairs.map((p) => (
          <Button key={p.question} variant="default" size="xs" onClick={() => onInspect(`${p.site}·${p.question}`)}>
            Chart {p.site.includes('.') ? p.site.split('.').slice(1).join('.') : p.question}
          </Button>
        ))}
        <AskButton ask={ask} prompt={prompt} label="Ask assistant" />
      </Group>
    </Card>
  );
}

function MiniStat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <Text size="xs" c="dimmed">{label}</Text>
      <Text fw={600}>{value}</Text>
    </div>
  );
}
