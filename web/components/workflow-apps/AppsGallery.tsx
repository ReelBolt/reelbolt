'use client';

import { useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Skeleton, Text, TextInput, VisuallyHidden } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconAlertTriangle,
  IconCircleCheck,
  IconCheck,
  IconCircleX,
  IconClockPlay,
  IconCoin,
  IconPlayerStop,
  IconSearch,
} from '@tabler/icons-react';
import { applyWorkflowTemplate } from '@/lib/api/workflows';
import { useWorkflowApps } from '@/lib/hooks/use-workflow-apps';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import type { WorkflowAppCard, WorkflowRecipeCard } from '@/lib/types/workflow';
import {
  CATEGORY_FILTERS,
  costBadgeFor,
  fileKindsOf,
  filterCards,
  isUnrunPromoApp,
  needsHref,
  rankRecipes,
  rankingKinds,
  runStatusFor,
  suggestedRecipes,
  type CategoryFilter,
  type CostBadge,
  type RunStatusKind,
} from './gallery-model';
import { useFormat, useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { FailureCard } from '@/components/shared/FailureCard';
import { LoadState } from '@/components/shared/LoadState';
import { useOptionalAssistant } from '@/components/assistant/AssistantShellProvider';
import { GOAL_TILES } from './goal-chooser';
import { FirstFilmButton } from '@/components/onboarding/FirstFilmCard';
import styles from './AppsGallery.module.css';
import { RecipeModelChoices } from './RecipeModelChoices';

const STATUS_ICON: Record<RunStatusKind, typeof IconCircleCheck> = {
  finished: IconCircleCheck,
  failed: IconCircleX,
  running: IconClockPlay,
  stopped: IconPlayerStop,
  never: IconClockPlay,
};

function CostBadgeChip({ badge }: { badge: CostBadge }) {
  const t = useT('apps.gallery');
  // Icon and words together, never colour alone; the hint is the plain-language tooltip.
  return (
    <>
      <span className={`${styles.badge} ${badge.tone === 'amber' ? styles.badgeAmber : ''}`}>
        {badge.tone === 'amber' ? <IconAlertTriangle size={14} aria-hidden /> : <IconCoin size={14} aria-hidden />}
        {t.msg(badge.label)}
      </span>
      {/* In the card text, not only a tooltip: touch and keyboard users read it too. */}
      {badge.hint && <span className={styles.costHint}>{t.msg(badge.hint)}</span>}
    </>
  );
}

function AppCard({ projectId, app }: { projectId: string; app: WorkflowAppCard }) {
  const t = useT('apps.gallery');
  const tr = useT();
  const fm = useFormat();
  const titleId = useId();
  const status = runStatusFor(app.lastRun, tr, fm.locale);
  const StatusIcon = STATUS_ICON[status.kind];
  return (
    <li>
      <article className={styles.card} aria-labelledby={titleId}>
        <h3 className={styles.cardTitle} id={titleId}>{app.title}</h3>
        {app.tagline && <p className={styles.tagline}>{app.tagline}</p>}
        <div className={styles.badgeRow}>
          <CostBadgeChip badge={costBadgeFor(app.costClass)} />
        </div>
        <div
          className={`${styles.statusLine} ${status.kind === 'finished' ? styles.statusOk : ''} ${status.kind === 'failed' ? styles.statusBad : ''}`}
        >
          <StatusIcon size={16} aria-hidden />
          <span>{status.when ? t('statusWhen', { word: t.msg(status.word), when: status.when }) : t.msg(status.word)}</span>
        </div>
        <div className={styles.cardActions}>
          <Link href={`/projects/${projectId}/workflows/${app.workflowId}`} className={styles.openLink} aria-label={t('openApp', { title: app.title })}>
            {t('open')}
          </Link>
        </div>
      </article>
    </li>
  );
}

function RecipeCardView({
  projectId,
  recipe,
  suggested,
  busy,
  anyBusy,
  onPick,
}: {
  projectId: string;
  recipe: WorkflowRecipeCard;
  suggested: boolean;
  busy: boolean;
  anyBusy: boolean;
  onPick: (recipe: WorkflowRecipeCard) => void;
}) {
  const t = useT('apps.gallery');
  const titleId = useId();
  const ready = recipe.fit === 'ready';
  return (
    <li>
      <article className={styles.card} aria-labelledby={titleId} aria-busy={busy}>
        {/* The heading stays while busy, so the card keeps its name. */}
        <h3 className={styles.cardTitle} id={titleId}>{recipe.title}</h3>
        {busy ? (
          // Provisioning: a skeleton in place of the card body, announced once.
          <div role="status" aria-label={t('settingUpLabel', { title: recipe.title })}>
            <Skeleton height={14} mb={6} />
            <Skeleton height={14} width="80%" mb={12} />
            <Text size="sm" className={styles.hint}>{t('settingUp', { title: recipe.title })}</Text>
          </div>
        ) : (
          <>
            {suggested && (
              <span className={styles.badge}>
                {t('suggested')}
              </span>
            )}
            {recipe.tagline && <p className={styles.tagline}>{recipe.tagline}</p>}
            <div className={styles.badgeRow}>
              <CostBadgeChip badge={costBadgeFor(recipe.costClass)} />
            </div>
            {ready ? (
              <div className={`${styles.needsRow} ${styles.statusOk}`}>
                <IconCircleCheck size={16} aria-hidden />
                <span>{t('ready')}</span>
              </div>
            ) : (
              recipe.missing.map((m) => {
                const href = needsHref(projectId, m.action);
                return (
                  <div key={`${m.need}-${m.action}`} className={styles.needsRow}>
                    <span>{t('needs')}</span>
                    {href ? (
                      <Link href={href} className={styles.needsLink}>
                        {m.message}
                      </Link>
                    ) : (
                      <span>{m.message}</span>
                    )}
                  </div>
                );
              })
            )}
            {(recipe.modelInputs ?? 0) > 0 && <RecipeModelChoices projectId={projectId} recipeKey={recipe.key} />}
          </>
        )}
        <div className={styles.cardActions}>
          <button
            type="button"
            className={styles.openLink}
            onClick={() => onPick(recipe)}
            // Stays focusable while another recipe is set up, looks disabled and ignores presses.
            aria-disabled={anyBusy}
          >
            {t('useRecipe')}
            <VisuallyHidden> {t('useRecipeFor', { title: recipe.title })}</VisuallyHidden>
          </button>
        </div>
      </article>
    </li>
  );
}

/**
 * The project's app gallery (docs/design/workflow-apps.md 4.2): "Your apps" (newest run first) and
 * "Start from a recipe", from one request. Picking a recipe provisions its workflow through the
 * template provisioning endpoint and opens its page. Replaces the plain Workflows tab while
 * `WORKFLOW_APPS_ENABLED` is on.
 */
export function AppsGallery({ projectId, initialCategory = 'all' }: { projectId: string; initialCategory?: CategoryFilter }) {
  const t = useT('apps.gallery');
  const tMarket = useT('marketplace');
  const tAll = useT();
  const router = useRouter();
  const assistant = useOptionalAssistant();
  const tGoal = useT('apps.goals');
  const goalsTitle = useId();
  const goalsBodyId = useId();
  const [goalsOpen, setGoalsOpen] = useState(false);
  const { data, error, isLoading, mutate } = useWorkflowApps(projectId);
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState<CategoryFilter>(initialCategory);
  const [provisioning, setProvisioning] = useState<string | null>(null);
  const appsTitle = useId();
  const recipesTitle = useId();
  const customTitle = useId();
  const chipRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const { data: files } = useProjectFiles(projectId);

  const apps = useMemo(() => filterCards(data?.apps ?? [], query, category), [data, query, category]);
  const recipes = useMemo(() => filterCards(data?.recipes ?? [], query, category), [data, query, category]);
  const allRecipes = data?.recipes;
  const noAppsYet = Boolean(data) && (data?.apps.length ?? 0) === 0;
  // What the person said they start from (the welcome answer): it leads the ranking until the
  // project's own files say something, so a footage or idea project never opens on the code promos.
  const material = useOnboarding().profile?.material ?? null;
  const kinds = useMemo(() => rankingKinds(fileKindsOf(files), material), [files, material]);
  const suggestedOrder = useMemo(
    () => (noAppsYet && allRecipes ? suggestedRecipes(allRecipes, undefined, kinds).map((r) => r.key) : []),
    [noAppsYet, allRecipes, kinds],
  );
  const suggestedKeys = useMemo(() => new Set(suggestedOrder), [suggestedOrder]);
  // In an empty project the suggested recipes lead the list, in suggestion order (ready ones first).
  const orderedRecipes = useMemo(
    () => [
      ...suggestedOrder.flatMap((k) => recipes.filter((r) => r.key === k)),
      ...rankRecipes(recipes.filter((r) => !suggestedKeys.has(r.key)), kinds),
    ],
    [recipes, suggestedOrder, suggestedKeys, kinds],
  );
  // Clips and no code (or a person who starts from footage and has no code here): an auto-created promo
  // app nobody has run would lead to a "needs code" dead end, so while that is all there is, the recipes
  // lead and "Your apps" follows them.
  const appsFollowRecipes = kinds.video && !kinds.code && (data?.apps.length ?? 0) > 0 && (data?.apps ?? []).every(isUnrunPromoApp);
  const filtering = query.trim().length > 0 || category !== 'all';

  const onChipKeyDown = (e: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const last = CATEGORY_FILTERS.length - 1;
    let next = -1;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = index === last ? 0 : index + 1;
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = index === 0 ? last : index - 1;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = last;
    if (next < 0) return;
    e.preventDefault();
    setCategory(CATEGORY_FILTERS[next].value);
    chipRefs.current[next]?.focus();
  };

  const pick = async (recipe: WorkflowRecipeCard) => {
    if (provisioning) return;
    setProvisioning(recipe.key);
    try {
      const workflow = await applyWorkflowTemplate(projectId, recipe.key, true);
      void mutate();
      router.push(`/projects/${projectId}/workflows/${workflow.id}`);
    } catch (err: unknown) {
      notifications.show({
        title: t('recipeFailedTitle'),
        message: describeError(tAll, err, t('tryAgainLater')),
        color: 'red',
      });
      setProvisioning(null);
    }
  };

  if (isLoading) {
    return (
      <div className={styles.root} aria-busy="true">
        <LoadState
          loading
          onRetry={() => void mutate()}
          skeleton={(
            <div role="status" aria-label={t('loading')}>
              <Skeleton height={44} mb="sm" />
              <ul className={styles.grid}>
                {[0, 1, 2].map((i) => (
                  <li key={i}><Skeleton height={140} radius={14} /></li>
                ))}
              </ul>
            </div>
          )}
        />
      </div>
    );
  }

  if (!data) {
    return <FailureCard error={error} onRetry={() => void mutate()} />;
  }

  const resultCount = apps.length + recipes.length;
  const anyBusy = provisioning !== null;

  // Nothing has been run yet (the auto-made promo app counts as untouched): the chooser leads, open.
  // Once something has run it stays at the top, collapsed behind its heading.
  const hasRunApp = data.apps.some((a) => a.lastRun);
  const goalsExpanded = !hasRunApp || goalsOpen;
  const goalsSection = !filtering ? (
    <div>
      <h2 className={styles.sectionTitle} id={goalsTitle}>
        {hasRunApp ? (
          <button
            type="button"
            className={styles.goalsToggle}
            aria-expanded={goalsExpanded}
            aria-controls={goalsBodyId}
            onClick={() => setGoalsOpen((o) => !o)}
          >
            {tGoal('title')}
          </button>
        ) : tGoal('title')}
      </h2>
      <div id={goalsBodyId} hidden={!goalsExpanded}>
      <p className={styles.hint}>{tGoal('hint')}</p>
      <div className={styles.goalGrid} role="group" aria-labelledby={goalsTitle}>
        {GOAL_TILES.map((g) => {
          const key = g.target.kind === 'template' ? g.target.key : null;
          const recipe = key ? data.recipes.find((r) => r.key === key) : undefined;
          const unavailable = !recipe;
          return (
            <button
              key={g.id}
              type="button"
              className={styles.goalTile}
              aria-disabled={unavailable || anyBusy}
              aria-busy={recipe ? provisioning === recipe.key : undefined}
              onClick={() => { if (recipe && !unavailable) void pick(recipe); }}
            >
              <span className={styles.goalLabel}>{t.msg(g.label)}</span>
              <span className={styles.goalHint}>
                {g.target.kind === 'soon' ? t.msg(g.hint) : unavailable ? tGoal('missing') : t.msg(g.hint)}
              </span>
            </button>
          );
        })}
        <button
          type="button"
          className={styles.goalTile}
          aria-disabled={!assistant}
          onClick={() => assistant?.open()}
        >
          <span className={styles.goalLabel}>{tGoal('somethingElse.label')}</span>
          <span className={styles.goalHint}>{tGoal('somethingElse.hint')}</span>
        </button>
      </div>
      </div>
    </div>
  ) : null;

  const appsSection = (
      <section aria-labelledby={appsTitle}>
        <h2 className={styles.sectionTitle} id={appsTitle}>{t('yourApps')}</h2>
        {noAppsYet ? (
          <div className={styles.empty}>
            <Text fw={650}>{t('noApps')}</Text>
            <p className={styles.hint}>
              {t('noAppsHint')}
            </p>
            <FirstFilmButton projectId={projectId} variant="light" />
          </div>
        ) : apps.length === 0 ? (
          <p className={styles.hint}>{t('noAppsMatch')}</p>
        ) : (
          <ul className={styles.grid}>
            {apps.map((a) => (
              <AppCard key={a.workflowId} projectId={projectId} app={a} />
            ))}
          </ul>
        )}
      </section>
  );
  const recipesSection = (
      <section aria-labelledby={recipesTitle} id="apps-recipes">
        <h2 className={styles.sectionTitle} id={recipesTitle}>{t('startFromRecipe')}</h2>
        <p className={styles.hint}>{t('recipeHint')}</p>
        <ul className={styles.grid}>
          {orderedRecipes.map((r) => (
            <RecipeCardView
              key={r.key}
              projectId={projectId}
              recipe={r}
              suggested={suggestedKeys.has(r.key)}
              busy={provisioning === r.key}
              anyBusy={anyBusy}
              onPick={pick}
            />
          ))}
          <li>
            <article className={styles.card} aria-labelledby={customTitle}>
              <h3 className={styles.cardTitle} id={customTitle}>{t('customTitle')}</h3>
              <p className={styles.tagline}>{t('customTagline')}</p>
              <div className={styles.cardActions}>
                <Link href={`/projects/${projectId}/workflows/new`} className={styles.openLink}>
                  {t('customCta')}
                </Link>
              </div>
            </article>
          </li>
        </ul>
        {filtering && recipes.length === 0 && <p className={styles.hint}>{t('noRecipesMatch')}</p>}
        <p className={styles.hint}>
          {tMarket('dashboardLink.hint')}{' '}
          <Link href="/community" className={styles.communityLink}>
            {tMarket('dashboardLink.cta')}
          </Link>
        </p>
      </section>
  );

  return (
    <div className={styles.root}>
      <div className={styles.toolbar}>
        <TextInput
          className={styles.search}
          aria-label={t('search')}
          placeholder={t('search')}
          leftSection={<IconSearch size={16} aria-hidden />}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          styles={{ input: { minHeight: 44 } }}
        />
        <div className={styles.chips} role="radiogroup" aria-label={t('category')}>
          {CATEGORY_FILTERS.map((c, i) => (
            <button
              key={c.value}
              ref={(el) => { chipRefs.current[i] = el; }}
              type="button"
              role="radio"
              aria-checked={category === c.value}
              // Roving tabindex: one tab stop, arrows move the choice.
              tabIndex={category === c.value ? 0 : -1}
              className={styles.chip}
              onClick={() => setCategory(c.value)}
              onKeyDown={(e) => onChipKeyDown(e, i)}
            >
              {category === c.value && <IconCheck size={14} aria-hidden />}
              {t.msg(c.label)}
            </button>
          ))}
        </div>
      </div>
      <VisuallyHidden role="status">
        {anyBusy
          ? t('settingUpRecipe')
          : filtering
            ? t('results', { count: resultCount })
            : ''}
      </VisuallyHidden>
      {error && (
        // A failed refresh keeps the cards that were already loaded.
        <p className={styles.hint} role="status">{t('refreshFailed')}</p>
      )}

      {goalsSection}
      {appsFollowRecipes ? <>{recipesSection}{appsSection}</> : <>{appsSection}{recipesSection}</>}
    </div>
  );
}
