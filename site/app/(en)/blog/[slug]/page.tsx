import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { Section } from '@/components/layout/Section';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { JsonLd } from '@/components/seo/JsonLd';
import { ArticleHeader } from '@/components/blog/ArticleHeader';
import { ArticleFaq } from '@/components/blog/ArticleFaq';
import { Figure } from '@/components/blog/Figure';
import { PostCard } from '@/components/blog/PostCard';
import { Prose } from '@/components/blog/Prose';
import { audienceOfPost } from '@/lib/audiences/blog-posts';
import { allPosts, listedPosts, postBySlug } from '@/lib/blog/posts';
import type { BlogPost } from '@/lib/blog/types';
import { RSS_ALTERNATE } from '@/lib/blog/rss';
import { buildMetadata } from '@/lib/seo';
import { articleSchema, breadcrumbSchema, faqSchema } from '@/lib/structured-data';

// Only the slugs in the registry are built; any other path is a 404 at build time.
export const dynamicParams = false;

export function generateStaticParams() {
  return allPosts().map((post) => ({ slug: post.slug }));
}

type Params = Promise<{ slug: string }>;

export async function generateMetadata({ params }: { params: Params }): Promise<Metadata> {
  const { slug } = await params;
  const post = postBySlug(slug);
  if (!post) notFound();
  const base = buildMetadata({
    title: post.title,
    description: post.description,
    path: `/blog/${post.slug}`,
    type: 'article',
    publishedTime: post.date,
    modifiedTime: post.updated ?? post.date,
    tags: post.tags,
    noIndex: post.unlisted === true,
    image: {
      url: `/blog/${post.slug}/opengraph-image`,
      width: 1200,
      height: 630,
      alt: post.title,
    },
  });
  return { ...base, alternates: { ...base.alternates, types: RSS_ALTERNATE } };
}

/** Up to three listed posts for "More from the blog": posts sharing a tag first, then the newest. */
function relatedPosts(post: BlogPost, posts: BlogPost[]): BlogPost[] {
  const others = posts.filter((other) => other.slug !== post.slug);
  const shares = (other: BlogPost) => other.tags.some((tag) => post.tags.includes(tag));
  return [...others.filter(shares), ...others.filter((other) => !shares(other))].slice(0, 3);
}

export default async function ArticlePage({ params }: { params: Params }) {
  const { slug } = await params;
  const post = postBySlug(slug);
  if (!post) notFound();
  const { Body } = post;
  const related = relatedPosts(post, listedPosts());

  return (
    <>
      <ArticleHeader post={post} />

      <Section>
        {post.hero && (
          <Figure
            name={post.hero.src}
            alt={post.hero.alt}
            width={post.hero.width}
            height={post.hero.height}
            priority
          />
        )}
        <Prose>
          <Body />
        </Prose>
        {post.faqs && <ArticleFaq faqs={post.faqs} />}
      </Section>

      <Section tone="paper-2">
        <div className="mx-auto max-w-[40rem] bg-paper-2 p-6 sm:p-8">
          <h2 className="font-display text-2xl font-bold text-ink">
            Turn the clips you already shot into a finished video
          </h2>
          <p className="mt-4 font-sans text-[0.95rem] leading-relaxed text-ink-muted">
            Drop in your clips and say what the video is for. An AI video team makes the first cut,
            you change what you want, and every paid step shows its estimate first.
          </p>
          <div className="mt-6">
            {/* A post written for one audience (lib/audiences/blog-posts) sends that audience to
                signup with the link, so the welcome questions start on the matching answer. */}
            <PrimaryCta location="blog-article-cta" persona={audienceOfPost(post.slug)} />
          </div>
        </div>
      </Section>

      {related.length > 0 && (
        <Section>
          <h2 className="font-display text-2xl font-bold text-ink">
            More from the blog
          </h2>
          <div className="mt-8 grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-3">
            {related.map((other) => (
              <PostCard key={other.slug} post={other} heading="h3" />
            ))}
          </div>
        </Section>
      )}

      <JsonLd data={articleSchema(post)} />
      {post.faqs && (
        <JsonLd
          data={faqSchema(post.faqs.map((faq) => ({ question: faq.q, answer: faq.a })))}
        />
      )}
      <JsonLd
        data={breadcrumbSchema([
          { name: 'Home', path: '/' },
          { name: 'Blog', path: '/blog' },
          { name: post.title, path: `/blog/${post.slug}` },
        ])}
      />
    </>
  );
}
