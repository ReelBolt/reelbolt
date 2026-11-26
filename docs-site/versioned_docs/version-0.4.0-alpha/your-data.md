---
title: Your data, exports and closing your account
sidebar_position: 13
description: How to download a copy of everything ReelBolt holds about you, how to close your account, what the 30-day grace period means, what ReelBolt keeps and why, and how to subscribe to or unsubscribe from marketing emails.
---

# Your data, exports and closing your account

You can take your data out of ReelBolt at any time, and you can close your account without asking anyone. This page explains both, and exactly what happens to your information when you do.

## Download a copy of your data

You can ask ReelBolt for a copy of everything it holds about **your account and your personal workspace**, as one zip file.

1. Open **Your data** under **Settings** in the left menu. (The same cards are also at the bottom of the **Account** page.)
2. In **Download your data**, select **Request a copy**.
3. ReelBolt puts the file together in the background — a few minutes, longer if you have a lot of footage — and emails you a download link when it is ready. You can leave the page while it works.
4. The copy also appears on the same card: select **Download your data (zip)**. Each time you select it you get a fresh download, so it does not matter if the emailed link has run out.

A copy is kept for **seven days** after it is made, then deleted. You can ask for **one copy a day**: if you ask again sooner, the card tells you how long to wait. If the copy you asked for has not appeared yet, it is still being prepared and the email is on its way.

If the card says downloading a copy isn't available, the server you use has the feature switched off: ask the person who runs it.

### What is inside the export

The zip contains a folder of plain JSON files — one per kind of record — plus your original files:

