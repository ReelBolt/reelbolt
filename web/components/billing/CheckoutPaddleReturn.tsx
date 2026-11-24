"use client";

import { useEffect, useRef, useState } from "react";
import { Alert, Card, Stack, Text } from "@mantine/core";
import { IconCircleCheck, IconProgress } from "@tabler/icons-react";
import { useT } from "@/i18n";
import { useBillingSummary } from "@/lib/hooks/use-billing";
import type { BillingSummary } from "@/lib/types/billing";
import { clearCheckoutSnapshot, purchaseVisible, readCheckoutSnapshot, type CheckoutSnapshot } from "./checkout-snapshot";
import { formatCredits } from "@/lib/utils/usage-labels";

/** How often the billing summary is read while waiting, and how long before the page stops asking. */
export const PADDLE_POLL_MS = 3000;
export const PADDLE_GIVE_UP_MS = 30000;

/**
 * The checkout return for a Paddle `_ptxn`. A redirect proves nothing (webhooks are authoritative),
 * so this compares the billing summary, every few seconds for about 30 s, with the snapshot the app
 * saved when it opened the checkout (plan, balance, intended plan or pack). Only when the summary shows
 * that purchase does it say so; with no snapshot it claims nothing; if it does not, it says calmly
 * that the payment is still being processed and that nothing needs doing.
 */
export function CheckoutPaddleReturn() {
  const t = useT("checkout.paddleReturn");
  const { data, mutate } = useBillingSummary();
  const [phase, setPhase] = useState<"neutral" | "waiting" | "received" | "slow">("waiting");
  const baseline = useRef<CheckoutSnapshot | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    let cancelled = false;
    const startedAt = Date.now();

    async function read(): Promise<BillingSummary | undefined> {
      try {
        return await mutate();
      } catch {
        return undefined;
      }
    }

    void (async () => {
      // The baseline is what the app saved when it opened the checkout. Without one (a bare visit,
      // a bookmark, another browser) there is nothing to compare, so the page claims nothing.
      const saved = readCheckoutSnapshot();
      if (!saved) {
        setPhase("neutral");
        return;
      }
      baseline.current = saved;
      while (!cancelled) {
        const latest = await read();
        if (cancelled) return;
        if (latest && baseline.current && purchaseVisible(baseline.current, latest)) {
          clearCheckoutSnapshot();
          setPhase("received");
          return;
        }
        if (Date.now() - startedAt >= PADDLE_GIVE_UP_MS) {
          clearCheckoutSnapshot();
          setPhase("slow");
          return;
        }
        await new Promise<void>((resolve) => setTimeout(resolve, PADDLE_POLL_MS));
      }
    })();

    return () => {
      cancelled = true;
      // Strict mode re-runs this effect; allow the second run to start the poll again.
      started.current = false;
    };
  }, [mutate]);

  if (phase === "received") {
    return (
      <Card withBorder radius="lg" padding="lg" data-testid="checkout-success-card" role="status">
        <Stack gap="xs">
          <Text fw={600}>
            <IconCircleCheck size={18} style={{ verticalAlign: "text-bottom" }} aria-hidden="true" /> {t("received")}
          </Text>
          {data?.subscription && (
            <Text size="sm" data-testid="checkout-plan">
              {t("plan", { plan: data.subscription.planName, status: data.subscription.status })}
            </Text>
          )}
          {data && (
            <Text size="sm" data-testid="checkout-credits">
              {t("credits", { credits: formatCredits(data.credits.available) })}
            </Text>
          )}
        </Stack>
      </Card>
    );
  }

  return (
    <Alert
      icon={<IconProgress size={16} />}
      color="blue"
      title={phase === "slow" ? t("titleSlow") : phase === "neutral" ? t("titleNeutral") : t("titleWaiting")}
      data-testid="checkout-pending-notice"
      role="status"
    >
      <Text size="sm">
        {phase === "neutral" ? t("neutral") : phase === "waiting" ? t("waiting") : t("slow")}
      </Text>
    </Alert>
  );
}
