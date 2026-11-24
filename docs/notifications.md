# Notifications (job and file e-mail)

ReelBolt sends e-mail when things happen in a workspace: a video job finishes or fails, a file is
added, or a thumbnail graphic is edited. Each of the seven kinds is independently on or off per user,
and delivery goes through the installation's existing mailer (Cloudflare Email Sending when
configured, else SMTP, else the console). This document is the developer reference for that system.
The user-facing point of view is in [Notifications (user guide)](user-guide/account/notifications.md).

The design goal throughout is that **a notification never breaks the thing it is about.** A job
completes, a file uploads, and a graphic edits normally even when the e-mail cannot be sent; the
e-mail is a best-effort side channel that records itself and logs on failure, never throwing back
into the original request.

## The seven categories and their wire names

There are exactly seven notification categories. The fixed wire names are constants on both sides and
are what the Go API switches on:

| Category | Wire name (`type` / preference key) | Trigger |
|----------|-------------------------------------|---------|
| Job finished | `job_succeeded` | a `execution.completed` workflow event |
| Job failed | `job_failed` | a `execution.failed` workflow event |
| File added | `file_created` | a project file upload completes |
| Thumbnail graphic edited | `graphics_edited` | a 2xx write to the image-document API |
| Review notes and decisions | `review_activity` | a guest note or decision on a shared review link (see below) |
| Avatar about to be deleted | `avatar_retention` | seven days before an avatar reaches its keep-until date (see below) |
| Social publishing | `social_publishing` | a social post was published or failed, or an account needs reconnecting (see below) |

These names appear in three places that must stay in lockstep: the Go constants
(`api/models/notification.go`), the Go `templateSpecs` / `AllTemplates` mail entries
(`api/services/smtp_service.go`), and the TypeScript `NotificationCategory` union
(`web/lib/types/notifications.ts`). They are byte-identical across all three.

## Review activity (fifth category, toggle, default on)

