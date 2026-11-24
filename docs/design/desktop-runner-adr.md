# ADR: Desktop runner UX, platform support and security model

- **Status:** Accepted, except item 8 (shell), which is a founder gate (D17). See "Founder decisions needed".
- **Date:** 2026-10-05
- **Work package:** E0 of the SaaS launch plan
- **Builds on:** the earlier SaaS launch decisions. Those are not re-opened here.
- **Consumers:** E1 to E14, and the D worker library (D4b), which must build against the answers below.
- **Excluded from the docs-site:** `docs/design/**` is in the `exclude` list of `docs-site/docusaurus.config.js`, so nothing here is link-checked. Do not link to this file from `docs/**` pages.

## Context

The desktop runner is a signed Go program that a user installs on their own machine. It pairs with an organisation, dials `runner-gateway` outbound only, and executes Remotion renders, `VideoCompile` and the extraction phase of `VideoAnalyze` (D14), so that compute costs the user 0 credits. The platform sends it model-authored code (Remotion TSX) and user media. The user can modify the binary. Both directions need a stated threat model.

What the repo already gives us, which constrains the choices: `sandbox/pkg/container` (driven by `sandbox/cmd/sandbox-executor`) starts each Remotion workspace with `docker run -d --read-only`, `--tmpfs /tmp` and `/home/node/.npm`, `--memory`, `--cpus`, `--pids-limit`, `--security-opt no-new-privileges`, `--cap-drop ALL`, `--user node`, on a docker network created `--internal` (and verified with `docker network inspect --format {{.Internal}}`). The desktop runner reuses exactly that contract.

## Time-sensitive facts checked for this ADR

All accessed 2026-10-05.

| Fact | Finding | Source |
|---|---|---|
| Wails v3 status | **Not GA.** Newest tag is `v3.0.0-beta.28`, published 5 Oct 2026; the release notes say "The API is stable, but you may still encounter issues before the final 3.0 release". Wails v2 is the stable line. | https://github.com/wailsapp/wails/releases |
| Docker Desktop licence | Free for personal use, education, non-commercial open source, and small businesses meeting **both** limits: fewer than 250 employees **and** less than $10M annual revenue. Exceeding **either** limit requires a paid plan. Docker Engine and Moby are unaffected. (The plan text says "more than 250 employees or more than $10M"; the UI text must say "250 or more employees, or $10M or more revenue".) | https://docs.docker.com/subscription/desktop-license/ |
| Podman on macOS/Windows | Podman 6.0 released 2026-06-24 (6.0.2 on 2026-07-22): default `podman machine` provider on macOS is now libkrun; Windows Hyper-V provider no longer needs admin to start/stop; Docker-compatible API v1.44. Podman Desktop on macOS exposes a Docker-compatible socket by default (`/var/run/docker.sock`, setting "Third-Party Docker Tool Compatibility"). On Windows and Linux that setting is not available; tools must be pointed at the Podman socket through `DOCKER_HOST` (Windows named pipe `npipe:////./pipe/docker_engine`). A running machine is a prerequisite. | https://versionlog.com/podman/6.0/ , https://podman-desktop.io/docs/migrating-from-docker/managing-docker-compatibility |
| macOS Gatekeeper | Since macOS 15 Sequoia the Control-click "Open" override is gone; a non-notarised app is blocked on first launch and the user must go to System Settings, Privacy & Security to allow it (15.1 closed the remaining bypasses). Notarisation is therefore a practical launch blocker for a mainstream audience. | https://eclecticlight.co/2024/08/10/gatekeeper-and-notarization-in-sequoia/ , https://hackaday.com/tag/appledrm/ |
| Apple Developer Program enrolment | **Individuals and sole proprietors can enrol as an individual**: no D-U-N-S number, purchase possible right after identity verification, $99/year. The seller name shown is the personal legal name. **Organisations need a D-U-N-S number**, must be a legal entity (no DBAs or trade names) and wait for Apple's verification. | https://developer.apple.com/support/enrollment/ |
| Azure Trusted Signing (now "Artifact Signing") | Public Trust certificates are available to **organisations** in the US, Canada, EU, UK, Australia, NZ, Japan, South Korea, Singapore, Switzerland, Norway and Israel. **Individual developers must be located in the US or Canada.** A Microsoft Q&A answer for an Irish sole trader confirms a non-incorporated sole trader is classified as an individual and is **not eligible**. Private Trust has no geographic limit but is not publicly trusted, so it does not clear SmartScreen. Identity validation takes 1 to 20 business days. | https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart , https://learn.microsoft.com/en-us/answers/questions/5915697/can-an-irish-sole-trader-(non-incorporated)-qualif |
| Windows code-signing rules | Private keys must live on FIPS 140-2 Level 2 hardware (token or cloud HSM) since 2023-06-01; publicly trusted code-signing certificates are capped at 458 days from 2026-03-01. Since the March 2024 SmartScreen change OV and EV both build reputation from download volume; EV still clears the warning sooner. (Secondary sources; verify with the chosen CA.) | https://sslinsights.com/best-code-signing-certificate-for-windows-application/ , https://textslashplain.com/2024/05/22/authenticode-in-2024/ |
| ffmpeg + GPL | A build with libx264/libx265 is GPL and cannot be distributed under the LGPL. When distributing binaries you must ship the corresponding source or a written offer, plus the licence text. Remotion's own docs make the same point about ffmpeg builds. The "separate process, so aggregation" reading is the standard position but was not stated in the sources found; it goes to the H7 lawyer review. | https://remotion.dev/docs/miscellaneous/ffmpeg-license , https://hoop.dev/blog/ffmpeg-license-and-compliance-essentials/ |

