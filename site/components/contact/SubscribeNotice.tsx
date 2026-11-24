'use client';

import { useT } from '@/i18n';
import { useSearchParams } from 'next/navigation';

// Shown on the thank-you page only when the visitor ticked the newsletter box: the subscription
// starts once they click the link in the confirmation email.
export function SubscribeNotice({ className }: { className?: string }) {
  const t = useT('contact');
  const searchParams = useSearchParams();
  if (searchParams.get('subscribe') !== 'check') return null;
  return <p className={className}>{t('subscribeNotice')}</p>;
}
