package main

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// rawURL is the base64url alphabet the job token uses.
func rawURL(s string) string { return base64.RawURLEncoding.EncodeToString([]byte(s)) }

// These tests cover the D5b logic that needs no database: the idempotency key,
// the capability filter, the job token and the lifecycle rules the reaper
// applies. Everything that touches SQL is in *_integration_test.go.

// TestIdempotencyKeyIsTheSpecifiedDigest pins the framing. The value is a fixed
// vector so a change to the way the components are joined — the thing the .NET
// engine client (D6) must agree with byte for byte — cannot pass unnoticed.
func TestIdempotencyKeyIsTheSpecifiedDigest(t *testing.T) {
	got := IdempotencyKey("exec-1", "step-1", 2, "video.compile")
	const want = "b53f9706565bd638c0c6b030b2b1fa5e998ecf6366e9d602007684304ca60e2c"
	if got != want {
		t.Fatalf("IdempotencyKey = %q, want %q (sha256 of \"exec-1|step-1|2|video.compile\")", got, want)
	}
	// Each component matters.
	for _, other := range []string{
		IdempotencyKey("exec-2", "step-1", 2, "video.compile"),
		IdempotencyKey("exec-1", "step-2", 2, "video.compile"),
		IdempotencyKey("exec-1", "step-1", 3, "video.compile"),
		IdempotencyKey("exec-1", "step-1", 2, "video.analyze.extract"),
	} {
		if other == got {
			t.Errorf("a different component produced the same key %q", other)
		}
	}
}

func TestRequiredCapsSatisfiedBy(t *testing.T) {
	digestA := "sha256:" + strings.Repeat("a", 64)
	digestB := "sha256:" + strings.Repeat("b", 64)

	base := func() protocol.Capabilities {
		caps := defaultCapabilities()
		caps.RuntimeImageDigest = &digestA
		return caps
	}

	cases := []struct {
		name    string
		caps    RequiredCaps
		jobType string
		mutate  func(*protocol.Capabilities)
		want    bool
	}{
		{name: "nothing required", caps: RequiredCaps{}, jobType: protocol.JobVideoCompile, want: true},
		{name: "job type not advertised", caps: RequiredCaps{}, jobType: "video.unknown", want: false},
		{
			name: "matching digest", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{RuntimeImageDigest: digestA}, want: true,
		},
		{
			name: "different digest", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{RuntimeImageDigest: digestB}, want: false,
		},
		{
			name: "digest required but the runner has none", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{RuntimeImageDigest: digestA}, want: false,
			mutate: func(c *protocol.Capabilities) { c.RuntimeImageDigest = nil },
		},
		{
			name: "container mismatch", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{Container: protocol.ContainerPodman}, want: false,
		},
		{
			name: "ffmpeg major is sufficient", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{FfmpegMajor: 7}, want: true,
		},
		{
			name: "ffmpeg major is too low", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{FfmpegMajor: 8}, want: false,
		},
		{
			name: "ffmpeg version unparseable fails closed", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{FfmpegMajor: 1}, want: false,
			mutate: func(c *protocol.Capabilities) { c.FfmpegVersion = "git" },
		},
		{
			name: "fontset tag must match", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{FontsetVersion: "2"}, want: false,
		},
		{
			name: "output cap is large enough", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{MinOutputBytes: 1 << 31}, want: true,
		},
		{
			name: "output cap is too small", jobType: protocol.JobVideoCompile,
			caps: RequiredCaps{MinOutputBytes: 1 << 40}, want: false,
		},
		{
			name: "a Remotion session needs a container runtime", jobType: protocol.JobRemotionWorkspace,
			caps: RequiredCaps{}, want: false,
			mutate: func(c *protocol.Capabilities) {
				c.Container = protocol.ContainerNone
				c.RuntimeImageDigest = nil
			},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			caps := base()
			if tc.mutate != nil {
				tc.mutate(&caps)
			}
			if got := tc.caps.SatisfiedBy(tc.jobType, caps); got != tc.want {
				t.Errorf("SatisfiedBy = %v, want %v (caps %+v against %+v)", got, tc.want, tc.caps, caps)
			}
		})
	}
}

func TestParseRequiredCaps(t *testing.T) {
	if caps, err := ParseRequiredCaps(""); err != nil || caps != (RequiredCaps{}) {
		t.Errorf("empty document = %+v, %v", caps, err)
	}
	caps, err := ParseRequiredCaps(`{"container":"docker","ffmpegMajor":7}`)
	if err != nil || caps.Container != protocol.ContainerDocker || caps.FfmpegMajor != 7 {
		t.Errorf("parsed = %+v, %v", caps, err)
	}
	// A document the gateway cannot read is an error, never a silently empty
	// requirement: that would assign a job to a runner that cannot run it.
	if _, err := ParseRequiredCaps(`{"ffmpegMajor":`); err == nil {
		t.Error("an unreadable requirement document was accepted")
	}
}

