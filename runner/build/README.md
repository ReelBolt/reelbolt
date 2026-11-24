# The runner release pipeline (WP E10)

Everything needed to turn a commit into signed installers and a signed update
manifest for the desktop runner. The workflow is
[`.github/workflows/runner-release.yml`](../../.github/workflows/runner-release.yml); this
directory holds the recipes and the operator's checklist.

WP E10 is two halves and only one of them is code:

* **E10a** is procurement and key custody - an Apple enrolment (deferred by
  D26), a Windows code-signing certificate, and the Ed25519 *release key* that
  decides which updates a runner will accept. **No such key exists in this
  repository and none can be created from it.** Every build made here carries no
  trust root, so `TrustedKeys()` returns `ErrNoTrustedKey` and the runner refuses every
  update before it touches the network. That is the intended state, not a gap to
  paper over.
* **E10b** is the pipeline itself: the manifest tool, the packaging recipes, the
  ffmpeg build, and the workflow that signs and publishes.

## What the operator has to do, once

Do these in order. Until steps 1-4 are done the pipeline still builds and the
packaging is still exercisable, but `publish` refuses and says which one is missing.

### 1. Create the release key (Ed25519)

On a machine you control, never in CI:

```sh
mkdir -p -m 700 "$HOME/reelbolt-release-key"
cd runner
go run ./cmd/reelbolt-manifest gen-key -out "$HOME/reelbolt-release-key/release.key"
```

It writes the private key to `release.key` in that directory (mode 0600), which is
outside the repository checkout on purpose so that no `git add` can pick it up, and
prints only the public half. **Never commit either half of a real key, and never print the private
half**: it is a code-execution credential for every installed runner.

* The file's contents go in the GitHub environment `release` as the **secret**
  `RUNNER_MANIFEST_SIGNING_KEY`.
* The printed stamp (`<id>:<hex>`) goes in the **repository variable**
  `RUNNER_RELEASE_PUBLIC_KEY_CURRENT`. It is public and deliberately not a secret:
  it is compiled into every released binary.
* Keep an offline copy of `release.key`. A lost release key cannot be recovered -
  every installed runner trusts only the keys compiled into it, so losing both the
  key and the ability to sign with it means the fleet can never be updated again.

**Key rotation.** `RUNNER_RELEASE_PUBLIC_KEY_NEXT` exists for this. Ship one release
signed by `CURRENT` that also carries the new key as `NEXT`; every runner that
installs it now trusts both. Then move the new key into `CURRENT` and start
signing with it. A runner on either build accepts either signature, so no machine
is ever stranded.

### 2. Windows code signing (E10b depends on it)

A Windows artifact with no Authenticode signature is not "unsigned but usable":
`runner/internal/update` calls `WinVerifyTrust` before it installs one, so an
unsigned `.exe` or `.msi` is refused by the runner itself. Options, in the plan's
order of preference:

Signing happens on Linux with [jsign](https://ebourg.github.io/jsign/) (pinned by
SHA-256 in `windows/Dockerfile.toolchain`) from `windows/package.sh`; there is no
signtool and no Windows runner. Secrets reach the container by name only and never
appear on a command line (jsign reads them as `env:NAME`).

* **Azure Trusted Signing** (~$10/month). Set the repository variable
  `RUNNER_WINDOWS_SIGNING=trusted-signing` and give the signing step
  `AZURE_TRUSTED_SIGNING_TOKEN` (an Azure AD access token for the Trusted Signing
  scope, obtained by GitHub OIDC login, so no static Azure secret), `TS_ENDPOINT`
  (`https://<region>.codesigning.azure.net`), `TS_ACCOUNT` and `TS_PROFILE`. The
  timestamp server defaults to `http://timestamp.acs.microsoft.com`
  (`TS_TIMESTAMP_URL` overrides it). This path is NOT exercised end to end (no
  account); the PFX path below was. Check
  eligibility: at last look it wanted an organisation with three or more years of
  history, or individual availability in limited regions.
* **An OV certificate** (~$200-400/year) on a cloud HSM or as a PFX. Set
  `RUNNER_WINDOWS_SIGNING=pfx`, `WINDOWS_SIGNING_PFX_BASE64` and
  `WINDOWS_SIGNING_PFX_PASSWORD`. The PFX is written to a 0600 file in a 0700
  temp directory and shredded on exit.

