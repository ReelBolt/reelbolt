# TLS

nginx terminates TLS on `:443`. A separate `caddy` container is used purely as
an ACME client to obtain and auto-renew a free Let's Encrypt certificate once
you have a real domain — it never serves application traffic itself.

## Why two containers instead of one

Caddy's headline feature is automatic HTTPS, but this stack's routing (the
cookie↔`Authorization`-header translation in `nginx/auth.js`, the sandbox's
deliberately-unproxied path, per-path `proxy_read_timeout`s for SSE, etc.) is
already implemented in nginx/njs. Replacing nginx would mean re-implementing
all of that. Instead:

- **nginx** stays the single public entry point on `:80`/`:443` and keeps
  100% of its existing routing, unchanged.
- **Caddy** only ever talks to Let's Encrypt. It isn't published to the host
  and is unreachable from outside the `reelbolt` docker network.

nginx reads the certificate Caddy obtains directly off a shared volume
(`caddy_certs`, mounted read-only into nginx). There is no copying, syncing,
or cross-container signaling: renewals just update the file in place, and
nginx's own background loop (in `nginx/docker-entrypoint.sh`) runs
`nginx -s reload` every 12 hours to pick up the new file — a no-op when
nothing changed.

## Local development (no domain)

Nothing to configure. `docker compose up` generates a self-signed certificate
the first time nginx starts (`nginx/docker-entrypoint.sh`) and serves it on
`:443` alongside the existing `:80`. Browsers will show a certificate warning
for the self-signed cert — that's expected, not a bug. `FORCE_HTTPS` defaults
to `false`, so `http://localhost` keeps working exactly as before TLS support
was added.

## Going live with a real domain

1. Point the domain's DNS `A`/`AAAA` record at this host's public IP, and
   make sure `:80` and `:443` are actually reachable from the internet (this
   is required for Let's Encrypt's HTTP-01 challenge, which always connects
   on port 80 first).
2. Set `DOMAIN` and `ACME_EMAIL` in `.env`.
3. `docker compose --profile tls up -d caddy` — Caddy is opt-in via the `tls`
   compose profile so it never even starts (let alone crash-loops on a
   missing `DOMAIN`) for anyone who hasn't gone through this setup.
4. `docker compose up -d --force-recreate nginx` — nginx only re-evaluates
   which certificate to load at container start (`nginx/docker-entrypoint.sh`
   runs once, at boot), so switching from "no domain" to "has a domain" needs
   this one manual recreate. After this, renewals are automatic — no further
   steps, ever.
5. Confirm `https://<DOMAIN>` works and shows a real, trusted certificate.
6. Only then, set `FORCE_HTTPS=true` in `.env` and restart nginx. This makes
   `http://` redirect to `https://`. Flipping it on before step 5 is
   confirmed just replaces a working `http://` dev flow with a broken or
   self-signed-warning `https://` one. With it on, nginx also sends
   `Strict-Transport-Security: max-age=31536000; includeSubDomains` on `:443` (no
   preload): a browser that has seen it refuses plain `http://` for this host
   and every subdomain for a year, so do not turn `FORCE_HTTPS` back off on a
   host people already use, and make sure every subdomain is served over
   https first. We do not submit the domain to the HSTS preload list; that is
   only worth considering once every subdomain (downloads., cdn., runner.,
   test. and any other) is https. Every response also carries the other
   security headers below, with or without `FORCE_HTTPS`.
7. Set `COOKIE_SECURE=true` in `.env` and restart `go-api`. Do this only
   after `https://<DOMAIN>` is confirmed working — browsers silently drop
   `Secure` cookies sent over plain HTTP, so setting this too early breaks
   login with no obvious error.

## Security headers

`nginx/security-headers.conf` is the one snippet that sets them; the values that differ by path come from
the `$sh_class` maps in `nginx/nginx.conf`, and the upstream's own copies are removed (`proxy_hide_header`),
so each header appears once. `nginx/tests/security-headers.sh` pins the set per path.

A deploy reloads the running nginx instead of recycling it, so the container still runs the previous image's
`render-config.sh`. `release.sh` therefore copies the activated release's `render-config.sh`,
`reload-config.sh` and `security-headers.conf` into the container before `reload-config.sh`, and the rendered
storage source is included by glob (`conf.d/security-storage*.map`) so its absence never fails `nginx -t`.
`nginx/tests/upgrade-reload.sh` reproduces the old-renderer failure and proves the fix.

