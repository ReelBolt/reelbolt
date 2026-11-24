import { CheckoutPageBody } from "@/components/billing/CheckoutPageBody";
import { singleParam } from "@/lib/auth/config";

/**
 * Where the payment provider sends a buyer after a checkout.
 *
 * Stripe returns carry `?session_id=`: for those, `CheckoutConfirmation` asks the server to confirm
 * the session (`POST /api/v1/billing/checkout/confirm`) and shows the result, with the plan and
 * credits read from the billing summary. That server-side confirmation replaces the "cannot know"
 * stance below, but only for Stripe; a Paddle return (`_ptxn`) and a bare visit keep it unchanged.
 *
 * The Paddle flow does not take the redirect as proof of payment. Webhooks are authoritative on
 * this platform: a checkout redirect never grants credits or changes a subscription, and this page
 * cannot see whether the provider's notification has been processed yet (docs/billing.md,
 * "Webhooks are authoritative"). Claiming a purchase here would be a user-visible lie whenever the
 * notification is slow, the payment fails, or the charge is later refunded — so the page reports a
 * confirmation in progress, polls the billing summary for about 30 s (`CheckoutPaddleReturn`), and
 * says "Payment received" only once the summary itself shows the plan or the balance moved.
 *
 * It is the success URL of every checkout this app opens: the embedded one passes it as
 * `settings.successUrl` to `Paddle.Checkout.open` (`lib/paddle/sdk.ts`), and it is also the
 * "default payment link" a buyer reaches when the overlay is not available and the provider's own
 * hosted page is used instead. The provider appends its transaction id to it as `_ptxn`.
 *
 * Being reachable both ways is why the page treats `_ptxn` as optional: it shows the reference when
 * the provider put one there, and is still correct when it did not.
 */
export default async function CheckoutPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  // A repeated `_ptxn` is never legitimate, so anything that is not a single string counts as
  // absent — the same rule the auth pages apply to their tokens through `singleParam`. Absent is a
  // supported state, not an error: people bookmark this page or reach it directly.
  // Paddle appends `_ptxn`; Stripe's hosted checkout appends `session_id`. Either is the reference.
  const params = await searchParams;
  const paddleId = singleParam(params._ptxn);
  const sessionId = singleParam(params.session_id);
  const transactionId = paddleId || sessionId;
  const confirmSessionId = paddleId ? undefined : sessionId;

  return <CheckoutPageBody confirmSessionId={confirmSessionId} transactionId={transactionId} />;
}
