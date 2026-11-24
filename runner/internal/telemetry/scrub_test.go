package telemetry

import (
	"strings"
	"testing"
)

// The acceptance criterion for E11 names three things by hand: a home path, an
// e-mail address and a presigned URL's query string. They are the first three
// cases below, and each one asserts on the WHOLE redacted document rather than
// on a substring that happens to be convenient.
func TestScrubberRemovesWhatThePlanNames(t *testing.T) {
	home := t.TempDir()
	s := NewScrubber()
	// The scrubber reads this machine's real home directory, so the test drives
	// it with one it chose: a test that depended on the developer's own home
	// directory would pass or fail by accident.
	s.home = home
	s.names = []string{"alice", "studio-laptop"}

	const presigned = "https://storage.reelbolt.ai/projects/0d2f8a11/outputFiles/exec/render.mp4" +
		"?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=AKIA" + "IOSFODNN7EXAMPLE%2F20261008" +
		"&X-Amz-Signature=9f8c1d2e3b4a5c6d7e8f9012345678abcdef0123456789abcdef0123456789ab"

	cases := []struct {
		name string
		in   string
		// gone must not survive; kept must.
		gone []string
		kept []string
	}{
		{
			name: "a home path",
			in:   "scratch root " + home + "/jobs/job-8f21/input.mp4",
			gone: []string{home},
			kept: []string{"~/jobs/", "input.mp4"},
		},
		{
			name: "another user's home path",
			in:   "could not read /home/bob/renders/x.mp4",
			gone: []string{"/home/bob"},
			kept: []string{"~/renders/x.mp4"},
		},
		{
			name: "an e-mail address",
			in:   "the invitation went to alice@example.com instead",
			gone: []string{"alice@example.com"},
			kept: []string{"<email>", "instead"},
		},
		{
			name: "a presigned URL's query string",
			in:   "staging from " + presigned,
			gone: []string{"X-Amz-Signature", "AKIA" + "IOSFODNN7EXAMPLE", "9f8c1d2e3b4a5c6d"},
			kept: []string{"https://storage.reelbolt.ai/projects/", "?<redacted>"},
		},
		{
			name: "a device id",
			in:   "device 6f1a2c34-9b7e-4c11-8a2d-6c1f0b9d4e77 refused the lease",
			gone: []string{"6f1a2c34-9b7e-4c11-8a2d-6c1f0b9d4e77"},
			kept: []string{"<id>", "refused the lease"},
		},
		{
			name: "an address on the network",
			in:   "the gateway at 10.4.2.19:8443 closed the socket",
			gone: []string{"10.4.2.19"},
			kept: []string{"<ip>", "closed the socket"},
		},
		{
			name: "this user's name",
			in:   "alice started the runner on studio-laptop",
			gone: []string{"alice", "studio-laptop"},
			kept: []string{"<redacted>"},
		},
		{
			name: "a credential inside the URL itself",
			in:   "dialing https://alice:hunter2@runner.reelbolt.ai/v1/connect",
			gone: []string{"hunter2"},
			kept: []string{"runner.reelbolt.ai/v1/connect"},
		},
		{
			name: "a whole log line",
			in:   "2026/10/08 09:14:12 job job-8f21 ended (close) after staging " + presigned,
			gone: []string{"X-Amz-Credential"},
			kept: []string{"job job-8f21 ended (close)"},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := s.Text(tc.in)
			for _, g := range tc.gone {
				if strings.Contains(got, g) {
					t.Errorf("%q survived the scrubber:\n in: %s\nout: %s", g, tc.in, got)
				}
			}
			for _, k := range tc.kept {
				if !strings.Contains(got, k) {
					t.Errorf("%q was lost, which makes the log useless:\n in: %s\nout: %s", k, tc.in, got)
				}
			}
		})
	}
}

// A scrubber with nothing to work from must still return the document rather
// than refuse: an export that fails is worse than one that redacts less.
func TestScrubberWithoutAHomeDirectoryStillWorks(t *testing.T) {
	s := &Scrubber{}
	got := s.Text("nothing to redact here")
	if got != "nothing to redact here" {
		t.Errorf("Text = %q", got)
	}
}

// The whole-word rule exists so a short user name does not eat ordinary prose.
func TestScrubberDoesNotEatWordsThatMerelyContainAName(t *testing.T) {
	s := (&Scrubber{}).WithNames("sam")
	got := s.Text("the same session started")
	if !strings.Contains(got, "same") {
		t.Errorf("Text = %q, want it to keep 'same'", got)
	}
}

// The allowlist half. Each of these is the difference between "a value the
// collector can reason about" and "whatever a caller handed us".
func TestAllowlistRefusesAnythingOutsideItsAlphabet(t *testing.T) {
	cases := []struct {
		name string
		got  string
		want string
	}{
		{"a version", AllowToken("1.4.2-beta.1"), "1.4.2-beta.1"},
		{"an OS", AllowToken("linux"), "linux"},
		{"a path is not a token", AllowToken("/home/alice"), Unknown},
		{"an e-mail is not a token", AllowToken("alice@example.com"), Unknown},
		{"an empty token", AllowToken(""), Unknown},
		{"a long token", AllowToken(strings.Repeat("a", 200)), Unknown},
		{"a failure code", AllowCode("stage_failed"), "stage_failed"},
		{"a failure message is not a code", AllowCode("could not stage /home/alice/x.mp4"), Unknown},
		{"a job type", AllowJobType("remotion.workspace-session"), "remotion.workspace-session"},
		{"a project name is not a job type", AllowJobType("Acme Spring Campaign"), Unknown},
		{"a panic type", AllowSymbol("runtime.boundsError"), "runtime.boundsError"},
		{"a stack symbol", AllowSymbol("github.com/vecchiotom/reelbolt/runner/internal/ui.(*Shell).Start"), "github.com/vecchiotom/reelbolt/runner/internal/ui.(*Shell).Start"},
		// A symbol is never a path: the stack's file name goes through
		// AllowFile, so this rule only has to refuse text that is obviously not
		// a Go symbol at all.
		{"a drive letter is not a symbol", AllowSymbol("C:/Users/alice/x.go"), Unknown},
		{"a file name", AllowFile("telemetry.go"), "telemetry.go"},
		{"a path is not a file name", AllowFile("/home/alice/telemetry.go"), ""},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if tc.got != tc.want {
				t.Errorf("got %q, want %q", tc.got, tc.want)
			}
		})
	}

	if kind, mode := AllowRuntime("docker", "rootless"); kind != "docker" || mode != "rootless" {
		t.Errorf("AllowRuntime = %q/%q", kind, mode)
	}
	// A kind outside the closed set is dropped entirely rather than passed
	// through: this field must never carry anything a probe was tricked into
	// returning.
	if kind, _ := AllowRuntime("../../bin/sh", "rootless"); kind != "" {
		t.Errorf("AllowRuntime kept %q", kind)
	}
}