After signing, `package.sh` checks every file with `osslsigncode verify` and fails
if a signature is missing or its digest does not match. It cannot check trust to
Microsoft's root (not in the Linux CA bundle); that is `WinVerifyTrust` on a real
Windows machine, covered by the manual smoke checklist.

Either way expect SmartScreen warnings until download reputation builds. An EV
certificate no longer buys an instant reputation advantage, which is why it is
not the recommendation.

### 3. The .deb repository key (the AppImage is no longer GPG-signed)

**Removed:** `package.sh --sign-appimage`, the `RUNNER_APPIMAGE_SIGNING_KEY`
secret and the `APPIMAGE_GPG_KEY` variable. The runner could not check that
signature itself, so it was an extra layer nobody on the update path relied on,
and it forced a secret into a build job. If you set those, delete them. What
protects a download instead: the manifest's Ed25519 signature (the update trust
root), Authenticode on the Windows files, the published `SHA256SUMS`, and the
optional keyless cosign bundles (`*.sigstore.json`, see "Where it runs").

One Linux-only thing remains:

* **The .deb repository.** The runner never installs a `.deb` (apt owns those
  files), so apt is what verifies it, and apt verifies the *repository*’s
  signature. That key belongs to wherever the repository is published, not to
  this workflow, and it must exist before the first `.deb` is offered.

### 4. R2 credentials for the download bucket

Cloudflare R2, one token scoped to the downloads bucket only (the same
one-token-per-purpose rule as [`docs/cloud-storage.md`](../../docs/cloud-storage.md)).
In the `release` environment:

| Name | What it is |
| --- | --- |
| `R2_ACCOUNT_ID` | the Cloudflare account id, used to build the `eu.r2.cloudflarestorage.com` endpoint |
| `R2_BUCKET` | the downloads bucket |
| `R2_ACCESS_KEY_ID` / `R2_SECRET_ACCESS_KEY` | Object Read and Write on that bucket, and nothing else |

The bucket must be served at `https://downloads.reelbolt.ai/runner/`.
`runner/internal/update` allowlists exactly that host over https and refuses
everything else in a release build, so the DNS and the bucket configuration are
part of the trust story rather than an implementation detail.

### 5. Pin the ffmpeg sources

`runner/build/ffmpeg/pins.env` ships with `CHANGE_ME` digests, and
`runner/build/ffmpeg/build.sh` **refuses to run** until they are filled in. Pin them
once, from a machine with network access:

```sh
runner/build/ffmpeg/build.sh --target linux/amd64 --pin
```

It downloads the three pinned sources, prints their SHA-256 and exits. Paste the
three values into `pins.env`, cross-check the ffmpeg one against the detached GPG
signature upstream publishes beside the tarball, and commit. From then on every
CI run re-downloads and verifies, so a changed upstream artifact fails the build
instead of being built into a released binary.

### 6. Reviewers on the environment

The `release` environment holds the manifest signing key, the Windows signing
credentials and the R2 credentials. Give it required reviewers: an approval is
what stands between a mistyped version and every installed runner.

Create a second environment, **`release-dry-run`**, with no reviewers and no
secrets. The Windows packaging job runs under it when `publish` is off, so a
dry run never waits for an approval and never sees a credential. Only the jobs
that sign or publish (`package-windows` with publish on, and `publish`) use
`release`; the build jobs use no environment at all.

## Two kinds of output, and they are not interchangeable

This is the part that is easy to get wrong, because
[`runner/internal/update/README.md`](../internal/update/README.md) describes the manifest
and `apply.go` decides what is actually installable.

| | What a human downloads | What the signed manifest describes |
| --- | --- | --- |
| Windows x64 | `...windows-amd64.msi` | `windows/amd64/msi` **and** `windows/amd64/exe` |
| Linux x64 / arm64 | `...linux-amd64.tar.gz`, `.AppImage`, `.deb` | `linux/<arch>/appimage`, `linux/<arch>/deb`, `linux/<arch>/tarball` |

**The `tarball` entry is the RAW EXECUTABLE, not a tar.gz.** `applyReplace()`
copies the staged artifact byte for byte over the installed binary and then
re-verifies the copy against the manifest digest; an archive there would leave a
gzipped file where the executable used to be. The format's name and its comment
in `manifest.go` say "a tar.gz holding the binary", and the code that actually
applies it does not agree - so the pipeline publishes the raw executable, which
is the behaviour the running code implements. The plain archive a person
downloads is a separate file with the same base name and a `.tar.gz` suffix; it is
not a manifest artifact.

