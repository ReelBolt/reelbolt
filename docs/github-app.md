# GitHub App integration (link a project to a repository, pull on push)

A ReelBolt project can be linked to a GitHub repository, so that a `git push` updates the project
without anybody clicking anything. The owner of the GitHub account creates and installs the GitHub App
himself — ReelBolt never creates an app, never asks for a personal access token, and never writes to a
repository. This document is the developer and operator reference for that integration: what it does,
what "pull" means, what the owner must configure, and the security rules the implementation is built
around.

The code lives in `inference/src/ReelBolt.Inference.Api/Services/GitHub/` (the app client, the
signature verifier, the link service, the pull service and the delivery worker) and
`Controllers/GitHubWebhookController.cs` + `Controllers/ProjectGitHubController.cs`. The feature is
inert until it is configured: with no app id, no private key or no webhook secret, the webhook answers
`503` and linking a repository is refused.

## What the integration does, in one paragraph

An owner links one of his projects to one repository. From then on, every push to that repository's
linked branch is delivered to ReelBolt as a signed webhook, verified, and materialized into the
project's file library. He can also pull on demand. Nothing else changes: the repository is a source of
files for the project, not a place ReelBolt deploys to, and ReelBolt has read-only access.

## What "pull" means for a ReelBolt project

A pull makes the project's **GitHub-sourced files equal the repository tree at one commit**. It is a
reconciliation, not an import:

