import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { JsonLd } from '@/components/seo/JsonLd';
import { PostCard, PostLeadCard } from '@/components/blog/PostCard';
import { TagList } from '@/components/blog/TagList';
import { listedPosts, tagsWithPosts } from '@/lib/blog/posts';
import { RSS_ALTERNATE } from '@/lib/blog/rss';
import { buildMetadata } from '@/lib/seo';
import { breadcrumbSchema } from '@/lib/structured-data';

const base = buildMetadata({
  title: 'Blog: video guides and comparisons',
  description:
    'Guides to turning the clips you already shot into finished videos, ReelBolt product news, and sourced comparisons with other video tools.',
  path: '/blog',
});

export const metadata = {
  ...base,
  alternates: { ...base.alternates, types: RSS_ALTERNATE },
};

/** The roundup for people who do not edit is the pillar post, so the index features it above the grid. */
const LEAD_SLUG = 'ai-video-editors-for-people-who-do-not-edit-2026';

export default function BlogIndexPage() {
  const listed = listedPosts();
  const lead = listed.find((post) => post.slug === LEAD_SLUG);
  const posts = lead ? listed.filter((post) => post.slug !== LEAD_SLUG) : listed;
  return (
    <>
      <div className="pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
            Blog
          </h1>
          <p className="mt-5 max-w-2xl font-sans text-lg leading-relaxed text-ink-2">
            Guides to making videos from the clips you already have, ReelBolt news, and sourced comparisons with other video tools.
          </p>
        </Container>
      </div>

      <Section>
        <TagList tags={tagsWithPosts()} allActive />
        {listed.length === 0 ? (
          <p className="mt-10 font-sans text-ink-muted">No articles yet.</p>
        ) : (
          <>
            {lead && (
              <div className="mt-10">
                <PostLeadCard post={lead} />
              </div>
            )}
            {posts.length > 0 && (
              <div className="mt-10 grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-3">
                {posts.map((post) => (
                  <PostCard key={post.slug} post={post} />
                ))}
              </div>
            )}
          </>
        )}
      </Section>

      <JsonLd
        data={breadcrumbSchema([
          { name: 'Home', path: '/' },
          { name: 'Blog', path: '/blog' },
        ])}
      />
    </>
  );
}