func TestJobTokenRoundTripAndFencing(t *testing.T) {
	signer, err := newJobTokenSigner([]byte("0123456789abcdef0123456789abcdef"))
	if err != nil {
		t.Fatalf("signer: %v", err)
	}
	now := time.Now().UTC()
	claims := protocol.JobTokenClaims{
		JobID: "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11", SessionID: "3f0c1a9b-2e11-4a43-9d57-7d1c2e0a5b7e",
		Epoch: 3, OrgID: testOrgA, Exp: now.Add(5 * time.Minute).Unix(),
	}
	token, err := signer.mint(claims)
	if err != nil {
		t.Fatalf("mint: %v", err)
	}
	got, err := signer.verify(token, now)
	if err != nil {
		t.Fatalf("verify: %v", err)
	}
	if got != claims {
		t.Errorf("verified claims = %+v, want %+v", got, claims)
	}

	// A token signed with another key is refused.
	other, err := newJobTokenSigner([]byte("ffffffffffffffffffffffffffffffff"))
	if err != nil {
		t.Fatalf("signer: %v", err)
	}
	if _, err := other.verify(token, now); err == nil {
		t.Error("a token signed with another key was accepted")
	}
	// So is a tampered payload, an expired token and a downgraded algorithm.
	parts := strings.Split(token, ".")
	tampered := parts[0] + "." + rawURL(`{"jobId":"other","epoch":9,"orgId":"x","exp":9999999999}`) + "." + parts[2]
	if _, err := signer.verify(tampered, now); err == nil {
		t.Error("a token with a rewritten payload was accepted")
	}
	expired, err := signer.mint(protocol.JobTokenClaims{
		JobID: claims.JobID, Epoch: 1, OrgID: testOrgA, Exp: now.Add(-time.Second).Unix(),
	})
	if err != nil {
		t.Fatalf("mint: %v", err)
	}
	if _, err := signer.verify(expired, now); err == nil {
		t.Error("an expired token was accepted")
	}
	if _, err := signer.verify("", now); err == nil {
		t.Error("an empty token was accepted")
	}
	if _, err := signer.verify(strings.Join([]string{rawURL(`{"alg":"none"}`), parts[1], parts[2]}, "."), now); err == nil {
		t.Error("a token with a rewritten header was accepted")
	}
}

func TestFenceTokenChecksClaimsAgainstTheConnection(t *testing.T) {
	signer, err := newJobTokenSigner([]byte("0123456789abcdef0123456789abcdef"))
	if err != nil {
		t.Fatalf("signer: %v", err)
	}
	g := &gateway{tokens: signer, now: time.Now}
	const jobID = "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11"
	token, err := signer.mint(protocol.JobTokenClaims{
		JobID: jobID, Epoch: 2, OrgID: testOrgA, Exp: time.Now().Add(time.Minute).Unix(),
	})
	if err != nil {
		t.Fatalf("mint: %v", err)
	}
	env := protocol.Envelope{V: protocol.Version, Type: protocol.TypeAccept, ID: "id", JobID: jobID}

	desktop := &conn{kind: protocol.KindDesktop, orgID: testOrgA}
	if _, ok := g.fenceToken(desktop, env, 2, token); !ok {
		t.Error("a valid token for the right job, epoch and organisation was refused")
	}
	if _, ok := g.fenceToken(desktop, env, 1, token); ok {
		t.Error("a token was accepted at the wrong epoch")
	}
	otherOrg := &conn{kind: protocol.KindDesktop, orgID: testOrgB}
	if _, ok := g.fenceToken(otherOrg, env, 2, token); ok {
		t.Error("an org-A job token was accepted on an org-B runner")
	}
	otherJob := env
	otherJob.JobID = "3f0c1a9b-2e11-4a43-9d57-7d1c2e0a5b7e"
	if _, ok := g.fenceToken(desktop, otherJob, 2, token); ok {
		t.Error("a token was accepted for another job")
	}
	// A pool worker serves any organisation, so the org check does not apply to
	// it: its token names whichever organisation owns the job it was given.
	pool := &conn{kind: protocol.KindCloudPool}
	if _, ok := g.fenceToken(pool, env, 2, token); !ok {
		t.Error("a pool worker's valid token was refused")
	}
}

