import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import {
  CREDIT_EXPIRY_PERIOD,
  PAYMENT_PROVIDER_STATEMENT,
  REFUND_WINDOW,
  SUPPORT_EMAIL,
  TERMS_LAST_UPDATED,
} from '@/lib/legal-placeholders';

export const metadata = buildMetadata({
  title: 'Refund & Cancellation Policy',
  description: `How to cancel your ${siteConfig.name} subscription and how to ask for a refund.`,
  path: '/legal/refunds',
});

const h2 = 'mt-10 font-display text-xl font-bold text-ink first:mt-0';
const p = 'mt-4 font-sans text-ink-muted';
const link = 'text-accent-ink hover:text-accent';

export default function RefundsPage() {
  return (
    <>
      <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
        Last updated: {TERMS_LAST_UPDATED}
      </p>
      <h1 className="mt-2 font-display text-3xl font-bold text-ink sm:text-4xl">
        Refund &amp; Cancellation Policy
      </h1>
      <p className={p}>
        This page summarises Sections 5, 6, 7 and 8 of our{' '}
        <a className={link} href="/legal/terms#refunds">Terms &amp; Conditions</a>, which remain the
        binding text. If the two ever differ, the Terms apply.
      </p>

      <h2 className={h2}>1. Who sells to you</h2>
      <p className={p}>{PAYMENT_PROVIDER_STATEMENT}</p>

      <h2 className={h2}>2. How to cancel</h2>
      <p className={p}>
        You can view your plan and cancel at any time from your account settings. Paid subscriptions
        renew automatically unless you cancel before the renewal date. Cancellation takes effect at
        the end of the current billing period, so you keep access until then and are not charged
        again.
      </p>

      <h2 className={h2}>3. Credits</h2>
      <p className={p}>
        Credits have no cash value and expire {CREDIT_EXPIRY_PERIOD} after they are added to your
        account. If a run fails because of a problem on our side, the credits for the failed step are
        returned, and you can resume without paying again for the steps already done.
      </p>

      <h2 className={h2}>4. Money-back refund</h2>
      <p className={p}>
        If you are a new paying customer, you may ask for a full refund of your first subscription
        payment or of a credit top-up within {REFUND_WINDOW} of the payment. Write to{' '}
        <a href={`mailto:${SUPPORT_EMAIL}`} className={link}>{SUPPORT_EMAIL}</a> from the email
        address on your account and say which payment you mean. We answer refund requests promptly.
      </p>
      <p className={p}>
        The refund is the full amount paid, with no deduction for credits you have already used. The
        credits granted by that payment are withdrawn.
      </p>
      <p className={p}>
        One refund is available per customer. We may refuse repeat refund requests in case of abuse.
        This promise is a contractual commitment on top of, and does not limit, any statutory right
        you have.
      </p>

      <h2 className={h2}>5. Right of withdrawal for EU consumers</h2>
      <p className={p}>
        If you are a consumer in the European Union, you have a 14-day right of withdrawal for
        digital services. Because the Service is delivered digitally, that right is lost once you
        expressly request immediate performance and acknowledge that you lose the right of
        withdrawal when performance begins; the acknowledgement you are asked to give at sign-up or
        checkout is where that request and acknowledgement are made.
      </p>

      <h2 className={h2}>6. Refunds handled by the payment provider</h2>
      <p className={p}>
        Because Stripe is the seller of record under Stripe Managed Payments, Stripe may also process
        refunds under its own rules, and your purchase is subject to its buyer terms. Taxes are
        collected at checkout as part of that purchase.
      </p>
    </>
  );
}
