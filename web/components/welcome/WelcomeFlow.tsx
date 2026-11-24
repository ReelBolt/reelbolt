'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Loader } from '@mantine/core';
import { useRouter } from 'next/navigation';
import useSWR, { useSWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import { getGitHubAccount } from '@/lib/api/github';
import { plainErrorMessage } from '@/lib/api/network-error';
import { importSampleClip } from '@/lib/api/onboarding';
import { createProject, getProject } from '@/lib/api/projects';
import { PERSONAS, type Persona } from '@/lib/attribution/persona';
import { useAuth } from '@/lib/hooks/use-auth';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import { MATERIALS, type Material } from '@/lib/onboarding/state';
import { firstProjectHref, projectFilesHref, shotListHref } from '@/lib/onboarding/welcome';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import type { GitHubAccount } from '@/lib/types/github';
import { useT } from '@/i18n';
import { MATERIAL_CARDS, PERSONA_CARDS, SHOT_LISTS } from './welcome-content';
import classes from './welcome.module.css';

type Step = 'persona' | 'material' | 'shots' | 'samples';

/** Touch targets: every button is at least 44px tall, a card twice that. */
const TARGET = { minHeight: 44 } as const;
const CARD_TARGET = { minHeight: 88 } as const;

/** Whether a project this browser remembers can still be opened (it may have been deleted since). */
async function projectStillThere(id: string): Promise<boolean> {
  try {
    await getProject(id);
    return true;
  } catch {
    return false;
  }
}

/** How a sample import that did not fully succeed ended. */
interface SampleOutcome {
  kind: 'unverified' | 'failed' | 'partial';
  added: number;
}

export interface WelcomeFlowProps {
  /**
   * The operator's sample clips (`ONBOARDING_SAMPLE_CLIP_URLS`), resolved on the server. Empty by
   * default, and then "Try it with our sample clips" is not rendered anywhere.
   */
  sampleClipUrls?: string[];
  /** `?step=shots&project=<id>`: reopen the shot list of a project that already exists. */
  shotsForProjectId?: string | null;
}

/**
 * The welcome flow at `/welcome`: two one-click questions (what the video is for, what the person
 * starts from), then the first project with the matching recipe, then the place to add material.
 *
 * - The audience is preselected from the landing page the person signed up from
 *   (`profile.suggestedPersona`), so that question is one click on Continue.
 * - The answer is saved with `PUT /api/v1/onboarding/profile`; the recipe it names is passed to
 *   `POST /api/v1/projects`. A failed save never blocks the project.
 * - Clips land on the Files tab's drop zone; code on the tab where code is added; an idea first
 *   gets a shot list to film, then the drop zone.
 * - "Skip for now" is always one click and records the skip.
 */
export function WelcomeFlow({ sampleClipUrls = [], shotsForProjectId = null }: WelcomeFlowProps) {
  const t = useT('welcome');
  const router = useRouter();
  const { mutate } = useSWRConfig();
  const { user } = useAuth();
  const userKey = userKeyFromEmail(user?.email);
  const { profile, settled, answerWelcome, skipWelcome, rememberWelcomeProject, welcomeProject } = useOnboarding();

  const [step, setStep] = useState<Step>(shotsForProjectId ? 'shots' : 'persona');
  const [picked, setPicked] = useState<Persona | null>(null);
  const [material, setMaterial] = useState<Material | null>(null);
  const [projectId, setProjectId] = useState<string | null>(shotsForProjectId);
  const [busy, setBusy] = useState<'creating' | 'importing' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pendingSamples, setPendingSamples] = useState<string[]>(sampleClipUrls);
  const [sampleOutcome, setSampleOutcome] = useState<SampleOutcome | null>(null);
  // Guards a double click: state has not re-rendered yet when the second click lands.
  const working = useRef(false);

  // What is highlighted: the person's own pick, else what they answered before, else the audience
  // of the landing page they came from.
  const selected: Persona | null = picked ?? profile?.persona ?? profile?.suggestedPersona ?? null;

  // Whether this server can connect a repository decides where code is added. Asked only once the
  // second question is on screen.
  const { data: github } = useSWR<GitHubAccount>(step === 'material' ? '/api/v1/github/account' : null, getGitHubAccount, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
  const repositoryAvailable = github?.canConnect === true;

  // Each screen starts at its heading, for keyboard and screen-reader users.
  const heading = useRef<HTMLHeadingElement>(null);
  const firstStep = useRef(step);
  useEffect(() => {
    if (firstStep.current === step) return;
    firstStep.current = step;
    heading.current?.focus();
  }, [step]);

  const importSamples = useCallback(
    async (intoProjectId: string, urls: string[]) => {
      setBusy('importing');
      let added = 0;
      let unverified = false;
      const left: string[] = [];
      for (const url of urls) {
        if (unverified) {
          left.push(url);
          continue;
        }
        try {
          await importSampleClip(intoProjectId, url);
          added += 1;
        } catch (err) {
          left.push(url);
          // An address that is not confirmed yet is refused for every clip: stop asking.
          if (err instanceof ApiError && err.code === 'EMAIL_NOT_VERIFIED') unverified = true;
        }
      }
      if (left.length === 0) {
        router.push(projectFilesHref(intoProjectId));
        return;
      }
      setPendingSamples(left);
      setSampleOutcome({ kind: unverified ? 'unverified' : added > 0 ? 'partial' : 'failed', added });
      setStep('samples');
      setBusy(null);
    },
    [router],
  );

  /** Saves the answers, creates the first project with its recipe, then goes where the material is added. */
  const start = useCallback(
    async (chosen: Material, withSamples: boolean) => {
      if (working.current) return;
      working.current = true;
      setMaterial(chosen);
      setError(null);
      setBusy('creating');
      try {
        let id = projectId;
        if (!id) {
          const { starterTemplateKey } = await answerWelcome(selected ?? 'other', chosen);
          // One first project per person per browser. The page can be opened again without its
          // project (a phone reloads a tab it had put aside, someone presses Back): answering a
          // second time opens the project the first answer made, as long as it still exists and
          // started from the same recipe, instead of leaving two "My first video" behind.
          const earlier = welcomeProject();
          if (earlier && earlier.starter === starterTemplateKey && (await projectStillThere(earlier.id))) {
            id = earlier.id;
          } else {
            const project = await createProject({ name: t('projectName'), description: '', starterTemplateKey });
            // The real transition: a project was just created. Once per user and only with consent
            // (the funnel's own marker and gate); a refusal changes nothing the person can see.
            emitFunnelEvent('alpha.first_project', userKey);
            void mutate('/api/v1/projects');
            void mutate('/api/v1/dashboard/overview');
            id = project.id;
            rememberWelcomeProject(id, starterTemplateKey);
          }
          setProjectId(id);
        }
        if (withSamples) {
          await importSamples(id, pendingSamples);
        } else if (chosen === 'idea') {
          setStep('shots');
          setBusy(null);
          // The shot list is where a person leaves to go and film. The address now names the step
          // and the project, so a reload (or the tab coming back from the background) reopens this
          // list instead of starting the questions over.
          router.replace(shotListHref(id));
        } else {
          router.push(firstProjectHref(chosen, id, { repositoryAvailable }));
        }
      } catch (err) {
        setError(plainErrorMessage(err));
        setBusy(null);
      } finally {
        working.current = false;
      }
    },
    [projectId, answerWelcome, selected, t, userKey, mutate, importSamples, pendingSamples, router, repositoryAvailable, rememberWelcomeProject, welcomeProject],
  );
  const startFootage = useCallback(() => void start('footage', false), [start]);
  const startCode = useCallback(() => void start('code', false), [start]);
  const startIdea = useCallback(() => void start('idea', false), [start]);
  const startByMaterial: Record<Material, () => void> = { footage: startFootage, code: startCode, idea: startIdea };
  const retryStart = useCallback(() => {
    if (material) void start(material, false);
  }, [material, start]);
  const startFootageWithSamples = useCallback(() => void start('footage', true), [start]);
  const startIdeaWithSamples = useCallback(() => void start('idea', true), [start]);

  const skip = useCallback(() => {
    if (working.current) return;
    working.current = true;
    // The browser remembers the skip before the request leaves, so the dashboard never sends the
    // person back here; the save itself is best effort.
    void skipWelcome();
    router.replace('/dashboard');
  }, [skipWelcome, router]);

  const retrySamples = useCallback(() => {
    if (working.current || !projectId) return;
    working.current = true;
    void importSamples(projectId, pendingSamples).finally(() => {
      working.current = false;
    });
  }, [projectId, pendingSamples, importSamples]);

  const disabled = busy !== null;
  const offersSamples = pendingSamples.length > 0;
  const busyLine = busy && (
    <p className={classes.busy} role="status">
      <Loader size="sm" aria-hidden />
      {busy === 'importing' ? t('samples.importing') : t('creating')}
    </p>
  );

  if (step === 'samples' && projectId && sampleOutcome) {
    const { kind, added } = sampleOutcome;
    return (
      <section className={classes.shell} data-testid="welcome-flow" data-step="samples">
        <Alert
          color={kind === 'unverified' ? 'yellow' : 'gray'}
          title={kind === 'unverified' ? t('samples.unverifiedTitle') : kind === 'partial' ? t('samples.someFailedTitle') : t('samples.failedTitle')}
          data-testid={`welcome-samples-${kind}`}
        >
          {kind === 'unverified' ? t('samples.unverifiedBody') : kind === 'partial' ? t('samples.someFailedBody', { added }) : t('samples.failedBody')}
        </Alert>
        {busyLine}
        <div className={classes.footer}>
          <div className={classes.footerMain}>
            <Button style={TARGET} disabled={disabled} onClick={() => router.push(projectFilesHref(projectId))}>
              {t('samples.open')}
            </Button>
            <Button style={TARGET} variant="default" disabled={disabled} onClick={retrySamples}>
              {t('samples.retry')}
            </Button>
          </div>
        </div>
      </section>
    );
  }

  if (step === 'shots' && projectId) {
    // A reopened shot list waits for the person's own audience rather than flashing the general one.
    if (!selected && !settled) {
      return (
        <section className={classes.shell} data-testid="welcome-flow" data-step="shots">
          <p className={classes.busy} role="status">
            <Loader size="sm" aria-hidden />
          </p>
        </section>
      );
    }
    const shots = SHOT_LISTS[selected ?? 'other'];
    return (
      <section className={classes.shell} data-testid="welcome-flow" data-step="shots">
        <div>
          <h1 className={classes.title} tabIndex={-1} ref={heading}>
            {t('shots.title')}
          </h1>
          <p className={classes.lead}>{t('shots.body')}</p>
        </div>
        <ol className={classes.shots} aria-label={t('shots.listLabel')}>
          {shots.map((shot) => (
            <li key={shot.$msg} className={classes.shot}>
              <span>{t.msg(shot)}</span>
            </li>
          ))}
        </ol>
        <p className={classes.lead}>{t('shots.tip')}</p>
        {busyLine}
        <div className={classes.footer}>
          <div className={classes.footerMain}>
            <Button style={TARGET} disabled={disabled} onClick={() => router.push(projectFilesHref(projectId))}>
              {t('shots.ready')}
            </Button>
            <Button style={TARGET} variant="default" disabled={disabled} onClick={() => router.push('/dashboard')}>
              {t('shots.later')}
            </Button>
          </div>
          {offersSamples && (
            <Button style={TARGET} variant="subtle" disabled={disabled} onClick={startIdeaWithSamples} data-testid="welcome-samples">
              {t('samples.offer')}
            </Button>
          )}
        </div>
        <p className={classes.progress}>{t('shots.laterHint')}</p>
      </section>
    );
  }

  const onPersona = step === 'persona';
  return (
    <section className={classes.shell} data-testid="welcome-flow" data-step={step}>
      <div>
        <p className={classes.progress}>{t('progress', { n: onPersona ? 1 : 2, total: 2 })}</p>
        <h1 className={classes.title} tabIndex={-1} ref={heading}>
          {onPersona ? t('persona.title') : t('material.title')}
        </h1>
        <p className={classes.lead}>{onPersona ? t('persona.body') : t('material.body')}</p>
      </div>

      {onPersona ? (
        <div className={classes.cards} role="group" aria-label={t('persona.groupLabel')}>
          {PERSONAS.map((persona) => (
            <button
              key={persona}
              type="button"
              className={classes.card}
              style={CARD_TARGET}
              aria-pressed={selected === persona}
              data-persona={persona}
              disabled={disabled}
              onClick={() => {
                setPicked(persona);
                setStep('material');
              }}
            >
              <span className={classes.cardTitle}>{t.msg(PERSONA_CARDS[persona].title)}</span>
              <span className={classes.cardBody}>{t.msg(PERSONA_CARDS[persona].words)}</span>
            </button>
          ))}
        </div>
      ) : (
        <div className={classes.cards} data-list="" role="group" aria-label={t('material.groupLabel')}>
          {MATERIALS.map((m) => (
            <button
              key={m}
              type="button"
              className={classes.card}
              style={CARD_TARGET}
              aria-pressed={material === m}
              data-material={m}
              disabled={disabled}
              onClick={startByMaterial[m]}
            >
              <span className={classes.cardTitle}>{t.msg(MATERIAL_CARDS[m].title)}</span>
              <span className={classes.cardBody}>{t.msg(MATERIAL_CARDS[m].body)}</span>
            </button>
          ))}
        </div>
      )}

      {error && (
        <Alert color="red" title={t('error.title')} data-testid="welcome-error">
          {error}
        </Alert>
      )}
      {busyLine}

      <div className={classes.footer}>
        <div className={classes.footerMain}>
          {onPersona && selected && (
            <Button style={TARGET} disabled={disabled} onClick={() => setStep('material')} data-testid="welcome-continue">
              {t('continue')}
            </Button>
          )}
          {!onPersona && (
            <Button style={TARGET} variant="default" disabled={disabled} onClick={() => setStep('persona')}>
              {t('back')}
            </Button>
          )}
          {!onPersona && error && material && (
            <Button style={TARGET} disabled={disabled} onClick={retryStart}>
              {t('error.retry')}
            </Button>
          )}
          {!onPersona && offersSamples && (
            <Button style={TARGET} variant="subtle" disabled={disabled} onClick={startFootageWithSamples} data-testid="welcome-samples">
              {t('samples.offer')}
            </Button>
          )}
        </div>
        <Button style={TARGET} variant="subtle" color="gray" disabled={disabled} onClick={skip} data-testid="welcome-skip">
          {t('skip')}
        </Button>
      </div>
    </section>
  );
}