The same reasoning gives the other formats their shapes: `appimage` is the whole
AppImage (the runner detects `$APPIMAGE` and swaps the image), `deb` is a real
`.deb` that is verified and then *not* installed (apt owns those files), `msi` is
run through `msiexec /i /qn`, and `exe` is the signed daemon.

**One limit worth knowing.** A self-update replaces the runner binary and nothing
else: the bundled `ffmpeg` stays as it was. A release that changes ffmpeg
therefore needs the installer re-run by hand. The manifest has no field that
could express "also replace ffmpeg", and this pipeline does not pretend
otherwise.

## Running the pipeline

* **Tag** `runner-v1.4.2`: builds, signs and publishes to the **beta** channel with a
  100% rollout. A beta release is opt-in, so holding part of that fleet back
  would only delay the feedback it exists to collect.
* **`workflow_dispatch` with `publish` unchecked**: builds and verifies everything
  and uploads nothing. This is how the toolchain is exercised before E10a is
  provisioned.
* **`workflow_dispatch` with `publish` checked**: builds and publishes a NEW
  version to the channel you choose. `publish.sh` refuses to overwrite any
  existing `runner/<version>/` object unless the bytes are identical, so a released
  version can never be silently replaced.
* **`workflow_dispatch` with `publish` and `promote` checked, `channel=stable`**:
  the promotion. The build jobs are skipped. `publish` downloads
  `runner/<version>/*` from the bucket over S3, checks those bytes against the
  signed beta manifest (signature, version, digests, then a public `-download`
  check), and signs the stable manifest over exactly those bytes. It writes only the
  stable manifest and `latest/`; nothing under `runner/<version>/` is touched.

Unlike the deploy workflow, `publish` **fails** rather than skipping when the
environment is unprovisioned. This workflow does not run on every push; it runs
when somebody asks for a release, and a release that silently does nothing is
worse than one that says what is missing.

### One trap worth knowing about

A **main** package is stamped as `-X main.Version=1.4.2`, not by its import path.
Go accepts `-X github.com/vecchiotom/reelbolt/runner/cmd/reelbolt-runner.Version=1.4.2`,
records it in the binary's build settings, and silently ignores it: the binary
reports `dev`, and `dev` refuses every update (`refuse-unknown-version`). A
library package is the other way round - `runner/internal/update`'s release-key
variables are addressed by full path and that is the only form that works. Both
halves are verified against the real toolchain by
`runner/cmd/reelbolt-manifest/ldflags_build_test.go`, which compiles the runner
and runs it, and the workflow re-checks the stamp on every build as well.

### The invariants the workflow enforces

1. The manifest's digests are computed **in the publish job, from the files it
   uploads**, by `reelbolt-manifest assemble`, which hashes each file itself. There
   is no step where a digest is computed somewhere else and pasted in.
2. The manifest is signed by `reelbolt-manifest sign` with
   `-expect-public "$RUNNER_RELEASE_PUBLIC_KEY_CURRENT"`. A signing key that is
   not the key compiled into the build is refused, which is the failure that
   would otherwise be discovered by every user at once.
3. Nothing is re-serialised after signing. The manifest is read as bytes, signed
   as bytes, uploaded as bytes.
4. After the upload, `verify -download` fetches every artifact back **from the
   bucket** and checks its size and SHA-256 against the manifest. A digest that
   was only ever compared against a local file does not tell you what a stranger
   can download.
5. The release key goes to `sign` through the environment, never argv.

## Where it runs

Every job runs on Linux on the label in the repository variable
`RUNNER_RELEASE_LABEL`, falling back to `CI_RUNNER_LINUX`, then `ubuntu-24.04`
(`prepare` resolves it once). A runner label is **not a security boundary**: a workflow file in a pull request can
name any label. The isolation is the ephemeral per-job VM (nothing survives a job)
plus the absence of any shared cache. Registering the pool, or a subset of it, with
a dedicated label that `ci.yml` never references and setting `RUNNER_RELEASE_LABEL`
to it is hygiene that keeps ordinary CI off the release runners.
Nothing schedules the workflow and nothing retries it; if the machine is off the
jobs simply queue until a runner with the label appears. A runner needs **docker
and curl**: no sudo, no apt, no `az` CLI. There is no shared cache anywhere
(per-run tmpfs only; `go mod verify`, `npm ci --ignore-scripts` and
`-mod=readonly` on every build). The toolchains are in pinned containers:

