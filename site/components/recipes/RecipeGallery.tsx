'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useCurrentLocale, useT } from '@/i18n';
import { stepTypeName, categoryName } from './labels';
import { costKey, type FacetEntry, type RecipeCard as Card, type RecipeFacets, type RecipeList } from '@/lib/recipes/types';
import { RecipeCard } from './RecipeCard';

type Sort = 'new' | 'copied' | 'rated';
const SORTS: readonly Sort[] = ['new', 'copied', 'rated'];

interface Filters {
  q: string;
  category: string;
  cost: string;
  stepType: string;
  language: string;
  sort: Sort;
}

const NO_FILTERS: Filters = { q: '', category: '', cost: '', stepType: '', language: '', sort: 'new' };

function queryFor(filters: Filters, cursor?: string): string {
  const p = new URLSearchParams();
  if (filters.q.trim() !== '') p.set('q', filters.q.trim());
  if (filters.category) p.set('category', filters.category);
  if (filters.cost) p.set('cost', filters.cost);
  if (filters.stepType) p.set('stepType', filters.stepType);
  if (filters.language) p.set('language', filters.language);
  if (filters.sort !== 'new') p.set('sort', filters.sort);
  if (cursor) p.set('cursor', cursor);
  const s = p.toString();
  return s === '' ? '' : `?${s}`;
}

async function getList(query: string, signal: AbortSignal): Promise<RecipeList | null> {
  try {
    const res = await fetch(`/api/recipes${query}`, { signal });
    if (!res.ok) return null;
    const body = (await res.json()) as RecipeList;
    return Array.isArray(body.items) ? body : null;
  } catch {
    return null;
  }
}

async function getFacets(signal: AbortSignal): Promise<RecipeFacets | null> {
  try {
    const res = await fetch('/api/recipes/facets', { signal });
    return res.ok ? ((await res.json()) as RecipeFacets) : null;
  } catch {
    return null;
  }
}

function Chips({
  label,
  entries,
  value,
  onChange,
  display,
}: {
  label: string;
  entries: FacetEntry[];
  value: string;
  onChange: (next: string) => void;
  display: (key: string) => string;
}) {
  const t = useT('recipes');
  if (entries.length === 0) return null;
  return (
    <div role="group" aria-label={label} className="flex flex-wrap items-center gap-2">
      <span className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">{label}</span>
      <button type="button" aria-pressed={value === ''} onClick={() => onChange('')} className={chipClass(value === '')}>
        {t('gallery.all')}
      </button>
      {entries.map((entry) => (
        <button
          key={entry.key}
          type="button"
          aria-pressed={value === entry.key}
          onClick={() => onChange(value === entry.key ? '' : entry.key)}
          className={chipClass(value === entry.key)}
        >
          {display(entry.key)}
        </button>
      ))}
    </div>
  );
}

function chipClass(on: boolean): string {
  return `inline-flex min-h-11 items-center border px-3 font-mono text-xs uppercase tracking-eyebrow transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong ${
    on ? 'border-accent bg-accent text-paper' : 'border-line-strong text-ink-muted hover:text-ink'
  }`;
}

