package telemetry

import (
	"os"
	"os/user"
	"regexp"
	"runtime"
	"strings"
)

// This file is the runner's ONE answer to "what is allowed to leave this
// machine", and it comes in two halves:
//
//   - The ALLOWLIST half (AllowToken, AllowCode, AllowJobType, AllowSymbol,
//     AllowRuntime) is what the wire uses. Each function either returns a value
//     that matches a bounded pattern drawn from the protocol's own alphabet, or
//     returns Unknown. There is no path that passes a raw string through, and a
//     panic MESSAGE never reaches one of these functions at all: the event
//     carries the panic's Go TYPE, which is a symbol from this program's own
//     namespace and not text a user or a project could have written.
//
//   - The REDACTION half (Scrubber) is what the local diagnostics export uses,
//     on its way to a zip the user reads before they send it. It is a denylist,
//     it is best-effort, and it is deliberately NOT used for a wire payload - a
//     denylist whose failure nobody can see is exactly the wrong tool for
//     something that leaves the machine on its own.

// Unknown replaces any value that does not match its allowlist.
const Unknown = "unknown"

// The allowlist patterns. Each is anchored, bounded, and drawn from an alphabet
// the corresponding field really uses.
var (
	// versionToken covers a version string ("1.4.2-beta.1"), an OS name
	// ("linux", "windows"), an architecture ("amd64") and a Go version
	// ("go1.26.0").
	versionToken = regexp.MustCompile("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$")
	// codeToken is the protocol's failure-code alphabet: "stage_failed",
	// "publish_failed", "render_failed", "timeout", "job_setup_failed",
	// "unsupported_op", "container_unavailable".
	codeToken = regexp.MustCompile("^[a-z][a-z0-9_]{0,63}$")
	// jobTypeToken is the protocol's job-type alphabet:
	// "remotion.workspace-session", "video.compile", "video.analyze.extract".
	jobTypeToken = regexp.MustCompile("^[a-z][a-z0-9._-]{0,63}$")
	// symbolToken covers a Go type or a stack function name, which is a symbol
	// from a namespace: "runtime.boundsError",
	// "github.com/vecchiotom/reelbolt/runner/internal/ui.(*Shell).Start",
	// "created by main.main in goroutine 1".
	symbolToken = regexp.MustCompile(`^[A-Za-z0-9_./*\[\](){}<>$,+ -]{1,160}$`)
	// baseNameToken is a bare file name: "telemetry.go", "executors_test.go".
	baseNameToken = regexp.MustCompile("^[A-Za-z0-9_.-]{1,64}$")
)

// allowedRuntimes is the closed set of container-runtime kinds E3 detects, and
// allowedRuntimeModes its modes. They are enumerated rather than
// pattern-matched because these two fields must never carry anything a probe
// could be tricked into returning.
var (
	allowedRuntimes     = map[string]bool{"docker": true, "podman": true, "": true}
	allowedRuntimeModes = map[string]bool{"rootful": true, "rootless": true, "vm": true, "": true}
)

// AllowToken bounds a low-cardinality identifier (a version, an OS, an arch).
func AllowToken(v string) string {
	if versionToken.MatchString(v) {
		return v
	}
	return Unknown
}

// AllowCode bounds a protocol failure code, or a stage name.
func AllowCode(v string) string {
	if codeToken.MatchString(v) {
		return v
	}
	return Unknown
}

// AllowJobType bounds a protocol job type.
func AllowJobType(v string) string {
	if jobTypeToken.MatchString(v) {
		return v
	}
	return Unknown
}

// AllowSymbol bounds a Go symbol: a panic's type or a stack frame's function.
func AllowSymbol(v string) string {
	if symbolToken.MatchString(v) {
		return v
	}
	return Unknown
}

// AllowFile bounds a file name. A PATH is refused rather than truncated: a
// truncated path is still a path, and the directories are the part that names
// the user.
func AllowFile(v string) string {
	if !baseNameToken.MatchString(v) {
		return ""
	}
	return v
}

// AllowRuntime bounds a container runtime kind and mode, as a pair.
func AllowRuntime(kind, mode string) (string, string) {
	if !allowedRuntimes[kind] {
		kind = ""
	}
	if !allowedRuntimeModes[mode] {
		mode = ""
	}
	if kind == "" {
		return "", ""
	}
	if mode == "" {
		return kind, ""
	}
	return kind, mode
}

// ---- the redaction half ----

// The patterns Scrubber applies. Compiled once: the diagnostics export is a
// user-facing action rather than a hot path, but a per-call compilation would
// still be a silly place to spend a millisecond.
var (
	// A URL's query string is where a presigned GET's signature, credential and
	// object key live. The whole query goes, not only the signature: the key is
	// the interesting part to a reader and the identifying part to a stranger.
	reQuery    = regexp.MustCompile("(?i)(https?://[^\\s\"'<>)*\\]]+)\\?[^\\s\"'<>)\\]]*")
	reFragment = regexp.MustCompile("(?i)(https?://[^\\s\"'<>)*\\]]+)#[^\\s\"'<>)\\]]*")
	// Credentials embedded in a URL (user:password@host).
	reURLUser = regexp.MustCompile("(?i)(https?://)[^/@\\s\"'<>]+@")
	reEmail   = regexp.MustCompile(`[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}`)
	reUUID    = regexp.MustCompile(`(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b`)
	reIPv4    = regexp.MustCompile(`\b(?:\d{1,3}\.){3}\d{1,3}\b`)
	// Long opaque runs: a token, a signature, a content hash. A 32-character
	// floor keeps ordinary words and identifiers out of it.
	reOpaque = regexp.MustCompile(`\b[A-Za-z0-9_\-+]{32,}={0,2}\b`)
	// Home directories of OTHER users on the same machine, and of this one when
	// the literal replacement below missed a spelling.
	reUnixHome = regexp.MustCompile("(?i)/(?:home|Users)/[^/\\s\"':]+")
	reWinHome  = regexp.MustCompile("(?i)[A-Z]:\\\\Users\\\\[^\\\\\\s\"']+")
)