| In the push | What happens in the project |
|---|---|
| A path that is new | A project file is created (category `userFiles`, path relative to the repository or to the link's path prefix). |
| A path whose content changed | The existing project file is rewritten in place. Its id stays the same, so nothing that references it breaks. |
| A path that is gone from the tree | The project file and its stored object are deleted. |
| A path whose content is byte-identical | Nothing at all: no download, no upload. |
| A zero-byte file (`.gitkeep`) | Written like any other file, with size 0. |
| A file GitHub lists but will not hand over (its blob answers 404, or is not base64) | Skipped and counted as skipped; the rest of the pull completes. |
| A path a person uploaded or an agent wrote | Nothing. A pushed file never takes a path that already belongs to someone else's file, and it is counted as skipped. |

Only rows a pull wrote are ever reconciled. Every one carries `project_files.source_kind = 'github'`
and remembers the git blob SHA it was decoded from in `project_files.source_revision`; a row written by
an upload or an agent has neither, and a pull cannot see it, rewrite it or delete it — whatever it is
called.

Only the files the link's **selection** names are pulled — every file when it names nothing (see
"Choosing which files are synced" below) — and a pulled file that leaves the selection is removed
exactly like one deleted in the repository. Two directories are never pulled: `.git/` and
`node_modules/`. Everything else is, including binary assets. A pulled file keeps its path in the
repository (`video/scene.tsx`), except on a link made with the older single-folder option
(`pathPrefix`), whose files keep their path **relative to that folder**, so a project linked to
`video/` shows `scene.tsx`. A pull reports a file as *skipped* only when it was selected and could not
be written (over `MaxFileBytes`, or its path already belongs to an upload or an agent's file); a file
that was never selected is not counted at all.

When a pull fails, the reason is written to `project_github_links.last_error` **in plain language** —
it is what the dashboard shows the project's owner — and the operator detail (REST paths, config keys,
GitHub's status) goes to the log. Every exception the pull raises itself is worded that way, and a
GitHub failure is re-worded by status (`401`/`403` "refused ReelBolt access", `404` "could not find",
`429`, `5xx`). A refusal the owner fixes by choosing fewer files is a separate exception type,
`GitHubSyncRefusedException`, with a code (`too_many_files`, `selection_too_large`,
`repository_too_large`, `selection_unreadable`) and the two numbers. A fault that is not GitHub's —
storage, the database, the network — is never worded as a GitHub failure (see "Syncs run in the
background").

## Syncs run in the background

"Sync now" (`POST /api/v1/projects/{id}/github/pull`), the first sync of a new link (`PUT`) and the
re-sync after a change of branch or selection (`PATCH`) do **not** pull inside the HTTP request. A
pull downloads every changed file one blob at a time, and for a few hundred files that takes longer than
nginx (60 s by default), Cloudflare and the browser will hold a request open: the proxy logged `499`, and
because the pull ran on the request's cancellation token, the abandoned request cancelled the pull too.

So the request does everything that can be decided at once — the project access check, "is GitHub
configured", the person check (`ResolveLinkedAsync`), the cooldown (`429 sync_too_soon` with
`Retry-After`) — then takes the project's slot in `GitHubSyncThrottle` and hands it with the sync to
`GitHubSyncQueue` (a singleton, in-memory channel). `POST .../pull` answers `202 {"status":"queued"}`, or
`202 {"status":"already_syncing"}` when a sync of that project is already queued or running; `PUT` and
`PATCH` answer `200` with the link and `syncing: true`. A `PATCH` that changes nothing queues nothing.

`GitHubSyncWorker` (a `BackgroundService`) runs each queued sync in its own DI scope, under the system
tenant scope, with the host's stopping token only — up to three projects at a time. It calls
`PullBranchAsync`, saves the outcome on the link (the commit and file count, or `last_error` in plain
words) and only then releases the throttle lease. The lease therefore spans "queued or running", which
is exactly what `GET /api/v1/projects/{id}/github` reports as `syncing`; the dashboard re-reads the link
every 3 seconds while it is true and then announces the file count or the reason. One sync per project
at a time still holds, because the lease is taken before anything is queued.

A failure GitHub caused (`401`/`403`/`404` on the tree or branch, `429`, `5xx`) or a refusal the owner
can fix (`too_many_files`, …) is recorded exactly as before. Anything else — the storage backend, the
database — is recorded as "a problem inside ReelBolt, not on GitHub", and a network failure or timeout on
the way to GitHub as "ReelBolt could not reach GitHub"; the exception goes to the log. Queue and lease are
per API process: a restart drops queued syncs (the next "Sync now" or push repairs the project, since a
pull reconciles by content). The webhook path is separate and unchanged — pushes are queued durably as
delivery rows and drained by `GitHubPushSyncService`.

## Empty and unreadable files in a pull

GitHub answers a zero-byte blob (a `.gitkeep`, an empty `__init__.py`) as
`{"encoding":"base64","content":""}`. That is a valid empty file: `GitHubAppClient.GetBlobAsync` returns
an empty array and the pull writes a file of size 0. Until 2026-10 the client required non-empty content
and threw a made-up `502`, which the link then reported as "GitHub had a problem answering (error 502).
This is usually temporary" — deterministically, on every attempt, for any selection that contained one
empty file.

A blob GitHub lists in the tree but will not return — `404` (it can disappear between the tree read and
the download), an encoding other than base64, or base64 that does not decode — is a
`GitHubBlobUnreadableException`. It is deliberately **not** a `GitHubApiException`, so nothing rewords it
as a GitHub outage: the pull skips that one file, counts it in `skipped`, logs a warning with the path,
blob SHA and reason (never content or tokens), and completes. An existing copy of that file keeps its old
blob SHA, so a later pull asks for it again; it is not deleted. Only GitHub refusing or failing a request
— auth, `429`, `5xx`, the tree itself — fails a pull.

## A pull saves its progress as it goes

`ReconcileAsync` saves the rows it has written every 25 files (`GitHubRepositoryPullService.SaveEvery`).
Each saved row carries the blob SHA it came from, so a pull that is interrupted — an API restart, a
GitHub failure half-way, a cancelled request on the webhook path — keeps its work, and the next pull
skips those files as unchanged and carries on from where it stopped. Before, the only `SaveChanges` came
at the very end, so an interrupted pull kept nothing and left its uploaded storage objects orphaned.

What needs a **complete** pass waits for one: deleting files that vanished from the repository or left
the selection, and stamping `last_pulled_commit`, `last_pulled_at`, `last_pulled_file_count` and
`last_error = null`. An interrupted pass cannot know what vanished, so it deletes nothing, and the link
does not claim a commit it has not fully applied.

## Choosing which files are synced

A link does not have to follow a whole repository. The dashboard shows the chosen branch as a tree with
a checkbox on every file and folder, and what is saved is the smallest list of repository-relative
paths that says the same thing: a folder whose every file is ticked is saved as the folder, so files
added to it later are synced too; a single file is saved as itself; and everything ticked is saved as
an empty list, "the whole repository". That list is `project_github_links.include_paths_json` (jsonb,
migration `AddGitHubLinkIncludePaths`); null means every file, which is what every link made before
the column existed already meant.

**A path is matched by whole segment.** `src/video` selects `src/video/scene.tsx` and not
`src/videos/other.tsx`. The server and the dashboard use the same rule (`GitHubIncludePaths.IsWithin`
and `isWithin` in `web/lib/github/selection.ts`).

**Every path is validated before it is stored** (`GitHubIncludePaths.TryNormalize`): trimmed, leading
and trailing `/` removed, and refused with `400 invalid_include_paths` when it is empty, contains `.` or
`..` or an empty segment, a backslash or a control character, is longer than 512 characters, or when
there are more than 500 of them. The stored list is canonical — de-duplicated, sorted, with every path
already covered by a chosen folder dropped — so two selections that mean the same thing store the same
bytes. A stored value that does not pass the same check on the way back is refused (the pull fails with
`selection_unreadable`) rather than read as "everything". On a link made with the old `pathPrefix`,
every chosen path must lie inside that folder.

**The limits apply to the selection, not to the repository.** `MaxFilesPerPull`, `MaxTotalBytes` and
`MaxFileBytes` are checked against the selected files, so a repository with 4,000 files links fine as
long as the chosen part fits. The file listing carries the three limits, the dashboard keeps a running
count against them, and a selection over a limit cannot be linked or saved — the reason is shown with
the numbers. A new link starts with every file ticked when the repository fits, and with nothing ticked
when it does not.

**Changing the selection** (`PATCH /api/v1/projects/{id}/github`) saves it and queues a re-sync with
`force` (it runs in the background, see "Syncs run in the background"), because the commit is the same but the set of files is not: files that left the selection are
removed and new ones arrive, and files still chosen are not downloaded again (blob-SHA keyed). A change
that normalizes to what is already stored — the same selection spelled differently, the same branch —
answers `200` with no sync, before any sync slot is taken, so re-sending a selection can never force a
pull. The branch can be changed the same way; it is checked against GitHub (`404 branch_not_found`).

**Branch names are validated before they reach GitHub.** A branch name ends up in a REST path
(`/git/ref/heads/{branch}`), so every one from a request — linking, changing, listing files — goes
through `GitHubBranchName.IsValid` (`git check-ref-format` rules: no empty, `.`-leading or `.lock`
segment, no `..`, `@{`, control character, space, `\ ~ ^ : ? * [ %`, no leading `-`, no trailing `/` or
`.`, at most 255 characters) and is refused `400 invalid_branch`. `GitHubAppClient.Seg` refuses an
empty, `.` or `..` path segment on its own as a second line.

**On-demand syncs are throttled per project** (`GitHubSyncThrottle`, a singleton): one sync at a time
(the background worker holds the slot until the pull ends), and a "Sync now" or a change of selection is
refused `429 sync_too_soon` (with `Retry-After` and `retryAfterSeconds`) for 20 seconds after the
previous one started. A "Sync now" while a sync is still queued or running answers
`202 {"status":"already_syncing"}` instead. A change that is refused or changes
nothing withdraws the cooldown it took. The window is per API process; the pull is idempotent, so one
that slips through on a second replica is wasted work, never wrong work. Webhook pushes are not
throttled here — they are queued and drained by the delivery worker.

Pulled files are deliberately **not** queued for the LLM summarizer or the embedder. A push arrives
with nobody watching, and a bill the owner never asked for is not a side effect of installing an app;
`summary_status` is set to `Done` (the same idiom the media-upload path uses for "nothing to
summarize") and `indexing_status` to `NotIndexed`. Use `POST /api/v1/projects/{id}/files/reindex`
when semantic search over a repository is actually wanted.

Unlinking a repository removes the link and **nothing else**: the files a previous pull wrote stay.
Silently deleting a customer's source tree because he detached a repository would be the more
surprising of the two behaviours.

## Security: the signature is the only credential

The webhook endpoint is anonymous by construction — GitHub is the caller, so there is no session and no
bearer token. The **only** credential is the `X-Hub-Signature-256` header: an HMAC-SHA256 over the
exact bytes of the request body, keyed with the app's webhook secret. It is verified before anything is
written, and a request that fails is refused and leaves no trace — not a delivery row, not a job, not a
log line containing the body.

Three properties are not negotiable and are enforced in code:

- **Constant-time comparison.** The digest is compared with `CryptographicOperations.FixedTimeEquals`.
  A byte-wise comparison would return as soon as it found a differing byte, letting an attacker who can
  time the endpoint recover the correct HMAC one byte at a time and then forge any delivery — including
  a push that overwrites a customer's project.
- **No secret means no acceptance.** If the webhook secret is not configured the endpoint answers
  `503`, not `200`. There is no "skip verification when unconfigured" branch, because an endpoint that
  cannot verify must not accept anything. The same is true of a missing header, a wrong scheme
  (`sha1=…`), a malformed or wrong-length digest, or a body that changed after signing.
- **No secret is logged.** The webhook secret, the app private key and the installation access tokens
  never appear in a log line. The rejected-body path logs the fact of the rejection, not the body,
  which is attacker-controlled text.

What a verified delivery can reach is bounded by the same principle: the payload names a **repository**,
and the project is the one that repository is linked to — resolved by GitHub's numeric repository id.
There is no field in a webhook body that names a ReelBolt project, a user or an organization, so a
verified push has no way to touch a project other than its own.

## The repository link is authorized twice

Creating a link requires two independent things to be true, and they are checked in this order:

1. **The caller may edit the project.** Every action on `/api/v1/projects/{projectId}/github`
   goes through the same `IProjectAccessService` check as the rest of the project API — `View` to read
   the link, `Edit` to create or remove one. A project in another organization is **404**, never 403.
2. **The app is really installed on that repository.** ReelBolt asks GitHub, as the app
   (`GET /repos/{owner}/{repo}/installation`), which installation covers the repository. A caller cannot
   assert this, and a repository the app cannot see has no installation, so it is refused with 404.
   The repository id, its default branch and the installation id all come from GitHub's answer, never
   from the request body.

There is a third rule, which is the cross-tenant one. GitHub can tell ReelBolt that the app is installed
on a repository, but it cannot tell ReelBolt **which organization installed it**. Without a rule here,
any user who may edit any project could link a colleague's private repository — GitHub would happily
deliver its pushes, because the app is installed on it — and read its contents out of his own project.
So the first organization to link a repository under an installation **claims that installation**
(`github_app_installations`), and every later link under the same installation from a different
organization is answered **404**, the same answer as a repository that does not exist. Never a 403,
which would confirm the repository and the installation are there.

**Everything that reaches a repository on somebody's behalf makes the same person check.** Linking,
browsing a repository's files or branches, changing a link's selection or branch, and "Sync now" all
require the caller's own connected GitHub account (in the active workspace) to list the repository —
`409 github_account_not_connected` without a connection, `404 repository_not_found` when the account
cannot see it — and, for an existing link, to reach it **through the installation the link pulls
with** (`GitHubLinkService.ResolveLinkedAsync`). The installation alone is never enough: it can read
everything the App was installed on, which is far more than any one person may. A push delivered by
the webhook is the one path without a person, and it is bounded by the signature and the link instead.

The trust chain for a delivery is therefore: a valid signature (GitHub sent this) → a repository id (in
a signed body) → at most one link row (created only by an authorized editor, under an installation the
organization owns) → one project. Each link is a single hop; none of them can be skipped.

## Connecting a GitHub account (how the repository list is possible)

The integration above authenticates as the **App**. That is the right credential for pulling a linked
repository, and it is structurally unable to answer the question the dashboard actually asks first:
*which repositories may this person choose from?* An installation token has no notion of a person, and
it sees only what the App was installed on.

So there is a **second, different token**: a **user-to-server** access token, obtained from the same
GitHub App through its OAuth flow. It represents the person, not the installation.

### Why the App needs client credentials, and what the owner had to change

The App may have been created without them, because an earlier instruction said to leave the callback
fields empty — ReelBolt identifies people through its own session, so OAuth looked unnecessary. That
instruction was **wrong for this feature** and has been corrected in
[`docs/github-app-setup.md`](github-app-setup.md). The owner must:

1. **Enable user authorization** — fill in the App's **Callback URL** with
   `https://<host>/api/v1/github/oauth/callback`. Without a callback URL there is nowhere for GitHub to
   send the browser and no flow at all.
2. **Generate a client secret**, and note the **client id** (`Iv1.…` — *not* the App ID).
3. **Subscribe to the `github_app_authorization` event**, so a revocation is reported to ReelBolt.
4. Optionally (recommended) **opt in to expiring user tokens**, so a leaked token is worth eight hours
   rather than forever.
5. Make the App installable on **any account**, so it can reach a customer's private repositories.

The **permissions do not change**. A GitHub App requests no scopes; its permissions come from its
registration, and this one has `contents: read` and `metadata: read`. A user access token is the
**intersection of the App's permissions and the person's own access** — so connecting an account widens
nothing. It can read file contents and metadata of repositories that both the App and the person could
already read, and nothing else.

### The flow

```
POST /api/v1/github/account/connect   (session required)
   │  mints a single-use state row, sets an HttpOnly binding cookie named
   │  reelbolt_github_oauth (SameSite=Lax, Path=/api/v1/github, HttpOnly),
   │  and answers {"authorizeUrl": "https://github.com/login/oauth/authorize?…"}
   ▼
browser → github.com/login/oauth/authorize?client_id=…&redirect_uri=…&state=…&
          code_challenge=…&code_challenge_method=S256&scope=offline_access
   ▼
GET /api/v1/github/oauth/callback?code=…&state=…   (anonymous)
   │  verifies all three defences, exchanges the code server-to-server
   │  with the client secret, calls GET /user, stores the token encrypted
   ▼
302 /app/projects/{projectId}?tab=repository&github=connected
```

**Three independent defences, all required before anything is exchanged:**

| Defence | What it stops |
|---|---|
| `state`, stored as a SHA-256 and spendable **exactly once** | A forged or replayed callback. A signed-only state would prove we minted it but could be replayed by anyone who saw it; a consumed row cannot. |
| A second random value in an `HttpOnly` cookie, stored as a SHA-256, checked with `CryptographicOperations.FixedTimeEquals` | Somebody who learned the `state` — a shared screen, a leaked referrer — but is not the browser that started the flow. A wrong cookie deliberately does **not** consume the ticket, so this is a check rather than a denial of service. |
| **PKCE** (`S256`), verifier stored encrypted | An intercepted authorization code. GitHub supports PKCE for Apps and this is the documented reason to use it. |

**Whose account is connected is read out of our own state row, never out of the callback.** The user id
and the organization are written at `connect` and read back at `callback`, so a valid code
intercepted on its way back cannot be aimed at somebody else's session. Every answer is a redirect to a
fixed relative path built from a `Guid` we stored and an outcome from an enum — nothing from the query
string reaches the `Location` header, so the callback is not an open redirect.

### Where the token is stored, and how

| Question | Answer |
|---|---|
| **Where** | `github_account_links.protected_access_token` / `protected_refresh_token`. |
| **How** | Encrypted at rest with `ISecretProtector` — ASP.NET Core Data Protection, the same `dpkeys` ring the inference-provider keys use, shared by both services. The column holds ciphertext; a plaintext token there would be a credential that reads a customer's private repositories. |
| **Scoped to** | One row per **`(user_id, organization_id)`**, unique. The token can read the person's private repositories, so *which workspace may spend it* is a decision rather than a detail: a connection made in one workspace is invisible to another, and a colleague in the same team cannot borrow it. Connecting the same GitHub account in two workspaces is the intended shape, two rows. |
| **Never leaked** | No endpoint returns it, no DTO has a field for it, and no log line contains it. `GitHubAccountService.GetUserTokenAsync` is `private` and is the only place plaintext exists; it goes to GitHub and nowhere else. `ProseMessage`-style guards keep a code out of a sentence; the token never enters a message at all. |
| **Expiry** | With the App opted in to expiring tokens, an access token lasts 8 hours and a refresh token 6 months. Reaching for an expired token refreshes it, persists the rotated pair, and spends the new one; a **refused** refresh (expired, or revoked) revokes the row and answers `409 github_account_revoked` so the person is told to reconnect rather than shown a `401` from GitHub. |
| **A lost key ring** | If Data Protection cannot decrypt what it wrote (a rotated `dpkeys` volume), the connection is revoked and the answer is `409`, never a fallback to the ciphertext. Same fail-closed direction as the inference-provider keys. |

### Revocation

GitHub sends a **`github_app_authorization`** delivery with `action: "revoked"` and the account in
`sender.id` whenever somebody revokes ReelBolt in their GitHub settings, and a GitHub App **cannot
unsubscribe from it**. That is the documented signal, and
`GitHubDeliveryProcessor.PlanAuthorizationAsync` handles it:

- The row is looked up by GitHub's **numeric account id**, never the login, which the person may have
  changed.
- **Every** connection that account holds is revoked, across every workspace. The person revoked
  ReelBolt, not one workspace's view of it.
- The ciphertext columns are **overwritten with empty**, so "revoked" means the credential is *gone*
  rather than ignored.
- Nothing touches `project_github_links`. A revoked OAuth grant is about a person; pulls use the App's
  installation token, which the event does not affect. Detaching a team's repository because one member
  revoked their personal authorization would be a silent outage — so this deliberately does **not**.

Disconnecting from the dashboard (`DELETE /api/v1/github/account`) does the same thing, recorded as
`disconnected`; a revoked row is kept rather than deleted so the UI can say *why* a connection stopped
working. Reconnecting clears the revocation.

### The pickers

| Endpoint | What it answers |
|---|---|
| `GET /api/v1/projects/{id}/github/repositories` | The repositories the caller may link, from `GET /user/installations` then `GET /user/installations/{installation_id}/repositories` — **with the caller's own token**. |
| `GET /api/v1/projects/{id}/github/branches?repositoryId=` | The repository's branches (one page of 100), default branch first. Without `repositoryId`, the linked repository. |
| `GET /api/v1/projects/{id}/github/files?repositoryId=&branch=` | Every file and folder of a branch, flattened (one recursive tree read at the branch head, at most 20,000 entries), with sizes and the three sync limits — what the file selection tree is built from. Without `repositoryId`, the linked repository and its branch. |
| `GET /api/v1/projects/{id}/github/tree?repositoryId=&path=` | One directory, resolved by **walking down from the default branch head**. Kept for compatibility; the dashboard no longer calls it. |

The file listing reads the whole tree in one recursive request, unlike the old one-level folder
browser. That is deliberate now that a selection is made from it: the pull itself reads the same
recursive tree and refuses one GitHub truncates, so a repository too large to list in one request is
also one that cannot be synced, and the listing says so (`truncated`) rather than offering a choice
that would fail.

**The picker cannot show a repository the person cannot access**, and not because a filter is applied
to it: the list *is* GitHub's answer, and that token is the intersection described above. Two rules sit
on top:

- A repository **this workspace** already follows is marked as taken, so the person does not walk into a
  `409`.
- A repository **another workspace** follows is **absent**, not annotated. The link endpoint answers
  `404` there, and an entry saying "taken" would say out loud what that `404` exists to hide — the same
  non-oracle rule the rest of the tenancy boundary uses.

**The folder browser never accepts a tree SHA from the client.** It takes a repository id and a relative
path, checks the repository is one the *person* can use, and then descends one level per request from
the branch head. That matters: the installation token can read every repository the App was installed
on, so a caller-supplied SHA would let somebody who may choose from repository A enumerate repository
B's file names. A path that is not a folder is `404 folder_not_found`; a repository that is not the
person's is `404 repository_not_found`, the same answer a repository that does not exist gets.

**A repository with no commits** has no tree, which GitHub answers `404` for; the browser returns an
empty root rather than an error, because "nothing to pull yet" is the truth.

### The link is still created by `owner/name`

The picker does not have its own write path. Choosing a repository sends its `owner/name` (plus the
branch and the chosen files) to the one `PUT`, and the server verifies it against GitHub. One write
path, one set of checks.

**A connected account is required.** `GitHubLinkService` refuses a link `409
github_account_not_connected` when the caller has no working GitHub connection in this workspace, and
`404 repository_not_found` when the repository is not one that account lists — checked **first**,
before any App-credentialed lookup — and the repository and installation the App resolves must match
the ones the caller's list names. There is no fallback to the installation claim alone: without the
person check, anyone in any workspace could link (and pull) a repository from an installation nobody
had claimed yet. A deployment with no App client credentials therefore cannot link repositories at
all, and the dashboard says so instead of offering a form that could only fail. The by-name form the
dashboard used to offer is gone for the same reason.

The `PUT` then queues the first sync and answers `200` with `syncing: true`; the files arrive in the
background (see "Syncs run in the background"). A sync that fails is reported later through `lastError`
on the link — the link exists, and saying it failed would be untrue.

## GitHub retries deliveries: idempotency and ordering

GitHub retries, redelivers and can deliver out of order. Every one of those cases is handled
explicitly:

- **Redelivery.** `X-GitHub-Delivery` is GitHub's idempotency key. It is unique-indexed on
  `github_webhook_deliveries`, and a second delivery of the same id is answered `200` with
  `{"outcome":"duplicate"}` and does no work. A duplicate is a 200 and not a 409 on purpose: GitHub
  retries any non-2xx.
- **The same commit twice.** The link remembers `last_pulled_commit`. A push that names it is settled
  as `unchanged` without contacting GitHub at all.
- **Out of order.** A push whose `head_commit.timestamp` is older than the last successful pull is
  dropped as `stale`. The project mirrors **one** commit, so applying a late retry of an older push
  would move it backwards.
- **Per file, too.** Because each row remembers its blob SHA, a pull that does run only downloads and
  stores what actually changed. That is what makes "a redelivered webhook must not duplicate work" a
  fact about the data rather than a hope about timing.
- **A failure is bounded.** A delivery is attempted `GitHub:MaxDeliveryAttempts` times and then parked
  with `outcome=failed` and the reason on the row. The next push to the same branch is a fresh delivery
  and repairs the project, because reconciliation is by content and not by delivery.
- **Slow work does not block the acknowledgement.** A repository can be large and GitHub abandons a
  delivery whose endpoint is slow, so the endpoint writes the delivery and answers `202 queued` in
  milliseconds; `GitHubPushSyncService` performs the pull. A restart between the two loses nothing:
  the row is durable and still unprocessed when the next sweep runs.

## Create the GitHub App (what the owner must do)

**This is done by the owner of the GitHub account, in his own account. ReelBolt cannot and does not
create an app, request a personal access token, or ask for one.**

### Option A — the web form

1. Go to <https://github.com/settings/apps/new> (or
   `https://github.com/organizations/<ORG>/settings/apps/new` for an organization-owned app).
2. Fill in:
   - **GitHub App name**: anything unique, e.g. `ReelBolt Sync`.
   - **Homepage URL**: any URL he controls. It is only shown on the app's page.
   - **Webhook**: **Active**, and **Webhook URL** = `https://<his-reelbolt-host>/api/v1/github/webhook`
     (see "Where the webhook URL points" below).
   - **Webhook secret**: generate one and paste it —
     `openssl rand -hex 32`. It must equal `GITHUB_WEBHOOK_SECRET` on the deployment.
3. **Repository permissions** — exactly two, and no organization permissions:
   | Permission | Access | Why |
   |---|---|---|
   | **Contents** | **Read-only** | Read the commit tree and the file contents it names. |
   | **Metadata** | **Read-only** | Mandatory; GitHub adds it automatically. Used to resolve the repository and its installation. |
   No other permission is needed or asked for. In particular there is **no write permission of any
   kind**: ReelBolt never pushes, never opens a pull request and never changes a repository.
4. **Subscribe to events**: tick **Push**, and nothing else. (GitHub sends a `ping` when the webhook is
   first configured; ReelBolt acknowledges it and does nothing, which is the intended answer.)
5. **Identifying and authorizing users** — the App's **Callback URL** must be
   `https://<his-reelbolt-host>/api/v1/github/oauth/callback`. Leave *Request user authorization
   (OAuth) during installation* unticked, and **opt in** to *Expire user authorization tokens*. This
   section is what makes the repository picker possible; see
   "Connecting a GitHub account" above. (An App created before this feature exists probably has it
   empty — the owner must go back and fill it in, generate a client secret, and subscribe to the
   `github_app_authorization` event.)
6. **Subscribe to events**: **Push** and **GitHub App authorization**. The second is how a revocation
   reaches ReelBolt; a GitHub App cannot unsubscribe from it.
7. **Where can this GitHub App be installed?** **Any account.** ReelBolt is offered to other people,
   and an App restricted to one account cannot reach a customer's private repositories.
8. **Create GitHub App**.
9. On the app's page, **Generate a private key**. A `.pem` file downloads. Its contents are the
   `GITHUB_APP_PRIVATE_KEY` secret. Keep it out of the repository.
10. Note the **App ID** at the top of the page. That is `GITHUB_APP_ID` — **not** the `Iv1.…` client id,
    which is a different value in the **Client secrets** section.
11. **Generate a client secret** there, and note the **Client ID**. They are `GITHUB_APP_CLIENT_SECRET`
    and `GITHUB_APP_CLIENT_ID`.

### Option B — from a manifest

A manifest creates the app in one POST and hands back the secrets. Use a random `state` that he keeps
and compares:

```bash
STATE=$(openssl rand -hex 16)
cat > /tmp/reelbolt-app.json <<'JSON'
{
  "name": "ReelBolt Sync",
  "url": "https://reelbolt.example.com",
  "hook_attributes": {
    "url": "https://reelbolt.example.com/api/v1/github/webhook",
    "active": true
  },
  "callback_urls": ["https://reelbolt.example.com/api/v1/github/oauth/callback"],
  "request_oauth_on_installation": false,
  "public": true,
  "default_permissions": {
    "contents": "read",
    "metadata": "read"
  },
  "default_events": ["push", "github_app_authorization"]
}
JSON
echo "open https://github.com/settings/apps/new?state=$STATE and paste the JSON above"
```

The manifest flow needs a page that posts the JSON as a form field named `manifest` to
`https://github.com/settings/apps/new?state=<state>` (that is what the "Create GitHub App from
manifest" button on GitHub's own settings page does). GitHub then redirects back with `?code=…`, and
that single-use code exchanges for the credentials — **including the webhook secret GitHub generated** —
at:

```bash
curl -s -X POST "https://api.github.com/app-manifests/$CODE/conversions"
```

The response contains `id` (`GITHUB_APP_ID`), `slug` (`GITHUB_APP_SLUG`), `pem`
(`GITHUB_APP_PRIVATE_KEY`), `webhook_secret` (`GITHUB_WEBHOOK_SECRET`) and `client_id` /
`client_secret` (`GITHUB_APP_CLIENT_ID` / `GITHUB_APP_CLIENT_SECRET`). Store all of them and delete the
code; it is single-use. Note that `public: true` and the `callback_urls` entry are what make
connecting an account work at all.

### Where the webhook URL points

`https://<the host that serves ReelBolt>/api/v1/github/webhook` — the Inference API behind the nginx
reverse proxy. `nginx/locations.conf` carries a location for that path (a case-insensitive regex
that also takes a trailing slash, because the API routes those variants to the same action), and it
is deliberately shaped like the Paddle webhook block next to it:

```
location ~* ^/api/v1/github/webhook(/|$) {
    client_max_body_size 2m;
    proxy_set_header Authorization "";
    auth_basic off;
    proxy_pass $inference_api;
}
```

There is no cookie-to-`Authorization` translation (`$auth_header`) because GitHub has no session, and
any client-supplied `Authorization` header is blanked. The body is forwarded untouched — no buffering
or rewriting — because the signature covers its exact bytes. A push payload is JSON prose about
commits and the endpoint refuses anything over 2 MB.

## Install the app on a repository

After the app exists, the owner installs it himself:

1. On the app's page, click **Install App** (or use the install URL
   `https://github.com/apps/<app-slug>/installations/new`).
2. Choose the account, then **Only select repositories** and pick the repository (or repositories) the
   app may read. "All repositories" works too and means every repository he owns matching that
   account.
3. **Install**. GitHub may ask him to authenticate again first; that is his own login, not ReelBolt's.
4. The install is what makes `GET /repos/{owner}/{repo}/installation` succeed for that repository. Until
   it is done, linking the repository answers `404 github_app_not_installed` — ReelBolt refuses links
   it cannot verify, rather than accepting one that would silently never receive a push.

To stop pulling a repository, either unlink it in ReelBolt (the files already pulled stay) or suspend
or uninstall the app on GitHub (pushes simply stop arriving).

## Configure the deployment: environment variables and secrets

| `.env` variable | Config key | Secret | Meaning |
|---|---|---|---|
| `GITHUB_APP_ID` | `GitHub__AppId` | no | The App ID from the app's settings page. |
| `GITHUB_APP_SLUG` | `GitHub__AppSlug` | no | The app slug. It builds `https://github.com/apps/<slug>/installations/new`, which the dashboard offers as **"Give ReelBolt access to more repositories on GitHub"** (returned as `installUrl` by `GET /api/v1/github/account` and the repository list). Optional, but without it that button is not shown. |
| `GITHUB_APP_PRIVATE_KEY` | `GitHub__PrivateKeyPem` | **yes** | The full PEM of the generated private key. A literal `\n` is accepted as a newline, because that is what a pasted multi-line PEM becomes in a single-line variable. |
| (a mounted file) | `GitHub__PrivateKeyPath` | **yes** | Path to the PEM instead of pasting it. Read once, lazily. |
| `GITHUB_WEBHOOK_SECRET` | `GitHub__WebhookSecret` | **yes** | The webhook secret. **This is the only credential the webhook endpoint trusts.** |
| `GITHUB_APP_CLIENT_ID` | `GitHub__ClientId` | no (but required for account linking) | The App's `Iv1.…` client id. **Not the App ID.** |
| `GITHUB_APP_CLIENT_SECRET` | `GitHub__ClientSecret` | **yes** | The client secret, generated under "Client secrets". Makes ReelBolt a confidential OAuth client, so the code exchange happens server-to-server and the secret never reaches the browser. |
| `GITHUB_APP_OAUTH_CALLBACK_URL` | `GitHub__OAuthCallbackUrl` | no (but required for account linking) | `https://<host>/api/v1/github/oauth/callback`. Must equal one of the App's registered Callback URLs **exactly**. Configured rather than derived from the request, because a redirect target taken from a `Host` header is a redirect target an attacker chooses. |

All three of app id, private key and webhook secret must be present. With any of them missing the
endpoint answers `503 github_not_configured` and every link is refused `503
github_app_not_configured`; nothing is half-enabled.

The three OAuth values are a **second, independent** all-or-nothing set. With any one of them missing,
`POST /api/v1/github/account/connect` and the picker endpoints answer `503
github_oauth_not_configured`, and the dashboard explains instead of offering a button that cannot
work. Because linking needs the caller's connected account, **no repository can be linked** on such a
deployment (existing links keep receiving pushes, which need only the App credentials and the webhook
secret).

Tuning keys (optional, with their defaults):

| Config key | Default | Meaning |
|---|---|---|
| `GitHub__ApiBaseUrl` | `https://api.github.com` | REST base; change it for a GitHub Enterprise install. |
| `GitHub__MaxFilesPerPull` | `2000` | Over this, the whole pull is refused rather than half-applied. |
| `GitHub__MaxFileBytes` | `52428800` (50 MB) | Larger files are skipped and counted. |
| `GitHub__MaxTotalBytes` | `536870912` (512 MB) | Declared tree over this refuses the pull. |
| `GitHub__WorkerIntervalSeconds` | `15` | How often the delivery worker looks for work. |
| `GitHub__MaxDeliveryAttempts` | `5` | Attempts before a delivery is parked as failed. |
| `GitHub__OAuthAuthorizeUrl` | `https://github.com/login/oauth/authorize` | Where the browser is sent to authorize. Overridable for GitHub Enterprise; there is no reason to change it on github.com. |
| `GitHub__OAuthTokenUrl` | `https://github.com/login/oauth/access_token` | Where the code is exchanged. A different host from `ApiBaseUrl`: the OAuth endpoints live on github.com, not api.github.com. |
| `GitHub__OAuthStateTtlSeconds` | `600` | How long a started authorization stays completable. |
| `GitHub__MaxRepositoriesListed` | `500` | Ceiling on the repositories one picker response carries, so a person with thousands cannot make one dashboard request unbounded work. |

`docker-compose.yml` passes the four credentials through to the `inference` service. In the cloud
deployment they are secrets, and belong in the same store as `JWT_SIGNING_KEY` and
`PADDLE_API_KEY` — see [`docs/secrets.md`](secrets.md).

## The API

All project routes require an authenticated session and go through the project access check.

| Method | Path | Permission | Answers |
|---|---|---|---|
| `POST` | `/api/v1/github/webhook` | none (HMAC signature) | `503` unconfigured, `401` missing/invalid signature, `400` not JSON or no event header, `200` settled (`ignored_event`, `duplicate`, `no_link`, `ignored_ref`, `installation_mismatch`, `unchanged`), `202` `queued`. |
| `GET` | `/api/v1/projects/{projectId}/github` | View | The link, or `404 not_linked`. `syncing` is true while a sync of the project is queued or running in the background; poll until it is false, then read `lastPulledFileCount` or `lastError`. |
| `PUT` | `/api/v1/projects/{projectId}/github` | Edit + connected account | Body `{"repository":"owner/name","branch":null,"includePaths":["video","README.md"]}` (`includePaths` null/empty = every file; the legacy `pathPrefix` is still accepted). `repository` also accepts a github.com URL. Links, then queues the first sync: `200` with the link and `syncing: true`; how the sync went shows up later on the link (`lastError` when it failed). `409 github_account_not_connected` / `project_already_linked` / `repository_already_linked`, `404 repository_not_found` / `github_app_not_installed` / `branch_not_found`, `400 invalid_repository` / `invalid_branch` / `invalid_path_prefix` / `invalid_include_paths`, `502 github_error`, `503 github_app_not_configured`. |
| `PATCH` | `/api/v1/projects/{projectId}/github` | Edit + connected account | Body `{"branch":null\|""\|"name","includePaths":null\|[…]}` — null leaves a field alone, `""` is the default branch, `[]` every file. Saves and queues a re-sync with `force` (`200`, `syncing: true`); an unchanged request answers `200` with nothing queued. `400 invalid_branch` / `invalid_include_paths`, `404 not_linked` / `branch_not_found` / `repository_not_found`, `409 github_account_not_connected`, `429 sync_too_soon`. |
| `DELETE` | `/api/v1/projects/{projectId}/github` | Edit | `204`, or `404 not_linked`. Files stay. |
| `POST` | `/api/v1/projects/{projectId}/github/pull` | Edit + connected account | "Sync now": queues a pull of the branch head and answers at once: `202 {"status":"queued"}`, or `202 {"status":"already_syncing"}` when a sync of the project is already queued or running. The outcome — including a limit refusal (`too_many_files`, …) or a GitHub failure — is written to the link's `lastError` in plain language. `404 not_linked`, `409 github_account_not_connected`, `429 sync_too_soon` with `Retry-After`, `503 github_app_not_configured`. |
| `GET` | `/api/v1/projects/{projectId}/github/files?repositoryId=&branch=` | Edit + connected account | Every file and folder of a branch: `200 {"repository","branch","commit","entries":[{"path","isDirectory","size"}],"truncated","maxFiles","maxTotalBytes","maxFileBytes"}`. Without `repositoryId`, the linked repository (and its branch). `400 invalid_branch`, `404 branch_not_found` / `repository_not_found` / `not_linked`, `409 github_account_not_connected`, `502 github_error`. |
| `GET` | `/api/v1/projects/{projectId}/github/branches?repositoryId=` | Edit + connected account | `200 {"defaultBranch","branches":[…],"truncated"}`, default first. Without `repositoryId`, the linked repository. |
| `GET` | `/api/v1/projects/{projectId}/github/repositories` | Edit | The repository picker. `200 {"repositories":[…],"truncated":…,"installUrl":…,"accountLogin":…}`, or `409 github_account_not_connected` / `github_account_revoked` / `github_account_expired` / `github_account_unreadable`, `502 github_error`, `503 github_oauth_not_configured`. |
| `GET` | `/api/v1/projects/{projectId}/github/tree?repositoryId=&path=` | Edit | One directory (`path` empty = the root). `200 {"path":…,"parentPath":…,"entries":[{"name","path","sha","isDirectory"}],"truncated":…}`, `404 folder_not_found` / `repository_not_found`, `502 github_error`. |
| `GET` | `/api/v1/github/account` | session | Whether this workspace has a working GitHub connection. `200 {"connected","login","avatarUrl","canConnect","revokedReason","connectedAt","installUrl"}`. **Carries no token.** |
| `POST` | `/api/v1/github/account/connect` | session | Starts the OAuth flow. Body `{"projectId":"…"}` or `{}`. `200 {"authorizeUrl":"…"}` and sets the `HttpOnly` binding cookie, or `503 github_oauth_not_configured`. |
| `DELETE` | `/api/v1/github/account` | session | Forgets the connection and destroys the stored credential. `204`, or `404 not_connected` for a caller with no row at all. Idempotent for an already-revoked row. |
| `GET` | `/api/v1/github/oauth/callback` | **none** (see below) | Completes the flow and redirects to a fixed relative path. Anonymous by construction: the caller is a browser navigation from github.com, the person is identified by our own single-use state row, and the request is authenticated by PKCE plus the binding cookie. |

The callback is deliberately **not** an API for clients. It is the one endpoint reached by a
top-level navigation, so it answers `302` and never JSON, and its `Location` is built only from a
`Guid` read out of our own ticket and an outcome from an enum — nothing from the query string.

## Data model

| Table | Owned by | Notes |
|---|---|---|
| `project_github_links` | Inference API | One row per project (unique `project_id`) and per repository (unique `repository_id`). No tenant query filter: it is a child of `projects`, reached through the project access check or by the webhook from a signature-verified repository id. `include_paths_json` (jsonb, migration `AddGitHubLinkIncludePaths`) holds the chosen files and folders; null means every file. |
| `github_app_installations` | Inference API | Which organization owns an installation (unique `installation_id`). No tenant filter, and that is the point — seeing another organization's claim is what makes the refusal possible. |
| `github_webhook_deliveries` | Inference API | The idempotency ledger and the work queue in one table. Unique `delivery_id`; a null `processed_at` means the pull worker still owes it work. Organization-less: the row is written before the payload's owner is known. |
| `project_files.source_kind` / `source_revision` | Inference API | `'github'` marks a pulled row and `source_revision` holds its blob SHA. Null for uploads and agent writes, which is what keeps a pull away from them. |
| `github_account_links` | Inference API | One row per **`(user_id, organization_id)`** (unique), holding that person's encrypted user access token and refresh token. Indexed on `github_user_id` so the revocation webhook, which has no tenant, can find every row for the account that revoked. No tenant query filter: the row is never looked up by an id a caller supplied, only by the session's own `(user id, active organization)` pair — and the webhook, which has neither, could not find it through a filter. |
| `github_oauth_states` | Inference API | Single-use OAuth tickets: the SHA-256 of the `state`, the SHA-256 of the browser-binding cookie value, the encrypted PKCE verifier, and the `(user, organization, project)` the flow was started for. Consumed by writing `consumed_at`, so a replay finds no live row. Expired and spent rows are swept when the same person starts another flow. |

Deleting a project cascades to its link. Deleting a link leaves the pulled files. **Revoking or
disconnecting a GitHub account touches only `github_account_links`** — never a project link, never a
file.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `503 github_not_configured` on the webhook | `GITHUB_APP_ID`, `GITHUB_APP_PRIVATE_KEY` and `GITHUB_WEBHOOK_SECRET` are not all set on the **inference** service. |
| `503 github_app_not_configured` when opening a repository in the folder picker (it used to be `502 github_error`, "The GitHub App is not configured") | The account connection (`GITHUB_APP_CLIENT_ID`/`_SECRET`) is set but the App itself is not: set `GITHUB_APP_ID` and `GITHUB_APP_PRIVATE_KEY` (or `GITHUB_APP_PRIVATE_KEY_PATH` to a mounted PEM). Listing repositories needs only the first set; reading their folders needs the second. |
| `401 invalid_signature` for a real delivery | `GITHUB_WEBHOOK_SECRET` does not match the secret on the app. A proxy that rewrites the body would do it too; the nginx location above does not. |
| Linking answers `404 github_app_not_installed` | The app is not installed on that repository (or is installed on a different account), or another organization already claimed that installation. |
| Linking answers `503 github_app_not_configured` | The app id or private key is missing, or the PEM is not a readable private key. |
| A push is accepted but nothing changes | Check `github_webhook_deliveries.outcome`: `ignored_ref` means the push was to another branch, `no_link` means the repository is not linked, `installation_mismatch` means the delivery came from a different installation, `stale` means a newer commit had already been applied. |
| A push settles as `failed` | `error` on the delivery row says why (a GitHub 404 on the tree, a revoked installation, a limit). The next push retries the work. |
| Files are missing from the project | They may exceed `MaxFileBytes`, sit under `.git/` or `node_modules/`, lie outside the link's selection (`include_paths_json`) or `pathPrefix`, or already exist as a file a person uploaded — a pull never takes a path somebody else owns. |
| Every sync fails with "GitHub had a problem answering (error 502). This is usually temporary", but GitHub answers fine | Before 2026-10, a single zero-byte file in the selection (a `.gitkeep`) did this on every attempt: the client called GitHub's empty base64 answer a 502. Fixed — empty files are now written as empty files (see "Empty and unreadable files in a pull"). If it persists on a current build, the log line `GitHub GET … answered 5xx` shows which call really failed. |
| "Sync now" used to end in `499` in the nginx log and nothing synced | The pull ran inside the request and outlived nginx's 60 s. It now runs in the background (`GitHubSyncWorker`) and the request answers `202` at once; a long sync shows `syncing: true` on the link until it ends. |
| The card says "Too many files are chosen" | The **selected** files are over `GitHub__MaxFilesPerPull` (default 2,000). The person chooses fewer files with **Change files**; raising the limit is the operator's alternative. `selection_too_large` is the same for `MaxTotalBytes`. Before this change the limit was applied to the whole repository and the card only said "try again". |
| "Sync now" answers `429 sync_too_soon` | A sync of that project started less than 20 seconds ago (`GitHubSyncThrottle`). Wait `retryAfterSeconds`. |
| Linking, browsing or "Sync now" answers `409 github_account_not_connected` | The caller has no working GitHub connection in this workspace. Every action that reaches a repository on somebody's behalf needs one; the dashboard offers **Connect GitHub account** in place. |
| A `last_error` still names a REST path or a config key | It was written by an older version. The dashboard re-words the known ones (`describeLastSyncError`); the next sync replaces it with a plain sentence. |
| The dashboard offers no "Connect GitHub account" button | `GITHUB_APP_CLIENT_ID`, `GITHUB_APP_CLIENT_SECRET` or `GITHUB_APP_OAUTH_CALLBACK_URL` is missing on the **inference** service, so `canConnect` is false. The tab says linking is not set up on this server, and no repository can be linked until the three values are set. |
| GitHub says "The redirect_uri MUST match the registered callback URL" | `GITHUB_APP_OAUTH_CALLBACK_URL` and the App's registered **Callback URL** differ. They must match byte for byte, including scheme, host and trailing path. |
| The callback lands back on the page as `?github=state_mismatch` | The binding cookie did not come back. Usually the flow was completed in a **different browser or profile**, or the App's callback URL changed host (the cookie is `Secure` when the callback URL is `https`, and `Path=/api/v1/github`). Start again from the project page. |
| The picker lists fewer repositories than the person owns | Expected. The App can only read what it was **installed on**, and the user token is that intersected with the person's own access. Use the "Change which ones on GitHub" link (for a *selected* install) or "Give ReelBolt access to more repositories on GitHub" (needs `GITHUB_APP_SLUG`). |
| The picker lists nothing at all, but the account is connected | The App is not installed on any account that person can reach, or the installation covers no repositories they can see. The picker says this rather than showing a bare empty list. |
| Connecting succeeds, then the next page load says the connection is revoked | GitHub refused a token refresh, or the `dpkeys` volume changed under the service so the stored ciphertext could not be decrypted. Both revoke the row deliberately and ask the person to reconnect; the second one logs `Could not decrypt a GitHub OAuth code verifier` or revokes with `decryption_failed`. |
| A revocation webhook arrives but nothing changes | The delivery settles as `authorization_unknown` when no `github_account_links` row carries that `sender.id`. Check `github_webhook_deliveries.outcome`; `authorization_revoked` means rows were revoked. |
| Behind a staging `auth_basic` front door, the callback stalls | The browser must be able to reach `/api/v1/github/oauth/callback` with its cached basic-auth credentials; the generic `location /api/v1/` block covers it. A proxy that rewrites the response or blocks the `Set-Cookie` on `connect` breaks the binding cookie, and the callback then reports `state_mismatch`. |

## What is deliberately not done

- **ReelBolt never writes to GitHub.** The app asks for `contents: read` and nothing else: no commits,
  no branches, no pull requests. A push is an input, not an output.
- **No personal access tokens, ever.** The only credential is the app's own key, which the owner
  generates in his own account and can revoke at any time without touching ReelBolt.
- **Pulled files are not summarized or embedded** (see "What \"pull\" means" above). Indexing a
  repository is a deliberate act, not a side effect of a push.
- **One repository per project, and one project per repository.** A repository linked by two projects
  would make "which project does this push update" ambiguous.
- **No force-push or history handling beyond the commit.** The pull materializes the tree at the pushed
  commit; it does not try to interpret the commits in between.
- **The picker does not change what ReelBolt may read.** Connecting an account grants no new
  permission: a GitHub App requests no scopes, this App has `contents: read` and `metadata: read`, and
  a user token is that intersected with the person's own access. The account connection exists to
  *enumerate*, not to widen.
- **No personal access tokens, and no second OAuth app.** The user token comes from the same GitHub App,
  through its own OAuth flow — one registration, one permission list, one thing to revoke.
- **No by-name link form in the dashboard.** Linking requires a connected account, so a form that
  types `owner/name` could only ever fail where the picker cannot help; the API still accepts a name
  or a github.com URL in the `PUT`, under the same checks.
- **No App setup instructions in the product.** Creating and configuring the GitHub App is the
  operator's job ([`docs/github-app-setup.md`](github-app-setup.md)); the dashboard talks about
  "connecting your GitHub account" and "giving ReelBolt access to repositories", never about
  installations, webhooks or credentials.
- **The file listing does not page through a tree GitHub truncates.** A repository too large for one
  recursive listing is too large for a pull as well, so offering part of it would only lead to a sync
  that fails.
- **A repository the App was never installed on cannot be listed, only linked to an install page.** No
  code can change this: GitHub will not enumerate a repository to a token that cannot read it, and the
  UI says so rather than showing a list that quietly omits it.
