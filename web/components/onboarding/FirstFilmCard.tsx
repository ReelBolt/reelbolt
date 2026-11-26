'use client';

import { useRef, useState } from 'react';
import { Anchor, Button, CloseButton, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconMovie, IconBulb } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { addSampleFile } from '@/lib/api/files';
import { createProject } from '@/lib/api/projects';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { applyWorkflowTemplate } from '@/lib/api/workflows';
import { FIRST_FILM_DISMISS_KEY, firstFilmErrorKind, shouldOfferFirstFilm, startFirstFilm } from '@/lib/onboarding/first-film';
import type { DashboardOverview } from '@/lib/types/dashboard';
import type { Project } from '@/lib/types/project';
import classes from './onboarding.module.css';

/**
 * The one-click path: a project with a bundled sample clip and the footage app, opened ready to run.
 * It only navigates; the person presses Run.
 */
export function FirstFilmButton({
  projects, projectId, variant = 'filled',
}: { projects?: Project[]; projectId?: string; variant?: 'filled' | 'light' }) {
  const t = useT('onboarding.firstFilm');
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  // A project made by an earlier try whose sample step failed: the retry uses it instead of making another.
  const created = useRef<{ id?: string }>({});

  const errorText = (kind: ReturnType<typeof firstFilmErrorKind>): string => {
    if (kind === 'unverified') return t('error.unverified');
    if (kind === 'tooLarge') return t('error.tooLarge');
    if (kind === 'network') return t('error.network');
    return t('error.generic');
  };

  const go = async () => {
    if (busy) return;
    setBusy(true);
    try {
      const path = await startFirstFilm(
        { createProject, addSample: (id) => addSampleFile(id), applyTemplate: applyWorkflowTemplate },
        { projects, projectId, description: t('projectDescription'), created: created.current },
      );
      router.push(path);
    } catch (err: unknown) {
      notifications.show({
        title: t('failedTitle'),
        message: errorText(firstFilmErrorKind(err)),
        color: 'red',
      });
      setBusy(false);
    }
  };

  return (
    <Button variant={variant} leftSection={<IconMovie size={18} aria-hidden />} loading={busy} style={{ minHeight: 44 }} onClick={() => void go()}>
      {busy ? t('settingUp') : t('button')}
    </Button>
  );
}

/** The ai-clip-from-idea recipe (inference WorkflowTemplatePresets.AiClipKey): a scene described in words. */
export const DESCRIBE_SCENE_TEMPLATE_KEY = 'ai-clip-from-idea';

/**
 * "Describe a scene instead": adds the idea app to the project (no run, no spend) and opens its
 * page; the person presses Run.
 *
 * The app makes AI video, which the Trial and Creator plans do not include (Run answers 402
 * PLAN_NOT_ENTITLED). So the link shows only once the plan is known to include video generation
 * (the summary's `unlocks` is present and does not list it): while the summary is unread, when billing is off, or when it is not included, it is not drawn,
 * and nobody lands on an app they cannot run.
 */
export function DescribeSceneLink({ projectId }: { projectId: string }) {
  const t = useT('onboarding.firstFilm');
  const tAll = useT();
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const { data: summary } = useBillingSummary();
  // The summary lists the features the plan lacks as `unlocks`; a loaded plan that does not list video generation has it.
  const entitlements = summary?.entitlements;
  if (!entitlements || entitlements.unlocks?.some((u) => u.feature === 'video_generation') !== false) return null;
  const go = async () => {
    if (busy) return;
    setBusy(true);
    try {
      const workflow = await applyWorkflowTemplate(projectId, DESCRIBE_SCENE_TEMPLATE_KEY, true);
      router.push(`/projects/${encodeURIComponent(projectId)}/workflows/${encodeURIComponent(workflow.id)}`);
    } catch (err: unknown) {
      notifications.show({ title: t('describeFailedTitle'), message: describeError(tAll, err, t('error.generic')), color: 'red' });
      setBusy(false);
    }
  };
  return (
    <Anchor component="button" type="button" size="sm" disabled={busy} onClick={() => void go()}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 6, minHeight: 44 }}>
      <IconBulb size={16} aria-hidden />
      {busy ? t('settingUp') : t('describeScene')}
    </Anchor>
  );
}

/** Dashboard card for someone with no run yet; dismissible, gone once a run exists. */
export function FirstFilmCard({ projects, overview }: { projects: Project[] | undefined; overview: DashboardOverview | undefined }) {
  const t = useT('onboarding.firstFilm');
  const [dismissed, setDismissed] = useState(() => {
    try { return typeof window !== 'undefined' && window.localStorage.getItem(FIRST_FILM_DISMISS_KEY) === '1'; } catch { return false; }
  });
  if (!projects || !shouldOfferFirstFilm(overview, dismissed)) return null;

  const dismiss = () => {
    setDismissed(true);
    try { window.localStorage.setItem(FIRST_FILM_DISMISS_KEY, '1'); } catch { /* private mode: hidden for this visit */ }
  };

  return (
    <section className={classes.checklist} aria-label={t('title')} data-testid="first-film">
      <div className={classes.checkHead}>
        <h2 className={classes.checkTitle}>{t('title')}</h2>
        <CloseButton aria-label={t('dismiss')} style={{ minWidth: 44, minHeight: 44 }} onClick={dismiss} />
      </div>
      <Text size="sm" my="xs">{t('body')}</Text>
      <FirstFilmButton projects={projects} />
    </section>
  );
}