// Scrubber redacts the personal data a free-text line can carry.
//
// It is used for the diagnostics zip and nowhere else. It is a denylist and is
// honest about it: it removes what it knows about (this machine's home
// directory, this user's name, this computer's name, other users' home
// directories, e-mail addresses, URL query strings and fragments, credentials
// in URLs, UUIDs, IP addresses and long opaque tokens) and it cannot promise
// that a novel field named in a panic message survives it. That is why the wire
// does not carry free text at all.
type Scrubber struct {
	home  string
	names []string
}

// NewScrubber reads this machine's own identifiers. It never fails: a machine
// whose home directory cannot be determined simply gets a scrubber with fewer
// rules, because refusing to export diagnostics would be a worse answer.
func NewScrubber() *Scrubber {
	s := &Scrubber{}
	if home, err := os.UserHomeDir(); err == nil {
		s.home = home
	}
	for _, v := range []string{os.Getenv("USER"), os.Getenv("USERNAME"), os.Getenv("LOGNAME")} {
		s.addName(v)
	}
	if u, err := user.Current(); err == nil {
		s.addName(u.Username)
		s.addName(strings.TrimPrefix(u.Username, ".\\"))
		// A Windows account is "DOMAIN\user"; both halves name the person.
		if i := strings.LastIndexAny(u.Username, "\\"); i >= 0 {
			s.addName(u.Username[i+1:])
		}
	}
	if host, err := os.Hostname(); err == nil {
		s.addName(host)
	}
	return s
}

// WithNames adds extra literals to redact (a device name, a workspace id).
func (s *Scrubber) WithNames(names ...string) *Scrubber {
	if s == nil {
		return s
	}
	for _, n := range names {
		s.addName(n)
	}
	return s
}

func (s *Scrubber) addName(v string) {
	v = strings.TrimSpace(v)
	// A one- or two-character name is a substring of ordinary words, and
	// replacing it everywhere would mangle the log rather than protect anyone.
	if len(v) < 3 {
		return
	}
	for _, existing := range s.names {
		if existing == v {
			return
		}
	}
	s.names = append(s.names, v)
}

// Text redacts one line, or a whole document.
func (s *Scrubber) Text(in string) string {
	if s == nil {
		return in
	}
	out := in
	if s.home != "" {
		out = strings.ReplaceAll(out, s.home, "~")
		// The same directory spelled the other way round, which a Windows log
		// carries when a tool reports a forward-slashed path.
		if runtime.GOOS == "windows" {
			out = strings.ReplaceAll(out, strings.ReplaceAll(s.home, "\\", "/"), "~")
		}
	}
	out = reQuery.ReplaceAllString(out, "$1?<redacted>")
	out = reFragment.ReplaceAllString(out, "$1#<redacted>")
	out = reURLUser.ReplaceAllString(out, "$1<redacted>@")
	out = reUnixHome.ReplaceAllString(out, "~")
	out = reWinHome.ReplaceAllString(out, "~")
	out = reEmail.ReplaceAllString(out, "<email>")
	out = reUUID.ReplaceAllString(out, "<id>")
	out = reIPv4.ReplaceAllString(out, "<ip>")
	out = reOpaque.ReplaceAllString(out, "<redacted>")
	for _, name := range s.names {
		out = replaceWord(out, name)
	}
	return out
}

// Path redacts one path. It is Text with nothing added, kept as a named method
// because a caller that means "this is a path" should say so.
func (s *Scrubber) Path(in string) string { return s.Text(in) }

// replaceWord replaces a literal case-insensitively when it stands alone, so a
// user named "sam" does not turn "same" into "<redacted>me".
func replaceWord(haystack, needle string) string {
	if needle == "" {
		return haystack
	}
	lower := strings.ToLower(haystack)
	lowerNeedle := strings.ToLower(needle)
	var b strings.Builder
	i := 0
	for {
		j := strings.Index(lower[i:], lowerNeedle)
		if j < 0 {
			b.WriteString(haystack[i:])
			return b.String()
		}
		j += i
		before := byte(' ')
		if j > 0 {
			before = haystack[j-1]
		}
		after := byte(' ')
		if j+len(needle) < len(haystack) {
			after = haystack[j+len(needle)]
		}
		if isWordByte(before) || isWordByte(after) {
			b.WriteString(haystack[i : j+len(needle)])
			i = j + len(needle)
			continue
		}
		b.WriteString(haystack[i:j])
		b.WriteString("<redacted>")
		i = j + len(needle)
	}
}

// isWordByte reports whether b continues a word, which is what decides whether
// a literal stands on its own in the haystack.
func isWordByte(b byte) bool {
	return b == '_' || b == '-' || b == '.' || b == '\\' || b == '/' ||
		(b >= '0' && b <= '9') || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || b >= 0x80
}
