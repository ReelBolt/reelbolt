import { Link } from '@/i18n/link';
import { useT } from '@/i18n';
import { categoryName } from './labels';
import { formatSeconds, type RecipeCard as Card } from '@/lib/recipes/types';
import { PreviewVideo } from './PreviewVideo';

/** One gallery card. Every user-written field is rendered as a React text node, never as markup. */
export function RecipeCard({ recipe }: { recipe: Card }) {
  const t = useT('recipes');
  return (
    <li className="flex flex-col bg-paper-2">
      <PreviewVideo id={recipe.id} name={recipe.name} hasPreview={recipe.previewSeconds > 0} mode="hover" />
      <div className="flex flex-1 flex-col gap-3 p-5">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
          {recipe.category !== '' && <span>{categoryName(t, recipe.category)}</span>}
          <span>{t('card.steps', { count: recipe.stepCount })}</span>
          {recipe.previewSeconds > 0 && <span>{formatSeconds(recipe.previewSeconds)}</span>}
        </div>
        <h3 className="font-display text-lg font-bold leading-tight text-ink">
          <Link
            href={`/recipes/${recipe.id}`}
            prefetch={false}
            className="break-words hover:text-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
          >
            {recipe.name}
          </Link>
        </h3>
        {recipe.description !== '' && (
          <p className="line-clamp-3 whitespace-pre-line break-words text-sm text-ink-muted">{recipe.description}</p>
        )}
        <p className="mt-auto flex flex-wrap items-center gap-x-3 gap-y-1 pt-2 text-xs text-ink-muted">
          {recipe.authorLabel !== '' && <span className="break-words">{t('card.by', { author: recipe.authorLabel })}</span>}
          {recipe.copyCount > 0 && <span>{t('card.copies', { count: recipe.copyCount })}</span>}
        </p>
      </div>
    </li>
  );
}
