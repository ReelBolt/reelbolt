'use client';

import { useId, useMemo, useState } from 'react';
import { NativeSelect, Skeleton, Text, TextInput, VisuallyHidden } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconCheck, IconSearch } from '@tabler/icons-react';
import { useFormat, useT } from '@/i18n';
import { useMarketplaceFacets, useMarketplaceRecipes } from '@/lib/hooks/use-marketplace';
import { costBadgeFor } from '@/components/workflow-apps/gallery-model';
import type { MarketplaceFilters, MarketplaceSort } from '@/lib/types/marketplace';
import type { AppCostClass } from '@/lib/types/workflow';
import { RecipeCardView } from './RecipeCardView';
import {
  COST_CLASSES,
  DEFAULT_FILTERS,
  SEARCH_DEBOUNCE_MS,
  SORTS,
  categoryLabel,
  hasActiveFilters,
  isSort,
  languageName,
} from './marketplace-model';
import styles from './marketplace.module.css';

type ChoiceKey = 'category' | 'cost' | 'stepType' | 'language';

interface Choice {
  value: string;
  label: string;
}

/** One filter row: toggle chips (pressing the chosen one again clears it). */
function ChipGroup({
  legend,
  choices,
  value,
  onChange,
  allLabel,
}: {
  legend: string;
  choices: Choice[];
  value: string;
  onChange: (value: string) => void;
  allLabel: string;
}) {
  if (choices.length === 0) return null;
  return (
    <fieldset className={styles.filterGroup}>
      <legend className={styles.filterLegend}>{legend}</legend>
      <button type="button" className={styles.chip} aria-pressed={value === ''} onClick={() => onChange('')}>
        {value === '' && <IconCheck size={14} aria-hidden />}
        {allLabel}
      </button>
      {choices.map((c) => (
        <button
          key={c.value}
          type="button"
          className={styles.chip}
          aria-pressed={value === c.value}
          onClick={() => onChange(value === c.value ? '' : c.value)}
        >
          {value === c.value && <IconCheck size={14} aria-hidden />}
          {c.label}
        </button>
      ))}
    </fieldset>
  );
}

const STATIC_CATEGORIES = ['promo', 'footage', 'brand', 'utility'];

