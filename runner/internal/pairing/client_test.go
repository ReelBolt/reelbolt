package pairing

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// fakeAPI is the pairing server E4b implements, reduced to what the client must
// cope with.
type fakeAPI struct {
	mu       sync.Mutex
	started  []map[string]string
	polls    int
	answers  []pollAnswer // consumed in order; the last one repeats
	interval int
}

type pollAnswer struct {
	status int
	code   string // RFC 8628 error code, or "" for success
	result map[string]string
}

func (f *fakeAPI) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v1/runners/pair/start", func(w http.ResponseWriter, r *http.Request) {
		var body map[string]string
		_ = json.NewDecoder(r.Body).Decode(&body)
		f.mu.Lock()
		f.started = append(f.started, body)
		interval := f.interval
		f.mu.Unlock()
		if interval == 0 {
			interval = 5
		}
		writeJSON(w, 200, map[string]any{
			"deviceCode":      "device-secret",
			"userCode":        "ABCD-EFGH",
			"verificationUri": "https://app.reelbolt.ai/app/runners/pair",
			"expiresIn":       600,
			"interval":        interval,
		})
	})
	mux.HandleFunc("/api/v1/runners/pair/poll", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		idx := f.polls
		f.polls++
		var answer pollAnswer
		switch {
		case len(f.answers) == 0:
			answer = pollAnswer{status: 400, code: codePending}
		case idx >= len(f.answers):
			answer = f.answers[len(f.answers)-1]
		default:
			answer = f.answers[idx]
		}
		f.mu.Unlock()
		if answer.code != "" {
			writeJSON(w, answer.status, map[string]string{"error": answer.code})
			return
		}
		body := map[string]any{}
		for k, v := range answer.result {
			body[k] = v
		}
		writeJSON(w, answer.status, body)
	})
	return mux
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

// newClient builds a client whose clock and sleep are driven by the test.
func newClient(t *testing.T, base string) (*Client, *fakeClock) {
	t.Helper()
	clock := &fakeClock{now: time.Unix(1_700_000_000, 0)}
	return &Client{
		APIBaseURL: base,
		DeviceName: "test-machine",
		Version:    "1.2.3",
		Now:        clock.Now,
		Sleep:      clock.Sleep,
		OpenBrowser: func(string) error {
			return nil
		},
	}, clock
}

type fakeClock struct {
	mu   sync.Mutex
	now  time.Time
	real time.Duration
}

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *fakeClock) Sleep(ctx context.Context, d time.Duration) error {
	c.mu.Lock()
	c.now = c.now.Add(d)
	c.real += d
	c.mu.Unlock()
	if err := ctx.Err(); err != nil {
		return err
	}
	return nil
}

func testPub(t *testing.T) ed25519.PublicKey {
	t.Helper()
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return pub
}

func TestPairSendsTheCanonicalPublicKeyAndTheDeviceFacts(t *testing.T) {
	api := &fakeAPI{}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()

	c, _ := newClient(t, srv.URL)
	pub := testPub(t)
	if _, err := c.Start(context.Background(), Request{PublicKey: pub, OS: "linux", Arch: "amd64"}); err != nil {
		t.Fatalf("Start: %v", err)
	}
	if len(api.started) != 1 {
		t.Fatalf("start requests = %d, want 1", len(api.started))
	}
	body := api.started[0]
	if body["publicKey"] != protocol.EncodeDevicePublicKey(pub) {
		t.Errorf("publicKey = %q, want the canonical encoding", body["publicKey"])
	}
	if body["name"] != "test-machine" || body["os"] != "linux" || body["arch"] != "amd64" || body["version"] != "1.2.3" {
		t.Errorf("start body = %v", body)
	}
	if strings.Contains(strings.ToLower(body["publicKey"]), "private") {
		t.Error("the start request must never carry private key material")
	}
}

func TestStartBuildsTheVerificationLinkWithTheCode(t *testing.T) {
	api := &fakeAPI{}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	p, err := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	if err != nil {
		t.Fatal(err)
	}
	if p.UserCode != "ABCD-EFGH" {
		t.Errorf("UserCode = %q", p.UserCode)
	}
	want := "https://app.reelbolt.ai/app/runners/pair?code=ABCD-EFGH"
	if p.CodeURL != want {
		t.Errorf("CodeURL = %q, want %q", p.CodeURL, want)
	}
}

func TestStartRejectsAnIncompleteResponse(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, 200, map[string]string{"deviceCode": "only-this"})
	}))
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	if _, err := c.Start(context.Background(), Request{PublicKey: testPub(t)}); err == nil {
		t.Fatal("Start accepted a response with no user code or verification URI")
	}
}

func TestStartMapsAlreadyPaired(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, 409, map[string]string{"error": "already_paired"})
	}))
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	_, err := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	if !errors.Is(err, ErrAlreadyPaired) {
		t.Fatalf("Start = %v, want ErrAlreadyPaired", err)
	}
}

