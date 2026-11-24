import { ImageResponse } from 'next/og';
import { notFound } from 'next/navigation';
import { allPosts, postBySlug } from '@/lib/blog/posts';
import { tagLabel } from '@/lib/blog/types';

export const size = { width: 1200, height: 630 };
export const contentType = 'image/png';
export const dynamic = 'force-static';

export function generateStaticParams() {
  return allPosts().map((post) => ({ slug: post.slug }));
}

// Same look as the site-wide card in app/opengraph-image.tsx: the violet gradient, the R-with-bolt
// mark and the wordmark. The post's title and tags sit where the tagline sits there.
export default async function PostOpengraphImage({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params;
  const post = postBySlug(slug);
  if (!post) notFound();

  return new ImageResponse(
    (
      <div
        style={{
          width: '100%',
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'flex-start',
          justifyContent: 'center',
          background: 'linear-gradient(135deg, #2E1065 0%, #6D28D9 55%, #A855F7 100%)',
          padding: '80px',
          fontFamily: 'sans-serif',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: '20px' }}>
          <svg width="64" height="64" viewBox="0 0 120 120" fill="none">
            <rect width="120" height="120" rx="28" fill="#2E1065" />
            <path d="M36 96V24H62a21 21 0 0 1 0 42H36" stroke="#C4B5FD" strokeWidth="14" strokeLinejoin="round" />
            <path d="M68 12 L28 68 H54 L46 108 L92 48 H64 Z" fill="#A855F7" stroke="#2E1065" strokeWidth="5" strokeLinejoin="round" transform="translate(66 80) scale(-.68 .68) translate(-60 -60)" />
          </svg>
          <div style={{ display: 'flex', fontSize: 36, fontWeight: 700, color: 'white' }}>ReelBolt blog</div>
        </div>

        <div
          style={{
            display: 'flex',
            marginTop: '48px',
            fontSize: 22,
            fontWeight: 700,
            letterSpacing: '0.18em',
            textTransform: 'uppercase',
            color: '#C4B5FD',
          }}
        >
          {post.tags.map(tagLabel).join(' · ')}
        </div>

        <div
          style={{
            display: 'flex',
            marginTop: '20px',
            fontSize: 64,
            fontWeight: 700,
            lineHeight: 1.1,
            color: 'white',
            maxWidth: '1000px',
          }}
        >
          {post.title}
        </div>
      </div>
    ),
    size,
  );
}
