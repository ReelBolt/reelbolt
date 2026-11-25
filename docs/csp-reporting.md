# CSP and COEP violation reporting

Internal operator reference (docs/*.md are not published). The browser sends Content-Security-Policy and
Cross-Origin-Embedder-Policy violation reports to the Inference API, which keeps a daily count per
closed key. Nothing here identifies a person.

## Endpoint

`POST /api/v1/telemetry/csp-report`, anonymous, on the Inference API, reached through nginx (see "nginx" below).

| Content type | Shape |
|---|---|
| `application/csp-report` | legacy `report-uri`: `{"csp-report":{...}}` |
| `application/reports+json` | Reporting API: an array of `{"type":"csp-violation"\|"coep","body":{...}}` (first 10 taken) |
| `application/json` | either of the above, or one `{type, body}` object |

The answer is **always `204` with an empty body**: malformed, oversized (cap 16 KB, read at most that),
wrong content type, switched off, rate-limited or a storage failure. It never echoes input and never
returns an error, so a browser does not retry and a probe learns nothing.

## What is stored

One row per key per UTC day in `csp_violation_counts` (API-owned, no tenant filter, `SELECT` granted to
`powerbi_ro`), incremented by a single `INSERT ... ON CONFLICT DO UPDATE`:

| Column | Value |
|---|---|
| `day` | UTC day of arrival |
| `report_type` | `csp` or `coep` |
| `disposition` | `report`, `enforce` or `unknown` |
| `effective_directive` | a CSP directive name from a closed list, `coep`, or `other` |
| `blocked_origin` | scheme and host (and port) of the blocked resource only; or the keyword `inline`, `eval`, `wasm-eval`, `data`, `blob`, `self`; or `other` |
| `document_path` | the path of the page, query and fragment never read, every id-like segment (number, GUID, long hex, token) replaced by `:id`; `(sensitive)` for private routes; `other` if unparsable |
| `ua_family` | `chrome`, `firefox`, `safari`, `edge` or `other` |
| `count` | reports under the key |

`(sensitive)` is stored instead of a path under `/app/review/g/`, `/api/v1/review-links/`,
`/api/v1/output-links/`, `/oauth/`, and for reset, verify, accept, invite, email-change and confirm
routes under `/app/` and `/api/`.

Every field is cut to a length cap first and then reduced to its closed set, so a hostile client cannot
put free text into a column.

## What is never stored or logged

Full URLs, query strings, fragments, the referrer, source file, script sample, line or column numbers,
the original policy, status code, IP address, user id, cookies, the raw User-Agent or the raw body. The
entity has no column for any of them (pinned by a test). Nothing is logged per report; the only signal
is the counter `reelbolt.csp_reports` by outcome (`stored`, `disabled`, `rate_limited`, `malformed`,
`rejected_type`, `error`).

## Consent

No cookie consent is needed. The consent rule in [`product-events.md`](product-events.md) is that
`client` rows exist only for a person who granted telemetry, because they are tied to a visitor id or
account. These rows carry no visitor id, no account and no identifier of any kind: only allowlisted
tokens and a count, so they are not personal data. The endpoint is therefore independent of
`Telemetry:VisitIngestEnabled`.

## Limits

* Cardinality: at most `Telemetry:CspReportMaxKeysPerDay` (2000) distinct keys per day; a new key past
  that folds into one `(overflow)` row per report type, disposition and browser family. Existing keys keep counting.
* Per-process token bucket (`CspReportRatePerSecond` 20, `CspReportBurst` 60); excess is a dropped
  `204`. nginx has its own zone in front (see "nginx").
* Retention: a daily sweep deletes rows older than `CspReportRetentionDays` (90).

## Switch and configuration

| Setting | Default | Meaning |
|---|---|---|
| `TELEMETRY_CSP_REPORT_ENABLED` (`Telemetry:CspReportEnabled`) | `true` | `false` = `204`, nothing stored |
| `Telemetry:CspReportRatePerSecond` / `CspReportBurst` | 20 / 60 | app-level token bucket |
| `Telemetry:CspReportMaxKeysPerDay` | 2000 | distinct keys per day before folding |
| `Telemetry:CspReportRetentionDays` | 90 | sweep window |

## Reading it

`infra/powerbi/csp-violations.sql` (one statement, as `powerbi_ro`): the top violations of the last 14
days by report count, with first and last day and the browser families.

```
docker exec -i reelbolt-postgres psql -U powerbi_ro -d "$POSTGRES_DB" -f - < infra/powerbi/csp-violations.sql
```

A high count on `(overflow)` is a flood, not a policy gap. A row with `other` as origin is a blocked
resource that is not an http(s) or ws(s) origin (for example a browser extension).

## nginx

* The two report-only headers carry the reporting target: the CSP ends in `report-uri /api/v1/telemetry/csp-report;
  report-to csp-endpoint`, the COEP header is `credentialless; report-to="csp-endpoint"`, and a
  `Reporting-Endpoints: csp-endpoint="/api/v1/telemetry/csp-report"` header (same-origin relative URL) is sent on
  the same document path classes (site, `/docs`, `/app`, guest review). Nothing else gets them.
* `location ~* ^/api/v1/telemetry/csp-report/?$` (`nginx/locations.conf`, before the telemetry ingest regex):
  anonymous (`auth_basic off`, `Authorization` and `Cookie` blanked), own zone `csp_report` (30r/m per client
  address, burst 20), body cap 16k.
* The answer is always an empty `204`: any method, a rate-limited request (429), an oversized body (413),
  an upstream error or refusal all end in the same `204` through `@csp_report_empty`. No `Set-Cookie`.
* The access log (`csp_report_log`) is path only: no query, referer, user agent or body. A rejected request is
  logged at `info`, below the error log threshold.
* `docker-compose.yml` passes `Telemetry__CspReportEnabled: ${TELEMETRY_CSP_REPORT_ENABLED:-true}`.
* `nginx/tests/security-headers.sh` pins all of it, including the flood case.
