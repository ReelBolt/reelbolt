---
title: Promotions and credit grants
sidebar_position: 5
description: How ReelBolt platform administrators create promo codes, automatic discounts and pack bonuses, pause or end them, see who used them, and grant free credits to a workspace.
---

# Promotions and credit grants

This page covers the **Promotions** screen (**Platform → Promotions** in the left menu, at `/app/admin/promotions`), which belongs to the platform administrator — the person who runs the whole ReelBolt installation. From it you run offers for every workspace and give free credits to one.

Promotions need billing. On an installation without billing, the page says **Billing is not enabled on this deployment** and nothing on it can be used.

## The four kinds of ReelBolt promotion

Select **New promotion** and pick a kind at the top of the form.

| Kind | What the customer gets | Needs a code? |
|---|---|---|
| **Credit code** | Free credits added to the workspace when someone redeems the code on **Plan and billing**. The credits expire after the number of days you set (90 by default). | Yes |
| **Plan discount** | A lower price on a plan subscription, for the first payment, a set number of months, or every payment. | Optional |
| **Pack discount** | A lower price on a credit pack, for that one purchase. | Optional |
| **Pack bonus** | Extra credits on top of a bought credit pack, as a percentage of the pack. Bonus credits expire twelve months after the purchase. | Optional |

**With a code or automatic.** A promotion with a code applies only when a customer types the code. A discount or bonus with no code is **automatic**: it applies to every matching checkout on its own, and the table marks it **Automatic**. Customers see an automatic offer as a badge with its public headline on the plans and packs it covers.

## Creating a ReelBolt promotion

1. Select **New promotion** and choose the kind.
2. Fill in the common fields:
   - **Name** — for administrators only.
   - **Public headline** — what customers see, for example "Spring sale: 20% off". Keep it accurate.
   - **Code** — letters and digits, 4 to 32 characters; case does not matter. Required for a credit code. Leave it empty to make a discount or bonus automatic.
   - **Starts** and **Ends** — empty means it starts now and never ends.
   - **Total redemptions** (empty means unlimited) and **Per workspace** (how many times one workspace can use it, 1 by default). The **Per workspace** limit also applies to each person across all their workspaces, so creating another workspace does not earn another use.