- **Your account**: your email address, display name, when the account was created and verified. Your password is never in there, in any form.
- **Your projects, files, workflows and runs**: names, descriptions, and everything the agents produced along the way — scripts, plans, transcripts of your footage, captions, review feedback.
- **Your uploads**: the original files you put into your projects, up to a size limit. Anything larger than the limit is listed in the manifest (`manifest.json`) instead of being copied in, so a very large footage library still gives you a manageable download.
- **Your assistant chats**: every conversation you had with the assistant.
- **Your saved pieces**: custom agents, editing styles, voices (including the consent record for a cloned voice, minus the vendor's copy), and editor documents with their history.
- **Your usage and billing**: what you used, what it cost, your plan and its status, and your credit history.
- **Your marketing-email choice**: whether you are subscribed, the exact wording you agreed to, when and from where, and every change since.

A few things are deliberately **not** included, and the manifest says so:

- **Your password**, in any form, and the security records behind the verification and reset links ReelBolt has emailed you. These protect your account; they are not your content.
- **Saved shortcuts**: the reuse cache that lets a repeated run skip work it has already done. The outputs themselves are in the export.
- **Another workspace's content.** An export covers you and your **personal workspace**. Work inside a team workspace belongs to the team and to the other people in it, so it is not included: ask a team Owner if the team's data is what you need.
- **Your AI connection keys.** If you set up your own AI service inside a workspace, the export lists the connection — its name, address and model — but never the key itself.

## Close your account

You can close your ReelBolt account yourself, from the dashboard. If you want a copy of your data, download it first (see [Download a copy of your data](#download-a-copy-of-your-data)).

1. Open **Your data** under **Settings** in the left menu. (The same cards are also at the bottom of the **Account** page.)
2. In **Delete account**, select **Delete account…**.
3. Enter your **password**. Asking for it again is deliberate: someone who finds your computer signed in must not be able to close your account.
   - Signed up with **Google or GitHub** and never set a password? Select **I sign in with Google or GitHub** instead. After you confirm, ReelBolt sends you to Google or GitHub to sign in; nothing is deleted until you come back from that sign-in.
4. Type **DELETE** in the box to confirm, then select **Delete my account** (or **Continue**, for Google or GitHub).

You are signed out everywhere straight away and taken to the sign-in page, which shows the date your account will be deleted. An email tells you the same date.

ReelBolt refuses if you are the **only Owner of a team workspace that still has other members**, and names the team: switch to that team with the workspace switcher at the top of the left menu, then on **Workspace → Members** make someone else an Owner or remove the other members, and try again. Nothing is deleted when this happens.

If you pay for a plan, cancel it at the payment provider first — see [What ReelBolt cannot remove for you](#what-reelbolt-cannot-remove-for-you).

### Change your mind

ReelBolt does not delete anything the moment you ask: your account is **scheduled** for deletion, **30 days** later. Sign in again any time during those 30 days — with your password, or with Google or GitHub — and the deletion is cancelled. Nothing is lost, and ReelBolt tells you so when you arrive. You are signed out of every device the moment you ask, so signing in again is always a deliberate step, never an accident.

### What happens at the end of the 30 days

ReelBolt removes:

- your projects, files and every object they stored — including anything left behind by an earlier failure,
- your workflows, runs, step results, review feedback and video-generation records,
- your editor documents and their history,
- the search index built from your files' text,
- your assistant chats,
- your custom agents, editing styles and voices; a cloned voice's model is deleted at the AI service as well,
- your membership of any workspace, and invitations addressed to your email.

ReelBolt keeps, for reasons it has to:

- **Billing records** — invoices and payments are kept for the period tax law requires (typically seven to ten years). Your email address is removed from them; they stay attached to the workspace they were paid for.
- **Usage records** — what a run cost, kept for accounting. They stay, but the link to you is removed.
- **A one-way fingerprint of your email address**, so the free trial cannot be claimed twice by deleting an account and signing up again. It cannot be turned back into your address.
- **If you had signed up for marketing emails, or unsubscribed from them**, a second one-way fingerprint of your email address, so ReelBolt never sends you marketing email again. Your consent record itself is deleted.

Two things about workspaces:

- Your **personal workspace** goes with you. Its content is deleted and it is closed and de-identified: any name that came from your own name is removed. The accounting rows attached to it (usage, credits, invoices) stay, without your email address.
- A **team workspace** you share with other people stays, and so does its work — those people still need it. Before you delete your account, ReelBolt will refuse if you are the only Owner of a team that still has other members, because the team would be left with nobody able to manage it. Hand the team over — or remove the other members — and try again.

### What ReelBolt cannot remove for you

Some of your data is held by other companies that ReelBolt uses to do its work — the payment provider that handles billing, the AI services that generate text, speech and video, and the email service that sends these messages. Each one keeps its own records under its own terms.

Two of those need your attention when you close an account:

- **A paid subscription.** ReelBolt cancels the plan it manages itself and stops granting access, but a subscription held at the payment provider is cancelled **there**: open **Plan & billing** under **Settings** in the left menu and select **Manage billing and invoices**, which opens their portal. Closing a ReelBolt account does not silently end a payment arrangement at the provider — cancel it first, or check the portal afterwards.
- **A cloned voice.** ReelBolt asks the AI service to delete the cloned model before it removes the recordings. If that service is unreachable at the time, the deletion is logged for follow-up rather than silently skipped.

## Marketing emails from ReelBolt

ReelBolt can send you product news, tips and offers by email, but only if you ask for them. The choice is yours alone: it belongs to your email address, not to a workspace, and nobody else can make it for you. Emails about your account and billing are sent either way.

**Signing up.** Wherever ReelBolt offers marketing emails — when you create an account, before a checkout, on the page that announces a Privacy Policy update, and on the contact form of the ReelBolt website — it is a separate box that starts unticked. You can create an account, buy a plan or accept the terms without ticking it.

- If you sign up from your account, the subscription counts once your email address is confirmed.
- If you sign up from the website's contact form, ReelBolt emails you a link first. Open it and select **Confirm subscription**; nothing is sent until you do. The link works for seven days. If you did not ask for it, ignore the email.

**Turning them on or off.** Open **Account** under **Settings** in the left menu and use the **Marketing emails** switch. The card shows when you agreed, or when you unsubscribed.

**Unsubscribing from an email.** Every marketing email has an unsubscribe link. It opens a page that asks **Unsubscribe from ReelBolt marketing emails?**; select **Unsubscribe**. You do not need to sign in, and it takes effect at once.

**What ReelBolt records.** When you agree, ReelBolt keeps the exact wording you agreed to, the date, where you agreed and the network address you used, so it can show that you asked. Every later change is recorded too. This record is part of your data download (see [Download a copy of your data](#download-a-copy-of-your-data)), and it is deleted when you close your account.

## When ReelBolt updates its Privacy Policy

When the Privacy Policy changes, ReelBolt tells you in the dashboard, at the latest the next time you sign in. A page headed **We've updated our Privacy Policy** summarises what changed and links to the policy; select **Continue** to carry on. You are not asked to agree to anything, and the marketing-email box on that page is optional.

That page never stops you from signing out, downloading your data or closing your account: it links to both, and **Your data** under **Settings** stays open. The rest of **Settings** opens again once you continue.

When the Terms of Service change, the page instead asks you to tick a box accepting the new terms before you continue.

## Retention: what ReelBolt deletes on its own

The plan your workspace is on decides how long generated files are kept — typically 30 days on the trial and Creator plans, 90 on Pro and Team. ReelBolt clears out older renders and analysis files automatically and tells you nothing, because that is what the plan means. **Your own uploads are never removed by that sweep**: only files ReelBolt generated.

One more thing worth knowing: **runs and their step results do not expire on their own**. They stay until you delete the project or the account. If you need them gone, delete the project.

## Related

- [Workspaces and teams](./workspaces.md): personal and team workspaces, roles and leaving a team.
- [Concepts](./concepts.md): what projects, files, runs and outputs are.
