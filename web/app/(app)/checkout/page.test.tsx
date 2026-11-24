// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { StrictMode } from "react";
import { SWRConfig } from "swr";
import { MantineProvider } from "@mantine/core";
import CheckoutPage from "./page";

vi.stubGlobal(
  "ResizeObserver",
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);
Element.prototype.scrollIntoView = vi.fn();

const TRANSACTION_ID = "txn_01m4f0t4pwhjqjt09z5cfchz3m";

const renderPage = async (
  params: Record<string, string | string[] | undefined> = {},
) => {
  const page = await CheckoutPage({ searchParams: Promise.resolve(params) });
  return render(
    <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
      <MantineProvider>{page}</MantineProvider>
    </SWRConfig>,
  );
};

const SUMMARY = {
  billingProvider: "stripe",
  subscription: { planKey: "pro", planName: "Pro", status: "active" },
  credits: { balance: 500, reserved: 0, available: 480 },
};

type Answer = { status: number; body: unknown };
const json = (a: Answer) =>
  new Response(JSON.stringify(a.body), {
    status: a.status,
    headers: { "Content-Type": "application/json" },
  });

/** Stubs fetch: confirm answers come from the queue (the last repeats); the summary is counted. */
function stubFetch(confirmAnswers: Answer[]) {
  let confirmCalls = 0;
  let summaryCalls = 0;
  const fetchMock = vi.fn(async (url: string) => {
    if (url.includes("/billing/checkout/confirm")) {
      const a =
        confirmAnswers[Math.min(confirmCalls, confirmAnswers.length - 1)];
      confirmCalls++;
      return json(a);
    }
    summaryCalls++;
    return json({ status: 200, body: SUMMARY });
  });
  vi.stubGlobal("fetch", fetchMock);
  return { confirmCalls: () => confirmCalls, summaryCalls: () => summaryCalls };
}

const confirmed = (kind = "subscription"): Answer => ({
  status: 200,
  body: { status: "confirmed", kind },
});
const pending: Answer = {
  status: 200,
  body: { status: "pending", kind: "subscription" },
};

const flush = () =>
  act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });

