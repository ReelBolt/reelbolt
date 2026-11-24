'use client';

import Link from 'next/link';
import useSWR from 'swr';
import { Anchor, Button, Group, Text, VisuallyHidden } from '@mantine/core';
import { IconCheck, IconPlayerPlay, IconUpload } from '@tabler/icons-react';
import { fileKindsOf } from '@/components/workflow-apps/gallery-model';
import { getGitHubAccount } from '@/lib/api/github';
import type { Persona } from '@/lib/attribution/persona';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useWorkflowApps } from '@/lib/hooks/use-workflow-apps';
import { useWorkflows } from '@/lib/hooks/use-workflows';
import type { Material } from '@/lib/onboarding/state';
import { WELCOME_PATH, firstProjectHref, shotListHref } from '@/lib/onboarding/welcome';
import type { GitHubAccount } from '@/lib/types/github';
import type { Project } from '@/lib/types/project';
import { useT } from '@/i18n';
import { Panel } from './Panel';
import classes from './dashboard.module.css';

type StepState = 'done' | 'next' | 'later';

export interface FirstVideoRailProps {
  /** Who the video is for, from the welcome answers (or the landing page); `null` when unknown. */
  persona: Persona | null;
  /** What the person starts from; `null` when the welcome questions were not answered. */
  material: Material | null;
  /** The project the first video is made in (the newest one); `undefined` while there is none. */
  project: Project | undefined;
  /** This project holds something to work from (the dashboard reads the project's own file count). */
  addDone: boolean;
  /** "Run your first video" of the Getting started checklist. */
  runDone: boolean;
  /** Whether `/welcome` exists to start from (`WELCOME_FLOW_ENABLED`). */
  welcomeEnabled: boolean;
  /** Opens the plain "New project" form: the start when the welcome flow is switched off. */
  onCreate: () => void;
}

/**
 * "Your first video": the three steps between a new account and a first result (add the material,
 * say what the video is for, run), each ticked from the Getting started checklist and each linking
 * to the exact place it happens. The dashboard shows it instead of the generic panel until the
 * first run has started.
 *
 * The middle step has no signal of its own (the brief is typed on the run page and sent with the
 * run), so it is ticked together with the run, never before.
 */
