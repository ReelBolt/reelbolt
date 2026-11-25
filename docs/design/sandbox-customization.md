# Remotion sandbox customization: measurement and design

Initiative `sandbox-customization` (Owner go 2026-10-27). Goal: agents that author videos in the Remotion sandbox can change
fonts, libraries, assets and building blocks as freely as is safe, with far fewer refusals. Internal design note, not
published. Measured 2026-10-27 on `origin/master` 5ca2195ba.

## 1. Measurement

### 1.1 The HyperFrames test denials did not come from our sandbox

PR #691's round-1 counts (77 Remotion, 36 HyperFrames) are the `permission_denials` of the `claude -p` result events
(`spike/round1/results_v2metrics.jsonl`, recounted by `spike/metrics.py`). Every denied call is a **Bash** call refused by the
test harness's own `--allowedTools` prefix list (`spike/run.sh`: `Bash(npx remotion:*)`, `Bash(ls:*)` ...). The commands
refused were heredocs (`cat > file <<EOF`, 28), `sed -i` (19), `for` loops, `cd`, `&&` and `;` chains, `/tmp` paths, `rm`, `bash x.sh`.
None reached the sandbox: authoring ran on the host, only the final render ran inside sandbox limits. The test therefore says
nothing about our command allowlist. It does show the shape of what agents try (heredocs, pipes, loops, scratch files), which our
tool layer already absorbs (agents use `WriteSandboxFile`, never a shell).

### 1.2 Real refusals in our own engine (local-staging DB, 2026-10-09 to 10-25)

108 sandbox-authoring step results (Author 53, RemotionComponentTranslator 27, CompositionReviser 15, MotionGraphicsPlanner 8,
GraphicDesigner 5), more than 2,000 tool calls, counted per distinct step. Command-allowlist hits (`command not allowed`,
`Unsupported npm script`) and `StageProjectFile` code-file refusals: **zero**. What blocked or wasted agent effort:

| Refusal | Steps | Cause |
|---|---|---|
| `NETWORK_FONT` (`@remotion/google-fonts/Inter`, `ChakraPetch`, `PlayfairDisplay` ...) | 11 | Agent wants a Google Font outside the 14 installed families. The vendored `remotion-markup/google-fonts.md` teaches that import. |
| `InstallNpmPackages` fails (HTTP 409, egress off) | 5 | Author, Translator and planner prompts say "call InstallNpmPackages"; with egress off it can never work. |
| TypeScript `Cannot find module` | 4 | `@phosphor-icons/react` (icons) and `@data/*.json` (data the agent expected to stage). |
| `RunSandboxNpmScript` / `remotion compositions` non-zero | 6 | Follow-on of the above. |
| Scene-declaration quality gate | 33 | Not a sandbox limit (copy pacing rule). Out of scope. |
| `headless shell unavailable` | 3 | Stale local image without Chrome. Environment, not product. |
| Edit protocol miss (`oldText not found`) | 8 | Hygiene of the edit tools. Out of scope. |

The demand is **missing content** (fonts, icons, libraries, JSON data), not missing commands.

Skills: Author opened a skill in 27 of 62 steps (44 %), the Translator in 21 of 33, the Reviser in 2 of 19. In the HyperFrames test the
Remotion agents opened none (its prompt did not instruct it; ours does). The vendored skill bodies are unpatched upstream text and
contradict the sandbox (`google-fonts.md`, `npx remotion add ...`, `npm i zod`, remote media URLs).

### 1.3 Does the 256 MB `/tmp` limit anything? (no)

Measured in `reelbolt-sandbox-runtime:local` with the production flags (read-only root, `/tmp` 256 MB, `~/.npm` 512 MB, 2 GB, 2 CPUs,
256 pids, no network):

- 60 s, 1920x1080, 30 fps (1,800 frames), heavy scene: **`/tmp` peak 56 MB**. Frames stream to ffmpeg, they are not kept.
- A 200 MB file in `public/`: `/tmp` peak 29 MB (public files are served, not copied into the bundle).
- Installing 18 packages (`@remotion/{transitions,shapes,paths,noise,motion-blur,lottie,media-utils,layout-utils,captions,three,gif,fonts}`,
  lucide-react, @phosphor-icons/react, lottie-web, zod, simplex-noise) with `--ignore-scripts`: `/tmp` peak **0 MB**, `~/.npm` 78 MB
  of 512, `node_modules` +160 MB. npm does not use `/tmp`.
- Fonts are workspace files (bind mount), not `/tmp`.

Limits that do bite: (1) every sandbox start copies the template `node_modules` (792 MB, of which 78 MB is the unusable
`@remotion/google-fonts`) into `/workspace`, so each added library costs disk and start time for every run; (2) a native 3840x2160 render of a
heavy synthetic scene is OOM-killed at the 2 GB default (`oom_kill`, ffmpeg SIGKILL at frame 35; unchanged with `--shm-size 512m`). Not caused
by this initiative; heavy 3D or shader blocks must be budgeted for 1080p.

