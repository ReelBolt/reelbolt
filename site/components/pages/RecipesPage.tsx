import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { localizePath } from '@/i18n/config';
import { Container } from '@/components/layout/Container';
import { JsonLd } from '@/components/seo/JsonLd';
import { PageMessages } from '@/components/layout/PageMessages';
import { RecipeGallery } from '@/components/recipes/RecipeGallery';
import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import { breadcrumbSchema } from '@/lib/structured-data';
import { fetchRecipeList } from '@/lib/recipes/server';
import { recipeGallerySchema } from '@/lib/recipes/schema';

// The first page of the gallery is read from the API when the page is built (static export) or rendered
// (container, cached 60 s). With no REELBOLT_API_URL, or an API that errors, this is an empty list and the
// page still builds; the gallery then fills itself from /api/recipes in the browser.
const loadInitial = () => fetchRecipeList(process.env.REELBOLT_API_URL, { limit: '24', sort: 'new' });

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('recipes', locale as Locale);
  const initial = await loadInitial();
  return buildMetadata({
    locale,
    title: t('seo.title'),
    description: t('seo.description'),
    path: '/recipes',
    // A gallery with nothing in it yet is not worth indexing.
    noIndex: initial.items.length === 0,
  });
}

async function RecipesPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = await getT('recipes', locale as Locale);
  const initial = await loadInitial();
  return (
    <>
      <div className="pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
            {t('page.title')}
          </h1>
          <p className="mt-6 max-w-3xl text-lg text-ink-muted">{t('page.intro')}</p>
        </Container>
      </div>
      <div className="py-10 sm:py-14">
        <Container>
          <RecipeGallery initial={initial} />
        </Container>
      </div>
      <JsonLd
        data={recipeGallerySchema(initial.items, {
          origin: siteConfig.url,
          name: t('page.title'),
          description: t('seo.description'),
          pathFor: (path) => localizePath(locale, path),
        })}
      />
      <JsonLd
        data={breadcrumbSchema(
          [
            { name: t('breadcrumb.home'), path: '/' },
            { name: t('breadcrumb.recipes'), path: '/recipes' },
          ],
          locale,
        )}
      />
    </>
  );
}

export function RecipesPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={['recipes']}>
      <RecipesPageBody locale={locale} />
    </PageMessages>
  );
}