* Linux: `ci/linux-container.sh` runs `ci/linux-inside.sh` in
  `ubuntu:24.04@sha256:...` (ffmpeg, daemon, GTK/webkit tray, nfpm, appimagetool,
  `package.sh`). Versions and sha256 pins are in `ci/pins.env`; appimagetool is a
  versioned release (1.9.1), not the moving `continuous` build.
* Windows: cross-built on Linux by `windows/run-in-container.sh` (`build-binaries.sh`,
  then `package.sh`; see section 2).

The linux-arm64 leg exists only when the repository variable `RUNNER_ARM64_RUNNER`
(a runner label) is set. Otherwise the release is linux-amd64 + windows-amd64 and
`publish.sh` warns for every format it did not find.

The Windows signing job exchanges its GitHub OIDC token for an Entra token with
`windows/azure-oidc-token.sh` (curl only; tested against a mock in
`windows/azure-oidc-token.test.sh`) when `RUNNER_WINDOWS_SIGNING=trusted-signing`;
set the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `TS_ENDPOINT`, `TS_ACCOUNT`,
`TS_PROFILE` on the `release` environment. `id-token: write` is granted only to
the signing and publish jobs. For `pfx`, set the two `WINDOWS_SIGNING_PFX_*` secrets.
A dry run (publish off) needs no secret and no approval.

`publish` also writes `SHA256SUMS` (`sha256sum -c` format) beside the installers
and listed in `latest.json` as `checksumsUrl`. When the job has an OIDC token it
signs each file and `SHA256SUMS` keylessly with cosign (`--bundle`, pinned
version and sha256); the publish does not depend on it unless the variable
`RUNNER_COSIGN_REQUIRED` is `true`.

## Windows MSI versions

Windows Installer needs a numeric `a.b.c(.d)` ProductVersion, so
`windows/msi-version.sh` maps the semver: `1.4.2` stays `1.4.2`; `1.4.2-beta.3`
(or `-rc.N`) becomes `1.4.2.3`; a prerelease without a number becomes `1.4.2`. The
full semver stays in the binaries, the manifest and the MSI description.
Windows Installer compares only the first three fields, so the prerelease number
does not order builds, and a promoted stable MSI is the same bytes as its beta.
`runner.wixl.wxs` sets `AllowSameVersionUpgrades`, so a self-update (`msiexec /i
/qn`) from beta.1 to beta.2, or to the stable of the same x.y.z, replaces the
installed product instead of installing beside it; installing the same bytes again
is a no-op reinstall. Consequence: a downgrade inside one x.y.z (stable to an older
beta of the same core) is not blocked by the MSI (the runner's own update path still
refuses it). `windows/msi-version.test.sh` checks the mapping.

## Operator checklist for the release path

Do these once, before the first real publish:

1. Environment `release`: required reviewers, and a deployment branch/tag policy
   limited to `master` and the tag pattern `runner-v*`.
2. A tag ruleset restricting who can create `runner-v*` tags.
3. Entra federated credential for Trusted Signing: subject exactly
   `repo:ReelBolt/reelbolt:environment:release`. That subject is also what stops
   a dry run (environment `release-dry-run`) from getting a signing token.
