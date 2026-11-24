// Package update is the runner's signed auto-update path.
//
// An auto-update is a remote-code-execution path on a user's machine, so this
// package is written as one. Four rules hold everywhere in it:
//
//  1. Nothing is applied that was not verified first. The manifest's detached
//     Ed25519 signature is checked against the keys compiled into this build,
//     then the artifact's size and SHA-256 are checked against the manifest,
//     then the operating system's own signature is checked. Only then is the
//     file allowed near the installed binary.
//  2. Verification fails closed. A missing trust root, an unparseable version,
//     an unknown platform, a signature that does not verify, a truncated
//     download, an artifact whose hash differs by one byte: every one of them
//     refuses the update. There is no "assume good" branch, and no environment
//     variable that relaxes one in a release build.
//  3. A refusal is not a failure of the runner. An update host that is
//     unreachable leaves the runner exactly as it was, keeps serving jobs, and
//     is retried later; it never blocks a job and never stops the daemon.
//  4. A verified update is never applied while a job is running, and a failed
//     apply rolls back to the previous binary.
//
// What is deliberately NOT here, because it does not exist yet: the release
// private key. This repository holds no signing key, and the release pipeline
// (E10b) is what signs a manifest. A build with no key compiled in refuses
// every update — see pubkeys.go.
//
// The manifest format is specified in README.md beside this file, which is the
// contract the release pipeline has to meet.
package update