func TestSessionJobClassificationAndRequeueDecision(t *testing.T) {
	for _, jobType := range protocol.JobTypes() {
		if !isSessionJobType(jobType) {
			t.Errorf("%s is a v1 session and must be treated as one", jobType)
		}
	}
	if isSessionJobType("video.render.one-shot") {
		t.Error("an unknown job type was treated as a session")
	}

	cases := []struct {
		name        string
		job         Job
		wantRequeue bool
		wantStatus  string
	}{
		{
			name: "session job expires", job: Job{JobType: protocol.JobRemotionWorkspace, Attempt: 1, MaxAttempts: 2},
			wantRequeue: false, wantStatus: jobExpired,
		},
		{
			name:        "one-shot job requeues inside its budget",
			job:         Job{JobType: "video.render.one-shot", Attempt: 1, MaxAttempts: 2},
			wantRequeue: true, wantStatus: jobQueued,
		},
		{
			name:        "one-shot job fails when the budget is spent",
			job:         Job{JobType: "video.render.one-shot", Attempt: 2, MaxAttempts: 2},
			wantRequeue: false, wantStatus: jobFailed,
		},
		{
			name:        "a zero budget takes the default",
			job:         Job{JobType: "video.render.one-shot", Attempt: 1, MaxAttempts: 0},
			wantRequeue: true, wantStatus: jobQueued,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			requeue, status := requeueDecision(tc.job)
			if requeue != tc.wantRequeue || status != tc.wantStatus {
				t.Errorf("requeueDecision = %v, %q; want %v, %q", requeue, status, tc.wantRequeue, tc.wantStatus)
			}
		})
	}
}

func TestAssignLimits(t *testing.T) {
	g := &gateway{now: time.Now}
	caps := defaultCapabilities()
	caps.MaxOutputBytes = 1 << 20

	// No spec: the defaults apply, and the output cap is the runner's own.
	timeout, idle, maxBytes := g.assignLimits(Job{SpecJSON: `{}`}, caps)
	if timeout != jobTimeoutDefaultSec || idle != jobIdleTimeoutSec || maxBytes != 1<<20 {
		t.Errorf("defaults = %d, %d, %d", timeout, idle, maxBytes)
	}

	// The spec may ask for less, never for more than the runner will publish.
	timeout, idle, maxBytes = g.assignLimits(Job{SpecJSON: `{"timeoutSec":60,"idleTimeoutSec":30,"maxOutputBytes":9000000}`}, caps)
	if timeout != 60 || idle != 30 || maxBytes != 1<<20 {
		t.Errorf("spec limits = %d, %d, %d", timeout, idle, maxBytes)
	}

	// A session lifetime beyond the protocol's cap is clamped rather than
	// honoured.
	timeout, _, _ = g.assignLimits(Job{SpecJSON: `{"timeoutSec":100000}`}, caps)
	if timeout != jobTimeoutDefaultSec {
		t.Errorf("timeout = %d, want the default for an out-of-range request", timeout)
	}

	// A spec the gateway cannot read falls back to the defaults rather than
	// stranding the job.
	timeout, idle, _ = g.assignLimits(Job{SpecJSON: "not json"}, caps)
	if timeout != jobTimeoutDefaultSec || idle != jobIdleTimeoutSec {
		t.Errorf("unreadable spec = %d, %d", timeout, idle)
	}
}

func TestParseWait(t *testing.T) {
	if got, err := parseWait(""); err != nil || got != 0 {
		t.Errorf("empty = %v, %v", got, err)
	}
	if got, err := parseWait("30s"); err != nil || got != 30*time.Second {
		t.Errorf("30s = %v, %v", got, err)
	}
	if got, err := parseWait("45"); err != nil || got != 45*time.Second {
		t.Errorf("45 = %v, %v", got, err)
	}
	// A wait longer than the cap is clamped, not refused: the caller's intent is
	// clear and a long poll holds a connection for nothing.
	if got, err := parseWait("1h"); err != nil || got != maxWait {
		t.Errorf("1h = %v, %v", got, err)
	}
	for _, bad := range []string{"-5", "-5s", "soon"} {
		if _, err := parseWait(bad); err == nil {
			t.Errorf("parseWait(%q) was accepted", bad)
		}
	}
}

func TestLoadJobTokenKey(t *testing.T) {
	key, ephemeral, err := LoadJobTokenKey("")
	if err != nil || !ephemeral || len(key) != 32 {
		t.Fatalf("empty key = %d bytes, ephemeral=%v, %v", len(key), ephemeral, err)
	}
	// Standard base64, base64url and the raw form are all accepted, because the
	// key travels through a secrets manager.
	for _, encoded := range []string{
		"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
		"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
	} {
		key, ephemeral, err := LoadJobTokenKey(encoded)
		if err != nil || ephemeral || len(key) != 32 {
			t.Errorf("LoadJobTokenKey(%q) = %d bytes, ephemeral=%v, %v", encoded, len(key), ephemeral, err)
		}
	}
	// A key too short to be keyspace is refused rather than padded.
	if _, _, err := LoadJobTokenKey("c2hvcnQ="); err == nil {
		t.Error("a short key was accepted")
	}
}

