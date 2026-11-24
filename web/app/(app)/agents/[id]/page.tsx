'use client';

import { use, useState } from 'react';
import { Card, Text, Stack, Group, Button, Code, Loader, Center, Badge, SimpleGrid } from '@mantine/core';
import { IconEdit, IconTrash, IconTools, IconFileExport, IconChevronDown, IconChevronUp } from '@tabler/icons-react';
import { agentDisplayDescription, agentDisplayName } from '@/lib/utils/agent-labels';
import { useAgent } from '@/lib/hooks/use-agents';
import { deleteAgent } from '@/lib/api/agents';
import { ApiError } from '@/lib/api/client';
import { PageHeader } from '@/components/shared/PageHeader';
import { AgentTypeBadge } from '@/components/agents/AgentTypeBadge';
import { AgentSchemaViewer } from '@/components/agents/AgentSchemaViewer';
import { AgentForm } from '@/components/agents/AgentForm';
import { AgentInferenceProviderSelect } from '@/components/agents/AgentInferenceProviderSelect';
import { AgentReasoningEffortSelect } from '@/components/agents/AgentReasoningEffortSelect';
import { AgentSkillsSelect } from '@/components/agents/AgentSkillsSelect';
import { useSkills } from '@/lib/hooks/use-skills';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { notifications } from '@mantine/notifications';
import { useRouter } from 'next/navigation';
import { useT } from '@/i18n';

