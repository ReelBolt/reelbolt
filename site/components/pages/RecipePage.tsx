import type { Metadata } from 'next';
import { cache } from 'react';
import { getT, setRequestLocale } from '@/i18n/server';
import { localizePath, prefixedLocales, type Locale } from '@/i18n/config';
import { JsonLd } from '@/components/seo/JsonLd';
import { PageMessages } from '@/components/layout/PageMessages';
import { RecipeDetailBody } from '@/components/recipes/RecipeDetailBody';
import { RecipeDetailLoader, RecipeUnavailable } from '@/components/recipes/RecipeDetailLoader';
import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import { breadcrumbSchema } from '@/lib/structured-data';
import { fetchAllListedRecipes, fetchRecipeDetail } from '@/lib/recipes/server';
import { recipeDetailSchema } from '@/lib/recipes/schema';
import { PLACEHOLDER_ID } from '@/lib/recipes/shell';


const apiUrl = () => process.env.REELBOLT_API_URL;
const load = cache((id: string) => fetchRecipeDetail(apiUrl(), id));

/**
 * The ids to prerender. In the static export this is every recipe listed at build time (cap 500) plus the
 * placeholder shell; a build with no API, or an API that errors, yields only the shell and never fails.
 */
export async function recipeIdsForBuild(): Promise<string[]> {
  const listed = (await fetchAllListedRecipes(apiUrl())).map((card) => card.id);
  return process.env.STATIC_EXPORT === '1' ? [...listed, PLACEHOLDER_ID] : listed;
}

export async function recipeStaticParams(): Promise<{ id: string }[]> {
  return (await recipeIdsForBuild()).map((id) => ({ id }));
}

export async function recipeLocaleStaticParams(): Promise<{ locale: string; id: string }[]> {
  const ids = await recipeIdsForBuild();
  return prefixedLocales.flatMap((locale) => ids.map((id) => ({ locale, id })));
}

const firstChars = (value: string, max: number) => Array.from(value.replace(/\s+/g, ' ').trim()).slice(0, max).join('');

export async function metadataFor(locale: string, id: string): Promise<Metadata> {
  const t = await getT('recipes', locale as Locale);
  const recipe = id === PLACEHOLDER_ID ? null : await load(id);
  if (!recipe) {
    return buildMetadata({
      locale,
      title: t('seo.unavailableTitle'),
      description: t('seo.unavailableDescription'),
      path: '/recipes',
      noIndex: true,
    });
  }
  return buildMetadata({
    locale,
    title: firstChars(recipe.name, 70) || t('seo.detailTitleFallback'),
    description:
      firstChars(recipe.description, 160) || t('seo.detailDescriptionFallback', { steps: recipe.stepCount }),
    path: `/recipes/${recipe.id}`,
  });
}

async function RecipePageBody({ locale, id }: { locale: string; id: string }) {
  setRequestLocale(locale);
  if (id === PLACEHOLDER_ID) return <RecipeDetailLoader />;
  const recipe = await load(id);
  if (!recipe) return <RecipeUnavailable />;
  const t = await getT('recipes', locale as Locale);
  return (
    <>
      <RecipeDetailBody recipe={recipe} />
      <JsonLd
        data={recipeDetailSchema(recipe, {
          origin: siteConfig.url,
          pathFor: (path) => localizePath(locale, path),
        })}
      />
      <JsonLd
        data={breadcrumbSchema(
          [
            { name: t('breadcrumb.home'), path: '/' },
            { name: t('breadcrumb.recipes'), path: '/recipes' },
            { name: recipe.name, path: `/recipes/${recipe.id}` },
          ],
          locale,
        )}
      />
    </>
  );
}

export function RecipePage({ locale, id }: { locale: string; id: string }) {
  return (
    <PageMessages locale={locale} extra={['recipes']}>
      <RecipePageBody locale={locale} id={id} />
    </PageMessages>
  );
}
