# Signed auto-update (WP E9)

This package is the contract between the release pipeline and a runner that
updates itself. The pipeline (E10b) publishes two files per channel; the runner
fetches them, verifies them, and only then touches the installed binary.

An auto-update is a remote-code-execution path on a user's machine. Everything
below is written as the mechanism that bound, and every refusal in it is a
tested path (`go test ./internal/update -race`).

## What is published

```
https://downloads.reelbolt.ai/runner/<channel>/manifest.json
https://downloads.reelbolt.ai/runner/<channel>/manifest.json.sig
```

`<channel>` is `stable` or `beta`. The channel in the URL and the `channel`
field of the document must agree; a mismatch is refused, so a host or a cache
cannot serve a beta manifest to a stable runner.

## The manifest

```json
{
  "version": "1.4.2",
  "minSupported": "1.2.0",
  "channel": "stable",
  "rolloutPercent": 10,
  "publishedAt": "2026-10-05T09:00:00Z",
  "notes": "Fixes the arm64 headless-shell path.",
  "artifacts": [
    {
      "os": "linux",
      "arch": "amd64",
      "format": "appimage",
      "url": "https://downloads.reelbolt.ai/runner/1.4.2/reelbolt-runner-1.4.2-linux-amd64.AppImage",
      "sha256": "<64 lowercase hex characters>",
      "size": 24117248
    }
  ]
}
```

| Field | Required | Meaning |
| --- | --- | --- |
| `version` | yes | The released build, dotted (`1.4.2`, `v1.4.2`, `1.4.2-beta.1`). A pre-release sorts before its release. |
| `minSupported` | no | The oldest version the platform still supports. A runner below it stops taking jobs and tells the user to update. This is the offline twin of the gateway's `welcome.minAgentVersion`, which the agent library enforces while connected. |
| `channel` | no | Must equal the channel in the URL when present. |
| `rolloutPercent` | yes | 0-100. The device's bucket is `sha256("reelbolt-runner-rollout" \| 0x00 \| deviceId)[0:8]` big-endian, modulo 100. A device is offered the release only when `bucket < rolloutPercent`. |
| `publishedAt` | no | RFC 3339, for the log and the UI. |
| `notes` | no | Free text shown to a user who asks what changed. |
| `artifacts` | yes | 1-32 builds. Each `(os, arch, format)` may appear at most once. |

Artifact fields:

| Field | Meaning |
| --- | --- |
| `os` | `windows` or `linux`. `darwin` is refused: D26 ships no macOS runner. |
| `arch` | `amd64` or `arm64`. |
| `format` | `exe`/`msi` (Windows), `appimage`/`deb`/`tarball` (Linux). |
| `url` | Absolute `https://` URL. An `http://` URL is accepted only for loopback or a development host that a **non-release** build was told about. |
| `sha256` | Lowercase or uppercase hex, 64 characters. |
| `size` | Exact byte length, 1 .. 512 MiB. |

An unknown field anywhere in the document is a refusal: a manifest is a closed
format, and a field this build does not understand cannot be one it ignores.

## The signature

`manifest.json.sig` holds a detached Ed25519 signature over

```
"reelbolt-runner-manifest-v1" || 0x00 || <the exact bytes of manifest.json>
```

encoded as base64 (standard or raw, padded or not, surrounding whitespace
ignored). A raw 64-byte file is also accepted. The domain prefix is what stops a
signature the release key produced for any other purpose from being replayed as a
manifest signature.

The signature covers **the bytes**, not the parsed document: `manifest.json` must
be transferred byte-for-byte, because re-serialising it (or appending a newline)
invalidates the signature. That is the property that makes the signature cover
everything, including fields this runner version does not know about.

Two public keys are compiled into the binary, `current` and `next`, so a key
rotation is staged by shipping release N signed by `current` and carrying `next`,
and release N+1 signed by `next`. A runner on either build accepts either
signature and is never stranded. They are stamped at build time:

```sh
go build -tags release -ldflags "\
  -X github.com/vecchiotom/reelbolt/runner/internal/update.ReleaseKeyCurrentHex=<id>:<hex> \
  -X github.com/vecchiotom/reelbolt/runner/internal/update.ReleaseKeyNextHex=<id>:<hex>"
```

**This repository holds no release key.** Both slots are empty in every build
made from this source tree, and an empty trust root is `ErrNoTrustedKey`: the
runner refuses to update and says so in `status` and `doctor`. There is
deliberately no fallback key, no "trust the download host instead" path, and no
default that verifies nothing. Provisioning the key is E10a/E10b.

A non-release build additionally trusts a key named by
`REELBOLT_UPDATE_TRUST_KEY` (`<id>:<hex>` or bare hex/base64), so the update path
can be developed and tested before a real key exists. A `-tags release` build
compiles that escape hatch out (`trust_release.go`).

## The order of verification

A runner refuses at the first step that fails, and never falls back to a later
one:

1. **Trust root.** No compiled-in key (and no development key in a non-release
   build) is a refusal, before any network request.
2. **Host allowlist.** `downloads.reelbolt.ai` and its subdomains over `https`.
   Loopback and explicitly named development hosts only in a non-release build.
