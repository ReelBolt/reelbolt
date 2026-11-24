# First-run reminder mail

Status: built, **off** (`FirstRunReminder:Enabled=false`), nothing is sent.

**Decision (Owner): option A plus C.** Mail goes only to users who opted in to marketing (`ConsentMode=RequireMarketingOptIn`,
the default). Option C, an in-product nudge, is separate work. Option B (service-message reading) is **rejected for now**:
`ServiceMessage` mode stays in the code but is **not approved**, and must not be used until counsel has reviewed it in writing.

**Precondition to enabling, not met today:** the Brevo DPA and the Brevo sub-processor entry in the privacy policy (tracked as an open legal item,
`docs/marketing-consent.md`, "Before the first marketing send"). Do not set `Enabled=true` and `DryRun=false` before both
are closed. Brevo transactional would also be the first non-campaign Brevo use; transactional mail otherwise goes via Cloudflare.

This is an engineering analysis, **not legal advice**; the legal reading needs a counsel check.

## What it is

One email, 1 to 2 hours after a user's first project, when nothing has been run in any of their projects:
"your footage is ready, one click to make the first cut", linking to the project's workflows page. At most one
per user, ever.

## Service message or marketing message?

Facts from the repo:

- Marketing consent (`docs/marketing-consent.md`) is per address, opt-in, double opt-in for anonymous capture,
  with a wording registry and signed unsubscribe links. The Brevo path (`Services/Marketing/*`) sends campaigns
  to the managed list of opted-in contacts only.
- The open legal item blocks every *marketing* send until a named controller and postal address exist, a sending tool is under
  a DPA, and the tool is a listed sub-processor. The 2026-10-26 update unblocks it with controller Tommaso Celano,
  the postal address, and Cloudflare Email. The owner has since chosen Brevo, and the docs list the Brevo
  DPA, sub-processor entry and privacy wording as **still open with counsel**.
- Transactional mail today (verification, reset, welcome) goes through the Go API's mailer (Cloudflare Email
  Sending, SMTP or console), **not** Brevo. Brevo has never carried a non-consented, per-user transactional send.

The legal question:

- **ePrivacy art. 13 (in Italy, D.Lgs. 196/2003 art. 130)** requires prior consent for email "for direct
  marketing". A message that only delivers or completes the service the person asked for is a service message and
  needs no marketing consent. The test is purpose, not label: a mail whose job is to bring the user back and get
  them to use the product is, on the common reading (Garante, EDPB, ICO guidance), promotional, even when it is
  triggered by their own action and carries no offer.
- The "soft opt-in" (art. 13(2), art. 130(4)) covers similar products to someone who *bought* from you. A free
  signup is a weak fit; do not rely on it without counsel.
- **GDPR**: a service mail rests on art. 6(1)(b) (performing the contract) at best; a nudge rests on legitimate
  interest (6(1)(f)) at best, with a right to object (art. 21) that must be easy to use. Either way the sender
  identity (13(1)(a)) is required, and Brevo becomes a processor that needs a DPA and a privacy-policy line.

Honest reading: the line is "one reminder about their own unfinished project", which is as close to a service
message as a nudge gets, but it is **not clearly** one. A reasonable regulator could call it marketing. We cannot
resolve that from the repo; counsel can.

### What each reading needs

| | Marketing reading | Service-message reading |
|---|---|---|
| Consent | An `OptedIn`, confirmed, deliverable row in `marketing_consents` (the campaign sendable rule), not suppressed. The wording version is not re-checked: any recorded version counts, as for campaigns | None under art. 13 (if counsel agrees it is not marketing) |
| Open legal item | Blocks it fully: controller, address, DPA and sub-processor entry for Brevo must be closed | The identity items are required anyway (footer). The Brevo DPA and sub-processor entry are needed for any Brevo send |
| Opt-out | One-click, the existing signed unsubscribe link (`u1.` token) | Not required by art. 13, but offered when the user has a consent row; withdrawal always wins |
| Reach | Only users who ticked the marketing box (small) | All verified users who qualify |
| Risk | Low | Medium: complaint to the Garante if judged marketing |

## Options for the owner

**A. Marketing reading, opted-in users only (recommended).** Send only to verified users whose address has an
`OptedIn` marketing consent. Safe under any reading. **Chosen.** Needs the open legal items closed first (they are for any
marketing mail anyway). Small reach, so pair it with an in-product nudge (a banner on the project page after
the first hour) that needs no consent at all.

**B. Service-message reading, after counsel signs off (rejected for now).** Send to every qualifying verified user, one mail,
plain footer with identity and a one-click opt-out when a consent row exists. Larger reach. Only after counsel
confirms in writing that this single, task-bound reminder is not "direct marketing" under art. 130, and the Brevo DPA
and sub-processor line are done. A withdrawn marketing address is still never mailed.

