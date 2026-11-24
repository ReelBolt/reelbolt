# Setting up the GitHub App (owner action)

ReelBolt cannot create a GitHub App for you. GitHub only creates Apps through a browser session in
the account that will own it, and no API endpoint exists for it. These are the exact values to enter.
The dashboard side is built; this is the only step that needs you.

Everything here happens on `https://github.com/settings/apps/new` in **your** account.

> **If you created the App before account linking existed, three things changed** and the App will not
> let anybody pick a repository until you do them: **enable user authorization** (fill in the Callback
> URL), **generate a client secret**, and **subscribe to `github_app_authorization`**. See
> [What changed](#what-changed-if-you-already-created-the-app) at the end. The earlier instruction to
> leave the callback fields empty was **wrong for this feature** and is corrected below.

## The values to enter

**GitHub App name**
    ReelBolt

**Homepage URL**
    https://reelbolt.example.com

**Identifying and authorizing users** — **this is the part that makes the repository list work.**
ReelBolt needs a *user-to-server* token to ask GitHub "which repositories may this person choose from?".
An App's own installation token cannot answer that: it has no notion of a person. Without this section
filled in, the dashboard can still link a repository by typing `owner/name`, but it can never show a
list.

- **Callback URL:** `https://reelbolt.example.com/api/v1/github/oauth/callback`
  This is where GitHub returns the browser after somebody authorizes the App, and it must match
  `GITHUB_APP_OAUTH_CALLBACK_URL` on the deployment **byte for byte**.
- **Request user authorization (OAuth) during installation:** leave **unticked**. ReelBolt asks for
  authorization from the project page, when somebody actually wants to pick a repository; forcing it
  during installation would put a consent screen in front of installs that do not need one.
- **Expire user authorization tokens:** **tick this.** It makes each user token last eight hours and
  issues a refresh token beside it, so a leaked token is worth hours rather than forever. ReelBolt
  refreshes them on its own and asks the person to reconnect only if GitHub refuses the refresh.
- Leave **Enable Device Flow** unticked. Nothing here uses it.

**Post installation → Setup URL** — leave **empty**, and leave *Redirect on update* **unticked**.

**Webhook**
- **Active:** ticked (this is the whole feature).
- **Webhook URL:** `https://reelbolt.example.com/api/v1/github/webhook`
- **Secret:** generate a long random string and keep it. It becomes `GITHUB_WEBHOOK_SECRET`. The
  backend verifies every delivery's `X-Hub-Signature-256` against this in constant time **before it
  writes anything**; without it the endpoint answers `503` rather than accepting anything.

**Client secrets** — below the webhook section.
- Press **Generate a new client secret** and copy the value immediately; GitHub shows it once. It
  becomes `GITHUB_APP_CLIENT_SECRET`.
- The **Client ID** at the top of the same section, beginning `Iv1.`, becomes
  `GITHUB_APP_CLIENT_ID`. **It is not the App ID** — that is a different number, further up the page,
  and pasting it here is the mistake this paragraph exists to prevent.

**Repository permissions** — exactly two, both read-only:
- **Contents: Read-only** (read the tree and the blobs)
- **Metadata: Read-only** (mandatory; GitHub selects this automatically)

A GitHub App requests no *scopes*; its permissions come from this page and nowhere else, which is why
the list is short and why it matters. There is deliberately **no write permission of any kind, no
organization permission and no account permission** — so the user token ReelBolt obtains can read file
contents and nothing else.

**Subscribe to events** — exactly two:
- **Push** — a push updates the linked project.
- **`GitHub App authorization`** — sent when somebody revokes ReelBolt in their GitHub settings. An App
  cannot unsubscribe from it. ReelBolt uses it to delete the stored token instead of discovering the
  revocation later as a `401`; without the subscription, a revoked connection keeps failing until the
  person reconnects.

**Where can this GitHub App be installed?** — **Any account.** ReelBolt is offered to other people, and
an App restricted to your own account cannot reach a customer's private repositories, which is the
whole point. ("Only on this account" is fine only for a single-tenant install where nobody else will
ever connect.)

## The icon

GitHub wants a PNG at least 200×200. Upload **`web/public/favicon-512.png`** — it is already the
brand mark at 512×512, which is the largest clean size we ship:

    web/public/favicon-512.png    512 x 512, PNG, RGBA

## After you press Create

1. **Note the App ID** (top of the App's settings page). That is `GITHUB_APP_ID`.
2. **Note the App slug** (the last path segment of the App's public URL). That is `GITHUB_APP_SLUG`;
   it is only used to build the "install the App" link the dashboard offers.
3. **Generate a private key** (App settings → Private keys → Generate). GitHub downloads a `.pem`
   file. *This is the key that signs the App's JWTs — treat it as a production secret.* GitHub shows
   it once.
4. **Generate a client secret** and note the client id, as described above.

The private key is the one value that must not travel through chat. Put it on the deployment yourself:

- locally, under `~/reelbolt-secrets/private/`, beside the other deployment secrets
- on the test VMs at `/opt/reelbolt/secrets/`, referenced by `GitHub__PrivateKeyPath`
- and set the rest in `/opt/reelbolt/.env`:

      GITHUB_APP_ID=123456
      GITHUB_APP_SLUG=reelbolt
      GITHUB_APP_WEBHOOK_SECRET=<the secret you generated on the webhook section>
      GITHUB_APP_CLIENT_ID=Iv1.abc123def456
      GITHUB_APP_CLIENT_SECRET=<the client secret>
      GITHUB_APP_OAUTH_CALLBACK_URL=https://reelbolt.example.com/api/v1/github/oauth/callback

`GITHUB_APP_OAUTH_CALLBACK_URL` is not derived from the request — a redirect target taken from a
`Host` header is a redirect target an attacker chooses — so it is configuration, and it has to equal
the Callback URL on the App's page exactly.

Restart the `inference` service after setting them. Nothing is half-enabled: with the App ID, the
private key or the webhook secret missing the webhook answers `503`; with any of the three OAuth
values missing, connecting an account answers `503 github_oauth_not_configured` and the dashboard says
so instead of offering a button that cannot work. **Linking a repository by name keeps working in
either case**, so a partially configured deployment is not a broken one.

## Then install it on a repository

App settings → Install App → choose your account → **Only select repositories** → pick the repos.

From the dashboard, the project's **Repository** tab now:

1. offers **Connect GitHub account**, which sends you to GitHub to authorize the App for your account;
2. lists **the repositories that account can use**, private ones included — that list is GitHub's own
   answer through your own token, so it cannot contain anything you cannot already reach;
3. lets you pick the **branch** from the repository's real branches and **tick the files and folders**
   to sync in a tree of that branch (see [`github-app.md`](github-app.md) "Choosing which files are
   synced");
4. offers **Give ReelBolt access to more repositories on GitHub** — the App's install page,
   `https://github.com/apps/<GITHUB_APP_SLUG>/installations/new` — so set `GITHUB_APP_SLUG`.

There is no by-name form any more: linking requires a connected account, so a deployment without the
App's client id, client secret and callback URL cannot link repositories at all, and the tab says so.
The product never shows these setup steps to end users; they live here and in
[`github-app.md`](github-app.md) only.

## When the picker cannot help

A GitHub App can only ever read the repositories it was **installed on**, and a user access token is
the intersection of the App's permissions and the person's own. So:

- A repository you own that the App was never installed on **does not appear in the picker**, and
  cannot — GitHub will not list it to a token that cannot read it. Use the "Give ReelBolt access to more
  repositories on GitHub" button the picker offers.
- A repository the App *is* installed on but the installation was granted only *selected*
  repositories: the picker offers a link straight to that installation's page on GitHub, where you add
  it. Reload the page afterwards.
- A repository whose App install belongs to somebody else on the same GitHub account: connect your
  account, and if it is still missing, that install is not yours to extend — an administrator adds it
  on GitHub.

## What changed, if you already created the App

| Setting | Where | Was | Now |
|---|---|---|---|
| **Callback URL** | Identifying and authorizing users | left empty | `https://reelbolt.example.com/api/v1/github/oauth/callback` |
| **Expire user authorization tokens** | Optional features | default | **Opt in** |
| **Client secret** | Client secrets | none | **Generate** one → `GITHUB_APP_CLIENT_SECRET` |
| **Client ID** | Client secrets | unused | `Iv1.…` → `GITHUB_APP_CLIENT_ID` |
| **Events** | Subscribe to events | `push` | `push` + `GitHub App authorization` |
| **Installable on** | Where can this App be installed | whatever it is | **Any account**, to reach customers' private repositories |
| **Repository permissions** | Repository permissions | `contents: read`, `metadata: read` | **unchanged** — nothing new is asked for |

The permissions do not change, and that is the important part: connecting an account does not widen
what ReelBolt can read. The user token carries the *same* read-only contents and metadata permissions,
scoped further by what the person themselves can see.