describe("CheckoutPage", () => {
  beforeEach(() => {
    global.matchMedia = ((q: string) => ({
      matches: false,
      media: q,
      onchange: null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => true,
    })) as unknown as typeof matchMedia;
  });

  afterEach(() => {
    cleanup();
    window.sessionStorage.clear();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.stubGlobal(
      "ResizeObserver",
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
  });

  it("renders the transaction id the provider put on the redirect", async () => {
    await renderPage({ _ptxn: TRANSACTION_ID });

    expect(screen.getByTestId("checkout-transaction-id").textContent).toBe(
      TRANSACTION_ID,
    );
  });

  it("renders Stripe's session_id as the transaction reference", async () => {
    stubFetch([pending]);
    await renderPage({ session_id: "cs_test_a1B2c3" });

    expect(screen.getByTestId("checkout-transaction-id").textContent).toBe(
      "cs_test_a1B2c3",
    );
  });

  it("renders with no query string at all, and says why the reference is missing", async () => {
    await renderPage();

    expect(
      screen.getByRole("heading", { name: "Confirming your payment" }),
    ).toBeTruthy();
    expect(screen.queryByTestId("checkout-transaction-id")).toBeNull();
    expect(screen.getByText(/carried no transaction reference/i)).toBeTruthy();
  });

  it("treats a repeated _ptxn as absent instead of picking one of them", async () => {
    await renderPage({ _ptxn: [TRANSACTION_ID, "txn_somethingelse"] });

    expect(screen.queryByTestId("checkout-transaction-id")).toBeNull();
    expect(screen.getByText(/carried no transaction reference/i)).toBeTruthy();
  });

  // The whole point of the page: webhooks are authoritative (docs/billing.md), so a checkout
  // redirect must never be reported as a completed purchase.
  it("never claims the payment succeeded", async () => {
    await renderPage({ _ptxn: TRANSACTION_ID });

    const text = (document.body.textContent ?? "").toLowerCase();
    for (const claim of [
      "success",
      "thank you for your purchase",
      "you have been charged",
      "payment received",
      "your payment went through",
      "purchase complete",
      "you are now on",
    ]) {
      expect(text).not.toContain(claim);
    }

    expect(screen.getByTestId("checkout-pending-notice").textContent).toMatch(
      /confirmed/i,
    );
    expect(text).toContain("notification");
  });

  it("offers the way onward: the billing page and the dashboard", async () => {
    await renderPage({ _ptxn: TRANSACTION_ID });

    expect(
      screen
        .getByRole("link", { name: "Check your plan and balance" })
        .getAttribute("href"),
    ).toBe("/settings/billing");
    expect(
      screen
        .getByRole("link", { name: "Go to the dashboard" })
        .getAttribute("href"),
    ).toBe("/dashboard");
  });

  it("Paddle _ptxn makes no confirm call and waits calmly for the notification", async () => {
    const f = stubFetch([confirmed()]);
    await renderPage({ _ptxn: TRANSACTION_ID });
    await act(async () => {});

    expect(f.confirmCalls()).toBe(0);
    expect(screen.getByTestId("checkout-pending-notice").textContent).toMatch(
      /cannot tell/i,
    );
  });

  describe("Paddle return polling", () => {
    const PAID = {
      billingProvider: "paddle",
      subscription: { planKey: "creator", planName: "Creator", status: "Active", periodEnd: "2027-01-01T00:00:00Z" },
      plans: [{ key: "creator", current: true }],
      credits: { balance: 1000, reserved: 0, available: 1000 },
    };
    const TRIAL = {
      billingProvider: "paddle",
      subscription: { planKey: "trial", planName: "Trial", status: "active", periodEnd: null },
      plans: [],
      credits: { balance: 100, reserved: 0, available: 100 },
    };

    const seed = (target: unknown = { kind: "plan", planKey: "creator" }) =>
      window.sessionStorage.setItem(
        "reelbolt.checkout-snapshot",
        JSON.stringify({ planKey: "trial", balance: 100, livePaid: false, at: Date.now(), target }),
      );

    function stubSummaries(bodies: unknown[]) {
      let calls = 0;
      vi.stubGlobal(
        "fetch",
        vi.fn(async () => {
          const body = bodies[Math.min(calls, bodies.length - 1)];
          calls++;
          return json({ status: 200, body });
        }),
      );
    }

    it("says the payment was received once the summary shows the new plan", async () => {
      vi.useFakeTimers();
      seed();
      stubSummaries([TRIAL, TRIAL, PAID]);
      await renderPage({ _ptxn: TRANSACTION_ID });
      await flush();
      expect(screen.queryByTestId("checkout-success-card")).toBeNull();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(7000);
      });
      expect(screen.getByTestId("checkout-success-card").textContent).toContain("Payment received");
      expect(screen.getByTestId("checkout-plan").textContent).toContain("Creator");
    });

    it("a visit with no saved snapshot claims nothing, even if the summary looks paid", async () => {
      vi.useFakeTimers();
      window.sessionStorage.clear();
      stubSummaries([PAID]);
      await renderPage({ _ptxn: TRANSACTION_ID });
      await flush();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(10000);
      });
      expect(screen.queryByTestId("checkout-success-card")).toBeNull();
      expect(screen.getByTestId("checkout-pending-notice").textContent).toMatch(/cannot tell/i);
    });

    it("a different plan than the one bought does not count as the purchase", async () => {
      vi.useFakeTimers();
      seed({ kind: "plan", planKey: "pro" });
      stubSummaries([PAID]);
      await renderPage({});
      await flush();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(10000);
      });
      expect(screen.queryByTestId("checkout-success-card")).toBeNull();
    });

    it("clears the snapshot after success", async () => {
      vi.useFakeTimers();
      seed();
      stubSummaries([PAID]);
      await renderPage({});
      await flush();
      expect(screen.getByTestId("checkout-success-card")).toBeTruthy();
      expect(window.sessionStorage.getItem("reelbolt.checkout-snapshot")).toBeNull();
    });

    it("after about 30 seconds without a change, says it can't see a change yet and claims nothing", async () => {
      vi.useFakeTimers();
      seed();
      stubSummaries([TRIAL]);
      await renderPage({ _ptxn: TRANSACTION_ID });
      await flush();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(35000);
      });
      expect(screen.queryByTestId("checkout-success-card")).toBeNull();
      expect(screen.getByTestId("checkout-pending-notice").textContent).toMatch(/can.t see a change yet/i);
      expect((document.body.textContent ?? "").toLowerCase()).not.toContain("payment received");
    });
  });

  it("makes no confirm call without a session_id", async () => {
    const f = stubFetch([confirmed()]);
    await renderPage();
    await act(async () => {});

    expect(f.confirmCalls()).toBe(0);
  });

  describe("Stripe session_id", () => {
    beforeEach(() => vi.useFakeTimers());

    const renderStripe = async (strict = false) => {
      const page = await CheckoutPage({
        searchParams: Promise.resolve({ session_id: "cs_test_1" }),
      });
      const tree = (
        <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>
          <MantineProvider>{page}</MantineProvider>
        </SWRConfig>
      );
      await act(async () => {
        render(strict ? <StrictMode>{tree}</StrictMode> : tree);
      });
      await flush();
    };

    it("confirmed: refetches the summary and shows plan and credits from it", async () => {
      const f = stubFetch([confirmed()]);
      await renderStripe();

      expect(f.confirmCalls()).toBe(1);
      expect(f.summaryCalls()).toBeGreaterThanOrEqual(2);
      expect(screen.getByTestId("checkout-plan").textContent).toContain("Pro");
      expect(screen.getByTestId("checkout-credits").textContent).toContain(
        "480",
      );
      expect(screen.getByTestId("checkout-credits").textContent).not.toMatch(
        /credits added/,
      );
      expect(
        screen.getAllByRole("link", { name: "Go to the dashboard" })[0],
      ).toBeTruthy();
    });

    it("formats the credit balance like the billing page", async () => {
      stubFetch([confirmed()]);
      const f = vi.mocked(fetch);
      const orig = f.getMockImplementation();
      f.mockImplementation(async (url, init) =>
        String(url).includes("/billing/checkout/confirm")
          ? orig!(url, init)
          : json({
              status: 200,
              body: {
                ...SUMMARY,
                credits: { balance: 3500, reserved: 0, available: 3500 },
              },
            }),
      );
      await renderStripe();

      expect(screen.getByTestId("checkout-credits").textContent).toContain(
        "3,500 credits",
      );
    });

    it("the header follows the state", async () => {
      stubFetch([pending, confirmed()]);
      await renderStripe();
      expect(
        screen.getByRole("heading", { name: "Confirming your payment" }),
      ).toBeTruthy();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2000);
      });
      expect(
        screen.getByRole("heading", { name: "Payment confirmed" }),
      ).toBeTruthy();
    });

    it("the header says the checkout was not completed when abandoned", async () => {
      stubFetch([
        { status: 200, body: { status: "abandoned", kind: "subscription" } },
      ]);
      await renderStripe();
      expect(
        screen.getByRole("heading", { name: "Checkout not completed" }),
      ).toBeTruthy();
    });

    it("a top-up says credits were added", async () => {
      stubFetch([confirmed("topUp")]);
      await renderStripe();

      expect(screen.getByTestId("checkout-credits").textContent).toMatch(
        /credits added/,
      );
    });

    it("pending then confirmed on retry", async () => {
      const f = stubFetch([pending, confirmed()]);
      await renderStripe();
      expect(screen.getByTestId("checkout-still-confirming")).toBeTruthy();

      await act(async () => {
        await vi.advanceTimersByTimeAsync(2000);
      });

      expect(f.confirmCalls()).toBe(2);
      expect(screen.getByTestId("checkout-success-card")).toBeTruthy();
    });

    it("pending forever: retries 3 times with back-off, then the existing text", async () => {
      const f = stubFetch([pending]);
      await renderStripe();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2000);
      });
      await act(async () => {
        await vi.advanceTimersByTimeAsync(4000);
      });
      expect(f.confirmCalls()).toBe(3);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(8000);
      });

      expect(f.confirmCalls()).toBe(4);
      expect(screen.queryByTestId("checkout-still-confirming")).toBeNull();
      expect(screen.getByTestId("checkout-pending-notice").textContent).toMatch(
        /has not confirmed this yet/i,
      );
    });

    it("clears a saved checkout snapshot once the Stripe session is confirmed", async () => {
      window.sessionStorage.setItem("reelbolt.checkout-snapshot", JSON.stringify({ planKey: "trial", balance: 1, livePaid: false, at: Date.now(), target: null }));
      stubFetch([confirmed()]);
      await renderPage({ session_id: "cs_test_1" });
      await flush();
      expect(window.sessionStorage.getItem("reelbolt.checkout-snapshot")).toBeNull();
    });

    it("abandoned: nothing was charged, with a way back to billing", async () => {
      stubFetch([
        { status: 200, body: { status: "abandoned", kind: "subscription" } },
      ]);
      await renderStripe();

      expect(
        screen.getByText(
          "The checkout was not completed. Nothing was charged.",
        ),
      ).toBeTruthy();
      expect(
        screen
          .getByRole("link", { name: "Back to billing" })
          .getAttribute("href"),
      ).toBe("/settings/billing");
    });

    it.each([
      [404, { error: "not_found" }],
      [503, { error: "provider_unavailable" }],
      [429, { error: "rate_limited_xyz" }],
      [409, { error: "conflict_xyz" }],
      [400, { error: "bad_request_xyz" }],
    ])(
      "%i shows the neutral text and no raw response",
      async (status, body) => {
        stubFetch([{ status, body }]);
        await renderStripe();

        const text = document.body.textContent ?? "";
        expect(screen.getByTestId("checkout-pending-notice")).toBeTruthy();
        expect(text).not.toContain(Object.values(body)[0]);
        expect(text).toContain("We could not confirm it here.");
      },
    );

    it("calls confirm once under strict mode", async () => {
      const f = stubFetch([confirmed()]);
      await renderStripe(true);

      expect(f.confirmCalls()).toBe(1);
      expect(screen.getByTestId("checkout-success-card")).toBeTruthy();
    });
  });
});