/** The community recipe list: debounced search, filter chips, sort, a card grid and load-more. Public, no sign-in needed. */
export function CommunityGallery() {
  const t = useT('marketplace.gallery');
  const tr = useT();
  const fm = useFormat();
  const searchId = useId();
  const [chosen, setFilters] = useState<MarketplaceFilters>(DEFAULT_FILTERS);
  const [input, setInput] = useState('');
  const [debounced] = useDebouncedValue(input, SEARCH_DEBOUNCE_MS);
  // The request follows the debounced text; an emptied box applies at once.
  const q = input === '' ? '' : debounced;
  const filters = useMemo<MarketplaceFilters>(() => ({ ...chosen, q }), [chosen, q]);

  const { items, hasMore, loadMore, isLoading, isLoadingMore, error, loaded, mutate } = useMarketplaceRecipes(filters);
  const { data: facets } = useMarketplaceFacets();

  const setChoice = (key: ChoiceKey, value: string) => setFilters((f) => ({ ...f, [key]: value }));

  const choices = useMemo(() => {
    const categoryKeys = facets?.categories.length ? facets.categories.map((c) => c.key) : STATIC_CATEGORIES;
    return {
      category: categoryKeys.map((key) => {
        const label = categoryLabel(key);
        return { value: key, label: label ? tr.msg(label) : key };
      }),
      cost: (facets?.costClasses?.length ? facets.costClasses.map((c) => c.key) : COST_CLASSES).map((c) => ({
        value: c,
        label: tr.msg(costBadgeFor(c as AppCostClass).label),
      })),
      stepType: (facets?.stepTypes ?? []).map((s) => ({ value: s.key, label: s.key })),
      language: (facets?.languages ?? []).map((l) => ({ value: l.key, label: languageName(l.key, fm.locale) })),
    };
  }, [facets, tr, fm.locale]);

  const filtering = hasActiveFilters(filters);
  const clear = () => {
    setInput('');
    setFilters((f) => ({ ...DEFAULT_FILTERS, sort: f.sort }));
  };

  let body;
  if (isLoading || (!loaded && !error)) {
    body = (
      <div role="status" aria-label={t('loading')} aria-busy="true">
        <ul className={styles.grid}>
          {[0, 1, 2].map((i) => (
            <li key={i}>
              <Skeleton height={280} radius="xs" />
            </li>
          ))}
        </ul>
      </div>
    );
  } else if (items.length === 0 && error) {
    body = (
      <div className={styles.empty} role="alert">
        <Text fw={650}>{t('loadFailed')}</Text>
        <p className={styles.hint}>{t('loadFailedHint')}</p>
        <button type="button" className={styles.button} onClick={() => void mutate()}>
          {t('tryAgain')}
        </button>
      </div>
    );
  } else if (items.length === 0) {
    body = (
      <div className={styles.empty}>
        <Text fw={650}>{filtering ? t('emptyFiltered') : t('empty')}</Text>
        <p className={styles.hint}>{filtering ? t('emptyFilteredHint') : t('emptyHint')}</p>
        {filtering && (
          <button type="button" className={styles.button} onClick={clear}>
            {t('clearFilters')}
          </button>
        )}
      </div>
    );
  } else {
    body = (
      <>
        <ul className={styles.grid}>
          {items.map((r) => (
            <RecipeCardView key={r.id} recipe={r} />
          ))}
        </ul>
        {error !== undefined && error !== null && <p className={styles.error} role="alert">{t('loadMoreFailed')}</p>}
        {hasMore && (
          <div>
            <button type="button" className={styles.button} onClick={loadMore} disabled={isLoadingMore}>
              {isLoadingMore ? t('loadingMore') : t('loadMore')}
            </button>
          </div>
        )}
      </>
    );
  }

  return (
    <section className={styles.root} aria-labelledby={`${searchId}-title`}>
      <div>
        <h1 className={styles.pageTitle} id={`${searchId}-title`}>{t('title')}</h1>
        <p className={styles.hint}>{t('subtitle')}</p>
      </div>
      <div className={styles.toolbar}>
        <div className={styles.toolbarTop}>
          <TextInput
            className={styles.search}
            label={t('searchLabel')}
            placeholder={t('searchPlaceholder')}
            leftSection={<IconSearch size={16} aria-hidden />}
            value={input}
            onChange={(e) => setInput(e.currentTarget.value)}
            maxLength={100}
            styles={{ input: { minHeight: 44 } }}
          />
          <NativeSelect
            className={styles.sort}
            label={t('sortLabel')}
            value={chosen.sort}
            onChange={(e) => {
              const next = e.currentTarget.value;
              if (isSort(next)) setFilters((f) => ({ ...f, sort: next as MarketplaceSort }));
            }}
            data={SORTS.map((s) => ({ value: s.value, label: tr.msg(s.label) }))}
            styles={{ input: { minHeight: 44 } }}
          />
        </div>
        <ChipGroup legend={t('category')} choices={choices.category} value={filters.category} onChange={(v) => setChoice('category', v)} allLabel={t('all')} />
        <ChipGroup legend={t('cost')} choices={choices.cost} value={filters.cost} onChange={(v) => setChoice('cost', v)} allLabel={t('all')} />
        <ChipGroup legend={t('stepType')} choices={choices.stepType} value={filters.stepType} onChange={(v) => setChoice('stepType', v)} allLabel={t('all')} />
        <ChipGroup legend={t('language')} choices={choices.language} value={filters.language} onChange={(v) => setChoice('language', v)} allLabel={t('all')} />
        {filtering && (
          <div>
            <button type="button" className={styles.button} onClick={clear}>
              {t('clearFilters')}
            </button>
          </div>
        )}
      </div>
      <VisuallyHidden role="status">{loaded && !isLoading ? t('results', { count: items.length }) : ''}</VisuallyHidden>
      {body}
    </section>
  );
}
