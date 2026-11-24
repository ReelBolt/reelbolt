"use client";

import { Card, Stack, Text } from "@mantine/core";
import { PageHeader } from "@/components/shared/PageHeader";
import { useT } from "@/i18n";
import { CheckoutActions } from "./CheckoutActions";
import { CheckoutConfirmation } from "./CheckoutConfirmation";
import { CheckoutPaddleReturn } from "./CheckoutPaddleReturn";
import { TransactionReference } from "./TransactionReference";

/**
 * The checkout page's body. A client component because the catalogue hooks are not available in an
 * async server component; the page itself only reads the search params.
 */
export function CheckoutPageBody({
  confirmSessionId,
  transactionId,
}: {
  confirmSessionId: string | undefined;
  transactionId: string | undefined;
}) {
  const t = useT("checkout.page");
  return (
    <>
      {!confirmSessionId && (
        <PageHeader
          title={t("title")}
          breadcrumbs={[
            { label: t("breadcrumbDashboard"), href: "/dashboard" },
            { label: t("breadcrumbCheckout") },
          ]}
        />
      )}

      <Stack gap="lg" maw={720}>
        {confirmSessionId ? (
          <CheckoutConfirmation sessionId={confirmSessionId} />
        ) : (
          <CheckoutPaddleReturn />
        )}

        <Card
          withBorder
          radius="lg"
          padding="lg"
          data-testid="checkout-reference-card"
        >
          <Stack gap="md">
            <div>
              <Text fw={600}>{t("referenceTitle")}</Text>
              <Text size="sm" c="dimmed">
                {transactionId
                  ? t("referenceQuote")
                  : t("referenceNone")}
              </Text>
            </div>

            {transactionId && <TransactionReference value={transactionId} />}
          </Stack>
        </Card>

        <Card
          withBorder
          radius="lg"
          padding="lg"
          data-testid="checkout-next-steps-card"
        >
          <Stack gap="md">
            <div>
              <Text fw={600}>{t("nextTitle")}</Text>
              <Text size="sm" c="dimmed">
                {confirmSessionId
                  ? t("nextConfirmed")
                  : t("nextPending")}
              </Text>
            </div>

            <CheckoutActions />
          </Stack>
        </Card>
      </Stack>
    </>
  );
}
