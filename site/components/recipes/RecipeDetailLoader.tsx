'use client';

import { useEffect, useState } from 'react';
import { Container } from '@/components/layout/Container';
import { Button } from '@/components/ui/Button';
import { buttonClass } from '@/components/ui/button-styles';
import { useT } from '@/i18n';
import { outcomeFor } from '@/lib/recipes/load-outcome';
import { isRecipeId, parseDetail, type RecipeDetail } from '@/lib/recipes/types';
import { RecipeDetailBody } from './RecipeDetailBody';

/** Shown when a recipe is gone, never existed or cannot be reached. A friendly page, never an error. */
export function RecipeUnavailable() {
  const t = useT('recipes');
  return (
    <div className="py-24">
      <Container>
        <h1 className="font-display text-[clamp(2rem,5vw,3rem)] font-bold leading-[1] text-ink">
          {t('detail.unavailableTitle')}
        </h1>
        <p className="mt-4 max-w-xl text-ink-muted">{t('detail.unavailableBody')}</p>
        <div className="mt-8">
          <Button href="/recipes" variant="accent" size="lg">
            {t('detail.unavailableCta')}
          </Button>
        </div>
      </Container>
    </div>
  );
}

type State = { phase: 'loading' } | { phase: 'ready'; recipe: RecipeDetail } | { phase: 'gone' } | { phase: 'failed' };

/**
 * The client-rendered detail page: for a recipe listed after the static build (the Pages deployment serves
 * the placeholder page for its URL) the id comes from the address bar and the data from /api/recipes/<id>.
 */
export function RecipeDetailLoader() {
  const t = useT('recipes');
  const [state, setState] = useState<State>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const id = window.location.pathname.replace(/\/+$/, '').split('/').pop() ?? '';
    const controller = new AbortController();
    const settle = (next: State) => {
      if (!controller.signal.aborted) setState(next);
    };
    if (!isRecipeId(id)) {
      settle({ phase: 'gone' });
    } else {
      fetch(`/api/recipes/${id}`, { signal: controller.signal })
        .then(async (res) => {
          const recipe = res.ok ? parseDetail(await res.json()) : null;
          const outcome = outcomeFor(res.status, recipe !== null);
          settle(outcome === 'ready' && recipe ? { phase: 'ready', recipe } : { phase: outcome === 'failed' ? 'failed' : 'gone' });
        })
        .catch(() => settle({ phase: 'failed' }));
    }
    return () => controller.abort();
  }, [attempt]);

  if (state.phase === 'ready') return <RecipeDetailBody recipe={state.recipe} />;
  if (state.phase === 'gone') return <RecipeUnavailable />;
  if (state.phase === 'failed') {
    return (
      <div className="py-24">
        <Container>
          <p role="alert" className="max-w-xl text-ink-muted">
            {t('detail.loadFailed')}
          </p>
          <div className="mt-6">
            <button
              type="button"
              className={buttonClass({ variant: 'accent' })}
              onClick={() => {
                setState({ phase: 'loading' });
                setAttempt((n) => n + 1);
              }}
            >
              {t('detail.retry')}
            </button>
          </div>
        </Container>
      </div>
    );
  }
  return (
    <div className="py-24">
      <Container>
        <p aria-live="polite" className="text-ink-muted">
          {t('detail.loading')}
        </p>
      </Container>
    </div>
  );
}
