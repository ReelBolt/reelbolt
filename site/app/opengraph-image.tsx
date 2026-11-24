import { OG_SIZE, renderOgCard } from '@/lib/og-card';
import { seoStrings } from '@/lib/seo-strings';

export const size = OG_SIZE;
export const contentType = 'image/png';
export const alt = seoStrings('en').ogAlt;

export const dynamic = 'force-static';

export default function OpengraphImage() {
  return renderOgCard('en');
}
