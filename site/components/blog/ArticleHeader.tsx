import { Link } from '@/i18n/link';
import { useT } from '@/i18n';
import { Container } from '@/components/layout/Container';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { tagLabel, type BlogPost } from '@/lib/blog/types';
import { formatPostDate } from '@/lib/blog/format';

/** Title block at the top of an article: tags, H1, standfirst, and dates with reading time. */
export function ArticleHeader({ post }: { post: BlogPost }) {
  const t = useT('blog.article');
  return (
    <header className="pt-16 pb-10 sm:pt-20 lg:pt-24">
      <Container>
        <div className="mx-auto max-w-[40rem]">
          <nav aria-label={t('breadcrumb')} className="font-mono text-[11px] uppercase tracking-eyebrow text-ink-faint">
            <ol className="flex flex-wrap items-center gap-x-2">
              <li>
                <Link href="/blog" className="hover:text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong">{t('blog')}</Link>
              </li>
              {post.tags[0] && (
                <>
                  <li aria-hidden="true">/</li>
                  <li>
                    <Link href={`/blog/tag/${post.tags[0]}`} className="hover:text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong">{tagLabel(post.tags[0])}</Link>
                  </li>
                </>
              )}
            </ol>
          </nav>
          <Eyebrow>{post.tags.map(tagLabel).join(' · ')}</Eyebrow>
          <h1 className="mt-4 text-balance font-display text-[clamp(1.875rem,4.5vw,3rem)] font-bold leading-[1.02] text-ink">
            {post.title}
          </h1>
          <p className="mt-5 font-sans text-lg leading-relaxed text-ink-2">{post.description}</p>
          <p className="mt-6 font-mono text-[11px] uppercase tracking-eyebrow text-ink-faint">
            {t('published')}{' '}
            <time dateTime={post.date}>{formatPostDate(post.date)}</time>
            {post.updated && (
              <>
                {' · '}{t('updated')} <time dateTime={post.updated}>{formatPostDate(post.updated)}</time>
              </>
            )}
            {' · '}
            {t('readingTime', { minutes: post.readingMinutes })}
          </p>
        </div>
      </Container>
    </header>
  );
}