/** The gallery: search, filter chips, sort, the card grid and "load more". Starts from the page's prerendered first page. */
export function RecipeGallery({ initial }: { initial: RecipeList }) {
  const t = useT('recipes');
  const locale = useCurrentLocale();
  const [filters, setFilters] = useState<Filters>(NO_FILTERS);
  const [items, setItems] = useState<Card[]>(initial.items);
  const [cursor, setCursor] = useState<string | null>(initial.nextCursor);
  const [facets, setFacets] = useState<RecipeFacets>({ costClasses: [], categories: [], stepTypes: [], languages: [] });
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState<'load' | 'more' | null>(null);
  const [retry, setRetry] = useState(0);
  const firstRun = useRef(true);

  // Facets once, on mount.
  useEffect(() => {
    const controller = new AbortController();
    void getFacets(controller.signal).then((f) => f && setFacets(f));
    return () => controller.abort();
  }, []);

  // Refetch page one when a filter changes. The very first run only refreshes the prerendered list
  // (the static page can be older than the API) and keeps it when the answer is empty.
  useEffect(() => {
    const controller = new AbortController();
    const first = firstRun.current;
    firstRun.current = false;
    const timer = window.setTimeout(
      () => {
        setLoading(true);
        void getList(queryFor(filters), controller.signal).then((list) => {
          if (controller.signal.aborted) return;
          setLoading(false);
          if (!list) {
            setFailed('load');
            return;
          }
          setFailed(null);
          if (first && list.items.length === 0) return;
          setItems(list.items);
          setCursor(list.nextCursor);
        });
      },
      first ? 0 : 250,
    );
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [filters, retry]);

  const loadMore = useCallback(async () => {
    if (!cursor || loading) return;
    setLoading(true);
    const controller = new AbortController();
    const list = await getList(queryFor(filters, cursor), controller.signal);
    setLoading(false);
    if (!list) {
      setFailed('more');
      return;
    }
    setFailed(null);
    setItems((current) => {
      const seen = new Set(current.map((c) => c.id));
      return [...current, ...list.items.filter((c) => !seen.has(c.id))];
    });
    setCursor(list.nextCursor);
  }, [cursor, loading, filters]);

  const languageName = useMemo(() => {
    let names: Intl.DisplayNames | null = null;
    try {
      names = new Intl.DisplayNames([locale], { type: 'language' });
    } catch {
      names = null;
    }
    return (code: string) => {
      try {
        return names?.of(code) ?? code;
      } catch {
        return code;
      }
    };
  }, [locale]);

  const costLabel = (key: string) => {
    const id = costKey(key);
    return id !== null ? t(`cost.${id}`) : t('field.other');
  };
  const set = (patch: Partial<Filters>) => setFilters((f) => ({ ...f, ...patch }));
  const filtered = filters.q !== '' || filters.category !== '' || filters.cost !== '' || filters.stepType !== '' || filters.language !== '';

  return (
    <div>
      <div className="flex flex-col gap-4 pb-6">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
          <label className="flex flex-1 flex-col gap-1.5">
            <span className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">{t('gallery.searchLabel')}</span>
            <input
              type="search"
              value={filters.q}
              maxLength={100}
              onChange={(e) => set({ q: e.target.value })}
              placeholder={t('gallery.searchPlaceholder')}
              className="min-h-11 w-full border border-line-strong bg-paper px-3 text-ink placeholder:text-ink-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
            />
          </label>
          <label className="flex flex-col gap-1.5 sm:w-56">
            <span className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">{t('gallery.sortLabel')}</span>
            <select
              value={filters.sort}
              onChange={(e) => set({ sort: SORTS.find((s) => s === e.target.value) ?? 'new' })}
              className="min-h-11 w-full border border-line-strong bg-paper px-3 text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
            >
              {SORTS.map((s) => (
                <option key={s} value={s}>
                  {t(`sort.${s}`)}
                </option>
              ))}
            </select>
          </label>
        </div>
        <Chips label={t('gallery.categoryLabel')} entries={facets.categories} value={filters.category} onChange={(category) => set({ category })} display={(k) => categoryName(t, k)} />
        <Chips label={t('gallery.costLabel')} entries={facets.costClasses} value={filters.cost} onChange={(cost) => set({ cost })} display={costLabel} />
        <Chips label={t('gallery.stepTypeLabel')} entries={facets.stepTypes} value={filters.stepType} onChange={(stepType) => set({ stepType })} display={(k) => stepTypeName(t, k)} />
        <Chips label={t('gallery.languageLabel')} entries={facets.languages} value={filters.language} onChange={(language) => set({ language })} display={languageName} />
        {filtered && (
          <div>
            <button
              type="button"
              onClick={() => setFilters(NO_FILTERS)}
              className="min-h-11 font-mono text-xs uppercase tracking-eyebrow text-accent-ink hover:text-accent underline underline-offset-4"
            >
              {t('gallery.clear')}
            </button>
          </div>
        )}
      </div>

      <p aria-live="polite" className="pt-4 font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
        {loading ? t('gallery.loading') : t('gallery.resultCount', { count: items.length })}
      </p>
      <div aria-busy={loading} className="pt-4">
        {failed === 'load' && items.length === 0 ? (
          <div role="alert" className="flex flex-col items-start gap-4 bg-paper-2 p-8 text-ink-muted">
            <p>{t('gallery.loadFailed')}</p>
            <button type="button" onClick={() => setRetry((n) => n + 1)} className={`${chipClass(false)} text-ink`}>
              {t('gallery.retry')}
            </button>
          </div>
        ) : items.length === 0 ? (
          <p className="bg-paper-2 p-8 text-ink-muted">
            {loading ? t('gallery.loading') : filtered ? t('gallery.emptyFiltered') : t('gallery.empty')}
          </p>
        ) : (
          <ul aria-label={t('gallery.resultsLabel')} className="grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
            {items.map((recipe) => (
              <RecipeCard key={recipe.id} recipe={recipe} />
            ))}
          </ul>
        )}
        {failed !== null && items.length > 0 && (
          <p role="alert" className="pt-6 text-center text-ink-muted">
            {failed === 'more' ? t('gallery.loadMoreFailed') : t('gallery.loadFailed')}
          </p>
        )}
        {(cursor || failed === 'more') && (
          <div className="flex justify-center pt-10">
            <button
              type="button"
              onClick={() => void loadMore()}
              disabled={loading}
              className="inline-flex min-h-11 items-center justify-center rounded-[2px] border border-line-strong px-7 font-display font-bold text-ink hover:bg-paper-2 disabled:opacity-60 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
            >
              {loading ? t('gallery.loading') : failed === 'more' ? t('gallery.retry') : t('gallery.loadMore')}
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