### 1.4 Facts about today's sandbox that shape the design

- Exec allowlist: `npm run {build,render,typecheck,compositions,lint}` (no `lint` script exists), `npx remotion {render,still,compositions}`,
  `node /opt/remotion-template/reelbolt/check-layout.mjs --k=v`. Trailing args unvalidated. The refusal text is the bare `command not allowed`.
  Hygiene, not containment (docs/sandbox-service.md "Threat model").
- Egress off by default; `POST /packages` answers 409. Package-name regex rejects ranges; install has no `--ignore-scripts`.
- The image build runs `npm install` from `package.json` only (the lockfile is unused), so versions float within carets.
- Fonts: 10 families + Liberation + emoji at a pinned google/fonts commit, sha256 in `sandbox/fonts/fonts.json`; no italics.
- `StageProjectFile` refuses `.js .ts .tsx .json .wasm .html` (a guardrail-bypass control while tool screening is on; shipped config has screening Off)
  and stages only under `public/`. Font files (`.ttf .otf .woff2`) are stageable and a local `@font-face` passes the network-font preflight, but no
  prompt or tool says so.
- Preinstalled: remotion 4.0.545 core/cli/renderer, react 19, three + react-three, d3, framer-motion, react-spring.

## 2. Design

Principle: **widen what the platform provides, never what the sandbox may reach.** No sandbox egress, no raw npm against the internet, no new
commands. Everything an agent gets is pinned, hashed, versioned and either baked into the image or written into the workspace by a trusted engine
tool, so workspace archives and revision replay stay byte-reproducible.

### 2.1 Fonts

| Option | Security | Verdict |
|---|---|---|
| A. Bigger baked set (about 40 families, with italics) | none new (OFL files, sha256-pinned) | Yes |
| B. Any Google Font through an engine-side tool `StageFont` | engine egress to one host at a pinned google/fonts commit; checked-in catalog with sha256 per file; 10 MB cap; no redirects; no agent-supplied URL; cache keyed by hash; licence filter (OFL/Apache/UFL) | Yes, flag `Sandbox:GoogleFonts:Enabled`, **default off** |
| C. Project fonts (`.ttf .otf .woff2`) through the same tool | sfnt/woff2 magic check, size cap, existing `projects/{id}/` prefix check | Yes |
| D. Make `@remotion/google-fonts` work | needs sandbox egress to gstatic | No: remove the package; the preflight message points at B |

`StageFont(family, source?, weights?, italic?)` stages into `public/fonts/<Family>/`, returns a ready `@font-face` block and the family name; the font
advisory treats a staged family as declared.

### 2.2 Libraries

| Option | Verdict |
|---|---|
| Wider baked, vetted catalog (about 25 packages, exact versions, lockfile, `npm ci --ignore-scripts`, size budget +120 MB): official `@remotion/*` (transitions, shapes, paths, noise, motion-blur, lottie, media-utils, layout-utils, captions, three, gif, fonts, zod-types), `lucide-react`, `@phosphor-icons/react`, `lottie-web`, `zod`, `simplex-noise` | Yes |
| `InstallNpmPackages` becomes catalog-aware: "already available" for catalog names, a refusal listing the catalog otherwise, never a surprise 409 | Yes |
| Pinned static registry on the sandbox network (nginx + generated packuments, `--ignore-scripts`, driver-supplied `--registry`) for a long tail | Documented, **not built**: the cost is curating packages, not serving them. Decide after real demand data (Owner call) |
| Open egress, or Verdaccio with an uplink | No (supply chain, exfiltration) |

Replay: template `package.json` changes alter the `reelbolt-sandbox-runtime` digest (`runtime-image.lock`); workspace archives exclude `node_modules`,
so a restored workspace reseeds from the image of that time. A block records its packages in `src/blocks/blocks.lock.json` so a replay can detect a missing one.

### 2.3 Command allowlist