| Header | Value |
|---|---|
| `X-Content-Type-Options` | `nosniff` everywhere |
| `X-Frame-Options` | `DENY` everywhere (the CSP's `frame-ancestors 'none'` says the same) |
| `Referrer-Policy` | `strict-origin-when-cross-origin`; `no-referrer` on `/app/review/g/`, the output-link and review-link API paths and `/oauth/` |
| `Permissions-Policy` | site, docs, API: `camera=(), microphone=(), geolocation=(), payment=()`; `/app`: `microphone=(self)` (voice-over recorder; no camera use) and `payment=(self "https://*.paddle.com")` |
| `Content-Security-Policy-Report-Only` | documents only (site, `/docs`, `/app`); never on `/api/`, `/_next/static`, `/health`. **Report-only, not enforced.** The API's own enforced CSP on file/SVG responses passes through untouched |
| `Cross-Origin-Opener-Policy` | `same-origin` on `/` and `/docs`; `same-origin-allow-popups` on `/app` (OAuth and checkout popups) |
| `Cross-Origin-Resource-Policy` | `same-origin`, except `cross-origin` on public-by-design paths: output/review links, `/l/`, `/api/v1/l/`, `/api/v1/public/`, brand and blog images, favicons, `/_next/static` |
| `Cross-Origin-Embedder-Policy-Report-Only` | `credentialless; report-to="csp-endpoint"` on documents; never enforced |
| `Reporting-Endpoints` | `csp-endpoint="/api/v1/telemetry/csp-report"` on the same documents as the two report-only headers. The CSP also carries `report-uri /api/v1/telemetry/csp-report; report-to csp-endpoint`. The endpoint is anonymous, always answers an empty `204`, has its own rate limit and a path-only access log (`docs/csp-reporting.md`) |
| `Strict-Transport-Security` | only with `FORCE_HTTPS=true`; see step 6 above |

The CSP names the deployment's storage origin (R2/S3) in `media-src` and `connect-src`. nginx takes it from
`MINIO_PUBLIC_ENDPOINT` (passed into the nginx container by `docker-compose.yml`), reduced to
`scheme://host[:port]`; empty or malformed means no storage source. There is no `upgrade-insecure-requests`
(plain-http local and e2e stacks). The policy is a starting point, tuned from real violation reports before
anyone proposes the enforcing header.

## How the pieces fit together

- `nginx/Dockerfile` — extends `nginx:1.31.9-alpine` with `openssl` (self-signed
  cert generation) and `gettext` (`envsubst`, used to render the HTTPS
  server block's certificate paths).
- `nginx/docker-entrypoint.sh` — on every container start: picks a
  certificate (Caddy's, if `DOMAIN` is set and one has actually been issued;
  otherwise a self-signed one, generated once and reused), renders
  `nginx/https-server.conf.template` into `/etc/nginx/conf.d/https-server.conf`,
  copies either `nginx/http-server-dev.conf` or
  `nginx/http-server-redirect.conf` into `/etc/nginx/conf.d/http-server.conf`
  depending on `FORCE_HTTPS`, starts the periodic reload loop, then execs
  nginx.
- `nginx/locations.conf` — the actual routing table (unchanged from before
  TLS support), shared via `include` by both the `:80` (dev mode) and `:443`
  server blocks so it only exists in one place. The anonymous output-link route
  (`~* ^/api/v1/output-links/`) is the one `/api/v1/` location with its own access
  log format (no URI, so the token in the path is not logged), its own limit zone
  (`output_links`, 120r/m, burst 60), `auth_basic off`, a blanked `Authorization` and
  an unbuffered proxy; see [Output links](platform-assistant-mcp.md#output-links-getoutput).
- `nginx/http-server-dev.conf` / `nginx/http-server-redirect.conf` — the two
  possible `:80` server blocks (passthrough vs. redirect-to-https). Both also
  forward `/.well-known/acme-challenge/` to the `caddy` container, which is
  how Let's Encrypt's HTTP-01 validation reaches Caddy even though Caddy
  itself is never exposed to the internet.
- `caddy/Caddyfile` — a minimal site block for `{$DOMAIN}` that does nothing
  but respond 200 (Caddy manages a certificate for any domain used as a site
  address in its config, automatically, on startup).
- `caddy/docker-entrypoint.sh` — refuses to start with a clear error if
  `DOMAIN`/`ACME_EMAIL` aren't set, instead of Caddy's own less obvious error
  from an empty `{$DOMAIN}` site address.

Caddy's on-disk certificate storage layout is a stable, documented
convention: `/data/caddy/certificates/acme-v02.api.letsencrypt.org-directory/<domain>/<domain>.crt`
(and `.key`) for the production Let's Encrypt CA. `nginx/docker-entrypoint.sh`
reads this path directly (mounted read-only at `/caddy-data`) — there's
nothing else keeping the two containers in sync.

## In the cloud (Cloudflare in front of nginx)

Production and staging (plan `saas-launch`, D19) keep nginx as the in-cluster router, since
the cookie to `Authorization` translation in `nginx/auth.js` and the per-path SSE timeouts
live there, and put the Cloudflare proxy (Free plan) in front for TLS, WAF and DDoS. Set
Cloudflare SSL mode to **Full (strict)**.

Two ways to give the origin a certificate, and both are in use:

- **Caddy + Let's Encrypt**, the same mechanism as self-host: enable the compose `tls` profile
  and set `DOMAIN`/`ACME_EMAIL`. HTTP-01 works through the Cloudflare proxy because Cloudflare
  forwards the challenge path to the origin — this is what our staging host runs. See
  "Renewals" below for the one non-obvious requirement.
- **A Cloudflare Origin CA certificate** for `<domain>` and `*.<domain>`, pointed at with
  `TLS_CERT_FILE`/`TLS_KEY_FILE` (mounted files; this overrides the Caddy and self-signed
  choice in `docker-entrypoint.sh`). Pick this when Cloudflare's SSL mode is Full (strict) and
  you would rather Cloudflare itself vouched for the origin, or when you need a wildcard. A
  wildcard matters because the `runner.` hostname below is a second name on the same listener;
  a single-name Let's Encrypt certificate does not cover it.

nginx behaviour is controlled by environment variables, all defaulting to the single-host
values so local compose behaves as before:

| Variable | Default | Meaning |
|----------|---------|---------|
| `UPSTREAM_GO_API`, `UPSTREAM_INFERENCE_API`, `UPSTREAM_WORKFLOW_ENGINE`, `UPSTREAM_WEB`, `UPSTREAM_SITE`, `UPSTREAM_DOCS`, `UPSTREAM_CADDY` | the compose service URLs (`http://workflow-engine:8080`, ...) | Upstream base URLs, substituted into `/etc/nginx/conf.d/common.inc` at start. The cloud control VM points `UPSTREAM_WORKFLOW_ENGINE` at `http://$ENGINE_PRIVATE_IP:8080`. |
| `REAL_IP_FROM_CLOUDFLARE` | `false` | `true` installs `nginx/cloudflare-realip.conf`: `set_real_ip_from` for Cloudflare's published ranges and `real_ip_header CF-Connecting-IP`. Needed for per-IP rate limits (C5) and logs behind the proxy. Only connections from those ranges are trusted, so a direct hit on the origin cannot spoof an address. Refresh the ranges from `cloudflare.com/ips-v4` and `/ips-v6` when they change. Leave off locally. |
| `CLIENT_MAX_BODY_SIZE` | `100M` | Request body limit for the main server blocks. |
| `UPSTREAM_RUNNER_GATEWAY`, `RUNNER_HOSTNAME` | empty, `runner.$DOMAIN` | See below. |
| `TLS_CERT_DIR` | `./data/nginx-certs` | Host directory bind-mounted read-only at `/etc/nginx/operator-certs`; the boxes set it to `/opt/reelbolt/certs`. An empty directory is inert. |
| `TLS_CERT_FILE`, `TLS_KEY_FILE` | empty | Operator-supplied certificate/key, as paths **inside the container** (e.g. `/etc/nginx/operator-certs/origin.crt`). Overrides the Caddy and self-signed choice. |

### runner.<domain>

`docs/runner-protocol.md` has runners dial `wss://runner.<domain>/v1/connect`. When
`UPSTREAM_RUNNER_GATEWAY` is set, the entrypoint renders `nginx/runner-server.conf.template`:
a separate `server` block for `RUNNER_HOSTNAME` that proxies only `/v1/connect` to the
gateway with WebSocket upgrade headers, **without** the cookie translation (runners
authenticate with a signed Ed25519 hello inside the protocol) and with any client
`Authorization` header cleared, `proxy_buffering off`, and 120 s read/send timeouts. The
protocol heartbeats every 15 s and declares a runner dead after 45 s, so the proxy never
cuts a healthy idle socket; Cloudflare's own WebSocket idle limit (100 s) is also above 45 s.
Everything else on that hostname is a 404. The gateway does not exist yet (decision D5a), so
the variable is unset in the cloud override and no block is rendered (an empty file is
included). TODO(runner-gateway WP): add the `runner-gateway` service and set
`RUNNER_GATEWAY_UPSTREAM` in the control VM's `.env`, create the proxied `runner` DNS record,
and run the one-hour WebSocket soak from F15's acceptance test. If `UPSTREAM_RUNNER_GATEWAY`
is set with neither `RUNNER_HOSTNAME` nor `DOMAIN`, nginx refuses to start.

### Renewals

This deployment obtains its certificate with **Caddy over ACME HTTP-01**, which works through
the Cloudflare proxy: Cloudflare forwards `/.well-known/acme-challenge/` to the origin, where
nginx proxies it to the `caddy` container. Two things are worth knowing before changing any of it.

**Cloudflare SSL mode must be Full or Full (strict) — never Flexible.** In the Full modes
Cloudflare reaches the origin on `:443`; in Flexible it fetches `:80`. With
`FORCE_HTTPS=true` that is an infinite redirect loop, because nginx answers every `:80`
request with a 301 to `https://` that Cloudflare then re-requests over plain HTTP. Check which
mode is in use by sampling the established sockets on the origin while driving traffic through
the edge:

```bash
ss -Htn state established | awk '{print $4}' | grep -oE ':(80|443)$' | sort | uniq -c
```

`:443` means Full or Full (strict), and `FORCE_HTTPS` is then safe. Full **(strict)** is the
right target: it makes Cloudflare validate the origin certificate, so an attacker cannot
impersonate the origin to the edge.

**The Host header on the challenge location is load-bearing.** Caddy matches an incoming
challenge to the site it manages *by hostname*, so it only answers while it sees
`Host: <DOMAIN>`. nginx's default for `proxy_pass` is `Host: $proxy_host` — here the upstream
name, the bare word `caddy`, which Caddy does not manage. The request then falls through to
Caddy's own HTTP→HTTPS redirect (a 308 to `https://caddy/.well-known/...`) and Let's Encrypt
refuses that URL outright: `invalid host: must end in IANA registered TLD`.

This failure is **invisible until renewal**. A certificate obtained before `FORCE_HTTPS` was
switched on keeps working, so the site looks healthy for ~60 days and then the certificate
simply expires. `nginx/http-server-dev.conf` has always forwarded `Host`;
`nginx/http-server-redirect.conf` did not, and only the redirect block is used once
`FORCE_HTTPS=true`. It now forwards `Host` and the forwarding headers, matching the dev block.

Prove renewal rather than assume it. A cached ACME authorization will happily hide the fault, so
clear the account as well as the certificate, and keep a copy first:

```bash
docker exec reelbolt-caddy-1 sh -c 'mv /data/caddy/certificates /data/caddy/certificates.bak; rm -rf /data/caddy/acme'
docker restart reelbolt-caddy-1
docker logs reelbolt-caddy-1 2>&1 | grep -E 'served key authentication|authz_status|certificate obtained|challenge failed'
```

`served key authentication` followed by `certificate obtained successfully` is a pass. Restore
`certificates.bak` if it is not. nginx reads the certificate only at start or on reload, so
after a re-issue run `docker exec reelbolt-nginx-1 nginx -s reload` or recreate the container —
and note that if nginx starts while no Caddy certificate exists yet it silently falls back to
the self-signed one, and keeps serving that until it is restarted.

### Upload size

Cloudflare Free rejects request bodies over 100 MB, and nginx's own limit is `100M`, while F15's
acceptance asks for a 500 MB upload. Options considered:

1. **Presigned direct-to-R2 browser uploads** (the browser PUTs to R2, nginx and Cloudflare's
   proxy never see the bytes). Cleanest and also removes upload load from the control VM, but
   it needs an Inference API endpoint that issues presigned URLs, an R2 CORS policy, and a
   client change. **Implemented** — the endpoints, the checks they perform and the bucket CORS
   rule are in [`direct-upload.md`](direct-upload.md).
2. **An unproxied (DNS-only) `upload.<domain>` hostname** straight to nginx. Cheap in nginx,
   but it exposes the origin IP, bypasses WAF/DDoS for that name, and needs the auth cookie
   to be valid on a second host (`Domain=` on `reelbolt_token`, which does not exist today)
   plus CORS from the app host.
3. Pay for a Cloudflare plan with a larger cap.

Option 1 now carries the large uploads, so the two proxies' caps no longer bound a source video: the
browser PUTs to the bucket, and only the two small control requests (the presign and the completion)
travel through Cloudflare and nginx. The multipart route keeps its 100 MB cap deliberately — it is the
fallback for a self-host install with no bucket CORS policy, and for non-browser clients — and
`CLIENT_MAX_BODY_SIZE` still lets an operator raise the nginx limit for it if the proxy cap is
later lifted. No `upload.` server block is implemented, because without the cookie-domain and
CORS work it would not authenticate.

### SSH

Deploy SSH from GitHub-hosted runners and its firewall and sshd hardening are described in
`docs/ci.md` ("SSH reachability for deploys").

## Serving `reelbolt.ai` from the two boxes

The launch shape: **`reelbolt.ai` is proxied through Cloudflare to the control box's nginx**, which
serves every path (`nginx/locations.conf`: `/` → the `site` container (the marketing site, a Next.js
server), `location ^~ /app` → `web`, `/api/v1/*` → the APIs, `/docs/` → `docs`, the Paddle webhook its
own block). The marketing site is not on Cloudflare Pages. There is **no production deployment** —
production is the two existing Hetzner boxes (a control box and a media box), released with
`REELBOLT_ROLE=control|media bash infra/scripts/release.sh deploy <sha>`.

This is a **staged cut-over**, not a config file to commit. It is an owner-scheduled operator action
because a deploy mid-run destroys a running workflow (re-point both boxes in a quiet window, control
first, then media).

### 1. Re-point the boxes from staging to production (operator, scheduled)

The boxes serve a staging hostname today. Production re-points their `/opt/reelbolt/.env`
and re-releases; the deltas are:

| Variable | Staging value today | Production value |
|---|---|---|
| `DOMAIN` | the staging hostname | `reelbolt.ai` |
| `PUBLIC_BASE_URL` | `https://` plus the staging hostname | `https://reelbolt.ai` |
| `NEXT_PUBLIC_SITE_URL` | staging host | `https://reelbolt.ai` (**build-time** — the `site` image must be rebuilt) |
| `FORCE_HTTPS` | `false` | `true` (only after §2's cert is confirmed, `docs/tls.md` step 6) |
| `COOKIE_SECURE` | `false` | `true` (only after HTTPS is confirmed — step 7) |
| `REAL_IP_FROM_CLOUDFLARE` | unset | `true` (per-IP limits and logs behind the proxy) |
| `BILLING_ENABLED` | `false` | `true` |
| `PADDLE_ENVIRONMENT` | `sandbox` | `live` (+ real `PADDLE_API_KEY`/`PADDLE_WEBHOOK_SECRET`/`PADDLE_CLIENT_TOKEN` and the live `pri_…` ids) |
| `SIGNUP_ENABLED` | `false` | `true` (+ `NEXT_PUBLIC_SIGNUP_ENABLED=true` at `site` build) |
| `BASIC_AUTH_USER`/`_PASSWORD` | (blank — the staging front door is **off** today, verified) | blank (production is open; the app's own auth is the door) |

Then, per box: `REELBOLT_ROLE=control bash infra/scripts/release.sh deploy` and
`REELBOLT_ROLE=media bash infra/scripts/release.sh deploy`, control first. Verify each box with
`REELBOLT_ROLE=<role> bash infra/scripts/release.sh verify` — both must report the **same**
`.deploy.sha` (a split-brain between the boxes is the failure this catches).

### 2. A certificate the origin can actually serve

The apex is proxied through Cloudflare, so Cloudflare answers `reelbolt.ai/.well-known/acme-challenge/`
at its edge — **Caddy's Let's Encrypt HTTP-01 cannot reach the origin for `reelbolt.ai`**. Use a
**Cloudflare Origin CA certificate** for `reelbolt.ai` **and `*.reelbolt.ai`** instead (the wildcard
covers the `runner.` hostname, a second name on the same listener); set Cloudflare SSL/TLS mode to
**Full (strict)**.

The wiring is in the shared base `docker-compose.yml` `nginx` service, so every deploy path
(`release.sh` with `control|media`, `infra/compose/host.sh` with `control|engine`, and self-host)
includes it — one definition, nothing to drift:

- the `nginx` service passes `TLS_CERT_FILE`/`TLS_KEY_FILE` through (both empty by default, so
  self-host and local dev are unchanged), and
- bind-mounts `${TLS_CERT_DIR}` read-only at `/etc/nginx/operator-certs`.

To use it, on the **control box** only (the engine box runs no nginx; `env.engine.example` records
that deliberately):

1. Put the Origin CA certificate and key on the box, e.g. `/opt/reelbolt/certs/origin.crt` and
   `/opt/reelbolt/certs/origin.key`, mode 0600. **Never commit them**
   (`infra/scripts/check-secrets.sh`).
2. In `/opt/reelbolt/.env` set the three names (`env.control.example` carries them):

   ```
   TLS_CERT_DIR=/opt/reelbolt/certs
   TLS_CERT_FILE=/etc/nginx/operator-certs/origin.crt
   TLS_KEY_FILE=/etc/nginx/operator-certs/origin.key
   ```

   `TLS_CERT_DIR` is the **host** path (the mount source); the two file paths are **inside the
   container**. Leave all three blank to keep the Caddy/self-signed choice.
3. `docker compose up -d --force-recreate nginx`. nginx reads its certificate **once, at container
   start** (`nginx/docker-entrypoint.sh`), so replacing a certificate always needs this recreate —
   the 12-hour reload loop only re-opens the same file, it never re-picks the path.
4. Confirm with `openssl s_client -connect reelbolt.ai:443 -servername reelbolt.ai` (a real chain,
   Origin CA issuer), then proceed to `FORCE_HTTPS`/`COOKIE_SECURE` in step 1.

### 3. Cloudflare routing (operator action)

Point the hostname at the control box with a **proxied DNS record** (the box's public IP, or a
proxied `origin.reelbolt.ai` record) and nothing else: no Worker, no Origin Rule and no Pages
project sit in front of it, because the origin already serves every path. Cloudflare must pass the
`Host` and `CF-Connecting-IP` headers through (it does for a proxied record) so nginx's `auth.js` and
`REAL_IP_FROM_CLOUDFLARE` see the real client, and the SSE routes must stay unbuffered.

### 4. Verify

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://reelbolt.ai/                       # 200 (the `site` container, through nginx)
curl -s -o /dev/null -w '%{http_code}\n' https://reelbolt.ai/api/v1/health          # 200 (origin, not 404)
curl -s -o /dev/null -w '%{http_code}\n' -X POST https://reelbolt.ai/api/v1/billing/webhooks/paddle \
  -H 'Content-Type: application/json' -d '{}'                                       # 400 = origin rejects the body (PASS); 405 or 404 = the request is not reaching the origin
```

The third line is the Paddle webhook and is the point of the ticket: a `404` there means a paying
customer's subscription never activates (`docs/billing.md` § "Webhooks are authoritative").

## Patching nginx (deploy checklist note)

The `nginx` and `docs` images pin `nginx:1.31.9-alpine` (fixes the November 2026 HTTP/2 CVE-2026-123007 and
CVE-2026-123633; 1.31.8 and older are affected). Only the `nginx` service answers HTTP/2 (`listen 443 ssl http2` in
`nginx/https-server.conf.template` and `nginx/runner-server.conf.template`); `docs` serves plain HTTP/1.1 on the
internal network. We use no HTTP/3 (`quic`), no `mail` or `stream` block, and `resolver` only for Docker DNS
(`127.0.0.11`).

Rolling it out needs a rebuild and recreate of both images (`docker compose build --no-cache nginx docs`, then
`up -d nginx docs`); a restart alone keeps the old binary. Nothing deploys without a owner order. The two media-box
edges (`engine-edge`, `pool-edge`) are in the repo too and pinned to the same version; see
`two-box-deployment.md`.
