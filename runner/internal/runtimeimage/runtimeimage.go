// Package runtimeimage resolves the container image a runner executes jobs in.
//
// The reference matters twice: it is what the driver starts containers from, and
// its digest is what the runner advertises as capabilities.runtimeImageDigest.
// The gateway refuses a job whose requires.runtimeImageDigest differs, so a
// runner that runs a different image than it advertised receives no Remotion
// work at all — which is the correct failure, and the reason this package
// resolves the reference in exactly one place.
//
// The pin is vendored here from sandbox/runtime-image.lock, copied byte for byte
// and kept honest by TestLockMatchesSandbox (which fails when the two differ).
// A go:embed directive cannot reach outside its own module (no ".."), and duplicating a
// pinned digest is the failure mode this repository handles with a drift test
// everywhere else it vendors a canonical file (site/lib/pricing, brand/scripts).
package runtimeimage

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"strings"
)

// DefaultRepository is the registry path of the released runtime image. The
// lock file carries only {version, digest}, so the repository is compiled in.
const DefaultRepository = "ghcr.io/reelbolt/reelbolt-sandbox-runtime"

// DefaultRef is used when nothing is pinned and nothing is configured. It
// resolves to a locally built development image, never to a released one: a
// release build is pinned by the lock file and refuses to fall through to this.
const DefaultRef = "reelbolt-sandbox-runtime:local"

//go:embed runtime-image.lock
var rawLock []byte

// Lock is sandbox/runtime-image.lock.
type Lock struct {
	Version string `json:"version"`
	Digest  string `json:"digest"`
}

// LockBytes is the vendored lock file, for the drift test.
func LockBytes() []byte { return rawLock }

// Pinned reads the vendored lock.
func Pinned() (Lock, error) {
	var l Lock
	if err := json.Unmarshal(rawLock, &l); err != nil {
		return Lock{}, fmt.Errorf("runtimeimage: parse the vendored lock: %w", err)
	}
	return l, nil
}

// Source explains where a reference came from, for `doctor` and the log.
type Source struct {
	Ref    string
	Origin string
}

// Resolve picks the image reference.
//
// Precedence: an explicit reference (flag or runner.json) wins, then the pinned
// release digest, then the REELBOLT_RUNTIME_IMAGE environment variable, then the
// development default. A pinned digest is deliberately not overridable by the
// environment: the pin is what the gateway matched the runner on.
func Resolve(explicit, env string) Source {
	l, err := Pinned()
	if err != nil {
		l = Lock{}
	}
	return resolve(explicit, env, l)
}

// resolve is Resolve without the embedded lock, so the precedence is a table
// test rather than a test of whatever the release pipeline last pinned.
func resolve(explicit, env string, l Lock) Source {
	if e := strings.TrimSpace(explicit); e != "" {
		return Source{Ref: e, Origin: "configured"}
	}
	if strings.HasPrefix(l.Digest, "sha256:") {
		return Source{
			Ref:    DefaultRepository + "@" + l.Digest,
			Origin: "pinned " + l.VersionLabel(),
		}
	}
	if e := strings.TrimSpace(env); e != "" {
		return Source{Ref: e, Origin: "REELBOLT_RUNTIME_IMAGE"}
	}
	return Source{Ref: DefaultRef, Origin: "development default"}
}

// VersionLabel names the pin in a log line.
func (l Lock) VersionLabel() string {
	if l.Version == "" {
		return "runtime-image.lock"
	}
	return "runtime-image.lock " + l.Version
}

// Digest extracts the sha256 a reference pins, or "" when it pins none. Only the
// digest of an image ID is advertised: a tag is mutable, and a runner that
// advertised a tag would be trusted for an image it may not be running.
func Digest(ref string) string {
	_, digest, ok := strings.Cut(ref, "@")
	if !ok || !strings.HasPrefix(digest, "sha256:") {
		return ""
	}
	return digest
}