No expansion (zero hits in 108 steps). Fixes only: drop the dead `lint` entry, make the refusal say what is allowed ("allowed: npm run
build|render|typecheck|compositions; npx remotion render|still|compositions; use the CheckLayout and CheckLintAndTypeErrors tools"), validate trailing
`npm run` args against a flag shape.

### 2.4 Data assets

`StageProjectFile` keeps refusing code (`.js .ts .tsx .mjs .cjs .wasm .html`). `.json` becomes stageable **only while tool screening is Off** (the shipped
state), after a parse check and an 8 MB cap, so Lottie, chart data and glTF-JSON arrive without being pasted. Turning screening on restores the old rule
(its stated reason). No other format changes.

### 2.5 Block library (Owner request after the HyperFrames comparison)

First-party, editable Remotion blocks in `inference/blocks/` (`catalog.json`; TSX per block; a preview composition each), shipped in the engine image next to
`skills/`, hash-checked at load, directory licensed MIT (Owner question, default MIT). No third-party code is vendored. Natural Earth (public domain) is the only
data source (map outlines).

- Tools (new group `SandboxBlocks`; Author, CompositionReviser, MotionGraphicsPlanner/Director, GraphicDesigner): `ListBlocks(category?, query?)`,
  `GetBlock(id)` (props, example, required packages), `AddBlock(id)` copies the files into `src/blocks/<id>/` (plus `src/blocks/_shared/`) through the sandbox
  file API and records `src/blocks/blocks.lock.json`. It checks each required package is in the workspace `package.json` and refuses otherwise with the
  alternative. Idempotent; never overwrites a file the agent edited.
- Launch scope: about 45 blocks in 9 categories: transitions, titles and kinetic type, captions, charts, map overlays, grain/CRT/glitch/film effects,
  backgrounds, device frames and UI, shapes and callouts. The HyperFrames registry has about 450; we do not claim parity: we ship a verified core and a repeatable
  pipeline (`inference/blocks/scripts/verify.mjs`: typecheck, still render, non-blank check, layout lint, run in the sandbox image).
- Getting agents to use it: (1) tools are always in the tool list with category names in their description (agents skip skills, they do read tool descriptions);
  (2) one short Author/Reviser/planner prompt paragraph: before building a transition, title, caption, chart, map or texture from scratch, call `ListBlocks`;
  (3) a first-party skill `reelbolt-blocks`; (4) skill prefaces so upstream text stops contradicting the sandbox. Metric: share of Author steps that call
  `ListBlocks`/`AddBlock`, and block use in accepted films.

### 2.6 Prompts and skills made truthful

Remove the "install packages" instructions (Author, planner), correct the fonts text (staged fonts are allowed; the new tool is the path), add a
`REELBOLT-PREFACE.md` to the `remotion-*` skills (vendored text untouched) saying what is offline and what to use instead. `DefaultPrompt` and
`DatabaseSeeder.BuiltInAgents` change together in one commit.

## 3. Security analysis

| Surface | Threat | Control |
|---|---|---|
| `StageFont` Google fetch | SSRF, redirect, exfiltration, tenant bleed, oversize, hostile font | fixed host and commit, family lookup in a checked-in catalog, sha256 verify, no redirects, 10 MB cap, TLS, shared cache keyed by hash (public data), font parsed only by Chromium in the sandbox as today, flag default off |
| `StageFont` project source | cross-tenant read | existing `projects/{id}/` prefix check and presigner |
| `AddBlock` | path traversal, overwriting agent code, supply chain | files from the engine's hash-verified catalog, destination fixed under `src/blocks/`, no agent-supplied paths, no network |
| Wider baked packages | supply chain, install scripts | exact versions, lockfile, `--ignore-scripts` at image build, integrity hashes, dependency audit before merge, native-binary packages excluded |
| `.json` staging | prompt injection into screened tools | only while screening is Off; parse-validated; adds no execution path beyond what the agent already writes |
| Allowlist messages | information disclosure | static text only |
| Image size and start time | DoS | budget +120 MB, drop 78 MB google-fonts, report the per-start copy cost |

## 4. Rollout

`Sandbox:GoogleFonts:Enabled` default false. Everything else is additive and on by default (new tools, new files, wider image). Step cache `SchemaVersion`
unchanged: Author, Reviser and planner steps are never cached and no cached step's framing changes. The `SandboxBlocks` group is added wherever sandbox
groups are enumerated (`StepCachePolicy`, `WorkflowStepDependencyGraph`, `ColdStartHoldPlan`, `ToolScopingDriftGuardTests`).

## Implementation notes (post-review)

What changed against the design above:

- **Fonts.** `StageFont` returns a JSON result with a `snippet` (`loadFont` from `@remotion/fonts` with a local
  `staticFile(...)`, one call per staged file) instead of an `@font-face` block. The agent pastes it at module top
  level and uses the plain family name; `loadFont` calls `delayRender` itself. Project fonts use the same snippet.
- **gsap** 3.15.1 is in the package catalog (Owner decision of 2026-10-27, recorded in the initiative's `decisions.md`). It
  ships under the GSAP Standard "No Charge" licence, not MIT, which prohibits use in no-code visual animation tools that
  compete with Webflow without written consent; the owner accepted that risk. Agents drive it only by the frame, see the
  "GSAP in Remotion (ReelBolt)" guide. `lucide-react` is 1.59.0.
- **Lockfile.** The image installs from a lockfile with a 7-day release cooldown, so a freshly published version is
  not picked up.
- **zod** is pinned through an `overrides` entry so every dependent resolves one version.
- **tsconfig** uses bundler module resolution.
- **`npm run`** takes script arguments only after `--`, each `--flag` or `--flag=value`.
- **`.json` staging** is allowed only while tool screening is Off; `.jsx .mts .cts .htm` join the refused code
  extensions. The `src/index.ts` rule is prompt guidance, not enforced.