func TestPollWaitsForApproval(t *testing.T) {
	api := &fakeAPI{answers: []pollAnswer{
		{status: 400, code: codePending},
		{status: 400, code: codePending},
		{status: 200, result: map[string]string{
			"deviceId":       "device-uuid",
			"organizationId": "org-uuid",
			"gatewayUrl":     "wss://runner.reelbolt.ai/v1/connect",
		}},
	}}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, clock := newClient(t, srv.URL)
	p, err := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	if err != nil {
		t.Fatal(err)
	}
	res, err := c.Poll(context.Background(), p)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if res.DeviceID != "device-uuid" || res.OrganizationID != "org-uuid" {
		t.Errorf("result = %+v", res)
	}
	if api.polls != 3 {
		t.Errorf("polls = %d, want 3", api.polls)
	}
	// Three polls at the advertised five-second interval.
	if clock.real != 15*time.Second {
		t.Errorf("slept %s, want 15s", clock.real)
	}
}

func TestPollBacksOffOnSlowDown(t *testing.T) {
	api := &fakeAPI{answers: []pollAnswer{
		{status: 400, code: codeSlowDown},
		{status: 200, result: map[string]string{"deviceId": "d", "gatewayUrl": "wss://runner.reelbolt.ai/v1/connect"}},
	}}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, clock := newClient(t, srv.URL)
	p, _ := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	if _, err := c.Poll(context.Background(), p); err != nil {
		t.Fatalf("Poll: %v", err)
	}
	// 5s for the first poll, then 10s after slow_down.
	if clock.real != 15*time.Second {
		t.Errorf("slept %s, want 15s", clock.real)
	}
}

func TestPollStopsOnDenialAndExpiry(t *testing.T) {
	for _, tc := range []struct {
		name string
		code string
		want error
	}{
		{"denied", codeDenied, ErrAccessDenied},
		{"expired", codeExpired, ErrExpired},
	} {
		t.Run(tc.name, func(t *testing.T) {
			api := &fakeAPI{answers: []pollAnswer{{status: 400, code: tc.code}}}
			srv := httptest.NewServer(api.handler())
			defer srv.Close()
			c, _ := newClient(t, srv.URL)
			p, _ := c.Start(context.Background(), Request{PublicKey: testPub(t)})
			if _, err := c.Poll(context.Background(), p); !errors.Is(err, tc.want) {
				t.Fatalf("Poll = %v, want %v", err, tc.want)
			}
		})
	}
}

func TestPollReportsAnUnknownErrorCode(t *testing.T) {
	api := &fakeAPI{answers: []pollAnswer{{status: 400, code: "some_new_code"}}}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	p, _ := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	_, err := c.Poll(context.Background(), p)
	if err == nil || !strings.Contains(err.Error(), "some_new_code") {
		t.Fatalf("Poll = %v, want the server's code reported", err)
	}
}

func TestPollGivesUpAtTheExpiry(t *testing.T) {
	api := &fakeAPI{}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, clock := newClient(t, srv.URL)
	p, _ := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	// The server said the code is good for 600s; the loop must stop after it.
	if _, err := c.Poll(context.Background(), p); !errors.Is(err, ErrExpired) {
		t.Fatalf("Poll = %v, want ErrExpired", err)
	}
	if clock.real < 600*time.Second {
		t.Errorf("gave up after %s, before the advertised window", clock.real)
	}
}

func TestPollSurvivesATransportFailure(t *testing.T) {
	var calls int
	var mu sync.Mutex
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		calls++
		n := calls
		mu.Unlock()
		if strings.HasSuffix(r.URL.Path, "/start") {
			writeJSON(w, 200, map[string]any{
				"deviceCode": "dc", "userCode": "AAAA-BBBB",
				"verificationUri": "https://app.reelbolt.ai/app/runners/pair",
				"expiresIn":       600, "interval": 5,
			})
			return
		}
		if n == 2 {
			// A 502 from a proxy is not the server's verdict on the request.
			http.Error(w, "bad gateway", http.StatusBadGateway)
			return
		}
		writeJSON(w, 200, map[string]string{
			"deviceId": "device-uuid", "gatewayUrl": "wss://runner.reelbolt.ai/v1/connect",
		})
	}))
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	p, _ := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	res, err := c.Poll(context.Background(), p)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if res.DeviceID != "device-uuid" {
		t.Errorf("result = %+v", res)
	}
}

func TestPollHonoursContextCancellation(t *testing.T) {
	api := &fakeAPI{}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	c.Sleep = func(ctx context.Context, d time.Duration) error { return ctx.Err() }
	p, _ := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := c.Poll(ctx, p); !errors.Is(err, context.Canceled) {
		t.Fatalf("Poll = %v, want context.Canceled", err)
	}
}

func TestStartNeedsANameAndAKey(t *testing.T) {
	c := &Client{APIBaseURL: "https://example.invalid", DeviceName: "x"}
	if _, err := c.Start(context.Background(), Request{PublicKey: []byte{1, 2, 3}}); err == nil {
		t.Fatal("Start accepted a malformed public key")
	}
	c.DeviceName = ""
	if _, err := c.Start(context.Background(), Request{PublicKey: testPub(t)}); err == nil {
		t.Fatal("Start accepted a missing device name")
	}
}