func TestCancelWatchdogBookkeeping(t *testing.T) {
	g := newGateway(appConfig{}, nil)
	now := time.Now().UTC()
	g.addPendingCancel(pendingCancel{jobID: "a", epoch: 1, deadline: now.Add(-time.Second)})
	g.addPendingCancel(pendingCancel{jobID: "b", epoch: 1, deadline: now.Add(time.Hour)})
	if g.pendingCancelCount() != 2 {
		t.Fatalf("pending = %d, want 2", g.pendingCancelCount())
	}
	overdue := g.overduePendingCancels(now)
	if len(overdue) != 1 || overdue[0].jobID != "a" {
		t.Fatalf("overdue = %+v, want only the elapsed one", overdue)
	}
	// Exactly one of the timer and the sweep acts on a cancellation.
	if !g.takePendingCancel("a") || g.takePendingCancel("a") {
		t.Error("a pending cancellation was taken twice")
	}
	g.clearPendingCancel("b")
	if g.pendingCancelCount() != 0 {
		t.Errorf("pending = %d, want 0", g.pendingCancelCount())
	}
}

func TestConnOfferAndRejectionBookkeeping(t *testing.T) {
	c := &conn{offers: map[string]int64{}, rejected: map[string]time.Time{}, sinceHeartbeat: map[string]struct{}{}}
	now := time.Now().UTC()

	c.addOffer("job-1", 1)
	c.addOffer("job-2", 1)
	c.addSinceHeartbeat("job-1")
	snap := c.snapshot()
	if len(snap.Offers) != 2 || snap.SinceHeartbeat != 1 {
		t.Fatalf("snapshot = %+v", snap)
	}
	c.removeOffer("job-1")
	c.forgetJob("job-1")
	if snap := c.snapshot(); len(snap.Offers) != 1 || snap.SinceHeartbeat != 0 {
		t.Fatalf("after forgetting: %+v", snap)
	}

	c.markRejected("job-3", now)
	if got := c.rejectedJobs(now, time.Minute); len(got) != 1 || got[0] != "job-3" {
		t.Fatalf("rejectedJobs = %v", got)
	}
	// The memory expires: a job rejected long ago may be offered again.
	if got := c.rejectedJobs(now.Add(2*time.Minute), time.Minute); len(got) != 0 {
		t.Errorf("a stale rejection is still remembered: %v", got)
	}
}

func TestSupportsAllAndSplitList(t *testing.T) {
	caps := defaultCapabilities()
	if !supportsAll(caps, []string{protocol.JobVideoCompile}) {
		t.Error("a capability the runner advertises was reported missing")
	}
	if supportsAll(caps, []string{protocol.JobVideoCompile, "video.unknown"}) {
		t.Error("an unknown job type was reported as supported")
	}
	if got := splitList(" a , b ,, c "); len(got) != 3 || got[0] != "a" || got[2] != "c" {
		t.Errorf("splitList = %v", got)
	}
}

func TestJobViewsCarryNoSecret(t *testing.T) {
	job := Job{
		ID: "7d1c2e0a-5b7e-4a43-9d57-3f0c1a9b2e11", Status: jobRunning, JobType: protocol.JobVideoCompile,
		Target: targetOrgRunner, OrganizationID: testOrgA, SpecJSON: `{"executionId":"x"}`,
		ResultJSON: `{"result":{"ok":true}}`, Attempt: 1, MaxAttempts: 2, LeaseEpoch: 3,
	}
	raw, err := json.Marshal(jobToView(job, true))
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	var decoded map[string]any
	if err := json.Unmarshal(raw, &decoded); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	// The view carries the job's state, never its token, spec or organisation
	// secret material: the engine already has the spec.
	for _, forbidden := range []string{"jobToken", "spec", "specJson", "organizationId", "jobId"} {
		if _, ok := decoded[forbidden]; ok && forbidden != "jobId" {
			t.Errorf("the job view carries %q: %s", forbidden, raw)
		}
	}
	if decoded["jobId"] != job.ID || decoded["status"] != jobRunning || decoded["created"] != true {
		t.Errorf("view = %s", raw)
	}
}

// TestExpiredJobReportsSessionLost: the engine is told session_lost on its next
// long-poll rather than being left to infer it from a status string.
func TestExpiredJobReportsSessionLost(t *testing.T) {
	view := jobToView(Job{ID: "x", Status: jobExpired}, false)
	if !view.SessionLost {
		t.Error("an Expired job does not report sessionLost")
	}
	if jobToView(Job{ID: "x", Status: jobFailed}, false).SessionLost {
		t.Error("a Failed job reports sessionLost")
	}
}
