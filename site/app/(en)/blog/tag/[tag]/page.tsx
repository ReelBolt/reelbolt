import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { JsonLd } from '@/components/seo/JsonLd';
import { PostCard } from '@/components/blog/PostCard';
import { TagList } from '@/components/blog/TagList';
import { postsByTag, tagsWithPosts } from '@/lib/blog/posts';
import { RSS_ALTERNATE } from '@/lib/blog/rss';
import { isBlogTag, tagLabel, type BlogTag } from '@/lib/blog/types';
import { buildMetadata } from '@/lib/seo';
import { breadcrumbSchema } from '@/lib/structured-data';

// Only tags that have at least one post are built; any other tag is a 404 at build time.
export const dynamicParams = false;

export function generateStaticParams() {
  return tagsWithPosts().map((tag) => ({ tag }));
}

type Params = Promise<{ tag: string }>;

// Titles and descriptions from the copy deck (section 11). Keyed by BlogTag, so a new tag will not
// build until it has its own copy.
const TAG_META: Record<BlogTag, { title: string; description: string }> = {
  comparisons: {
    title: 'ReelBolt compared with other video tools',
    description:
      'Honest, sourced comparisons between ReelBolt and other AI video, promo video and editing tools: who each is for, how its plans work and where each is stronger.',
  },
  guides: {
    title: 'Guides to making videos with AI',
    description:
      'Practical guides for anyone with a story to tell: turning raw clips into a finished cut, planning a shoot, adding captions, and reading the credit estimate.',
  },
  product: {
    title: 'ReelBolt product news and updates',
    description:
      'What is new in ReelBolt: features that shipped, how they work for you, and what changed in the editor, the assistant, brand kit, sync and plans along the way.',
  },
};

export async function generateMetadata({ params }: { params: Params }): Promise<Metadata> {
  const { tag } = await params;
  if (!isBlogTag(tag)) notFound();
  const base = buildMetadata({
    title: TAG_META[tag].title,
    description: TAG_META[tag].description,
    path: `/blog/tag/${tag}`,
  });
  return { ...base, alternates: { ...base.alternates, types: RSS_ALTERNATE } };
}

export default async function BlogTagPage({ params }: { params: Params }) {
  const { tag } = await params;
  if (!isBlogTag(tag)) notFound();
  const posts = postsByTag(tag);
  const label = tagLabel(tag);

  return (
    <>
      <div className="pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
            {label}
          </h1>
        </Container>
      </div>

      <Section>
        <TagList tags={tagsWithPosts()} current={tag} />
        <div className="mt-10 grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-3">
          {posts.map((post) => (
            <PostCard key={post.slug} post={post} />
          ))}
        </div>
      </Section>

      <JsonLd
        data={breadcrumbSchema([
          { name: 'Home', path: '/' },
          { name: 'Blog', path: '/blog' },
          { name: label, path: `/blog/tag/${tag}` },
        ])}
      />
    </>
  );
}