Two of these change the plan (Apple enrolment path, Windows signing path). They are called out in the decisions below and summarised at the end.

## Decisions

### 1. Platforms

**Decision.** v1 supports macOS 13+ (universal binary, arm64 + amd64), Windows 10 22H2 and Windows 11 on x64, and Linux x64 and arm64 with glibc 2.31 or newer. The runtime image is published for linux/amd64 and linux/arm64, and the runner always pulls the image matching the **host architecture**: an Apple Silicon Mac gets arm64, never amd64 under emulation. Windows arm64 is deferred. musl/Alpine Linux is unsupported. Linux ships as `.deb`, `.rpm` and AppImage.

**Alternatives rejected.** Shipping amd64-only and relying on Rosetta/QEMU (Chrome under emulation is several times slower and flaky); supporting Windows arm64 now (container-runtime support is immature and we cannot test it in CI).

### 2. Container runtime detection order

**Decision.** The probe (E3) tries, in order, and takes the first that passes a **live capability probe**:

1. `DOCKER_HOST` if set.
2. The active Docker context (`docker context inspect`), which covers Docker Desktop and Colima.
3. Rootless Docker socket `$XDG_RUNTIME_DIR/docker.sock` (Linux).
4. Podman: `podman machine` socket on macOS and Windows (use the Docker-compatible API; fall back to the `podman` CLI), rootless `podman.sock` on Linux.
5. Rootful `/var/run/docker.sock` (Linux), last.