3. **Signature.** The detached signature must verify against one of the trusted
   keys over the exact manifest bytes.
4. **Manifest validity.** Version, sizes, digests, formats, rollout, and the
   closed-field rule above.
5. **Channel agreement** between the URL and the document.
6. **Artifact choice.** A build for this `os`/`arch` and a format this
   installation can apply. Downgrades are refused here; so is a device outside
   the rollout.
7. **Job gate.** A job in flight defers the update; nothing is downloaded while
   a render is running.
8. **Download with hashing.** Written to a `.part` file, hashed as it is read,
   and renamed into place only when the byte count **and** the digest match the
   manifest exactly. Any failure removes the `.part` file: a partial download is
   never left where anything could pick it up.
9. **Re-verification at the moment of the swap.** The staged file is hashed
   again immediately before the install, so a file that changed after step 8 is
   not installed.
10. **Platform signature.** Windows: `WinVerifyTrust` (Authenticode) on `exe`
    and `msi`. Linux: none — a bare binary has no OS signature, the AppImage's
    optional GPG signature is checked by a similarly configured AppImage
    runtime, and a `.deb` is never installed by the runner, so apt/dpkg (which
    does verify the repository signature) is the thing that applies it.
11. **Atomic swap.** Linux: rename the current binary aside, rename the new one
    in, both in the same directory, so the path holds either the old file or the
    new one and never a partial one. Windows: a helper waits for the process to
    exit and then swaps, restoring the backup if the move fails.
12. **Rollback.** The previous binary is kept as `<target>.prev` and
    `reelbolt-runner update --rollback` restores it. A failed rename restores it
    automatically.

## What happens when things go wrong

| Condition | Behaviour |
| --- | --- |
| Update host unreachable (DNS, connection refused, 4xx/5xx, timeout) | The check fails with `ErrHostUnreachable`, the daemon logs it and **keeps serving jobs**. The next check retries. Nothing is downloaded, nothing changes on disk. |
| Signature does not verify (tampered manifest, wrong key, malformed signature file) | The update is refused with `ErrSignature`, nothing is downloaded or installed, and the check is retried later. The runner keeps running the current build. |
| Artifact bytes do not match the signed digest (tampered download, truncation, a longer response) | Refused with `ErrDigest`; the `.part` file is deleted. Nothing is installed. |
| Manifest offers an older version | Refused with `refuse-downgrade`; no download. |
| Device outside the rollout | `hold-back`; no download. |
| Device below `minSupported` | `refuse-below-floor`: the runner reports that it must not take jobs and tells the user to update. |
| This build's version does not parse (`dev`, unstamped) | `refuse-unknown-version`: a developer build is never replaced by a release. |
| This platform has no artifact, or no self-update path (macOS, D26) | `refuse-no-artifact` / `refuse-unsupported-platform`. |
| A job is running | The update is deferred (`ErrJobRunning`); nothing is downloaded. |
| The install is package-managed (a `.deb`) | The artifact is verified and the user is told how to install it; the file is not replaced behind the package manager's back. |

None of these stops the runner: the daemon treats every one of them as a log
line and a retry. The only thing that stops the daemon is a **successful
install**, because an update that is installed but never run is not an update —
on Linux the new file is in place while this process still runs the old image,
and on Windows the swap only happens once the process has exited.

## Publishing a release (E10b)

1. Build the artifacts for `windows/amd64`, `linux/amd64` and `linux/arm64`,
   sign the Windows binaries, and hash them.
2. Write the manifest with the real sizes and digests; **do not** re-serialise it
   after signing.
3. `base64 < manifest.json.sig > manifest.json.sig.b64` using
   `update.SignManifest` (`internal/update` exports it, so the pipeline and the
   tests share one definition of what is signed).
4. Upload both files to `<channel>/manifest.json` and `<channel>/manifest.json.sig`
   under `downloads.reelbolt.ai/runner/`.
5. Verify with `reelbolt-runner update --check --channel <channel>` on a machine
   running a build stamped with the matching public key.

## What this package does not do

* It does not hold, fetch or derive the release private key. E10a provisions it,
  E10b uses it, and nothing in the shipped runner can sign anything.
* It does not install a `.deb` or an `.msi` without the platform's own
  verifier — the `.deb` is never installed at all.
* It does not support macOS: D26 defers every macOS step, so
  `apply_other.go` fails closed on any platform that is neither Windows nor
  Linux rather than pretending to update.

## Testing

```sh
cd runner && go test ./internal/update -race -v
```

The suite runs entirely offline against `httptest` local file servers: a release
key is generated in the test, the manifest is signed with it, and the artifact is
served by a local HTTP server. `update_integration_test.go` is the end-to-end
path (v0.0.1 to v0.0.2 through fetch, verify, download, verify, install), and it
covers the tampered artifact, the foreign signature, the downgrade, the held-back
rollout, the job in flight and the unreachable host.

Verified by tests: every refusal above.

NOT verified here, and requiring a real release pipeline (E10b) and a real key:
that a manifest produced by CI verifies against the public key compiled into a
shipped build; that the published artifacts are the ones CI built; that
Authenticode verification passes on a signed Windows binary on Windows; and that
an MSI installs through `msiexec`.
