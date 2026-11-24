import type { Metadata } from 'next';
import { GuestReviewPage } from '@/components/review-guest/GuestReviewPage';

// The token in the path is the credential: keep the page out of search engines and never send it on as a referrer.
export const metadata: Metadata = {
  title: 'Review',
  robots: { index: false, follow: false, nocache: true },
  referrer: 'no-referrer',
};

export default async function GuestReviewRoute({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  return <GuestReviewPage token={token} />;
}