4. The R2 API token: Object Read & Write, scoped to the one bucket.
5. **Required to publish a signed Windows release:** set the repository variable
   `RUNNER_WINDOWS_SIGNER_SUBJECT` to the signer certificate's FULL subject DN,
   exactly as `osslsigncode verify -in <a signed exe>` prints it after "Signer's
   certificate ... Subject:" (for example `/C=US/ST=Washington/O=ReelBolt Inc/CN=ReelBolt Inc`;
   no substring, no prefix, no case folding), and/or `RUNNER_WINDOWS_SIGNER_THUMBPRINT`
   to the leaf certificate's SHA-256 (hex, case and colons ignored; for PFX signing).
   Trusted Signing certificates rotate, so pin the subject there. A publishing run with
   neither set fails before anything is built, and packaging fails if the signature
   names any other signer (a CA's subject never satisfies the pin). Format and how to
   read the values: `windows/signer-check.sh`; tests: `windows/signer-check.test.sh`
   (run with `windows/run-in-container.sh -- sh windows/signer-check.test.sh`).
6. Before the repository goes public: Settings > Actions > "Require approval for
   all outside collaborators" for workflow runs.

## Rehearsing the publish

`publish.sh` is environment-driven and takes any S3 endpoint, so it can be run
against a local MinIO/garage standing in for R2, with a throwaway key:

```sh
# throwaway key, never committed
reelbolt-manifest gen-key -out /tmp/rehearsal/release.key
# a bucket with anonymous read at http://127.0.0.1:9000/<bucket>
DIST=dist/artifacts VERSION=0.0.0-rehearsal CHANNEL=beta ROLLOUT=100 \
DOWNLOAD_BASE=http://127.0.0.1:9000/<bucket>/runner \
RUNNER_RELEASE_PUBLIC_KEY_CURRENT=<id:hex> RUNNER_MANIFEST_SIGNING_KEY=$(cat /tmp/rehearsal/release.key) \
R2_ENDPOINT=http://127.0.0.1:9000 R2_BUCKET=<bucket> AWS_ACCESS_KEY_ID=... AWS_SECRET_ACCESS_KEY=... \
SOURCES_DIR=dist/sources runner/build/publish.sh
```

The signer pin is rehearsed by `windows/signer-check.test.sh` with a throwaway CA
plus leaf (a self-signed PFX embeds no chain). 

`aws` is used from PATH, else the pinned `amazon/aws-cli` image. Build the Linux
tree first with `ci/linux-container.sh --target linux/amd64 --version V --dist dist`
(`LDFLAGS`/`TAGS`/`HAS_KEY` from `reelbolt-manifest ldflags`).

## Running it locally, with no account and no real key

```sh
runner/build/local-release.sh --work /tmp/reelbolt-release
```

This generates a throwaway Ed25519 key, builds two tagged runners stamped with
its public half, packages one, assembles and signs a manifest, serves it over
loopback, and then drives the **real** `reelbolt-runner update` code path:
`--check` sees the release, `--apply` verifies it against the compiled-in key
and installs it keeping `.prev`, a tampered artifact is refused, a manifest
signed by another key is refused, and a build with no trust root refuses before
it makes a request.

That closes the gap E9 recorded as "NOT verified: that a manifest produced by CI
verifies against the public key compiled into a shipped build" - locally, with a
development key. It does **not** close:

* that the real E10a key exists, or that a shipped build carries it;
* that the Windows artifacts are Authenticode-signed, or that an MSI installs
  through `msiexec` - no Windows host and no certificate;
* that R2 is reachable, or that `downloads.reelbolt.ai` resolves to a bucket;
* that the workflow as written runs end to end: **GitHub Actions has never run
  this**, and it cannot be run from here. It is correct by inspection, its YAML
  parses, `actionlint`-style conventions are followed, and the commands it
  invokes are the commands the local run above exercises.

## Building the Linux desktop app locally

`desktop-local.sh` builds the tray and the window for Ubuntu **without** root and
without a desktop toolchain on the machine doing the building: it compiles inside
a pinned `ubuntu:24.04` container, which is the oldest glibc we support.

```sh
runner/build/desktop-local.sh                       # binaries + .deb + tarball in runner/build/out
runner/build/desktop-local.sh --version v0.2.0 --out /tmp/rb --no-deb
```

It needs `node` (for the window's vite bundle), and `podman` or `docker`. It
produces the headless daemon, the tray/window app, a `.deb` installable with
`sudo apt install ./reelbolt-runner_<version>_amd64.deb`, and the same files as a
tarball. Everything it writes goes to `runner/build/out/`, which is git-ignored.

**Why Ubuntu and not Alpine.** `runner/cmd/reelbolt-runner-desktop/README.md`
carries a fast Alpine recipe, and that one is a **compile-and-vet check**: an
Alpine build links musl and Alpine's webview, so its output will not run on
Ubuntu at all. Any artifact a user installs has to be built on glibc. Wails v3
beta.28 needs `libgtk-4-dev` and `libwebkitgtk-6.0-dev` to compile, and the
resulting binary links `libgtk-4-1` and `libwebkitgtk-6.0-4` at runtime (verified
with `ldd`; it links no appindicator).

**What it is not.** This is a local build: unsigned, and carrying no release
public key, so the runner refuses every self-update by design. Signed,
self-updating artifacts come from `.github/workflows/runner-release.yml`. It also
does not bundle ffmpeg: the package *recommends* the distribution's ffmpeg, and
folder sync needs none.

## What is in here

| Path | What it is |
| --- | --- |
| `package.sh` | Packages one built tree into the artifacts a release publishes. |
| `publish.sh` | Assemble, sign, verify, SHA256SUMS, optional cosign, upload and verify-from-bucket; env-driven (CI and rehearsal). |
| `ci/linux-container.sh`, `ci/linux-inside.sh`, `ci/pins.env` | The Linux release build in a pinned Ubuntu container, and the pinned tool versions. |
| `desktop-local.sh` | Builds the Linux desktop app (daemon + tray + .deb + tarball) locally, in a container. |
| `local-release.sh` | The whole pipeline, locally, with no credentials. |
| `ffmpeg/build.sh` | Builds the pinned static GPL ffmpeg and ffprobe, and refuses an unpinned source. |
| `ffmpeg/pins.env` | The pinned versions and digests. **CHANGE_ME until an operator pins them.** |
| `ffmpeg/THIRD_PARTY_NOTICES.md` | The GPL notice and the written source offer. |
| `linux/nfpm.yaml` | The .deb recipe. |
| `linux/nfpm-local.yaml` | The .deb recipe `desktop-local.sh` uses (no bundled ffmpeg). |
| `linux/AppRun`, `linux/reelbolt-runner-wrapper`, `linux/reelbolt-runner-desktop-wrapper` | The wrappers that put the bundled ffmpeg on PATH and the tray on the applications menu. |
| `linux/reelbolt-runner.desktop` | The desktop entry. |
| `windows/runner.wxs` | The WiX 6 source for the per-user MSI (Windows host; WiX 6 cannot bind an MSI on Linux). |
| `windows/runner.wixl.wxs` | The same package for `wixl`, which is what the Linux pipeline builds. Change both together. |
| `windows/package.ps1` | The Windows-host packager (signtool). Superseded by `package.sh` for releases. |
| `windows/Dockerfile.toolchain`, `run-in-container.sh` | The pinned Linux toolchain (mingw-w64, Go, Node, wixl, jsign, osslsigncode) and its wrapper. |
| `windows/build-binaries.sh` | Cross-builds daemon, tray, ffmpeg, ffprobe on Linux. |
| `windows/package.sh` | Signs the exes, builds the MSI, signs it. Signing happens **before** the MSI is built so it embeds signed files. |


The manifest tool lives with the code it depends on, not here:
`runner/cmd/reelbolt-manifest` (`gen-key`, `ldflags`, `assemble`, `sign`, `verify`,
`latest`). It is the only place a digest, a signature or the `-ldflags` line is
produced, so the pipeline and the runner agree on the format by construction
rather than by review.

## The download page

`site/lib/runner-downloads.ts` is deliberately empty and fails closed while it is.
E10b is the WP that fills it, and that is a **manual step after the first real
publish** - not something this WP can do, because a URL written there before a
file exists is a fabricated download. The pipeline gives you everything you
need: `latest.json`, published at
`https://downloads.reelbolt.ai/runner/<channel>/latest.json`, carries one entry per
artifact with `platform` (`windows-x64` / `linux-x64` / `linux-arm64`), `kind`
(`installer` or `update-payload`), `label`, the stable `url` and the `sha256`.
Offer only the `kind: "installer"` entries.

`latest.json` is generated from a *verified* manifest, so it can never describe
artifacts the release key did not sign - but it is not itself signed, and it is
not a trust anchor: a runner reads the signed manifest, and the checksum on a
download page is a convenience for someone typing it into `shasum`.

The stable URLs are **copies** of the release's artifacts under
`<channel>/latest/`, not redirects. Object storage has no redirect that works on
every host and path, and a URL that resolves to the file cannot be wrong. The
signed manifest keeps pointing at the immutable per-version paths, so the copy
is a convenience for a human and never an input to an update.

## D26: what is not here

Windows x64 and Linux x64/arm64. There is no macOS artifact, no `.pkg`, no `.dmg`,
no `Info.plist`, no entitlements file, no `lipo` and no notarisation step, because
D26 deferred the whole macOS runner. Three separate things enforce it rather than
one comment: `reelbolt-manifest assemble` refuses a `darwin` artifact,
`runner/internal/update` refuses one at parse time, and `Decide` answers
`refuse-unsupported-platform`.