**C. No email; in-product only (chosen, alongside A).** A first-hour banner or notification in the app and the editor, no mail, no new
processor use. Zero legal exposure, weakest recall for users who left the tab.

**Recommendation, taken: A, plus C now; revisit B only with counsel's note.** Question put to the owner: "The first-run
reminder mail is built and off. Do you want it (A) only for users who opted in to marketing, (B) for all verified
users as a service message, accepting counsel's view first, or (C) not by email? It also needs the Brevo DPA and
sub-processor entry for any option that sends. Which, and may we use Brevo transactional for it (today transactional
mail goes via Cloudflare)?"

## How it is built

`FirstRunReminderSweeper` (Inference API, `Services/FirstRunReminder/`), a `BackgroundService` registered always,
doing nothing when `Enabled` is false (it returns before the first tick and a manual pass returns at once).

Selection, per pass (inside a tenant system scope, since a hosted service has no organization): the user's earliest
project was created `MinAgeMinutes`..`MaxAgeMinutes` ago (default 60..120), and the user is verified, has no
`DeletionRequestedAt`, is not an admin or service account, not a member of a Platform organization, not at an excluded
domain, has no row in `first_run_reminders`, has no workflow execution in any of their projects, and passes the consent
check. The consent check, in every mode: a `marketing_consents` row exists and is not `Withdrawn` (the unsubscribe link
needs it), and the address is not in `marketing_suppressions`. `RequireMarketingOptIn` also applies the campaign
sendable rule (`MarketingConsentService.SendableRows`: OptedIn, confirmed, not undeliverable, verified non-service
account), shared with the export so they cannot drift. Consent is re-read after the claim; a withdrawal in between marks
the row `OptedOut` and nothing is sent. The sender's key is checked before any claim: a missing key claims nothing.

Controls: `DailyCap` (default 50, counted on `first_run_reminders.claimed_at` per UTC day, also in dry run; read once per
pass, so two replicas could overshoot by up to one batch, while one mail per user stays exact), quiet hours (default
21:00 to 08:00 `Europe/Rome`; a user whose window passes overnight is simply not reminded), `BatchSize`.

`first_run_reminders` (migration `AddFirstRunReminders`): primary key `user_id` (the at-most-once claim, cascades
with the account), `state` (`Claimed`, `Sent`, `Failed`, `OptedOut`), `claimed_at`, `sent_at`, `locale`,
`consent_mode`, `error_code`. No address, no body. The row is claimed **before** the send: a crash or an unknown
outcome never produces a second mail. A per-user opt-out can be recorded ahead of time as an `OptedOut` row.

Config (section `FirstRunReminder`, absent from the shipped appsettings):

| Key | Default | |
|---|---|---|
| `Enabled` | `false` | Master flag. |
| `DryRun` | `true` | With Enabled, counts candidates only; no row, no send. |
| `ConsentMode` | `RequireMarketingOptIn` | Or `ServiceMessage` (option B, not approved: does nothing unless `ServiceMessageCounselReviewed=true`, default false). |
| `MinAgeMinutes` / `MaxAgeMinutes` | 60 / 120 | The window. |
| `DailyCap`, `BatchSize`, `PollIntervalMinutes` | 50, 50, 10 | |
| `QuietHoursStartHour` / `EndHour` / `TimeZone` | 21 / 8 / `Europe/Rome` | Equal hours disable. |
| `ExcludedEmailDomains` | empty | Internal and test domains. |
| `FromAddress`, `FromName` | `Marketing:Campaigns:FromAddress`, `ReelBolt` | |
| `BrevoTransactionalApiKey` | env `BREVO_TRANSACTIONAL_API_KEY` | Never logged. |

A mail can leave only when `Enabled=true`, `DryRun=false`, the key is set, and the footer identity
(`Marketing:Campaigns:FooterControllerName`, `FooterAddress`) and the public base URL are configured; otherwise the
pass sends nothing and warns. The default `IFirstRunReminderSender` is a no-op; `BrevoTransactionalFirstRunReminderSender`
(plain `HttpClient`, `POST https://api.brevo.com/v3/smtp/email`, `api-key` header) is resolved only for
`Enabled && !DryRun`.

Mail: English or Italian from the user's locale, one line, one link, no tracking pixel and no remote image, a footer
with "operated by", P.IVA, postal address, why they got it and that it is the only one. The opt-out link is the
existing marketing `u1.` unsubscribe token when the address has an `OptedIn` row. An address with no consent row (option B) gets no mail: there is no opt-out path for it yet, and that needs a new token
purpose and an endpoint, not built. A row in `Pending` is reachable through its own token. The mail also sends
`List-Unsubscribe` and `List-Unsubscribe-Post` (RFC 8058). Logs carry counts and the
`rcpt=` HMAC ref only, never an address, a body or the key.

Not done: no `reminder_sent` product event (it would need an events-doc and CHECK-list change; `first_run_reminders`
records the send). No link to the privacy policy in the mail body (add the route once final).