A rootful-socket-only Linux setup is **allowed**, with a persistent warning in the app that this user is root-equivalent through the `docker` group. Refusing it would turn away most Linux users, and the group membership exists before we arrive. The capability report carries `runtime.mode = rootful | rootless | vm` so the platform can refuse rootful by policy later without a client release. Minimum versions: Docker Engine 24, Podman 5.0 (raised from the plan's 4.6, because Podman 6.x is current and 4.6 is not worth a test lane; the matrix is Docker Desktop current, Podman 5.x, Podman 6.x). Support is confirmed by running the probe, never inferred from a version number: create a throwaway `network create --internal`, verify it with `network inspect`, start the pinned image on it with the exact flag set from item 3, and confirm there is no route out. The probe re-runs on every start and when the runtime changes.

**Alternatives rejected.** Requiring rootless-only (excludes most Linux users); trusting version numbers (Podman and Docker differ on `--internal` edge cases); auto-installing a runtime (item 12).

### 3. Native versus container

**Decision.** ffmpeg and ffprobe run **natively** (bundled, item 4) for `video.compile` and `video.analyze.extract`. They only ever see the user's own media plus server-authored, schema-validated filter specs, and the worker re-validates every argv (D1: no URL or `protocol:` inputs, `-protocol_whitelist file,pipe`, no `movie=`/`amovie=`/`subtitles=`/`textfile=` outside the session directory). Remotion **always** runs in the container, with exactly the flags in `sandbox/pkg/container` today: `--read-only`, tmpfs `/tmp` and npm cache, memory/cpu/pids caps, `--security-opt no-new-privileges`, `--cap-drop ALL`, `--user node`, and an `--internal` network. This is mandatory and not user-configurable. With no usable container runtime the runner advertises only `video.compile` and `video.analyze.extract` and the UI says why.

The container never has a network and never fetches anything: the runner (on the host, with scratch-root-confined paths) performs `stage` GETs and `publish` PUTs and moves bytes in and out of the workspace. The workspace must not bind-mount `node_modules` from the host (item 12, VirtioFS).

**Alternatives rejected.** ffmpeg inside the container (more attack surface for no containment benefit, plus slow media I/O through the VM; same reasoning as `docs/video-editing.md`); Remotion natively on the host (model-authored code, never); a per-user "run without container" switch (it would turn a render job into arbitrary code execution on the user's machine).

### 4. ffmpeg licensing

**Decision.** Ship a **GPL** static ffmpeg and ffprobe (libx264 and libmp3lame enabled) for output parity with the cloud, built by us from pinned upstream source in the release pipeline (E10), not downloaded from a third-party binary site. It is invoked only as a separate child process; we never link libav* into the runner, which keeps the runner's own licence independent (aggregation). Obligations we meet: the installer and the About screen carry the GPL text and `THIRD_PARTY_NOTICES`; the exact corresponding ffmpeg/x264/lame source plus the build scripts are published at a stable URL that is part of every release, with a written offer as a backstop (valid for three years); `ffmpeg -version` and its configure line stay unmodified. The Debian ffmpeg inside `sandbox-runtime` is already a GPL build and ships under the same notice. H.264/AAC patent-pool exposure of redistributing encoders, and the aggregation reading above, go to the H7 lawyer review and do not block launch.

**Alternatives rejected.** LGPL build plus OS hardware encoders (VideoToolbox, `h264_mf`, VAAPI): inconsistent output quality across machines, kept as a later "hardware encoder" capability; linking libav* into the Go binary (would pull the whole runner under the GPL); using the user's system ffmpeg (unknown build, no parity).

### 5. Threat model, platform to user

**Decision.** Treat everything the platform (or a model acting through it) sends as hostile.

- Remotion code runs only in the container of item 3, with no egress.
- Inputs arrive only as presigned single-object GET URLs, each fetched by the runner into the job's scratch directory. The runner never mounts a host path outside its scratch root. Jobs cannot name host paths: the runner rejects any job field containing an absolute path, `..`, a drive letter, a UNC prefix or a `file:` scheme, and every path is resolved inside the job directory and re-checked after symlink resolution.
- The runner executes only the closed job and op set of the D1 protocol; there is no "run this command" op beyond the existing sandbox `exec` allowlist.
- **No provider keys on the wire.** The protocol has no op that carries a provider key or any platform credential (merge ruling R11); provider resolution and every model call stay in the engine. E14 tests this.
- Gateway authenticity: the runner connects only to the compiled-in gateway hostname over TLS, validating against a CA bundle embedded in the build; every `assign` carries a per-job HS256 token; the job envelope is additionally signed by the platform if D1 adopts a signed envelope (it should).
- Resource governance: user-set caps (CPU share, memory, concurrent jobs, quiet hours, battery-aware pause) are enforced by the runner and passed in the `docker run` flags.

**Alternatives rejected.** Trusting the gateway on the strength of TLS alone (a compromised gateway would own every runner); letting jobs reference local files for "faster" access to the user's own media (a path-taking op is the first thing an attacker looks for).

### 6. Threat model, user to platform

**Decision.** The runner binary, its configuration and its output are **untrusted**, consistent with D16. The per-device Ed25519 key proves *which device* produced an output, not that the output is honest. A stolen or extracted device token can (a) pull jobs routed to its own organisation and (b) PUT to the presigned URLs inside those jobs; it can never reach provider keys, the platform API, other organisations or other jobs. Outputs land in `runner-staging/{jobId}/{n}`, are size-capped by HEAD after upload, are re-probed server-side (D12), are tagged `FromRunner`, are excluded from the trusted step cache, and only then move to their final key. A job's price is computed from the platform-side job spec, never from anything the runner reports. Devices are revocable (E4) and a revoked device is disconnected within one heartbeat interval.

**Alternatives rejected.** Remote attestation or hardware-bound keys as a requirement (unavailable on many Linux desktops, over-engineered for the threat); trusting runner-reported metrics for anything billable.

### 7. "DRM" stance

**Decision.** No obfuscation, no anti-debugging, no packing, no integrity checks that exist only to frustrate the user. The runner is auditable. The value is server-side: jobs flow only under an active plan, billing is priced from the platform job spec, output is untrusted, and devices can be revoked. A cracked runner gains nothing it was not already entitled to, because the platform never sends it anything that is not the user's own project or the already-disclosed render workspace.

**Alternatives rejected.** Obfuscated or anti-tamper builds (cost, antivirus false positives, no effect on the actual risk); licence-key checks in the client (entitlement is already enforced server-side).

### 8. Shell (D17, founder gate)

**Recommendation.** **Go + Wails v3**, with a shell-agnostic headless core so the choice is cheap to reverse. Wails v3 is still beta (`v3.0.0-beta.28`, 2026-10-05, no release candidate), so:

- E5 builds the runner as a **headless Go core** (no UI imports) plus a thin CLI. It is useful on its own (servers, headless Linux) and is the reversible part.
- E6 starts with a one-week spike on Wails v3 **pinned to an exact beta tag** (never `latest`). Go/no-go checklist: system tray with menu on all three OSes, one settings window, **no loopback listening socket** (verified in E13), signed and notarised build works, single-instance lock. If any item fails, E6 uses **Wails v2 + `fyne.io/systray`** over the same core, costing about a week.
- If Wails v3 GA has shipped when E6 starts, pin GA instead.

Why Wails: one Go module that imports the D worker library and `sandbox/` directly; system webview over IPC, so no loopback HTTP listener; the settings and job UI can reuse React skills from `web/`; a binary around 15 MB.

**Alternatives rejected.** Tauri (the agent would be a Go sidecar under a Rust host: two toolchains, two binaries to sign and notarise); Electron (about 150 MB, a bundled Chromium to patch, large attack surface for an app holding a device key); systray-only (job list, settings and pairing need real windows); waiting for Wails v3 GA (open-ended, and the headless core plus the v2 fallback remove the dependency).

### 9. Pairing

**Decision.** An RFC 8628-style device authorisation flow, specified in E4. The runner generates an **Ed25519** keypair on first run and registers the public key during pairing; the user approves a short user code in the dashboard (E7-UI) while signed in to the target organisation. The private key is stored in the OS keychain: macOS Keychain, Windows Credential Manager (DPAPI), or Linux Secret Service. If none is available the key goes to a 0600 file in the runner's data directory and the app shows a persistent warning. The gateway verifies a signature over the `welcome` nonce against `runner_devices.public_key` and `revoked_at` on every connect. The pairing code expires in 10 minutes, is single-use, polling is rate-limited, and approval records `PairedByUserId`. Only org Owner and Admin can approve (to be finalised with A0's permission matrix).

**Alternatives rejected.** Copy-pasting a long-lived token into the app (no per-device identity or revocation, leaks into shell history); browser OAuth redirect to a loopback port (violates the no-listening-port rule); a shared per-org secret (cannot revoke one machine).

### 10. Update and distribution

**Decision.** Signed update manifests and staged rollout (E9): the manifest and each artifact hash are signed with an **offline Ed25519 release key** whose public half is compiled into the runner; the update channel's TLS certificate is not the trust root. Stages 1%, 10%, 50%, 100%, with a server-side kill switch and a minimum-supported-version field the gateway enforces at `hello`. Rollback is allowed only to a signed version at or above the minimum. The runtime image digest is part of the manifest, so image and agent roll forward together. Distribution is from our own site (`/runner`), no third-party mirrors in v1.

Signing paths, which **differ from the plan**:

- **macOS: Developer ID signing and notarisation are a launch blocker (E10a).** Because the publisher is an individual and not a registered company, enrol **now as an individual** (no D-U-N-S, purchasable right after identity verification, $99/year), not as an organisation. Gatekeeper then shows the publisher's personal legal name as the developer. If a company is formed later, enrol that entity separately and re-sign; the in-app updater trusts our own manifest key, not the Apple Team ID, so the switch does not break updates.
- **Windows: Azure Artifact Signing (formerly Trusted Signing) is not available to an individual publisher in the EU.** Decision: buy an **OV code-signing certificate with cloud-HSM signing from a CA that validates individual publishers** (shortlist: SSL.com eSigner, Certum cloud signing, Sectigo via a reseller; confirm with the CA that an individual publisher is accepted before paying), accept a SmartScreen warm-up, and re-evaluate Artifact Signing the day a company exists. Certificates are capped at 458 days, so a renewal runbook is part of E10b.
- **Linux:** `.deb`/`.rpm` signed with a project GPG key, AppImage with a detached signature; there is no notarisation equivalent.

**Alternatives rejected.** Waiting for a company before signing (blocks macOS and Windows for weeks); shipping unsigned (Sequoia blocks first launch, SmartScreen warns, antivirus may quarantine the bundled ffmpeg); a third-party updater whose trust root we do not control; Azure Private Trust (not publicly trusted, no SmartScreen benefit).

### 11. Telemetry

**Decision.** Off by default, opt-in only (E11), asked once in the first-run flow in plain words. Even when off, the runner sends **no** analytics; it still sends the protocol messages that are the service itself (capabilities in `hello`, job progress, failure class and reason). Opt-in adds crash reports and aggregate performance metrics (job duration, encode speed, OS/arch, runtime kind, versions). Never collected under any setting: file names, paths, media content, prompts, project names. The endpoint is first-party; no third-party SDK that phones home on its own.

**Alternatives rejected.** Opt-out telemetry (trust cost for a tool that sits next to a user's media); no crash reporting at all (cannot support a signed-binary fleet blind).

### 12. Platform risks and mitigations

**Decision.** Each risk gets a concrete product behaviour.

| Risk | Decision |
|---|---|
| Docker Desktop licensing | Free below 250 employees and $10M revenue, paid at or above either. The first-run screen asks no company-size question; it **suggests Podman Desktop first**, names Docker Desktop and Colima as supported, links the licence page with the thresholds stated plainly, and **never auto-installs or silently launches a container runtime**. |
| Windows prerequisites | Requires WSL2 (or Hyper-V for Podman) and virtualisation enabled in firmware; Windows Home works through WSL2 only. The probe distinguishes "no runtime", "runtime installed but stopped" and "virtualisation disabled" and shows a specific fix for each. |
| macOS bind-mount speed (VirtioFS) | The Remotion workspace does not copy or mount `node_modules` from the host: it lives in the container filesystem, with only source and output exchanged through the runner (E1/E5c). |
| Antivirus quarantining bundled ffmpeg | Signed bundle, ffmpeg under the signed install directory, and a first-run self-check that reports a quarantined binary in plain words with AV-exclusion guidance. |
| Laptop sleep or lid close mid-job | The runner holds a power assertion (macOS, Windows execution-state request, Linux logind inhibitor) while a job runs. If the connection still drops, the 45 s lease expires and D requeues a one-shot job or marks a session `Expired` (`session_lost`). Users can choose pause-on-battery and quiet hours. |
| Disk usage | Scratch root has a user-set cap (default 20 GiB); old workspaces and the runtime image are pruned; a job that would exceed the cap is rejected with `fail{class: runner}` instead of filling the disk. |
| Image pull size and first run | The runtime image is pulled by digest on first run with visible progress; no Remotion capability is advertised until it succeeds. |

**Alternatives rejected.** Bundling a container runtime in our installer (licensing, size, root-level installs); detecting company size to decide Docker eligibility (we cannot know it and should not ask).

## Consequences

- E3 implements the item 2 order and live probe; E5 builds the headless core; E6 holds the Wails spike and fallback; E10a/E10b carry two signing flows that differ from the plan; E1/E2 produce the multi-arch image that the item 1 pull rule needs.
- The D1 protocol must contain no provider-key op and must keep the job-field path-rejection rules testable from the shared golden fixtures.
- The D owner signs off on items 3, 5, 6 and 9 before E5 starts.

## Facts that changed versus the plan's assumptions

1. **Wails v3 is still beta** (beta.28, 2026-10-05). The plan's fallback is the default risk; item 8 turns it into a spike with exit criteria.
2. **Azure Trusted Signing does not cover an individual publisher in the EU.** The plan's "Azure Trusted Signing or an OV certificate" collapses to the OV route unless a company is formed.
3. **Apple enrolment does not need a D-U-N-S number for an individual or sole proprietor.** The plan's "organisation, D-U-N-S, 1 to 3 weeks" lead time drops to days by enrolling as an individual; the cost is the personal name in the Gatekeeper dialog.
4. **Docker Desktop thresholds** are unchanged but the condition is "either limit exceeded".
5. **Podman is now 6.x** (libkrun on macOS); minimum raised from 4.6 to 5.0, and `DOCKER_HOST` is the route to the Podman socket on Windows and Linux.

## Founder decisions needed

**F1. Desktop shell (D17).** Recommendation: Wails v3 pinned beta, headless core, one-week go/no-go spike; Wails v2 + systray as the pre-agreed fallback.

| | Wails v3 (recommended) | Wails v2 + systray | Tauri | Electron |
|---|---|---|---|---|
| Release status 2026-10-05 | Beta (beta.28), API declared stable | Stable | Stable | Stable |
| Language fit | One Go module with the worker lib | Same | Rust host + Go sidecar | JS host + Go sidecar |
| Native tray | Built in | Via `fyne.io/systray` | Plugin | Built in |
| Loopback port | None (IPC) | None | None | None, but large surface |
| Binary size | about 15 MB | about 15 MB | about 10 MB + sidecar | about 150 MB |
| Signing burden | One binary | One binary | Two | One bundle, many files |
| Main risk | Beta breakage | Older multi-window API | Two toolchains | Size, patch cadence |

Answer needed before E6 starts; it does not block E1 to E5.

**F2. Apple enrolment as an individual, now.** Recommendation: yes, start this week. It costs $99 and shows the publisher's legal name to macOS users until a company is enrolled. The alternative is waiting for a company and a D-U-N-S number.

**F3. Windows signing route.** Recommendation: OV certificate with cloud-HSM signing from a CA that validates an individual publisher (confirm before paying), accepting SmartScreen warm-up. Alternative: incorporate first and use Azure Artifact Signing, which also removes the personal-name issue on macOS, but depends on a company that does not yet exist. Needed before E10a.
