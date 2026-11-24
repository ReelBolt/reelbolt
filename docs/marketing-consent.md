# Marketing consent

Developer and operator reference for ReelBolt's own marketing-email consent: the consent store, the
wording registry, the signed confirm and unsubscribe links, double opt-in, the admin export,
account deletion and the privacy-policy acknowledgement. The compliance basis is the
saas-promotions compliance check (GDPR arts 6, 7, 15, 17, 20, 21; ePrivacy art 13; RFC 8058).

Marketing consent does **not** depend on `Billing:Enabled`: every endpoint here works on a
self-host with billing off. The code is `Services/Marketing/*`, `Controllers/MarketingConsentController.cs`
and `Controllers/Admin/MarketingConsentAdminController.cs` in the Inference API.

## The consent model

Consent belongs to one person's email address, never to an organization. There is no org-level
toggle.

| Table | What it holds |
|---|---|
| `marketing_consents` | One current-state row per normalized email (trim and lowercase). `status` is `Pending`, `OptedIn` or `Withdrawn`. It records `wording_version`, `source`, `requested_at`, `consented_at`, `confirmed_at`, `withdrawn_at`, the request `ip` and `user_agent` (truncated to 64 and 256 characters), `last_confirmation_sent_at`, and `user_id` (nullable, `SET NULL` when the account goes). Names and sync state: `first_name` and `last_name` (each at most 100 characters), `name_source` (`Account`, `Form` or `Admin`), `name_updated_at`, `brevo_pushed_at`, `brevo_push_attempts`, `brevo_push_attempted_at` and `brevo_push_error_code`. See [Contact sync and names](#contact-sync-and-names). |
| `marketing_consent_events` | The append-only history. Every state change writes exactly one event: `Requested`, `Granted`, `Confirmed` or `Withdrawn`, with source, wording version, IP, user agent and time. |
| `marketing_suppressions` | A keyed HMAC of an erased address and its `withdrawn_at`. See [Account deletion and suppression](#account-deletion-and-suppression). |
| `marketing_consent_exports` | One row per admin CSV export: who, when, how many rows. |
| `marketing_contact_sync_state` | One row (`Id` = 1) with the contact sync worker's state: `LastRunAt`, `LastRunOk`, `LastErrorCode`, `LastReconcileAt`, `RunRequestedAt`. No address, no name. |

`source` is one of `Signup`, `Settings`, `Checkout`, `AcceptTerms`, `SiteContact`, `SiteFooter`
or `Unsubscribe`. Withdrawal keeps the row. Only account deletion and the pending sweeper delete
rows. The Inference API owns and migrates the first four tables (migration
`AddPromotionsAndMarketingConsent`); the Go API writes `marketing_consents` and its events only at
signup and on email verification.

The checkbox is unticked by default, optional and separate from the terms checkbox wherever it
appears: signup, `/accept-terms`, Account > Marketing emails, and the site's contact form (it is no longer
offered on the billing page; the server still accepts `Checkout` as a source).
Signup, checkout and accepting the terms all complete without it.

## The wording registry and its drift check

The text a person agreed to is evidence, so it lives in one versioned registry:
`inference/src/ReelBolt.Inference.Api/Marketing/marketing-consent.wording.json`.

```json
{
  "current": "2026-10-marketing-v1",
  "versions": {
    "2026-10-marketing-v1": "Email me ReelBolt product news, tips and offers. You can unsubscribe at any time from any email or in Settings. See our Privacy Policy."
  }
}
```

- **A published version's text is never edited or deleted.** New text gets a new version, and
  `current` moves to it. `MarketingWordingRegistryTests` pins the shipped text.
- **Every grant names the version.** An opt-in naming anything but `current` is refused: `409
  stale_wording` (with `currentWordingVersion`) from the Inference API, `400
  invalid_marketing_wording` from Go signup.
- **The site vendors the file byte for byte** at `site/lib/marketing/marketing-consent.wording.json`.
  The CI step "Marketing consent wording drift" runs `diff -u` on the two files.
- **Go holds the current version as a constant**, `services.MarketingWordingVersion`, and
  `TestMarketingWordingVersionMatchesRegistry` compares it with the registry.

To publish new wording, add a version to the registry, move `current`, copy the file to the site,
and update the Go constant, all in one PR.

`Marketing:WordingPath` overrides where the API loads the registry from. The default is next to the
assembly.

## Endpoints

All routes are on the Inference API under `/api/v1/marketing`.

| Method | Path | Auth | What |
|---|---|---|---|
| `GET` | `/wording` | Anonymous | `{ version, text }` of the current registry entry. |
| `GET` | `/consent` | Signed in | `{ status, wordingVersion, consentedAt, confirmedAt, withdrawnAt }` for the caller's email. `status` is `none`, `pending`, `optedIn` or `withdrawn`. |
| `PUT` | `/consent` | Signed in | `{ optIn, source, wordingVersion }`, `source` = `settings`, `checkout` or `acceptTerms`. Same shape back. |
| `POST` | `/subscribe` | Anonymous | `{ email, wordingVersion, source, firstName? }`, `source` = `siteContact` or `siteFooter`. `firstName` is optional; an invalid one is ignored. `202 { status: "checkEmail" }`. |
| `POST` | `/confirm` | Anonymous | `{ token }`. `200 { status: "optedIn" }`. |
| `POST` | `/unsubscribe` | Anonymous | A token (see below). `200 { status: "unsubscribed" }`. |
| `GET` | `/admin/consents` | Platform admin | `{ counts: { pending, optedIn, withdrawn, exportable }, lastExport }`. |
| `GET` | `/admin/consents/export.csv` | Platform admin | The sendable list as CSV. |
| `GET` | `/admin/contacts` | Platform admin | Query `status` (`optedIn` default, `pending`, `withdrawn`, `all`), `q` (trimmed; email contains, or name; over 200 characters answers `400 { code: "invalid_query" }`), `limit` (50, max 200), `offset`. `{ items: [{ id, email, status, firstName, lastName, nameSource, brevo: { state, syncedAt, errorCode } }], total }`. `brevo.state` is `synced`, `pending`, `failed` or `notAtBrevo`. |
| `PATCH` | `/admin/consents/{id}/name` | Platform admin | `{ firstName, lastName }`, both optional. `200` with the same item. An invalid name answers `400 { code: "invalid_name" }`, an unknown id `404`, a `Withdrawn` row `409 { code: "withdrawn" }`. Sets `name_source` to `Admin`; clearing both means "no name". |
| `GET` | `/admin/contact-sync` | Platform admin | The sync status: `{ enabled, configured, campaignActive, lastRunAt, lastRunOk, lastErrorCode, lastReconcileAt, counts: { synced, pendingPush, failed } }`. |
| `POST` | `/admin/contact-sync/run` | Platform admin | "Sync now". `202 { requested: true }`. `409 { code: "sync_disabled" }` when sync is off or not configured; `429 { code: "rate_limited" }` only while an earlier request is still pending (no pass has picked it up yet). |

`GET` and `PUT /consent` answer `403 { code: "EMAIL_NOT_VERIFIED" }` until the account has verified
its email. Consent is keyed by address, so an unverified account could otherwise read or withdraw
the consent of whoever subscribed with the address it typed. Both read `email_verified_at` from
`application_users` on every call, never the JWT's `emailVerified` claim, which can be stale and
counts as verified when it is missing.

Signed-in opt-in sets `OptedIn` and `consented_at`. `confirmed_at` is set at once when the account's
email is verified, otherwise when it is verified later. Repeating the current choice writes nothing.
An unknown `source` is `400 invalid_source`.

`subscribe` answers `400 invalid_email` or `409 stale_wording` for bad input; both depend on the
request alone. Every other case answers the same `202`, whether the address is new, pending,
opted in or withdrawn, so the endpoint is no list-membership oracle. The confirmation mail is queued
off the request path for the same reason.

nginx gives `/subscribe`, `/confirm` and `/unsubscribe` their own locations in
`nginx/locations.conf`. Each is a case-insensitive regex with an optional trailing slash
(`location ~* ^/api/v1/marketing/subscribe(/|$)`), because the API routes
`/api/v1/Marketing/Subscribe/` to the same action and an exact match would let that variant skip the
limit. Each location sets `limit_req zone=auth_public burst=5 nodelay`, `auth_basic off`, any client
`Authorization` header blanked, and `X-Real-IP` set from the connection. `/subscribe` moves requests
that carry `X-ReelBolt-Site-Token` to the looser `site_forward` zone instead (see
[Sending limits](#sending-limits)). The API records `X-Real-IP` as the consent IP, unless a trusted
site forward names the visitor's IP. The rest of `/api/v1/marketing` uses the normal
cookie-to-bearer route.

## Signed confirm and unsubscribe links

`MarketingLinkTokens` signs links with HMAC-SHA256. A token carries the key id, the consent row id
and, for confirm, the expiry. It never carries an email.

| Token | Format | Expires |
|---|---|---|
| Unsubscribe | `u1.{kid}.{b64url(consentId)}.{b64url(mac)}` | Never |
| Confirm | `c1.{kid}.{b64url(consentId)}.{expUnix}.{b64url(mac)}` | After `Marketing:ConfirmTtlDays` (default 7, clamped to 1..30) |

- **MAC.** Over length-prefixed fields (4-byte big-endian length, then UTF-8 bytes): the purpose
  (`unsubscribe` or `confirm`), the consent id and, for confirm, the expiry. The purpose is inside
  the MAC, so a confirm token used as an unsubscribe token is invalid. Compared with
  `CryptographicOperations.FixedTimeEquals`.
- **Keys.** `platform_secrets` rows named `marketing-link-token-{kid}`, today `k1`. Never the
  assistant confirmation key.
- **Rotation.** `MarketingLinkTokens.KnownKeyIds` lists every id that verifies; the last one signs.
  Unsubscribe links never expire, so an old id stays in the list. An unknown id is invalid without
  touching the secret store.
- **URLs.** `{base}/app/marketing/confirm?token=...` and `{base}/app/unsubscribe?token=...`. Both
  pages are public in `web/proxy.ts`, send no referrer and are not indexed.

`PlatformSecretKeyStore` resolves a key the way `ConfirmationTokenService` does: read the row; if
absent, generate 32 random bytes, protect them with `ISecretProtector` and `INSERT ... ON CONFLICT
DO NOTHING`, then re-read the winner. It **fails closed**: a key that cannot be read or stored
throws, and is never replaced by a process-local key, because that would mint unsubscribe links
other replicas reject.

## Double opt-in for anonymous sign-ups

An anonymous capture (the site's contact form) must confirm before it counts.

1. `POST /subscribe` creates or keeps a `Pending` row with `Requested`. A `Withdrawn` row stays
   `Withdrawn`: the request is stored in `resubscribe_requested_at` and as a `Requested` event,
   and only the confirm click opts the address back in. An `OptedIn` address is left alone and gets
   no mail.
2. It queues a confirmation mail, at most one per address per
   `Marketing:ConfirmMailboxCooldownMinutes` (default 10) and within the other caps in
   [Sending limits](#sending-limits). Each mail restarts a `Pending` row's clock.
3. The mail goes through `MarketingMailOutbox`, an in-process bounded queue (1,000), to the Go relay
   `POST /internal/mail/send` with the template `marketing-confirm`. A failed send is logged, and a
   mail still queued at shutdown is lost; the visitor can ask again after the cooldown.
4. The visitor opens the link. The page shows a **Confirm subscription** button; GET changes
   nothing, so link scanners cannot confirm. The button POSTs `/confirm`, which sets `OptedIn`,
   `consented_at` and `confirmed_at` to now, with the click's IP.

`/confirm` answers `400 invalid_token` for a bad token, a deleted row, or a `Withdrawn` row with no
re-subscribe request made after the withdrawal (and within `Marketing:PendingRetentionDays`), so an
old link does not revive a withdrawal. It answers `410 token_expired` for an expired token. A
confirm that revives a `Withdrawn` row records the wording version and source of the re-subscribe
request.

**The confirmation mail** is plain: subject "Confirm your ReelBolt email subscription", what was
requested, the link, and "if this wasn't you, ignore this email". It carries no promotional content
and no unsubscribe footer. It is the one relay template the Go API sends to an address with no
account. The Go mailer still accepts only links on its allowed origins (`PUBLIC_BASE_URL`, plus
`MAIL_LINK_HOSTS`), so a `MARKETING_PUBLIC_BASE_URL` on another origin must also be listed in
`MAIL_LINK_HOSTS`.

**Signed-in opt-ins** need no confirmation mail: the account's email verification covers them.
An invite signup is confirmed at once, because the invite proved the address. For any other signup
that ticked the box, Go (`services.SignupMarketingConsent`) does one of these:

- **No row for the address.** It writes the row `OptedIn` with `confirmed_at` null, and the
  verify-email handler sets `confirmed_at` in the verification's transaction.
- **A row already exists** (and is not a confirmed opt-in). An unverified signup proves nothing
  about the address, so it never rewrites or claims that row: the status, evidence and owner stay.
  It appends a `Requested` event naming the signup's user, and a `Withdrawn` row also gets
  `resubscribe_requested_at`. Verifying the address then opts the row in under the request's
  wording and binds it to the user if it had no owner, as the Inference API's confirm click does.
  A request made before the latest withdrawal, or a `Withdrawn` row whose request was cleared,
  is not confirmed.
- **A confirmed opt-in.** Nothing changes.

Every verification mail for such an account carries the line "Verifying this address also confirms
the ReelBolt marketing email subscription you asked for." while verifying would confirm an opt-in:
the first mail, a resent one, and the mail a duplicate signup triggers.

## Sending limits

`POST /api/v1/marketing/subscribe` is anonymous, and it makes ReelBolt mail an address that may
have no account. Three layers cap that confirmation mail, so nobody can aim it at one person, one
domain or the world. The anonymous double opt-in mail is the only mail they cap.

Whatever the layers decide, the API answers every valid request with the same
`202 { status: "checkEmail" }`. A refused mail is not sent, and the answer does not change, so the
caps reveal nothing about who is on the list. The one other answer is nginx's `429` on a burst. It
counts requests per client IP, whatever address they carry, so it reveals nothing about the list
either.

**A genuine visitor can be locked out for a while.** If someone else spends an address's allowance
(the cooldown, or the 3 mails per 24 hours), the owner who then subscribes still sees "check your
email" but gets no mail. They can ask again once the 10-minute cooldown has passed, or once the
24-hour window has slid far enough that the address is under 3 mails again. Refused requests count
for nothing, so a flood of them does not extend the lockout.

### The canonical mailbox

The caps count inboxes, not spellings. `MarketingEmail.CanonicalMailbox` (C#) and
`internalmail.CanonicalMailbox` (Go) apply the same rule:

1. Trim and lowercase the address, then split it at the last `@`. With no `@`, the result is the
   trimmed, lowercased input.
2. Cut the local part at its first `+`, unless that leaves it empty.
3. For `gmail.com` and `googlemail.com`, remove every `.` from the local part and use `gmail.com`.

| Address | Counted as |
|---|---|
| `Victim@Gmail.com` | `victim@gmail.com` |
| ` v.i.c.t.i.m+news@googlemail.com ` | `victim@gmail.com` |
| `a.b+x@example.com` | `a.b@example.com` |
| `+tag@example.com` | `+tag@example.com` |

Consent rows still store the literal address (trimmed and lowercased), and mail still goes to the
address the visitor typed. Only the limit keys use the canonical form.

### The layers, outermost first

**1. nginx**, `location ~* ^/api/v1/marketing/subscribe(/|$)` in `nginx/locations.conf`, with the
zones in `nginx/nginx.conf`. Over the limit, nginx answers `429`.

| Request | Zone | Limit | Key |
|---|---|---|---|
| Without `X-ReelBolt-Site-Token` | `auth_public` (shared with login and signup) | 5 per minute, burst 5 | `$client_ip_key` |
| With `X-ReelBolt-Site-Token` | `site_forward` | 60 per minute, burst 20 | `$client_ip_key` |

`$client_ip_key` is the client's IPv4 address, or the /64 prefix of its IPv6 address (one
subscriber line usually holds a whole /64, so a per-address key would let one host rotate through
fresh buckets). An IPv4-mapped IPv6 address counts as its IPv4 address. The site's server-side
forward arrives from one or a few addresses, so 5 per minute per address would throttle
every visitor together. nginx does not check the token. A forged header only moves a caller into
the 60 per minute zone for this one location; the API's caps still apply. Behind Cloudflare the
client IP is the visitor's only with `REAL_IP_FROM_CLOUDFLARE=true` (see [`tls.md`](tls.md)).

**2. Inference API** (`MarketingConsentService`). Every window is sliding (see
[The counter table](#the-counter-table)).

| Cap | Config key | Default | Kind |
|---|---|---|---|
| Literal address | `Marketing:ConfirmMailboxCooldownMinutes` | 1 per 10 minutes | Refuses. Checked on the row's `last_confirmation_sent_at`. |
| Canonical mailbox, cooldown | `Marketing:ConfirmMailboxCooldownMinutes` | 1 per 10 minutes | Refuses. |
| Canonical mailbox, per day | `Marketing:ConfirmMailboxPerDay` | 3 per 24 hours | Refuses. |
| Client IP (IPv4 address, IPv6 /64) | `Marketing:ConfirmPerIpPerHour` | 5 per hour | Refuses. The tight first layer. |
| Recipient domain (of the canonical mailbox) | `Marketing:ConfirmPerDomainPerHour` | 30 per hour | Alarm threshold. |
| Global | `Marketing:ConfirmGlobalPerDay` | 200 per 24 hours | Alarm threshold. |
| Global hard ceiling | `Marketing:ConfirmGlobalHardCeilingPerDay` | 1,000 per 24 hours | Refuses everyone, and alerts. |

The literal-address claim runs first. The remaining caps are one decision: a mail is sent only when
every refusing cap has room for it. An **alarm threshold** refuses nothing by itself. Above it, only
a [trusted site forward](#the-trusted-site-forward) still gets a mail, because the site ran its bot
checks on that request; a direct anonymous call gets none. Once the domain or global count falls
back under the threshold, everyone gets mail again.

A value of `0` or less falls back to the default. An `OptedIn` address gets no mail at all. If the
counter table cannot be reached, the API sends no mail and logs an error (fail closed).

**3. Go relay** (`api/handlers/internalmail/limit.go`), on every `marketing-confirm` send to
`POST /internal/mail/send`. These caps hold even if the API's are bypassed.

| Cap | Setting | Default | Kind |
|---|---|---|---|
| Canonical mailbox | fixed | 3 per 24 hours | Refuses. |
| Recipient domain | `MARKETING_RELAY_PER_DOMAIN_PER_HOUR` | 30 per hour | Alarm only: logs a warning, sends anyway. |
| Global | `MARKETING_RELAY_GLOBAL_PER_HOUR` | 100 per hour | Refuses. The hard ceiling that protects the sending reputation. |

An unset, non-numeric or non-positive value falls back to the default. The relay also keeps an
in-process sliding window (mailbox and global) in front of the shared counters, and both must
admit the send; a send the shared counters refuse is taken back out of the in-process window. A
refusal answers the API `429`, which logs a Warning ("The Go API refused the marketing-confirm mail
to rcpt=...") and sends nothing. A database error, or a missing `mail_rate_counters` table, refuses
every marketing confirmation mail and logs an error: the relay fails closed until the Inference API
has migrated.

### The counter table

The API and relay caps, apart from the literal-address check and the relay's in-process window,
count in `mail_rate_counters` (migration `AddMailRateCounters`, owned by the Inference API; the Go
API never migrates it).

- **Columns.** `bucket`, `window_start` (UTC, floored to the window length), `count`, `updated_at`.
  The primary key is `(bucket, window_start)`.
- **Sliding windows.** A cap's estimate is the count of the current window plus the count of the
  previous window times the fraction of the current window still to run. A send is allowed when
  the estimate plus one stays within the limit. C# (`SlidingWindow`) and Go (`slidingEstimate`) use
  the same formula.
- **Refused attempts count for nothing.** One decision reads every bucket it needs, and increments
  them only when the send is allowed.
- **Atomic.** A decision runs in one transaction that first takes
  `pg_advisory_xact_lock(hashtext(bucket))` for each bucket, in ordinal-sorted bucket order. C# and
  Go take them in the same order, so two decisions never deadlock, and two concurrent decisions on
  one bucket cannot both see room for the last send.
- **Shared and durable.** Every API replica and the relay count in the same rows, and a restart
  resets nothing.
- **No addresses or IPs.** Bucket names carry a SHA-256 hash of the canonical mailbox or the IP key:
  `confirm:mbox:cool:<sha256>`, `confirm:mbox:day:<sha256>`, `confirm:ip:<sha256>`,
  `confirm:domain:<domain>`, `confirm:global`, `relay:rcpt:<sha256>`, `relay:domain:<domain>` and
  `relay:global`. The promo checkout limit (`promo:checkout:user:<userId>`, see
  [`billing.md`](billing.md#promotions-and-discounts)) uses the same table.
- **Alarm markers.** `confirm:alarm:<cap>:<bucket>` rows are not counters. Each one records that an
  alarm or the hard ceiling has already been logged for that window, so the warning appears once
  per window across all replicas.
- **Swept.** `PendingConsentSweeper` deletes rows whose window started more than 2 days ago.

### Alerts and metrics

| Event | Level | When |
|---|---|---|
| `41601` `MarketingConfirmGlobalCapReached` | Warning | The global alarm threshold is passed. Once per window across replicas. |
| `41602` `MarketingConfirmDomainAlarmReached` | Warning | One domain passed its alarm threshold. Once per window and domain; the line names the domain. |
| `41603` `MarketingConfirmGlobalHardCeilingReached` | Error | The hard ceiling refuses every confirmation mail. Once per window. |

- **Metrics** (meter `ReelBolt.Inference.Marketing`). `reelbolt.marketing.confirm_cap_hit` adds one
  per refused mail, tagged `cap` = `mailbox`, `ip`, `ceiling`, `domain` or `global`.
  `reelbolt.marketing.confirm_alarm` adds one per request over an alarm threshold, tagged `alarm` =
  `domain` or `global` and `outcome` = `sent` (a trusted site forward) or `refused`.
- **Relay.** The relay logs `slog.Warn` once per hour window per process: `internal mail:
  marketing-confirm global relay cap reached; further sends refused this window`, and
  `internal mail: marketing-confirm per-domain alarm threshold passed; sends continue` (naming no
  domain).

Alert on event `41603`: nobody gets a confirmation mail until the 24-hour window falls below the
ceiling. Event `41601` or `41602` means anonymous traffic is being held back and only site
forwards get through.

### Log references

No log line on the mail path carries an address, a token or a tokenised link. A recipient appears
as `rcpt=` plus the first 12 hex characters of HMAC-SHA256 over its canonical mailbox, keyed with a
shared secret. Go (`services.MailLogRef`, `api/services/mail_log.go`) and the Inference API
(`MailLogRef`, `Services/Marketing/MailLogRef.cs`) compute the same reference, so one mailbox has
one reference in both services' logs.

- **The key.** Go reads `MAIL_LOG_REF_KEY`; the Inference API reads `Mail:LogRefKey`. Compose passes
  the same `.env` value to both (`Mail__LogRefKey` for `inference`). Generate it with
  `openssl rand -hex 32`. Unset, each process uses a random 32-byte key and logs one warning at
  startup (Inference event `41611` `MailLogRefKeyMissing`); the references then match nothing
  across services or restarts. The reference is never an unkeyed hash, which a dictionary of
  addresses would reverse.
- **Errors.** An SMTP reply is logged as its class (`smtp_transient` or `smtp_permanent`), status
  code and enhanced code only (`class=smtp_permanent status=550 enhanced=5.1.1`), and a network
  or DNS error as its class (and operation). Any other error text has every `\S+@\S+` replaced; the
  Inference API also blanks `token=`, `code=` and `key=` values.
- **Database queries.** The Go API's GORM logger (`api/database/database.go`) prints statements
  with placeholders, never bound values, and ignores `record not found`. A lookup by email that
  misses (signup, forgot-password, resend) logs nothing, and a slow (over 200 ms) or failed query
  logs its SQL without the address. Admin user creation logs a failure by reference and sanitised
  error, never the requested email.
- **The console mailer.** With no mail provider configured, the Go API logs each unsent mail by
  reference. It prints the mail's link only with `APP_ENV=development`, and never for
  `marketing-confirm`, whose token would confirm a subscription for someone else's address.

### The trusted site forward

The marketing site forwards opt-ins from its own server, so the API sees the site's address, or a
Cloudflare egress address, as the client IP. Without help, the per-IP cap would count every visitor
together. The site therefore names the visitor:

- **Headers.** `site/lib/marketing/subscribe.ts` sends `X-ReelBolt-Site-Token: <SITE_FORWARD_TOKEN>`
  and `X-ReelBolt-Client-IP: <visitor IP>`. With `SITE_FORWARD_TOKEN` unset or blank, it sends
  neither header.
- **Visitor IP.** The self-hosted route (`site/app/api/contact/route.ts`) takes `X-Real-IP`, which
  nginx sets (behind Cloudflare, with `REAL_IP_FROM_CLOUDFLARE`, the real client address).
- **Trust.** The API reads `X-ReelBolt-Client-IP` only when `Marketing:SiteForwardToken` is set and
  the request's token matches it (fixed-time comparison), and only when the value parses as an IP.
  Otherwise it ignores the header and uses `X-Real-IP`, then the connection address. The IP it
  settles on feeds the per-IP cap and is recorded as the consent IP.
- **Bot-checked.** A request whose token matches is the only anonymous request that still gets a
  confirmation mail above the domain and global alarm thresholds (see
  [The layers, outermost first](#the-layers-outermost-first)). The site forwards an opt-in only
  after its bot checks pass.

With `Marketing:SiteForwardToken` empty, the API logs one warning at startup (event `41610`
`MarketingSiteForwardTokenMissing`): every site opt-in then shares the site server's per-IP bucket,
and above an alarm threshold no request gets a mail.

To turn it on, generate one secret and set it in both places:

```bash
openssl rand -hex 32
```

- Server `.env`: `SITE_FORWARD_TOKEN`. Compose passes it to `site`, and to `inference` as
  `Marketing__SiteForwardToken`.
- Nothing else holds a copy: the marketing site is the `site` container, so it reads the same
  `.env` value. See the launch checklist in [`marketing-site.md`](marketing-site.md#launch-checklist).

## The pending sweeper

`PendingConsentSweeper` deletes `Pending` rows, and their events, whose last request is older than
`Marketing:PendingRetentionDays` (default 30). An address nobody confirmed is not consent and is not
kept. The same run deletes `mail_rate_counters` rows whose window started more than 2 days ago (see
[Sending limits](#sending-limits)). It first runs 3 minutes after start, then every
`Marketing:PendingSweepIntervalHours` (default 24; `0` turns it off, and the counter rows then
stay). A failed sweep is retried on the next run.

## Unsubscribing

Withdrawal is as easy as consent and needs no sign-in.

- **Settings.** The **Marketing emails** card on the Account page (`MarketingEmailsCard`) switches
  it off in one click (`PUT /consent { optIn: false }`).
- **A link.** `/app/unsubscribe?token=...` shows one **Unsubscribe** button, which POSTs. GET never
  unsubscribes.
- **One-click (RFC 8058).** `POST /unsubscribe` takes JSON `{ token }`, or `?token=` with an
  `application/x-www-form-urlencoded` or `multipart/form-data` body
  (`List-Unsubscribe=One-Click`). It sets no cookie, needs no CSRF token and never redirects.

Any validly signed unsubscribe token answers `200`, including the token of a row that no longer
exists. Only an invalid token is `400 invalid_token`. Withdrawal is effective at once: the row is
excluded from the next export.

## The admin export and the per-send process rule

`GET /api/v1/marketing/admin/consents/export.csv` is the sendable list as of now. There is no
dashboard page for it yet: call the endpoint as a platform admin.

- **Rows:** `OptedIn`, `confirmed_at` set, and either no account or an account whose
  `email_verified_at` is set, minus suppressed addresses.
- **Header:** `email,user_id,consented_at,confirmed_at,wording_version,source,unsubscribe_url`.
  Times are ISO 8601 UTC. `unsubscribe_url` is that row's signed link.
- **Formula escaping:** a cell starting with `=`, `+`, `-`, `@`, tab or carriage return gets a `'`
  prefix.
- **No copy kept:** `Cache-Control: no-store`, streamed and never stored server-side. Each export is
  logged and writes a `marketing_consent_exports` row.
- **No base URL:** with neither `Marketing:PublicBaseUrl` nor `CloudStorage:PublicBaseUrl` set, the
  export answers `503 public_base_url_missing` rather than ship links that go nowhere.

**The process rule for every send:**

1. The campaign send reads the sendable list itself immediately before it sends, so no manual export
   is needed. An export taken earlier may include people who have since withdrawn; do not send from one.
2. Run `dry-run`, then `test-send`, then **open the test message and follow its unsubscribe link** (this human check
   is the first pre-send step; the system records that a test went out, not that anyone looked at it), then `send`
   with the recipient count you confirmed. See
   [Campaigns (Brevo)](#campaigns-brevo). The unsubscribe link is Brevo's placeholder, not our signed
   `unsubscribe_url`; the campaign client sets no custom `List-Unsubscribe` header.
3. Suppressions flow back automatically: the Brevo webhook and the pre-send read of the managed list
   withdraw anyone who unsubscribed or complained, and mark hard bounces undeliverable.

**The CSV export reads local state only.** It never reads Brevo, so it cannot know about an unsubscribe or
complaint whose webhook event was lost. A lost event is only caught by the pre-send read of a list send.
A dry run is not a read of Brevo either. Whoever uses the CSV should therefore run a dry run first, and
should know that the export may still include a person whose unsubscribe event never arrived.

## Contact sync and names

Confirmed contacts are kept at Brevo continuously, with a first and last name, so a campaign can greet
people. The local table stays the source of truth: Brevo is a copy that the sync worker keeps current.

### Names

- **Sources and precedence.** `Admin` beats `Account` (for a row with a user id), which beats `Form`. Admin names are never
  touched by the reconciler or by a sign-up.
- **Account.** For rows whose `name_source` is empty, `Form` or `Account` and whose status is `Pending` or `OptedIn`, the name
  reconciler follows the account's display name. It replaces a `Form` name with the account name, and clears a `Form` name when
  the account name is unusable. It splits the display name: the first word is the first name and the rest is the last name
  (empty when there is only one word). It skips a name that equals the email local part (the signup fallback), that contains
  `@`, or that is blank. The first pass also backfills existing rows.
- **Form.** `POST /subscribe` takes an optional `firstName`. It is stored with `name_source` `Form` only when that request
  creates the row, never on an existing row, and never over an `Admin` name. It is provisional: it is not pushed to Brevo
  until the mailed confirm link is redeemed, because the sync only pushes `OptedIn`, confirmed rows. A name that fails
  validation is ignored and the answer stays the same `202`, so the endpoint reveals nothing. The site applies the same rule
  (`cleanFirstName`), and the contact form shows the field only while the newsletter box is ticked.
- **Form name rule.** After the validation below (which also refuses a run of more than 2 combining marks and a name made only of marks), a
  form name must:
  - be at most 40 characters and start with a letter;
  - contain only letters, combining marks (each directly after a letter), single spaces, hyphen and apostrophe (`'` or U+2019), so digits and symbols are refused;
  - contain no modifier letter (category Lm, such as U+02BC or U+A78F). The one exception is U+30FC (katakana prolonged sound mark), and only inside a Han, Hiragana or Katakana name;
  - contain none of the Hangul fillers U+115F, U+1160, U+3164 and U+FFA0;
  - carry combining marks that follow a letter and are either in U+0300 to U+036F (not U+034F, U+0337 or U+0338) or belong to the same script as the previous letter, with at most 2 marks in a row (3 after Hebrew or Arabic letters). Thai letters are U+0E01 to U+0E45, and Thai marks may follow only a Thai letter;
  - use letters of one script only, from an explicit allowlist: Latin, Greek, Cyrillic, Armenian, Hebrew, Arabic, Devanagari, Bengali,
    Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam, Thai, Georgian and Hangul. Han, Hiragana and Katakana may mix with each other.
  Letters outside the allowlist (Lisu, Canadian Syllabics, Cherokee, fullwidth and mathematical letters, Ethiopic, Khmer, Sinhala, Tibetan)
  are refused: an accepted tradeoff. The site's `cleanFirstName` mirrors the rule by a code port, and the two copies must be kept in step; the API stays authoritative.
  Admin and account names keep the looser rules.
- **Validation (account and admin names).** A name is trimmed, normalized (NFC) and has inner whitespace collapsed. It is refused, not
  truncated, when it is empty, longer than 100 characters, or holds control, format or bidirectional
  characters.
- **Withdrawal.** Every withdrawal path (opt-out, unsubscribe link, Brevo unsubscribe, complaint or blocklist) clears `first_name` and `last_name` in the same save. The pending sweeper also clears the names of `Withdrawn` rows, whatever the Brevo, campaign or sync switches say. `name_source` is kept, and a re-opt-in does not bring a name back.
- **Never recorded.** A name is not written to `marketing_consent_events` and never appears in logs.

### The sync worker

`BrevoContactSyncWorker` runs a pass every `TickSeconds` (60) after a 60-second initial delay.

- **Gating.** It does nothing unless `Marketing:ContactSync:Enabled` is true and both `BREVO_CAMPAIGN_API_KEY` and
  `BREVO_LIST_ID` are set. It does not need `Marketing:Campaigns:Enabled`. `Enabled` defaults to **true**
  (the owner accepted sync on at deploy), so a deploy that has the key and list starts syncing.
  The API logs one line at startup saying whether sync is active, never the key.
- **Pause.** A pass skips all pushing while a campaign is `Queued` or has a live `Syncing` lease, because the
  send pipeline empties and rebuilds the list and checks it for exact equality. It tries again next tick.
- **Names first.** Every `NameReconcileSeconds` (300), or when `RunRequestedAt` is set, the pass runs the name reconciler.
- **Push.** It selects sendable rows (the export set, including the keyed suppression: `OptedIn`, confirmed, not undeliverable, not suppressed) that are dirty:
  `brevo_pushed_at` is empty or older than `updated_at` or `name_updated_at`. It takes at most `MaxPerTick` (200).
- **Write-ahead flag.** Before each push it sets `brevo_synced_at` with a conditional update. If the row changed or was
  purged meanwhile, nothing is pushed. A purge that runs after the flag stages a Brevo erasure.
- **Erasures win.** After each upsert the worker re-reads the row and deletes the Brevo contact again if the row was purged or stopped being sendable. The erasure drain skips while a sync pass is in flight.
- **Guarded first add.** For a row never pushed, or one that opted in again after a withdrawal (`brevo_pushed_at` is cleared with the sync mark), the worker first asks the contact guard to classify the address. It
  pushes only when Brevo does not already hold it as blocked; a blocked or withdrawn contact is withdrawn
  locally and not pushed.
- **Upsert, no blacklist fields.** It calls `POST /contacts` with `updateEnabled` and `FIRSTNAME` and `LASTNAME`
  attributes (an empty value removes the attribute, so a cleared name is cleared at Brevo). `listIds` goes only with the first add; later name
  changes send attributes alone. It never sends `emailBlacklisted` or `emailBlacklist`: a Brevo-side unsubscribe cannot be reversed
  by a push.
- **Result.** On success it sets `brevo_pushed_at` to the time the push started, clears the attempt count and error code. On a Brevo error it
  records `brevo_push_attempts`, `brevo_push_attempted_at` and a stable `brevo_push_error_code`, backs off and goes on to the next row. Logs
  carry counts and codes only.
- **Deletes.** Each pass also deletes contacts that stopped being sendable and drains the erasure outbox, so a withdrawal or an
  erasure reaches Brevo within about a minute. The [erasure worker](#erasure) keeps running; both are idempotent.
- **Nightly reconcile.** Around `NightlyReconcileHourUtc` (3), at most once per 20 hours, it reads the managed list. Blocked members are classified
  by the guard and deleted when withdrawn. Members that are not sendable are deleted. Sendable rows missing from the list get
  `brevo_pushed_at` cleared so the guarded first add re-adds them. A failed list read changes nothing.
- **Sync now.** `POST /admin/contact-sync/run` sets `RunRequestedAt`. The worker checks it about every 10 seconds, so the run starts in 10 to 15 seconds.
- **Status.** `GET /admin/contact-sync` reports the counts and the last run. Each pass updates `marketing_contact_sync_state`. Only one replica runs a pass at a time: the pass holds a lease on that row, owned by a token (the `LastRunAt` value it wrote). It renews or releases the lease only while it still owns it and stops if it loses it. It renews in every phase, before each row or contact in the erasure drain and the unsendable sweep, and every 5 items or 60 seconds in the push and reconcile loops.

### How it fits the send-time rebuild

The send pipeline still empties the managed list and re-imports the sendable set before a send, and checks
the list against it. The import always carries `FIRSTNAME` and `LASTNAME` and sends `emptyContactsAttributes: true`, so an empty value removes the attribute, and after each
imported chunk it sets `brevo_pushed_at` and resets the push attempts so the worker does not push those rows again. Verification is unchanged.

## Account deletion and suppression

`AccountPurgeService` calls `MarketingConsentErasure.StageAsync` inside the purge's single
transaction.

- It deletes the person's consent rows and their events. Rows match by user id, and by normalized
  email only when the account verified that email, so purging an unverified account never erases
  the consent of whoever subscribed with the address it typed.
- For a row that was `OptedIn` or `Withdrawn`, it writes or updates a `marketing_suppressions` entry:
  the lowercase hex HMAC-SHA256 of the normalized email under the `marketing-suppression-pepper`
  platform secret, plus `withdrawn_at`. The key makes the list irreversible without the database's
  protected secret.
- `Pending` rows are deleted outright.
- The name is a column of the row, so it goes with it. A contact already at Brevo is deleted there through the erasure outbox.
- If the hasher is unavailable, the purge throws and retries on the next sweep, rather than delete
  a row without its suppression entry.

The export skips every suppressed address. A later confirmed opt-in through the Inference API (a
confirm click, or a signed-in opt-in from a verified account) removes the entry. The Go signup
opt-in, confirmed by email verification, does **not**: Go has no access to the pepper, so such a
person stays suppressed until they switch marketing emails off and on again in Settings.

The account data export (`AccountExportService`) writes `data/marketing-consent.json`: the consent
row (with `firstName`, `lastName` and `nameSource`) and all its events, matched the same way (by user id, or by email once verified). `marketing_suppressions` is deliberately not exported. See the data map in
[`design/saas-accounts.md`](design/saas-accounts.md#gdpr-data-map).

## The privacy-policy acknowledgement

The policy now covers marketing consent, so the Privacy version moved to `2026-10-12`. Migration
`AddPromotionsAndMarketingConsent` updates the `legal_documents` Privacy row only if it still holds
the shipped default `2026-10`; an operator-published version is never touched. Fresh installs get
`2026-10-12` from `LegalDocumentSeeder`. Terms stayed `2026-10` at that point.

On 2026-10-26 (launch alpha) Terms moved to `2026-10-26`, which is a **re-acceptance**: the contract
party is now a named person, there is a new 14-day refund promise and a forum clause. Privacy moved
to `2026-10-26` with it. The two are shown together on `/accept-terms`, and the Terms change drives
the re-acceptance. Migration `LaunchAlphaLegalVersions` moves each row only while it still holds the
previous shipped default (`2026-10-22` Terms, `2026-10-12` Privacy); an operator-published version is
never touched.

On 2026-10-31 Privacy moved to `2026-10-31`, because the policy now states that uploads and created
videos are automatically safety-screened. Terms stayed at `2026-10-26`. Migration
`PrivacySafetyScreeningVersion` moves the row only while it still holds the previous shipped default.

On 2026-11-05 (avatars) Terms and Privacy both moved to `2026-11-05`, which is a **re-acceptance**:
Terms 9.1 changed from a flat ban on depicting an identifiable person to a consent-based rule with new
duties (keep the subject's consent record, no minors, no public figures unless self, keep the AI
label), and the Privacy Policy now describes avatar data, its lawful basis and retention. The two are
shown together on `/accept-terms`. Migration `AvatarsLegalVersions` moves each row only while it
still holds the previous shipped default (`2026-10-26` Terms, `2026-10-31` Privacy); it ships in the
same release that turns the `avatars` plan feature on, so nobody re-accepts for a feature they cannot
see. A Privacy-only bump stays an acknowledgement, as below.

On 2026-11-10 (recipe marketplace) Terms and Privacy both moved to `2026-11-10`, again a **re-acceptance**:
Terms 10 now carries the gallery licence and the takedown and reply steps, and the Privacy Policy
describes recipe data, moderation, lawful basis and a 12-month retention for reports and the moderation
log. The legal text is English only, as for every legal page. The migration moves each row only while it
still holds the previous shipped default (`2026-11-05`).

On 2026-11-11 (new-user funnel) Privacy moved alone to `2026-11-11`; Terms stay `2026-11-10`. The policy
now describes short-link counts, visit and sign-up source data, what a sign-up link carries, the welcome
questions, first-step records, the website and short-link host, and the reporting tool. Migration
`AcquisitionPrivacyVersion` moves the Privacy row only while it still holds the shipped default
`2026-11-10`. It is an **acknowledgement**, not a re-acceptance. The acknowledgement screen's summary
(`auth.terms.privacyBody`) is version-neutral ("We have changed our Privacy Policy. Please read what
changed before you continue."), so a later Privacy-only bump cannot show an outdated summary.

A privacy notice is information, not consent, so a Privacy-only change asks for an
**acknowledgement**:

- The Go session carries `privacyVersion`, the user's latest **accepted** Privacy version, in the
  JWT claim, the `reelbolt_user` cookie and `TokenResponse`.
- `web/proxy.ts` sends a session whose `privacyVersion` is behind the served one to
  `/accept-terms`. A session without `privacyVersion` (issued before the deploy) is left alone until
  its next sign-in.
- When only Privacy moved, `/accept-terms` shows "We've updated our Privacy Policy", a summary, a
  link and **Continue**. There is no required checkbox and no "I agree". It posts the same
  accept call, which records a `legal_acceptances` row for the new version.
- The page carries the optional marketing checkbox. When ticked, `PUT /consent { source:
  "acceptTerms" }` runs after the acceptance succeeds, and its failure never holds the user there.
- The gate exempts only `/accept-terms` and `/settings/data` (**Settings → Your data**, which
  holds only the data export and account deletion cards; `/accept-terms` links to it).
  The rest of `/settings` stays gated. Sign-out (`/api/auth/logout`), `/unsubscribe` and
  `/marketing/confirm` are public paths, so the gate never reaches them.

Design detail is in [`design/saas-accounts.md`](design/saas-accounts.md#privacy-acknowledgement-and-privacyversion).

## Configuration

| Config key | `.env` variable | Default | Meaning |
|---|---|---|---|
| `Marketing:PublicBaseUrl` | `MARKETING_PUBLIC_BASE_URL` | empty | Origin of the confirm and unsubscribe links. Empty falls back to `CloudStorage:PublicBaseUrl` (`PUBLIC_BASE_URL`). With neither, subscribe still records the row but sends no mail, and the export answers `503`. |
| `Marketing:ConfirmTtlDays` | none | `7` | How long a confirm link is valid (1 to 30). |
| `Marketing:ConfirmMailboxCooldownMinutes` | none | `10` | At most one confirmation mail per address, and per canonical mailbox, per this many minutes. See [Sending limits](#sending-limits). |
| `Marketing:ConfirmMailboxPerDay` | none | `3` | Confirmation mails per canonical mailbox per 24 hours. |
| `Marketing:ConfirmPerIpPerHour` | none | `5` | Confirmation mails per client IP (IPv6 per /64) per hour. |
| `Marketing:ConfirmPerDomainPerHour` | none | `30` | Alarm threshold: confirmation mails per recipient domain per hour. |
| `Marketing:ConfirmGlobalPerDay` | none | `200` | Alarm threshold: confirmation mails in total per 24 hours. |
| `Marketing:ConfirmGlobalHardCeilingPerDay` | none | `1000` | Hard ceiling: above it nobody gets a confirmation mail. |
| `Marketing:SiteForwardToken` | `SITE_FORWARD_TOKEN` | empty | Shared secret that lets the API trust the site's `X-ReelBolt-Client-IP` and treat the request as bot-checked. Empty: that header is always ignored, with a startup warning. |
| `Mail:LogRefKey` | `MAIL_LOG_REF_KEY` | empty | Key of the `rcpt=` log reference. Empty: a random key per process, with a startup warning. See [Log references](#log-references). |
| `Marketing:PendingRetentionDays` | none | `30` | Pending rows older than this are deleted. |
| `Marketing:PendingSweepIntervalHours` | none | `24` | Sweep interval; `0` turns the sweeper off. |
| `Marketing:WordingPath` | none | next to the assembly | Overrides the registry location. |
| `Marketing:ContactSync:Enabled` | `MARKETING_CONTACT_SYNC_ENABLED` | `true` | Turns the [contact sync worker](#the-sync-worker) on. It stays inert without `BREVO_CAMPAIGN_API_KEY` and `BREVO_LIST_ID`. |

Contact sync tuning keys, no `.env` variable: `TickSeconds` (60), `MaxPerTick` (200), `NameReconcileSeconds` (300), `NightlyReconcileHourUtc` (3) and a push backoff.
| site `REELBOLT_API_URL` | `REELBOLT_API_URL` | `http://inference:8080` in compose | Where the site's contact form forwards an opt-in. |
| site `SITE_FORWARD_TOKEN` | `SITE_FORWARD_TOKEN` | empty | Sent as `X-ReelBolt-Site-Token` with the visitor's IP. Empty: neither header is sent. |
| Go relay | `MARKETING_RELAY_PER_DOMAIN_PER_HOUR` | `30` | Alarm threshold: relayed confirmation mails per recipient domain per hour. Logs a warning, refuses nothing. |
| Go relay | `MARKETING_RELAY_GLOBAL_PER_HOUR` | `100` | Hard cap: relayed confirmation mails in total per hour. |
| Go API | `MAIL_LOG_REF_KEY` | empty | The same log reference key as `Mail:LogRefKey`. |

The secrets `marketing-link-token-k1` and `marketing-suppression-pepper` are generated on first use
in `platform_secrets` and stored encrypted. They are not `.env` values. Losing them invalidates
every unsubscribe link and every suppression entry, so back them up with the database.

**The site's contact form.** When the visitor ticks the box, `site/lib/marketing/subscribe.ts`
forwards `{ email, wordingVersion, source: "siteContact" }` server-side to `POST
{REELBOLT_API_URL}/api/v1/marketing/subscribe`, with a 5-second timeout. Errors are logged without
the address and never fail the enquiry. The forward is skipped when
`REELBOLT_API_URL` is unset. With `SITE_FORWARD_TOKEN` set, the forward also carries the
[trusted site forward](#the-trusted-site-forward) headers. A submission that fails the site's bot
checks is never forwarded (see [`marketing-site.md`](marketing-site.md#contact-form-abuse-protection)).

## Campaigns (Brevo)

Marketing mail goes out as a Brevo email campaign to a managed Brevo list. Our consent list stays the
source of truth. Confirmed contacts are kept at Brevo by the [contact sync](#contact-sync-and-names); before each send the worker also rebuilds the Brevo list from it, and a contact that is no
longer sendable is deleted at Brevo. The list send is off by default (`Marketing:Campaigns:ListSendEnabled`
is `false`); the first real send is a owner order. The privacy-policy wording for contacts held at Brevo is a
draft for counsel and is not published.

### Endpoints

All are platform-admin only, under `/api/v1/marketing/admin/campaigns`, and answer `404` while
`Marketing:Campaigns:Enabled` is false. A campaign is a stored draft (`marketing_campaigns`).

| Method and path | What it does |
|---|---|
| `GET /` | List campaigns (`limit`, default 50). |
| `GET /settings` | `{ footerOk, listSendEnabled, testRecipientMasked, brevoConfigured }`. |
| `POST /` | Create a draft from `{ subject, html }`. |
| `GET /{id}` | One campaign, with its `html` and `previewHtml` (the exact HTML sent to Brevo, footer included). `previewHtml` is `null` with `previewError` set when the campaign cannot render (for example a missing footer identity). |
| `PUT /{id}` | Replace subject and html. Draft only (`409 not_draft` otherwise). |
| `DELETE /{id}` | Delete a draft. |
| `POST /{id}/dry-run` | Sends nothing. Returns `{ recipientCount, suppressedCount, sampleMasked (at most 3), footerOk, errors[], listSendEnabled }`. |
| `POST /{id}/test-send` | One Brevo test message to `Marketing:Campaigns:TestRecipient` through the test list. At most 5 per hour per admin. Returns `{ "sent": true }`. |
| `POST /{id}/send` | Body `{ confirmRecipientCount }`. Queues the send and answers `202`. |
| `POST /{id}/refresh-results` | Reads status and statistics from Brevo. `Sent` when Brevo says `sent`. Only `sent` settles a campaign without a human. A `Sending` campaign Brevo reports as `draft`, `suspended`, `cancelled`, `cancelling`, `archive` or not found becomes `SendUnknown`. A `SendUnknown` campaign Brevo reports as `in_process` or `queued` returns to `Sending`; `draft` (Brevo may still be processing a `sendNow`) or any other status leaves it `SendUnknown`. The only way to `Failed` (`not_sent`) is `resolve`. A malformed Brevo status answers `502 refresh_failed`, and a refresh that would break the one-active rule `409 campaign_active`. |
| `POST /{id}/resolve` | Body `{ "outcome": "sent" \| "notSent" }`. The way out of `SendUnknown` after you checked Brevo by hand: `Sent`, or `Failed` with `not_sent`. Allowed from `SendUnknown` only (`409 not_send_unknown` otherwise; `400 outcome_invalid` for any other outcome). Calls Brevo nothing; the admin id is logged. |
| `POST /{id}/duplicate` | From a campaign in any status: a new `Draft` with the same subject and html and no test record, so a failed campaign can be reopened without retyping it. Returns the new draft. |

Refusals carry a code. `send` answers `403 list_send_disabled` while the flag is off; `409` for
`not_draft`, `recipient_count_changed`, `campaign_active` and `unresolved_send` (a campaign is in
`SendUnknown`: resolve it first); `429 rate_limited`. `send` is also refused
with `test_send_required` (no test send of the current subject and html), `no_recipients`, and
missing Brevo configuration (`brevo_key_missing`, `brevo_list_missing`; the test send also needs
`brevo_test_list_missing`). The configuration is checked when the send is requested, before anything is
queued, not later in the worker.
Only one campaign may be Queued, Syncing or Sending at a time.

**Footer, fail closed.** Every campaign carries "ReelBolt is operated by {name}", the postal address,
why the reader receives it, an unsubscribe link and the privacy link. If the controller name or the
address is blank (or the public base URL is unset), nothing renders or sends and dry-run reports
`footerOk: false`. The unsubscribe link is Brevo's `{{ unsubscribe }}` placeholder (see
[unverified](#to-confirm-on-a-real-brevo-account)). The test send's recipient and the footer identity come from
configuration only. Logs carry `rcpt=` refs, never addresses or bodies.

**How the footer is placed.** The admin's HTML is parsed with an HTML5 tree builder (AngleSharp, no scripting, no CSS
engine, nothing fetched), the footer is appended as the last child of the real `body`, and the document is serialized
with its doctype. An unclosed `div`, `template`, `select`, `details`, `script`, comment and so on can no longer swallow
it. The output is parsed again and must show the footer's parent as `body` with the unsubscribe placeholder and the
identity inside it; otherwise the campaign is refused. Refusal codes: `html_too_deep` (more than 400 nested open
elements, or a parse that exceeds its 10 second budget; the 1 MB size limit `html_too_large` is checked first),
`html_no_body` (a `frameset` document) and `footer_placement_failed` (for example an unclosed `plaintext`, or an element
with the footer's id `rb-campaign-footer` in the admin's HTML). The serializer normalizes the markup: attribute values
are double-quoted, tag and attribute names are lower case, `html`, `head`, `body` and table `tbody` appear when
implied, and a downlevel-revealed `<![if ...]>` becomes a comment. `{{ params.x }}` and `{{ unsubscribe }}` are not
encoded. `previewHtml`, the test send and the list send all use the same function, so the preview is byte-identical to
what goes to Brevo. A parser models browsers, not mail clients, and does not stop an admin hiding the footer on purpose
with CSS: the test send stays mandatory, and the human check of it (open it, see the footer, follow the unsubscribe
link) comes before every send.

### The send pipeline

A background worker runs one campaign at a time, in this order. Any failure stops the run and sends
nothing.

1. Drain pending Brevo erasures. Any left: the run fails `erasure_pending`.
2. Read the managed list back. A contact blocked at Brevo is classified (below), and the local state is
   committed before anything is deleted at Brevo.
3. Read the sendable set from our list. Empty: `no_recipients`.
4. Delete from Brevo every contact we synced earlier that is no longer sendable.
5. Remove all contacts from the managed list, import the sendable set in chunks, wait for each Brevo
   process.
6. Read the list back. Its members must equal the sendable set exactly, with no blocked contact. A
   blocked contact is marked locally and the run fails, so the next run drops it.
7. Re-read the sendable set. Any difference: `list_changed`.
8. Check `ListSendEnabled` again, create the Brevo campaign, record `dispatch_started_at`, then call
   `sendNow` once. It is never retried. A `204` is `Sending`; a 4xx answer from Brevo is `Failed`; a transport error, a
   5xx or a restart during the call is `SendUnknown`: check Brevo by hand, then `resolve` it, and never resend
   automatically. Once `dispatch_started_at` is set no path writes `Failed`: a database error recording the outcome after a `204` is retried a
   few times, then the row is left for recovery, which makes it `SendUnknown`. A restart before `dispatch_started_at` is set ends as `Failed` (`interrupted`). Recovery runs
   on every worker pass, not only at start: a `Syncing` row untouched for `SyncStaleMinutes` is settled on the
   next pass. A stale row never defers the erasure drain or the sweep; only a live lease does. If the managed-list
read-back keeps failing, recovery retries it until the row is `SyncRecoveryMaxAgeHours` old (24), then settles it
(`interrupted`, or `SendUnknown` once `dispatch_started_at` is set), logs a warning and stages nothing.

**The managed list belongs to ReelBolt alone.** Recovery stages erasures for addresses on the list with no consent row
only when there are at most `RecoveryMaxStagedErasures` (25) of them. The bound limits how much one recovery pass may
delete from Brevo on its own: a few strays (a person purged while a run was crashing) are cleaned up, but a larger
mismatch is not treated as ours to delete, because it more likely means the list was edited or shared. Above the bound
recovery stages nothing, logs a warning with the count (no addresses) and settles the row as usual. After that warning
the operator compares the list against the consent rows by hand before the next send, because the next send empties
that list (step 5 of the pipeline) and the contacts on it are not recoverable from ReelBolt afterwards. Never point
`BREVO_LIST_ID` at a list shared with anything else or edited by hand: those contacts are not ReelBolt's to delete.

Campaign status values: `Draft`, `Queued`, `Syncing`, `Sending`, `Sent`, `Failed`, `SendUnknown`.

**How a blocked contact is classified.** Brevo's API exposes no blocklist reason, so
`BrevoContactGuard` reads the contact. Complaint: consent withdrawn (source `EmailComplaint`).
Unsubscribe: withdrawn (`BrevoUnsubscribe`). Hard bounce only: marked undeliverable, which is not a
withdrawal. Any other or unknown reason is treated as a withdrawal; mail resumes only after a new confirmed
opt-in. The local state is committed first, then the contact is deleted at Brevo, so an unsubscribe is
never lost. A failed read changes nothing and deletes nothing.

### The webhook

`POST /api/v1/marketing/webhooks/brevo` is anonymous and authenticated by the static header
`X-ReelBolt-Webhook-Token`, compared in constant time. Brevo signs nothing. A blank
`BREVO_WEBHOOK_TOKEN` answers `404`; a wrong or missing header `401`; a body over 1 MB `413` (with a warning log that carries no address). An array
or object body is read and every event in it is processed; the body limit is the only bound. `unsubscribe` and `spam` withdraw consent, `hard_bounce` marks the
address undeliverable, everything else (soft bounces included) answers `200` and does nothing. An unknown
address answers `200` and writes nothing. A failure applying an event answers `500` so Brevo may retry.
The webhook can only reduce sending. nginx limits it to 1 MB (`client_max_body_size` in `nginx/locations.conf`, equal to the API limit) and the `telemetry_ingest` zone.

### Erasure

When an account is purged, each consent row that was synced to Brevo (`brevo_synced_at` set) stages a row
in `marketing_brevo_erasures` inside the purge transaction. The address is encrypted with
`ISecretProtector`. The worker deletes the contact at Brevo (a `404` counts as done) and then the outbox
row, retrying with backoff. A `404` counts as absent only when the body is Brevo's own `document_not_found`;
any other `404` (a proxy, a wrong base URL) is an error, and the row stays and is retried. The erasure worker
runs whether or not `Marketing:Campaigns:Enabled` is set, so turning campaigns off never strands an erasure;
it is inert only while `BREVO_CAMPAIGN_API_KEY` is blank, and then leaves every row in place and makes no
Brevo call. The worker also deletes withdrawn or undeliverable synced contacts without
waiting for the next send.

### Configuration

| Config key | `.env` variable | Default | Meaning |
|---|---|---|---|
| `Marketing:Campaigns:Enabled` | `MARKETING_CAMPAIGNS_ENABLED` | `false` | Off: the endpoints answer 404. |
| `Marketing:Campaigns:BrevoCampaignApiKey` | `BREVO_CAMPAIGN_API_KEY` | empty | Brevo API key (`api-key` header). A secret; env only, never logged. A new variable: `BREVO_API_KEY` is not read. |
| `Marketing:Campaigns:BrevoListId` | `BREVO_LIST_ID` | `0` | The managed list the campaign goes to. `0` or empty means not configured: the host still starts, and a send is refused `brevo_list_missing`. |
| `Marketing:Campaigns:BrevoTestListId` | `BREVO_TEST_LIST_ID` | `0` | A list holding only the test recipient. `0` or empty means not configured (`brevo_test_list_missing` on a test send). |
| `Marketing:Campaigns:ListSendEnabled` | `MARKETING_LIST_SEND_ENABLED` | `false` | Off: nothing is written to the managed list and nothing is sent. |
| `Marketing:Campaigns:WebhookToken` | `BREVO_WEBHOOK_TOKEN` | empty | Secret for the webhook header. Blank: the webhook answers 404. |
| `Marketing:Campaigns:FromAddress` | `MARKETING_FROM_ADDRESS` | `hello@reelbolt.ai` | Sender. |
| `Marketing:Campaigns:TestRecipient` | `MARKETING_TEST_RECIPIENT` | none | Test-send target. Env only. |
| `Marketing:Campaigns:FooterControllerName` | `MARKETING_CONTROLLER_NAME` | none | Footer identity. Env only. |
| `Marketing:Campaigns:FooterVatNumber` | `MARKETING_FOOTER_VAT_NUMBER` | `IT02862070030` | Rendered as "P.IVA ..." after the controller line; blank omits it. |
| `Marketing:Campaigns:FooterAddress` | `MARKETING_FOOTER_ADDRESS` | none | Footer postal address. Env only, never in the repo. |
| `Marketing:ContactSync:Enabled` | `MARKETING_CONTACT_SYNC_ENABLED` | `true` | Contact sync; needs the key and list above. See [Configuration](#configuration). |

Tuning keys with no `.env` variable: `BrevoMinCallIntervalMs` (120), `ProcessPollMs` (2000),
`ProcessWaitSeconds` (600), `ImportChunkSize` (1000), `SyncStaleMinutes` (30), `SyncRecoveryMaxAgeHours` (24), `RecoveryMaxStagedErasures` (25), `ResultsRefreshMinutes`
(10), `TestSendsPerHour` (5).

### Operator runbook

1. **Account and key.** Create the Brevo account. Create a new API key and put it in the control env
   as `BREVO_CAMPAIGN_API_KEY`. Leave `BREVO_API_KEY` as it is. Neither goes in the repo.
2. **Two lists.** In Brevo, create the managed list and a test list. Put the test recipient alone in the
   test list. Set `BREVO_LIST_ID` and `BREVO_TEST_LIST_ID`. The managed list is emptied and rebuilt on
   every send, so never keep other contacts in it. With contact sync on (the default), confirmed contacts are added to the managed list as they confirm, not only at send time. Set `MARKETING_CONTACT_SYNC_ENABLED=false` to stop it.
3. **Webhook.** In Brevo (Transactional or Marketing webhooks) add a marketing webhook to
   `https://<api host>/api/v1/marketing/webhooks/brevo` for the unsubscribe, hard bounce, soft bounce and
   spam events, with a custom header `X-ReelBolt-Webhook-Token` carrying the value of `BREVO_WEBHOOK_TOKEN`
   (generate with `openssl rand -hex 32`).
4. **Flags.** Set `MARKETING_CAMPAIGNS_ENABLED=true`. Leave `MARKETING_LIST_SEND_ENABLED=false` until the
   Owner orders the first send, then set it to `true`.
5. **Authenticate the sending domain** in Brevo (Senders, Domains and Dedicated IPs): add the SPF
   include and the DKIM records (CNAME/TXT) exactly as Brevo shows them, and keep the From domain
   aligned with the DKIM/SPF domain. Start DMARC at `p=none`, read the reports, then tighten to
   `p=quarantine`.
6. **Warm-up.** Start with small batches to the most engaged addresses and grow gradually.
7. **Gmail and Yahoo bulk-sender rules.** One-click unsubscribe, authenticated domain, and a
   spam-complaint rate under 0.3 %. Whether Brevo's unsubscribe is one-click is unverified (below).
8. **Sub-processor.** Brevo needs a DPA and an entry in the privacy policy as a sub-processor (with a
   transfer mechanism if outside the EEA). Open; with counsel.
9. **After a send.** Call `refresh-results`. A `SendUnknown` campaign needs a manual check in Brevo, then `resolve`. A failed campaign can be
   reopened with `duplicate`.

### To confirm on a real Brevo account

Verified live on 2026-11-02 against the control-box Brevo account (list 7, throwaway contacts, deleted afterwards):

- `FIRSTNAME` and `LASTNAME` exist as normal text attributes.
- `POST /contacts` with `updateEnabled` creates a contact (`201`) or updates it (`204`).
- An empty string on an upsert removes the attribute. On an import, `''` does not clear it unless `emptyContactsAttributes: true` is sent (then it does).
- An upsert or an import of a blacklisted contact without a blacklist field leaves it blacklisted.
- `contacts/import` with attributes and `updateExistingContacts` updates attributes.
- `contacts/lists/{id}/contacts/remove` with emails works.

Still unverified (they need a real campaign). The code was built against Brevo's documentation and a fake Brevo in tests. Confirm each before the first real send:

- The exact unsubscribe placeholder (`{{ unsubscribe }}` is assumed). Brevo reportedly adds its own
  footer when the placeholder is missing.
- Whether the unsubscribe is one-click.
- Whether an unsubscribe sets `emailBlacklisted` or `listUnsubscribed` on the contact (list-level unsubscribe behaviour).
- Whether `sendTest` needs the recipient to exist as a contact.
- The permission names on the API key.

Verified on the production account (2026-11-02): `POST /v3/emailCampaigns` with a `tag` field answers
`405 {"code":"method_not_allowed"}` because the plan does not allow campaign tags, and the same body without `tag`
answers `201`. The create call therefore sends no `tag`; nothing reads one (results, the webhook and reconciliation
find a campaign by its Brevo id and the campaign name). Every Brevo rejection logs `status=` and Brevo's own error
`code=` (a short lowercase word, never the response body or an address) so the next refusal can be read from the log.

## Before the first marketing send

These are not code. They need the maintainers and counsel, and all six must be closed before ReelBolt sends
its first marketing email.

1. **Controller identity.** Settled: the controller is Tommaso Celano (sole trader, P.IVA in
   the footer). The postal address lives in the site legal constants and, for email, only in
   `MARKETING_FOOTER_ADDRESS` (config, never in the repo).
2. **Sending tool.** Decided: Brevo (option C); the campaign send and the suppression sync are
   built and the list send is off by default. Still open: the sending tool's DPA, the sub-processor
   entry and the privacy wording for contacts held at Brevo (a draft, not published). The legal items
   are with counsel. Contact sync on at deploy was accepted by the owner, with the DPA and sub-processor entry still open with counsel. Also open: domain authentication (SPF, DKIM, DMARC) and the
   [live-account checks](#to-confirm-on-a-real-brevo-account), all unverified.
3. **Per-send process.** Adopt the [process rule](#the-admin-export-and-the-per-send-process-rule).
4. **Retention of consent proof.** Decided: while opted in, plus 3 years after withdrawal, then the
   suppression entry only. Counsel still to confirm.
5. **Counsel review** of the Garante 24-month inactivity re-confirmation, the hashed suppression
   list surviving erasure, and the final policy text.
6. **Promo-credit terms.** Closed (option A): the conditions are shown in the app. The
   2026-10-26 Terms also state the first-video grant conditions, and that Terms bump is a
   re-acceptance anyway.

## First-run reminder mail

A one-time reminder to users who created a project and have not run it is built but **off** and sends nothing. Whether
it is a service message or marketing was decided by the owner: marketing reading, opted-in users only. See
[design/first-run-reminder-mail.md](design/first-run-reminder-mail.md). **Precondition to enabling:** the Brevo DPA and
sub-processor entry are closed. Its default consent mode requires an `OptedIn`
row here and reuses the `u1.` unsubscribe link.

## Draft privacy wording for counsel (NOT published)

This is a draft for the next batch sent to counsel. It is not on the site and the legal constants are untouched.

> If you give us your first name, from your account or typed on a form, we keep it with your marketing
> record to greet you in our emails. When you have confirmed your subscription, we hold your email address
> and first name at our email provider, Brevo, between sends. We remove them from Brevo when you unsubscribe or
> when your account is deleted. The legal basis is your consent, which you can withdraw at any time. We keep
> the record for the same period as the rest of the marketing consent record: while you are subscribed, plus
> three years after you withdraw.