`review_activity` is published by the Inference API as a `UserActivityNotificationRequested` event with
`Type = review_activity` and a `RecipientUserId` (the review link's creator); contract in
[`docs/editor.md`](editor.md#notifications-c-event-additions-for-the-go-api-half). The Go API
(`HandleActivityEvent`) mails **that one recipient only** with the `review_activity` template (subject
uses the timeline name, body the note or decision text, all HTML-escaped). It skips silently when the
recipient is missing, is not a member of the event's organization, or is the actor.

**There is a toggle.** `user_notification_preferences.review_activity` (boolean, NOT NULL, DEFAULT true,
added by migration `AddReviewActivityNotificationPreference`) holds it, so existing rows read as on. The
Go API reads the recipient's stored value (absent row = on) before mailing; when off, no mail is sent and no delivery-log row is written, like the other categories. `GET/PUT /api/v1/notifications/preferences` carry it as `reviewActivity`; a PUT that omits it leaves it unchanged. Deliveries land in the same delivery log.

## Avatar retention (sixth category, toggle, default on)

`avatar_retention` is published by the Inference API's `ProjectAvatarRetentionService` (through
`UserActivityNotifier`) as a `UserActivityNotificationRequested` event with `Type = avatar_retention` and a
`RecipientUserId` (the member who attested the avatar's consent), seven days before the avatar is deleted at
its `retention_until`. The Go API (`handleAvatarRetention`) mails that one recipient only with the
`avatar_retention` template: subject is the avatar's display name, body the deletion date and a link to
`/app/projects/{id}?tab=avatars`, where the owner can confirm the consent again to keep it. The subject's own
name is never in the event. It skips silently when the recipient is missing or no longer a member.

**There is a toggle.** `user_notification_preferences.avatar_retention` (boolean, NOT NULL, DEFAULT true,
migration `AddAvatarRetentionNotificationPreference`), carried as `avatarRetention` on
`GET/PUT /api/v1/notifications/preferences` and shown on the web Notifications settings page ("Avatar about
to be deleted"). Absent row = on; off = no mail and no delivery-log row. See [avatars.md](avatars.md).

## Social publishing (seventh category, toggle, default on)

`social_publishing` is published by the Inference API (`UserActivityNotifier.SocialPublishingAsync`) as a
`UserActivityNotificationRequested` event with `Type = social_publishing` and one `RecipientUserId`: the person who scheduled the post
("published", "failed", "check your post") or the person who connected the account ("reconnect needed", once, when a token refresh is
refused: during publishing or a metrics read; later failed refreshes of an already flagged account stay quiet). The Go API mails that
one recipient with the `social_publishing` template when `user_notification_preferences.social_publishing` is on (column added by
migration `SocialPublishingEngine`, default true). Details: [Social publishing](social-publishing.md).

## Preferences: one row per user

A user's on/off choices live in one row in `user_notification_preferences`, keyed by `user_id`, with
one boolean column per category (`job_succeeded`, `job_failed`, `file_created`,
`graphics_edited`, `review_activity`, `avatar_retention`, `social_publishing`; the last three default true in the DB) plus `created_at` / `updated_at`.

- **Schema is owned by the Inference API** through EF Core migration `AddUserNotifications`
  (`inference/src/ReelBolt.Inference.Api/Migrations/…_AddUserNotifications.cs`). The Go API reads and
  writes the table by GORM and **never migrates**, matching the rest of the tenancy and billing
  tables it shares.
- **There is no row for a user until the first write.** Reading preferences for a user with no row
  returns **all seven on** — that is the default, not a stored value. The first `PUT` materializes the
  row.
- **Writing is a real upsert** (`GormNotificationStore.SavePreferences`,
  `api/services/notifications.go`): `Clauses(clause.OnConflict{Columns: user_id, DoUpdates: …})` so a
  second save updates the existing row (advancing `updated_at`, preserving `created_at`) instead of
  being a silent no-op. The `PUT` handler folds a partial patch over the stored row first, so the
  upsert always receives a fully-merged struct and a plain column list is safe.
- A Postgres-gated regression test (`api/services/notifications_gorm_test.go`) asserts the upsert
  actually updates on a second save; it skips cleanly where Postgres is unreachable.
- **An absent table degrades like an absent row.** In a deployment where the Go API (or the web) is
  running before the Inference API's migration has created the tables, a read gets Postgres's
  `undefined_table` (42P01); `errorsIsMissingTable` folds that into the same "absent" state — the
  preferences read answers the all-on defaults, the delivery-log read answers empty, and a delivery
  is recorded with a log line instead of a row. `SavePreferences` deliberately does NOT use this
  check: a switch change that cannot be persisted fails so the page rolls it back, not pretend it
  saved.

## Events: who publishes what

The two kinds of trigger are produced by different services and both land in the Go API:

- **Job finished / failed** are not newly published. The Go API already consumes workflow
  `execution.completed` / `execution.failed` events (it owns the SSE streams for them); those consumers
  additionally call `NotificationDispatcher.HandleWorkflowEvent`. The dispatcher resolves the
  recipient from the execution row and mails.
- **File added / graphic edited** are published by the Inference API as the MassTransit message
  `UserActivityNotificationRequested`
  (`inference/src/ReelBolt.Shared/IntegrationEvents/UserActivityEvents.cs`). The Go API self-declares
  and binds the `ReelBolt.Shared.IntegrationEvents:UserActivityNotificationRequested` fanout exchange
  and routes it to `NotificationDispatcher.HandleActivityEvent`
  (`api/services/rabbitmq_service.go`).

The wire shape is a `record` with `OrganizationId`, `ActorUserId` (nullable — null when the action was
system-triggered), `ProjectId`, `Type` (`file_created` / `graphics_edited`), `Subject` (the added file's
name, or the edited graphic's subject line), an optional `Details` line, and `OccurredAt`. Go decodes
it into `UserActivityEvent` (`api/services/notifications.go`); Go's `encoding/json` is
case-insensitive, so the C# PascalCase wire form decodes cleanly.

**Publishing is best-effort.** In the Inference API, `UserActivityNotifier`
(`inference/src/ReelBolt.Inference.Api/Services/Notifications/UserActivityNotifier.cs`) wraps the
publish in `try/catch`, logs a warning on failure, and never throws. The two call sites —
`ProjectFilesController.Upload` and `CompleteUpload` — call it inside `catch (OperationCanceledException)
{ throw; } catch { /* swallow */ }`, so a notification failure can never break the upload. (A graphic
edit is reported by the terminal `UserActivityNotificationMiddleware` on a 2xx
`POST/PUT/PATCH/DELETE` under `/api/v1/projects/{id}/image-documents/**`, excluding `/exports` and
`/assets/upload-url`.)

## Recipient rules

- **Job finished / failed** go to the **initiator** recorded on the execution
  (`exec.InitiatedByUserID`). When a run has no recorded initiator (a system trigger), they go to the
  workspace **Owner** (`OrgOwner`) so a failure is never lost. If neither exists, nothing is sent.
- **File added / graphic edited** go to **every member of the workspace except the actor**
  (`OrgMembers`, actor excluded). A system actor (no recorded user) therefore reaches everyone. In a
  one-person workspace, the actor is excluded and no one is left, so a self-action mails no one.

## Delivery and the delivery log

`NotificationDispatcher.sendToUser` (per recipient) checks that recipient's stored preference for the
category; if it is off (or the user has no row, which reads as on), it sends. It renders the template
and calls the mailer, then writes a row to the **delivery log** `user_notifications`
(`models.UserNotification`) recording the category, organization, project, the rendered `title`, the
recipient, and whether it was delivered. A send failure is **recorded and logged, not thrown** — the
loop moves on to the next recipient.

Delivery uses `DefaultMailer()` (`api/services/smtp_service.go`), whose provider precedence is
**Cloudflare Email Sending > SMTP > console**: `ChooseMailerProvider` picks Cloudflare when
`CloudflareToken` and `CloudflareAccountID` are set, else SMTP when a host and from are set, else the
console logger (which renders the mail and logs that it did not send it, naming the recipient only by
its `rcpt=` reference; the mail's link is printed only with `APP_ENV=development` and never for
`marketing-confirm`). The `From` address is shared
across providers on purpose, so one identity sends the deployment's mail. No new configuration variables
were introduced for notifications — they ride the existing mailer config.

Each of the seven categories has a `.html` and a `.txt` template under `api/services/email_templates/<locale>/` (en fallback; subject in `email_templates/subjects.json`)
(`job_succeeded`, `job_failed`, `file_created`, `graphics_edited`), each with a `templateSpecs` and an
`AllTemplates` entry. `MailData` carries `OrgName`, `ProjectName`, `FileName`, `ActorName`,
`ErrorDetail`, and `Link`. Because `MailData` has no base-URL field, templates **cannot hyperlink to
the app**; job mails link to the execution, file/graphic mails link to the project's files page, and
the footer points to **Settings → Notifications** in plain text.

## The HTTP endpoints

Both are on the Go API, registered in `api/handlers/notifications.go`:

| Method | Path | Description |
|--------|------|-----------|
| `GET` | `/api/v1/notifications/preferences` | The caller's seven booleans (all-on defaults when no row exists yet). |
| `PUT` | `/api/v1/notifications/preferences` | Partial patch of one or more of the seven booleans; merges over the stored row and upserts. |
| `GET` | `/api/v1/notifications?limit=N` | The caller's most recent delivery-log rows (default `N` capped by the handler). |

## Routing through nginx

The browser reaches these endpoints only through nginx, where **the `/api/v1/` catch-all proxies to
the Inference API** — the Go API is reached only through its explicit `location` blocks
(`nginx/locations.conf`: auth, admin, orgs, runners, workflows, …). The notifications endpoints have
their own pair of blocks (`location = /api/v1/notifications` and `location /api/v1/notifications/`,
both proxying to `$go_api` with the cookie-derived `Authorization` header). Without them the
catch-all sent every notification call to the Inference API, which has no such route and 404s it,
so the settings page showed "Could not load your notification settings" even with everything else
healthy. Any new Go API route under `/api/v1/` needs the same treatment.

## The dashboard page

`Settings → Notifications` (`web/app/(app)/settings/notifications/page.tsx`) renders the seven switches
(one per category, all on by default) and a **Recent activity** list from the delivery log. Each switch
is optimistic: it flips immediately, `PUT`s the one changed key, and rolls back on error. The API
client is `web/lib/api/notifications.ts` over the two preference endpoints and the delivery-log list;
the SWR hooks are `web/lib/hooks/use-notifications.ts`. The nav entry is in
`web/components/shell/NavLinks.tsx` under the Settings group.

## The internal-mail allowlist

The internal-mail allowlist (`AllTemplates`) includes the new templates, but its `mailData` mapping
does not carry the new `MailData` fields, so posting one of these templates through the internal-mail
path answers `400`. That is expected and harmless — notifications are sent through `DefaultMailer()`,
not the internal-mail path — and is noted here so it is not mistaken for a bug.
