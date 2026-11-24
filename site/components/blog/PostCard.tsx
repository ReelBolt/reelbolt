import { Link } from '@/i18n/link';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { tagLabel, type BlogPost } from '@/lib/blog/types';
import { formatPostDate } from '@/lib/blog/format';

/** The featured post on the index: full width, with room for the description. */
export function PostLeadCard({ post }: { post: BlogPost }) {
  return (
    <article className="flex flex-col bg-paper-2 p-6 transition-colors sm:p-8 lg:p-10">
      <Eyebrow>{post.tags.map(tagLabel).join(' · ')}</Eyebrow>
      <h2 className="mt-4 font-display text-2xl font-bold leading-tight text-ink sm:text-3xl">
        <Link href={`/blog/${post.slug}`} className="hover:text-accent-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong">
          {post.title}
        </Link>
      </h2>
      <p className="mt-4 max-w-[40rem] font-sans text-base leading-relaxed text-ink-muted">{post.description}</p>
      <p className="mt-auto pt-6 font-mono text-[11px] uppercase tracking-eyebrow text-ink-faint">
        <time dateTime={post.date}>{formatPostDate(post.date)}</time> · {post.readingMinutes} min read
      </p>
    </article>
  );
}

/** One post in a grid or list. `heading` picks the level so the page outline stays correct. */
export function PostCard({ post, heading = 'h2' }: { post: BlogPost; heading?: 'h2' | 'h3' }) {
  const Heading = heading;
  return (
    <article className="flex h-full flex-col bg-paper-2 p-6 transition-colors">
      <Eyebrow>{post.tags.map(tagLabel).join(' · ')}</Eyebrow>
      <Heading className="mt-4 font-display text-xl font-bold leading-tight text-ink">
        <Link href={`/blog/${post.slug}`} className="hover:text-accent-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong">
          {post.title}
        </Link>
      </Heading>
      <p className="mt-3 font-sans text-[0.95rem] leading-relaxed text-ink-muted">{post.description}</p>
      <p className="mt-auto pt-5 font-mono text-[11px] uppercase tracking-eyebrow text-ink-faint">
        <time dateTime={post.date}>{formatPostDate(post.date)}</time> · {post.readingMinutes} min read
      </p>
    </article>
  );
}
