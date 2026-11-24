# Webapp latency measurement (Playwright + HAR)

Repeatable measurements of the dashboard's real network behaviour: what a page
actually requests, over which protocol, how long each request waits for its
first byte, what it transfers, and which requests never finish.

This exists because the dashboard's worst behaviour is only visible in a real
browser: HTTP/3 (QUIC) to Cloudflare, a long-lived SSE feed, `<video>` Range
requests, and Next.js RSC prefetches. `curl` sees none of it.

## Run it

```bash
cd e2e
node perf/measure.mjs --scenario full --out /tmp/reelbolt-perf/baseline
node perf/analyze.mjs /tmp/reelbolt-perf/baseline
```

Scenarios: `dashboard`, `project`, `video`, `render`, `full`.

Credentials are read from `REELBOLT_PERF_EMAIL` / `REELBOLT_PERF_PASSWORD`, or
from the git-ignored `.local/perf-credentials.md`.

**What the tooling does with them.** The login runs in a context that records
nothing and is saved as a `storageState`; the recording context then starts from
that state, so the password never enters a HAR. Before a HAR touches the disk it
is sanitized anyway: cookie/authorization/`X-Amz-*`/`X-Goog-*` headers removed,
request bodies deleted, capability-bearing query parameters (`token`,
`X-Amz-Signature`, `X-Amz-Credential`, …) redacted, and the
`/api/v1/output-links/{token}` path replaced with `{token}` (that URL alone
fetches the video). Output files are mode 0600 in a 0700 directory, and `--out`
is refused when it — or a symlink leading to it — resolves inside the
repository. `analyze.mjs` prints paths only, never query strings. The rules live
in `sanitize.mjs` and are pinned by `perf:test` (`node --test
perf/sanitize.test.mjs`). **Still treat the output directory as secret:** it
holds the account's session state.

`measure.mjs` logs in once and saves the browser state to
`<out>/state.json`; pass `--auth <file>` to reuse it. Reuse matters: the login
route is rate-limited (5 requests/minute per address) and a tool that logs in on
every run measures the limiter.

Each run writes:

| File | What it is |
|---|---|
| `run-N.har` | Standard HAR (import into Chrome DevTools or any waterfall viewer) |
| `run-N.requests.json` | CDP network log — protocol per request, TTFB, bytes received, plus the scenario summary and playback samples |
| `summary.json` | All runs, for scripted comparisons |

## What `analyze.mjs` reports

- request count and total bytes for the run;
- the protocol actually negotiated for the navigation and the subresources
  (`h2` vs `h3`) — this is how the "HTTP/3 is saturated" hypothesis is settled;
- TTFB percentiles and per-endpoint averages;
- the slowest requests, including failures;
- requests still open at capture (an SSE stream is expected; anything else is
  not);
- endpoints called more than twice in one run (polling);
- achieved throughput for each download the scenario measured.

## Reading a result

- A multi-second TTFB on a **200** is server work on the read path.
- A multi-second TTFB followed by **409/422** is a refusal that paid the same
  server work before refusing.
- A large `bytes` with a short duration is a healthy transfer; a small transfer
  with a long duration is not a bandwidth problem, it is a wait.
- Identical endpoints appearing 3–8 times in one run means panels are being
  mounted (and fetching) more often than a person opened them.

## Comparing before/after

Run the same scenario against the same environment and diff the analysis output.
Keep the same `--settle-ms` and `--video-wait-ms`; the tool waits fixed periods
rather than `networkidle` because the dashboard's SSE stream never goes idle.