3. Fill in the fields of the kind:
   - **Credit code:** **Credits** (1 to 100,000) and **Credits expire after (days)** (1 to 730).
   - **Plan discount:** **Percentage** (**Percent off**, 0.01 to 100) or **Flat amount (USD)** (**Amount off (US cents)**: 500 is five dollars). Then the duration — **First payment**, **Several months** (with **Months**) or **Forever** — and the **Plans** it covers.
   - **Pack discount:** the same amount fields, and the **Credit packs** it covers. It always covers one payment.
   - **Pack bonus:** **Bonus (percent of the pack's credits)**, 1 to 100, and the **Credit packs** it covers.
   - Leave **Plans** or **Credit packs** empty to cover everything on sale.
4. Select **Create promotion**.

**Why a promotion can be refused.**

- **Another promotion already uses that code.** Codes are unique: pick another.
- **An automatic promotion already covers these plans or packs.** Two automatic offers of the same kind cannot overlap in time on the same plan or pack, because only one can apply to a checkout. Pause the other one or narrow the plans or packs.
- **A chosen plan or pack cannot be discounted**, because it is not on sale at the payment provider.
- **The payment provider rejected the request.** Discounts are created at the payment provider first; if it refuses, nothing is saved. Try again, or check the billing setup.

## Changing, pausing and ending a ReelBolt promotion

Each row in the table has an actions menu.

- **Edit** changes the **Name**, the **Public headline** and the end date (**Ends**, or **No end date**). Nothing else can change after a promotion is created — not the code, the amount, the duration or the plans and packs it covers. To change those, archive the promotion and create a new one.
- **Pause** stops the promotion at once; **Activate** turns it back on. Reactivating is refused if it would now overlap another automatic promotion.
- **Archive** turns it off and marks it as retired. An archived promotion can still be turned back on with **Activate**.

A change to the status or the end date of a discount reaches the payment provider first, and is saved in ReelBolt only once the provider accepts it.

A bonus promised at checkout is still granted when the payment completes, even if you paused or ended the promotion in between: the customer was shown it when they paid.

## What happens to a plan discount when a customer changes plan

A plan discount that lasts more than one payment (**Several months** or **Forever**) follows the subscription when the customer changes plan:

- **To a plan the promotion covers**, the discount stays, for the payments it has left.
- **To a plan the promotion does not cover**, the discount ends at once, in the same change. The customer is told before they confirm, for example **Your 20% promo ends with this change.**
- **It does not come back.** If the customer switched to a smaller plan that ended the discount and then selects **Keep {current plan}**, they stay on their plan at the regular price.

A promotion with **Plans** left empty covers every plan on sale when it was created, so its discount stays on a change to any of those plans. A discount that covered only the first payment is already used up and is not affected.

## Seeing who used a ReelBolt promotion

Select **Redemptions** in a row's actions menu. The panel lists the newest 500 uses, each with when it happened, the workspace, what happened (**Code redeemed**, **Discount applied** or **Bonus granted**), the credits given, the discount amount and the payment provider's transaction.

The panel also lists payments where the offer was not given. They show a red **Not given** badge with a short reason, for example that the person or workspace had already used the offer or that the checkout was replaced by a newer one. Uses given just past a limit, because the checkout was started before the limit filled, show an orange **Over limit** badge. A plan discount that ended because the customer changed to a plan the promotion does not cover shows **Ended on plan change** with the date. To check what a workspace received, open one of its members on **Platform → Users**: the **Organizations and credits** card shows each workspace's balance.

The **Redemptions** column in the table counts only the uses that were given: codes redeemed, discounts applied and bonuses granted. A discount or bonus is counted when the payment completes. New checkouts stop at the **Total redemptions** and **Per workspace** limits.

**When a customer pays after a limit is reached.** A checkout holds its offer for an hour, so a customer can still pay after the limit was reached by someone else. The customer gets the offer, and it counts as one use above the limit, only if all of these are true:

- Their checkout was opened while the promotion was still within every limit.
- No newer checkout or payment for the same offer, by the same person or workspace, replaced it, and they paid within the hour.
- Neither the person nor the workspace already got a use above the limit of this promotion.
- The promotion has not already gone over its limits too many times: at most 5 extra uses, and for a **Total redemptions** under 500, one extra use for every full 100 (at least one).

Otherwise no bonus credits are added. A discount the payment provider already took off the price stays with the customer, but it does not count as a use. When a customer opens a new checkout for the same offer, or leaves one unpaid for an hour, ReelBolt cancels the old one at the payment provider so it can no longer be paid. If a customer reports a missing bonus, check the workspace's balance and use **Grant credits** to make up for it.

Only customers whose email address is verified can use a promotion. A customer who has not verified it is asked to, and an offer with no code is not applied to their checkout.

## Granting free credits to a ReelBolt workspace

You can give a workspace promo credits by hand, for example to make up for a problem.

1. Find the workspace. On the **Promotions** page, open the **Grant credits** tab and search by workspace name or owner email. Or open a person on **Platform → Users**: the **Organizations and credits** card lists the workspaces they belong to, with their balances.
2. Select **Grant credits** on the workspace's row.
3. Enter the **Credits** (1 to 1,000,000), the **Expires on** date (between tomorrow and three years from now) and a **Reason**. The reason is required and kept with the grant.
4. Select **Grant credits**.

The credits are added to the workspace's balance at once, as promo credits with that expiry. Each grant records who made it and why; select a workspace row to see its recent grants. Submitting the same form twice by accident grants once.

## What customers are told about promo credits

When a workspace redeems a credit code, ReelBolt shows the credits added, the date they expire, and that promo credits have no cash value and cannot be transferred. The workspace's **Plan and billing** page lists every batch of credits with its expiry under **Credits by expiry**. For what customers see, read [Plans and credits](../account/plans-and-credits.md).
