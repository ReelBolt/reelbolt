'use client';
/* eslint-disable i18next/no-literal-string -- the remaining literals are ids, enum keys and t.rich tag handlers, not user-visible text */

import { use, useState } from 'react';
import { TextInput, Button, Stack, Divider } from '@mantine/core';
import { useDocumentTitle } from '@mantine/hooks';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { applyWorkflowTemplate, createWorkflow, getWorkflowTemplates } from '@/lib/api/workflows';
import { PageHeader } from '@/components/shared/PageHeader';
import { FlowchartBuilderWrapper } from '@/components/workflows/FlowchartBuilder';
import { TemplatePicker } from '@/components/workflows/TemplatePicker';
import { useProject } from '@/lib/hooks/use-projects';
import type { StepData } from '@/components/workflows/WorkflowStepList';
import type { WorkflowTemplateSummary } from '@/lib/types/workflow';
import { findInvalidVideoGenerateStep } from '@/lib/utils/video-generate-validation';
import { stepOptionsToRequest } from '@/lib/utils/step-options';
import { useRouter } from 'next/navigation';
import useSWR from 'swr';
import { useT } from '@/i18n';
import { plainErrorMessage } from '@/lib/api/network-error';

export default function NewWorkflowPage({ params }: { params: Promise<{ id: string }> }) {
  const { id: projectId } = use(params);
  const t = useT('projects.newWorkflow');
  const router = useRouter();
  const [loading, setLoading] = useState(false);
  const [steps, setSteps] = useState<StepData[]>([]);
  const [selectedTemplateKey, setSelectedTemplateKey] = useState<string | null>(null);
  const { data: project } = useProject(projectId);
  // The route's title is metadata (this segment's layout.tsx); this refines it with the project
  // name once the client-side fetch resolves, which a static metadata export cannot know. It is
  // the only remaining `document.title` writer besides the workflow page below.
  useDocumentTitle(t('documentTitle', { project: project?.name ?? t('projectFallback') }));

  const { data: templates } = useSWR<WorkflowTemplateSummary[]>(
    projectId ? `workflow-templates-${projectId}` : null,
    () => getWorkflowTemplates(projectId),
  );

  const form = useForm({
    initialValues: { name: '' },
    validate: {
      name: (v) => (!v.trim() ? t('nameRequired') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    if (steps.length === 0) {
      notifications.show({ title: t('errorTitle'), message: t('addStep'), color: 'red' });
      return;
    }

    for (const [i, s] of steps.entries()) {
      if (s.stepType !== 'Conditional' && s.stepType !== 'Parallel' && !s.agentDefinitionId) {
        notifications.show({ title: t('errorTitle'), message: t('stepNeedsAgent', { step: i + 1 }), color: 'red' });
        return;
      }
      if (s.stepType === 'Parallel' && s.parallelAgentIds.length === 0) {
        notifications.show({ title: t('errorTitle'), message: t('stepNeedsOneAgent', { step: i + 1 }), color: 'red' });
        return;
      }
      if (s.stepType === 'Conditional' && !s.conditionExpression) {
        notifications.show({ title: t('errorTitle'), message: t('stepNeedsCondition', { step: i + 1 }), color: 'red' });
        return;
      }
    }

    const videoGenerateError = findInvalidVideoGenerateStep(steps);
    if (videoGenerateError) {
      notifications.show({ title: t('errorTitle'), message: videoGenerateError, color: 'red' });
      return;
    }

    setLoading(true);
    try {
      const workflow = await createWorkflow(projectId, {
        name: values.name,
        steps: steps.map((s, i) => ({
          agentDefinitionId: s.agentDefinitionId,
          stepOrder: i + 1,
          label: s.label || `Step ${i + 1}`,
          stepType: s.stepType,
          conditionExpression: s.conditionExpression,
          loopSourceExpression: s.loopSourceExpression,
          loopTargetStepOrder: s.loopTargetStepOrder,
          maxIterations: s.maxIterations,
          minScore: s.minScore,
          inputMappingJson: s.inputMappingJson,
          agentInputContextMode: s.agentInputContextMode,
          selectedPriorStepOrdersJson: s.selectedPriorStepOrders.length > 0 ? JSON.stringify(s.selectedPriorStepOrders) : null,
          trueBranchStepOrder: s.trueBranchStepOrder,
          falseBranchStepOrder: s.falseBranchStepOrder,
          parallelAgentIdsJson: s.parallelAgentIds.length > 0 ? JSON.stringify(s.parallelAgentIds) : null,
          extractConfigJson: s.extractConfig ? JSON.stringify(s.extractConfig) : null,
          videoAnalyzeConfigJson: s.videoAnalyzeConfig ? JSON.stringify(s.videoAnalyzeConfig) : null,
          videoCompileConfigJson: s.videoCompileConfig ? JSON.stringify(s.videoCompileConfig) : null,
          videoGenerateConfigJson: s.videoGenerateConfig ? JSON.stringify(s.videoGenerateConfig) : null,
          voiceoverConfigJson: s.voiceoverConfig ? JSON.stringify(s.voiceoverConfig) : null,
          thumbnailGenerateConfigJson: s.thumbnailGenerateConfig ? JSON.stringify(s.thumbnailGenerateConfig) : null,
          timelineRenderConfigJson: s.timelineRenderConfig ? JSON.stringify(s.timelineRenderConfig) : null,
          designSystemConfigJson: s.designSystemConfig ? JSON.stringify(s.designSystemConfig) : null,
          extractFrameConfigJson: s.extractFrameConfig ? JSON.stringify(s.extractFrameConfig) : null,
          audioGenerateConfigJson: s.audioGenerateConfig ? JSON.stringify(s.audioGenerateConfig) : null,
          imageGenerateConfigJson: s.imageGenerateConfig ? JSON.stringify(s.imageGenerateConfig) : null,
          audioCleanupConfigJson: s.audioCleanupConfig ? JSON.stringify(s.audioCleanupConfig) : null,
          videoEnhanceConfigJson: s.videoEnhanceConfig ? JSON.stringify(s.videoEnhanceConfig) : null,
          ...stepOptionsToRequest(s),
          conditionalConfigJson: s.conditionalConfig ? JSON.stringify(s.conditionalConfig) : null,
          reviewLoopConfigJson: s.reviewLoopConfig ? JSON.stringify(s.reviewLoopConfig) : null,
          editRoomConfigJson: s.editRoomConfigJson ?? null,
          graphicsRoomConfigJson: s.graphicsRoomConfigJson ?? null,
          colorGradeRoomConfigJson: s.colorGradeRoomConfigJson ?? null,
          dailiesRoomConfigJson: s.dailiesRoomConfigJson ?? null,
          cacheMode: s.cacheMode,
        })),
      });
      notifications.show({ title: t('createdTitle'), message: t('createdMessage'), color: 'green' });
      router.push(`/projects/${projectId}/workflows/${workflow.id}?view=advanced`);
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: plainErrorMessage(err, t('createFailed')),
        color: 'red',
      });
    } finally {
      setLoading(false);
    }
  });

  const handleApplyTemplate = async () => {
    if (!selectedTemplateKey) {
      notifications.show({ title: t('errorTitle'), message: t('selectTemplate'), color: 'red' });
      return;
    }

    setLoading(true);
    try {
      const workflow = await applyWorkflowTemplate(projectId, selectedTemplateKey, true);
      notifications.show({ title: t('templateAppliedTitle'), message: t('templateAppliedMessage', { name: workflow.name }), color: 'green' });
      router.push(`/projects/${projectId}/workflows/${workflow.id}`);
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: plainErrorMessage(err, t('applyFailed')),
        color: 'red',
      });
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <PageHeader
        title={t('title')}
        breadcrumbs={[
          { label: t('breadcrumbProjects'), href: '/projects' },
          { label: project?.name ?? t('projectFallback'), href: `/projects/${projectId}` },
          { label: t('title') },
        ]}
      />
      <form onSubmit={handleSubmit}>
        <Stack gap="lg" maw={1200}>
          <TemplatePicker
            projectId={projectId}
            templates={templates}
            selectedKey={selectedTemplateKey}
            onSelect={setSelectedTemplateKey}
            onApply={handleApplyTemplate}
            loading={loading}
          />
          <Divider label={t('orBuild')} labelPosition="center" />

          <TextInput
            label={t('workflowName')}
            placeholder={t('workflowNamePlaceholder')}
            size="lg"
            {...form.getInputProps('name')}
          />
          <FlowchartBuilderWrapper steps={steps} onChange={setSteps} projectId={projectId} />
          <Button type="submit" loading={loading} size="lg" variant="gradient" gradient={{ from: 'violet', to: 'purple' }}>
            {t('create')}
          </Button>
        </Stack>
      </form>
    </>
  );
}