export function FirstVideoRail({ persona, material, project, addDone, runDone, welcomeEnabled, onCreate }: FirstVideoRailProps) {
  const t = useT('dashboard.firstVideo');
  const { data: workflows } = useWorkflows(project?.id ?? '');

  // Someone who never answered "what do you have" (an account older than the welcome questions, a
  // skip, the flow switched off) has no material on record. The rail then goes by what the project
  // really is: its one app's kind, and for a promo-from-code app whether the project holds any code.
  // It never tells a person to add clips and then sends them to an app that needs source code.
  const unknownMaterial = material === null;
  const onlyWorkflowId = workflows?.length === 1 ? workflows[0].id : null;
  const { data: gallery } = useWorkflowApps(project && unknownMaterial ? project.id : undefined);
  const onlyAppCategory = onlyWorkflowId ? (gallery?.apps?.find((app) => app.workflowId === onlyWorkflowId)?.category ?? null) : null;
  const promoOnly = unknownMaterial && onlyAppCategory === 'promo';
  const { data: files } = useProjectFiles(promoOnly && project ? project.id : '');
  const holdsCode = promoOnly && files ? fileKindsOf(files).code : false;
  const words: Material | 'any' =
    material ?? (onlyAppCategory === 'footage' ? 'footage' : promoOnly && holdsCode ? 'code' : 'any');

  // Where code is added depends on whether this server can connect a repository; asked only for a
  // project that starts from code.
  const { data: github } = useSWR<GitHubAccount>(project && words === 'code' ? '/api/v1/github/account' : null, getGitHubAccount, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });

  const addHref = project
    ? firstProjectHref(words === 'any' ? 'footage' : words, project.id, { repositoryAvailable: github?.canConnect === true })
    : null;
  // The run page of the project's one app (the recipe the welcome flow created it with); with
  // several apps, or while the list loads, the project's Apps tab. With no material on record the
  // one app is only the destination when it fits what is known: a promo-from-code app in a project
  // without code is not where a first video starts, so the Apps tab (whose order follows the
  // project's files) is.
  const appsHref = project ? `/projects/${encodeURIComponent(project.id)}?tab=workflows` : null;
  const oneAppFits = !unknownMaterial || (onlyAppCategory !== null && (onlyAppCategory !== 'promo' || holdsCode));
  const runHref = !project ? null
    : onlyWorkflowId && oneAppFits ? `/projects/${encodeURIComponent(project.id)}/workflows/${encodeURIComponent(onlyWorkflowId)}`
    : appsHref;

  const added = addDone || runDone;
  const steps: { key: string; title: string; body: string; example?: string; href: string | null; state: StepState }[] = [
    {
      key: 'add',
      title: t('addTitle', { material: words }),
      body: t('addBody', { material: words }),
      href: addHref,
      state: added ? 'done' : project ? 'next' : 'later',
    },
    {
      key: 'brief',
      title: t('briefTitle'),
      body: t('briefBody'),
      // An example brief in the audience's own terms: what to type, never a claim about a result.
      example: t('briefExample', { persona: persona ?? 'other' }),
      href: runHref,
      state: runDone ? 'done' : project && added ? 'next' : 'later',
    },
    { key: 'run', title: t('runTitle'), body: t('runBody'), href: runHref, state: runDone ? 'done' : 'later' },
  ];

  return (
    <Panel id="first-video" title={t('title')}>
      <div data-testid="first-video-rail" data-persona={persona ?? 'other'}>
        <ol className={classes.railSteps} aria-label={t('listLabel')}>
          {steps.map((step, index) => {
            const body = (
              <>
                <span className={classes.railMark} data-state={step.state} aria-hidden>
                  {step.state === 'done' ? <IconCheck size={16} /> : index + 1}
                </span>
                <span className={classes.railText}>
                  <Text span size="sm" fw={600}>
                    {step.title}
                    {step.state !== 'later' && <VisuallyHidden component="span">{step.state === 'done' ? t('done') : t('next')}</VisuallyHidden>}
                  </Text>
                  <Text span size="xs" c="dimmed">{step.body}</Text>
                  {step.example && <Text span size="xs" c="dimmed" fs="italic">{step.example}</Text>}
                </span>
              </>
            );
            return (
              <li key={step.key} className={classes.railStep} data-step={step.key} data-state={step.state} aria-current={step.state === 'next' ? 'step' : undefined}>
                {step.href ? <Link href={step.href} className={classes.railLink}>{body}</Link> : <div className={classes.railLink}>{body}</div>}
              </li>
            );
          })}
        </ol>
        <Group mt="md" gap="sm">
          {!project ? (
            welcomeEnabled ? (
              <Button component={Link} href={WELCOME_PATH} leftSection={<IconPlayerPlay size={16} />} style={{ minHeight: 44 }} data-tour="new-project">
                {t('start')}
              </Button>
            ) : (
              <Button onClick={onCreate} leftSection={<IconPlayerPlay size={16} />} style={{ minHeight: 44 }} data-tour="new-project">
                {t('start')}
              </Button>
            )
          ) : !added && addHref ? (
            <Button component={Link} href={addHref} leftSection={<IconUpload size={16} />} style={{ minHeight: 44 }}>
              {t('addTitle', { material: words })}
            </Button>
          ) : runHref ? (
            <Button component={Link} href={runHref} leftSection={<IconPlayerPlay size={16} />} style={{ minHeight: 44 }}>
              {t('ctaRun')}
            </Button>
          ) : null}
          {project && material === 'idea' && !added && welcomeEnabled && (
            <Anchor component={Link} href={shotListHref(project.id)} size="sm" style={{ display: 'inline-flex', alignItems: 'center', minHeight: 44 }}>
              {t('shotList')}
            </Anchor>
          )}
        </Group>
      </div>
    </Panel>
  );
}