export default function AgentDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const t = useT('agents.detail');
  const { data: agent, isLoading, mutate } = useAgent(id);
  const { data: skillsData } = useSkills();
  const router = useRouter();
  const [editOpened, setEditOpened] = useState(false);
  const [deleteOpened, setDeleteOpened] = useState(false);
  const [deleteLoading, setDeleteLoading] = useState(false);
  const [technicalOpen, setTechnicalOpen] = useState(false);

  if (isLoading) return <Center h={300}><Loader /></Center>;
  if (!agent) return <Text>{t('notFound')}</Text>;
  const displayName = agentDisplayName(agent);
  const displayDescription = agentDisplayDescription(agent);

  const handleDelete = async () => {
    setDeleteLoading(true);
    try {
      await deleteAgent(id);
      notifications.show({ title: t('deletedTitle'), message: t('deletedBody'), color: 'green' });
      router.push('/agents');
    } catch (err) {
      // 409 AGENT_IN_USE_BY_RUNS carries a plain message saying which past runs still use the agent.
      const message = err instanceof ApiError && err.code === 'AGENT_IN_USE_BY_RUNS' && err.message
        ? err.message
        : t('deleteFailed');
      notifications.show({ title: t('errorTitle'), message, color: 'red' });
    } finally {
      setDeleteLoading(false);
    }
  };

  return (
    <>
      <PageHeader
        title={displayName}
        breadcrumbs={[{ label: t('crumb'), href: '/agents' }, { label: displayName }]}
      >
        {!agent.isBuiltIn && (
          <>
            <Button variant="default" leftSection={<IconEdit size={16} />} onClick={() => setEditOpened(true)}>
              {t('edit')}
            </Button>
            <Button color="red" variant="outline" leftSection={<IconTrash size={16} />} onClick={() => setDeleteOpened(true)}>
              {t('delete')}
            </Button>
          </>
        )}
      </PageHeader>

      <Stack gap="md">
        {displayDescription && <Text>{displayDescription}</Text>}
        {agent.isBuiltIn && <Text size="xs" c="dimmed">{t('builtIn')}</Text>}

        <Card withBorder>
          <AgentInferenceProviderSelect
            agentId={agent.id}
            inferenceProviderId={agent.inferenceProviderId}
            inferenceProviderName={agent.inferenceProviderName}
            onUpdated={() => mutate()}
            agentType={agent.agentType}
            isBuiltIn={agent.isBuiltIn}
          />
        </Card>

        <Card withBorder>
          <AgentReasoningEffortSelect
            agentId={agent.id}
            reasoningEffort={agent.reasoningEffort ?? null}
            isBuiltIn={agent.isBuiltIn}
            onUpdated={() => mutate()}
          />
        </Card>

        <Card withBorder>
          {agent.isBuiltIn ? (
            <Stack gap="xs">
              <Text fw={500} size="sm">{t('skills')}</Text>
              {agent.effectiveSkills.length > 0 ? (
                <Group gap="xs">
                  {agent.effectiveSkills.map((skillName) => {
                    const skill = skillsData?.find((s) => s.name === skillName);
                    return (
                      <Badge key={skillName} color="green" variant="light" size="sm">
                        {skill?.displayName ?? skillName}
                      </Badge>
                    );
                  })}
                </Group>
              ) : (
                <Text size="sm" c="dimmed">{t('noSkills')}</Text>
              )}
              <Text size="xs" c="dimmed">{t('skillsFixed')}</Text>
            </Stack>
          ) : (
            <AgentSkillsSelect
              agentId={agent.id}
              assignedSkills={agent.assignedSkills}
              onUpdated={() => mutate()}
            />
          )}
        </Card>

        <Button
          variant="subtle"
          color="gray"
          size="compact-sm"
          style={{ alignSelf: 'flex-start' }}
          onClick={() => setTechnicalOpen((o) => !o)}
          rightSection={technicalOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
          aria-expanded={technicalOpen}
        >
          {technicalOpen ? t('hideTech') : t('tech')}
        </Button>

        {technicalOpen && (
        <Stack gap="md" data-testid="agent-technical">
        <Group>
          <Text size="xs" c="dimmed">{t('internalName', { name: agent.name })}</Text>
          <AgentTypeBadge agentType={agent.agentType} color={agent.color} />
          {agent.generatesOutput && (
            <Badge color="green" variant="outline" size="sm" leftSection={<IconFileExport size={10} />}>
              {agent.outputSchemaName ?? t('output')}
            </Badge>
          )}
        </Group>
        {agent.isBuiltIn && agent.description && agent.description !== displayDescription && (
          <Text size="sm" c="dimmed">{agent.description}</Text>
        )}

        {agent.availableTools && agent.availableTools.length > 0 && (
          <Card withBorder>
            <Text size="sm" fw={600} mb="xs">
              <Group gap="xs" display="inline-flex">
                <IconTools size={14} />
                {t('tools', { count: agent.availableTools.length })}
              </Group>
            </Text>
            <SimpleGrid cols={{ base: 2, sm: 3, md: 4 }} spacing="xs">
              {agent.availableTools.map((tool) => (
                <Badge key={tool} color="blue" variant="light" size="sm" fullWidth style={{ justifyContent: 'flex-start' }}>
                  {tool}
                </Badge>
              ))}
            </SimpleGrid>
          </Card>
        )}

        <Card withBorder>
          <Text size="sm" fw={600} mb="xs">{t('prompt')}</Text>
          <Code block style={{ whiteSpace: 'pre-wrap' }}>{agent.systemPrompt}</Code>
        </Card>

        {agent.outputSchemaJson && (
          <Card withBorder>
            <Text size="sm" fw={600} mb="xs">{t('schemaTitle')}</Text>
            <Text size="xs" c="dimmed" mb="sm">
              {t('schemaBody')}
            </Text>
            <AgentSchemaViewer schemaJson={agent.outputSchemaJson} />
          </Card>
        )}
        </Stack>
        )}
      </Stack>

      {!agent.isBuiltIn && (
        <>
          <AgentForm opened={editOpened} onClose={() => setEditOpened(false)} onSuccess={() => mutate()} agent={agent} />
          <ConfirmModal
            opened={deleteOpened}
            onClose={() => setDeleteOpened(false)}
            onConfirm={handleDelete}
            title={t('deleteTitle')}
            message={t('deleteBody')}
            loading={deleteLoading}
          />
        </>
      )}
    </>
  );
}
