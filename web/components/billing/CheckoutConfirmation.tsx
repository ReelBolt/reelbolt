"use client";

import { useEffect, useRef, useState } from "react";
import { Alert, Button, Card, Group, Loader, Stack, Text } from "@mantine/core";
import { IconCircleCheck, IconProgress } from "@tabler/icons-react";
import Link from "next/link";
import { apiFetch } from "@/lib/api/client";
import { PageHeader } from "@/components/shared/PageHeader";
import { useBillingSummary } from "@/lib/hooks/use-billing";
import { useT } from "@/i18n";
import { clearCheckoutSnapshot } from "./checkout-snapshot";
import { formatCredits } from "@/lib/utils/usage-labels";
import { subscriptionStatusLabel } from "./useBillingFormat";

export const CHECKOUT_CONFIRM_PATH = "/api/v1/billing/checkout/confirm";

/** Waits before each retry of a `pending` answer: 2 s, 4 s, 8 s, then the page gives up and explains. */
export const CONFIRM_RETRY_DELAYS_MS = [2000, 4000, 8000] as const;

interface ConfirmAnswer {
  status: "confirmed" | "pending" | "abandoned";
  kind: "subscription" | "topUp";
}

type Phase =
  | { name: "confirming" }
  | { name: "confirmed"; kind: ConfirmAnswer["kind"] }
  | { name: "pending"; exhausted: boolean }
  | { name: "abandoned" }
  | { name: "neutral" };

const sleep = (ms: number) =>
  new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * Asks the server to look the checkout session up and apply it. The answer only says whether it is
 * settled: the plan and the credits shown afterwards always come from the billing summary.
 * Any refusal (404, 400, 403, 409, 429, 503) or network failure is the neutral state, never an error.
 */
async function confirmOnce(sessionId: string): Promise<ConfirmAnswer | null> {
  try {
    const answer = await apiFetch<ConfirmAnswer>(CHECKOUT_CONFIRM_PATH, {
      method: "POST",
      body: JSON.stringify({ sessionId }),
    });
    return answer.status === "confirmed" ||
      answer.status === "pending" ||
      answer.status === "abandoned"
      ? answer
      : null;
  } catch {
    return null;
  }
}

type PendingState = "confirming" | "pending" | "neutral";

function PendingNotice({ state }: { state: PendingState }) {
  const t = useT("checkout.confirmation");
  return (
    <Alert
      icon={<IconProgress size={16} />}
      color="blue"
      title={
        state === "confirming" ? t("confirmingTitle") : t("notConfirmedTitle")
      }
      data-testid="checkout-pending-notice"
    >
      <Text
        size="sm"
        data-testid={
          state === "confirming" ? "checkout-still-confirming" : undefined
        }
      >
        {state === "confirming" ? t("pendingCopy.confirming") : state === "pending" ? t("pendingCopy.pending") : t("pendingCopy.neutral")}
      </Text>
    </Alert>
  );
}

/**
 * The checkout page's answer for a Stripe return (`?session_id=`): confirms the session on the
 * server once on mount, retries a still-pending one with back-off, and on success shows the plan and
 * credits read from the billing summary (server truth), never from the confirm response.
 */
export function CheckoutConfirmation({ sessionId }: { sessionId: string }) {
  const t = useT("checkout.confirmation");
  const [phase, setPhase] = useState<Phase>({ name: "confirming" });
  const started = useRef(false);
  const mounted = useRef(true);
  const { data: summary, mutate } = useBillingSummary();

  useEffect(() => {
    mounted.current = true;
    // Strict mode runs effects twice on mount; the ref survives the simulated remount, so the
    // confirmation is requested once.
    if (!started.current) {
      started.current = true;
      void (async () => {
        let answer = await confirmOnce(sessionId);
        for (
          let attempt = 0;
          answer?.status === "pending" &&
          attempt < CONFIRM_RETRY_DELAYS_MS.length;
          attempt++
        ) {
          if (!mounted.current) return;
          setPhase({ name: "pending", exhausted: false });
          await sleep(CONFIRM_RETRY_DELAYS_MS[attempt]);
          if (!mounted.current) return;
          answer = await confirmOnce(sessionId);
        }
        if (!mounted.current) return;
        // This return is answered by the server, not by the saved baseline; leaving it would let a
        // later bare visit wait on a purchase that is already settled (or was abandoned).
        if (answer?.status === "confirmed" || answer?.status === "abandoned") clearCheckoutSnapshot();
        if (answer?.status === "confirmed") {
          try {
            await mutate();
          } catch {
            // the summary shows what it can; the success card still stands
          }
          if (mounted.current)
            setPhase({ name: "confirmed", kind: answer.kind });
        } else if (answer?.status === "abandoned") {
          setPhase({ name: "abandoned" });
        } else if (answer?.status === "pending") {
          setPhase({ name: "pending", exhausted: true });
        } else {
          setPhase({ name: "neutral" });
        }
      })();
    }
    return () => {
      mounted.current = false;
    };
  }, [sessionId, mutate]);

  return (
    <>
      <PageHeader
        title={phase.name === "confirmed" ? t("titleConfirmed") : phase.name === "abandoned" ? t("titleAbandoned") : t("confirmingTitle")}
        breadcrumbs={[
          { label: t("breadcrumbDashboard"), href: "/dashboard" },
          { label: t("breadcrumbCheckout") },
        ]}
      />
      <PhaseBody phase={phase} summary={summary} />
    </>
  );
}

function PhaseBody({
  phase,
  summary,
}: {
  phase: Phase;
  summary: ReturnType<typeof useBillingSummary>["data"];
}) {
  const t = useT("checkout.confirmation");
  const root = useT();
  switch (phase.name) {
    case "confirming":
      return (
        <Card
          withBorder
          radius="lg"
          padding="lg"
          data-testid="checkout-confirming"
        >
          <Group gap="sm">
            <Loader size="sm" />
            <Text>{t("confirming")}</Text>
          </Group>
        </Card>
      );
    case "confirmed": {
      const subscription = summary?.subscription;
      const credits = summary?.credits.available;
      return (
        <Card
          withBorder
          radius="lg"
          padding="lg"
          data-testid="checkout-success-card"
        >
          <Stack gap="md">
            <Group gap="xs">
              <IconCircleCheck size={20} color="var(--mantine-color-green-6)" />
              <Text fw={600}>{t("success")}</Text>
            </Group>
            {subscription && (
              <Text size="sm" data-testid="checkout-plan">
                {t("plan", { plan: subscription.planName, status: subscriptionStatusLabel(root, subscription.status) })}
              </Text>
            )}
            {credits !== undefined && (
              <Text size="sm" data-testid="checkout-credits">
                {phase.kind === "topUp"
                  ? t("creditsAvailableAdded", { credits: formatCredits(credits) })
                  : t("creditsAvailable", { credits: formatCredits(credits) })}
              </Text>
            )}
          </Stack>
        </Card>
      );
    }
    case "pending":
      return (
        <PendingNotice state={phase.exhausted ? "pending" : "confirming"} />
      );
    case "abandoned":
      return (
        <Card
          withBorder
          radius="lg"
          padding="lg"
          data-testid="checkout-abandoned"
        >
          <Stack gap="md">
            <Text>{t("abandoned")}</Text>
            <Group>
              <Button component={Link} href="/settings/billing">
                {t("backToBilling")}
              </Button>
            </Group>
          </Stack>
        </Card>
      );
    case "neutral":
      return <PendingNotice state="neutral" />;
  }
}
