package hetzner

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"go/parser"
	"go/token"
	"log"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/poolwaker"
)

const secret = "SUPERSECRET-token-value-123"

func newP(t *testing.T, h http.Handler, mut func(*Config)) *Provider {
	t.Helper()
	ts := httptest.NewServer(h)
	t.Cleanup(ts.Close)
	cfg := Config{Token: NewToken(secret), Instance: "stg", BaseURL: ts.URL, SnapshotID: "4242", Location: "fsn1", Sleep: func(time.Duration) {}}
	if mut != nil {
		mut(&cfg)
	}
	p, err := New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	return p
}

func TestCreateBody(t *testing.T) {
	var got map[string]any
	p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer "+secret {
			t.Error("auth header missing")
		}
		if r.Method == http.MethodPost {
			_ = json.NewDecoder(r.Body).Decode(&got)
			fmt.Fprint(w, `{"server":{"id":7,"name":"rb-pool-a","status":"initializing","labels":{},"server_type":{"name":"cpx42"}}}`)
			return
		}
		fmt.Fprint(w, `{"server_types":[{"name":"cpx42","prices":[{"location":"nbg1","price_hourly":{"net":"0.05"}},{"location":"fsn1","price_hourly":{"net":"0.04"}}]}]}`)
	}), func(c *Config) { c.Firewall = "9" })
	n, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "cpx42", UserData: "#cloud-config"})
	if err != nil {
		t.Fatal(err)
	}
	if n.ID != "7" || n.Status != poolwaker.StatusStarting || n.HourlyEUR != 0.04 {
		t.Fatalf("node %+v", n)
	}
	lb := got["labels"].(map[string]any)
	if lb[poolwaker.LabelPool] != "1" || lb[poolwaker.LabelInstance] != "stg" || lb[poolwaker.LabelNode] != "rb-pool-a" {
		t.Fatalf("labels %v", lb)
	}
	if got["user_data"] != "#cloud-config" || got["image"] != float64(4242) || got["start_after_create"] != true {
		t.Fatalf("body %v", got)
	}
	if _, ok := got["ssh_keys"]; ok {
		t.Fatal("ssh_keys must be omitted")
	}
	if got["firewalls"] == nil {
		t.Fatal("firewall missing")
	}
}

func TestListPagination(t *testing.T) {
	var q []string
	p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasPrefix(r.URL.Path, "/server_types") {
			fmt.Fprint(w, `{"server_types":[]}`)
			return
		}
		q = append(q, r.URL.Query().Get("label_selector"))
		lab := `{"reelbolt-pool":"1","reelbolt-pool-instance":"stg"}`
		if r.URL.Query().Get("page") == "1" {
			fmt.Fprint(w, `{"servers":[{"id":1,"name":"rb-pool-1","status":"running","labels":`+lab+`}],"meta":{"pagination":{"next_page":2}}}`)
			return
		}
		fmt.Fprint(w, `{"servers":[{"id":2,"name":"rb-pool-2","status":"off","labels":`+lab+`},{"id":3,"status":"running","labels":{}}],"meta":{"pagination":{"next_page":null}}}`)
	}), nil)
	ns, err := p.List(context.Background(), "stg")
	if err != nil || len(ns) != 2 || ns[0].Status != poolwaker.StatusRunning || ns[1].Status != poolwaker.StatusStopping {
		t.Fatalf("%v %+v", err, ns)
	}
	if q[0] != "reelbolt-pool=1,reelbolt-pool-instance=stg" {
		t.Fatal(q)
	}
}

func deleteHandler(labels string, deletes *int32, gone bool) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if gone {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		if r.Method == http.MethodGet {
			fmt.Fprint(w, `{"server":{"id":5,"name":"rb-pool-5","labels":`+labels+`}}`)
			return
		}
		atomic.AddInt32(deletes, 1)
		fmt.Fprint(w, `{}`)
	})
}

func TestDelete(t *testing.T) {
	var n int32
	p := newP(t, deleteHandler(`{"reelbolt-pool":"1","reelbolt-pool-instance":"stg"}`, &n, false), nil)
	if err := p.Delete(context.Background(), "5"); err != nil || n != 1 {
		t.Fatal(err, n)
	}
	p = newP(t, deleteHandler(``, &n, true), nil)
	if err := p.Delete(context.Background(), "5"); err != nil {
		t.Fatal("404 must succeed", err)
	}
}

func TestDeleteRefusedUnlabelled(t *testing.T) {
	var n int32
	p := newP(t, deleteHandler(`{"other":"x"}`, &n, false), nil)
	if err := p.Delete(context.Background(), "5"); !errors.Is(err, ErrNotOurs) || n != 0 {
		t.Fatal("must refuse", err, n)
	}
}

func Test429Backoff(t *testing.T) {
	var calls int32
	var waits []time.Duration
	p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if atomic.AddInt32(&calls, 1) < 3 {
			w.Header().Set("Retry-After", "2")
			w.WriteHeader(http.StatusTooManyRequests)
			return
		}
		fmt.Fprint(w, `{"servers":[],"meta":{"pagination":{}}}`)
	}), func(c *Config) { c.Sleep = func(d time.Duration) { waits = append(waits, d) } })
	if _, err := p.List(context.Background(), "stg"); err != nil || len(waits) != 2 || waits[0] != 2*time.Second {
		t.Fatal(err, waits)
	}
	// Always 429: bounded.
	calls = 0
	p = newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&calls, 1)
		w.WriteHeader(http.StatusTooManyRequests)
	}), nil)
	if _, err := p.List(context.Background(), "stg"); err == nil || calls != 4 {
		t.Fatal(err, calls)
	}
}

func TestNoLeak(t *testing.T) {
	var buf bytes.Buffer
	log.SetOutput(&buf)
	defer log.SetOutput(os.Stderr)
	p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusInternalServerError)
		fmt.Fprint(w, `{"error":{"code":"boom","message":"server broke"}}`)
	}), nil)
	_, e1 := p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-n", ServerType: "t"})
	_, e2 := p.List(context.Background(), "stg")
	e3 := p.Delete(context.Background(), "5")
	for _, e := range []error{e1, e2, e3} {
		if e == nil || strings.Contains(e.Error(), secret) || !strings.Contains(e.Error(), "500") {
			t.Fatalf("bad error %v", e)
		}
	}
	// Transport error (closed server) must not leak either.
	ts := httptest.NewServer(http.NotFoundHandler())
	url := ts.URL
	ts.Close()
	p2, _ := New(Config{Token: NewToken(secret), Instance: "stg", BaseURL: url, SnapshotID: "1"})
	_, e4 := p2.List(context.Background(), "stg")
	if e4 == nil || strings.Contains(e4.Error(), secret) {
		t.Fatal(e4)
	}
	for _, s := range []string{fmt.Sprintf("%v %s %+v %#v", p.cfg.Token, p.cfg.Token, p.cfg, p.cfg.Token), buf.String()} {
		if strings.Contains(s, secret) {
			t.Fatal("secret printed")
		}
	}
	b, err := json.Marshal(struct{ Token Token }{p.cfg.Token})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(b), secret) {
		t.Fatal("secret marshalled")
	}
}

func TestBaseURLRules(t *testing.T) {
	for _, u := range []string{"http://example.com/v1", "ftp://x"} {
		if _, err := New(Config{Token: NewToken("t"), Instance: "stg", BaseURL: u, SnapshotID: "1"}); err == nil {
			t.Fatal("should refuse", u)
		}
	}
	for _, u := range []string{"", "http://127.0.0.1:1/v1", "https://example.com/v1"} {
		if _, err := New(Config{Token: NewToken("t"), Instance: "stg", BaseURL: u, SnapshotID: "1"}); err != nil {
			t.Fatal(u, err)
		}
	}
}

func TestLoadToken(t *testing.T) {
	env := func(m map[string]string) func(string) string { return func(k string) string { return m[k] } }
	if tk, err := LoadToken(env(map[string]string{"HCLOUD_TOKEN": " abc \n"}), ""); err != nil || tk.reveal() != "abc" {
		t.Fatal(err)
	}
	home := t.TempDir()
	path := filepath.Join(home, TokenFileRel)
	_ = os.MkdirAll(filepath.Dir(path), 0o700)
	_ = os.WriteFile(path, []byte("  "+secret+"\n"), 0o644)
	_ = os.Chmod(path, 0o644)
	_, err := LoadToken(env(nil), home)
	if err == nil || !strings.Contains(err.Error(), path) || strings.Contains(err.Error(), secret) {
		t.Fatalf("loose mode: %v", err)
	}
	_ = os.Chmod(path, 0o600)
	tk, err := LoadToken(env(nil), home)
	if err != nil || tk.reveal() != secret || tk.String() != redacted {
		t.Fatal(err)
	}
	// symlink is not a regular file
	link := filepath.Join(home, "l")
	_ = os.Symlink(path, link)
	_ = os.Remove(path + "x")
	home2 := t.TempDir()
	_ = os.MkdirAll(filepath.Join(home2, ".config/reelbolt"), 0o700)
	_ = os.Symlink(path, filepath.Join(home2, TokenFileRel))
	if _, err := LoadToken(env(nil), home2); err == nil {
		t.Fatal("symlink must be refused")
	}
	if _, err := LoadToken(env(nil), t.TempDir()); err == nil {
		t.Fatal("missing must fail")
	}
}

func TestLoadTokenFromNamedFile(t *testing.T) {
	env := func(m map[string]string) func(string) string { return func(k string) string { return m[k] } }
	dir := t.TempDir()
	path := filepath.Join(dir, "tok")
	if err := os.WriteFile(path, []byte(secret+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	tk, err := LoadToken(env(map[string]string{"HCLOUD_TOKEN_FILE": path}), "")
	if err != nil || tk.reveal() != secret || tk.String() != redacted {
		t.Fatal(err)
	}
	// The environment value still wins over the file.
	if tk, err := LoadToken(env(map[string]string{"HCLOUD_TOKEN": "abc", "HCLOUD_TOKEN_FILE": path}), ""); err != nil || tk.reveal() != "abc" {
		t.Fatal(err)
	}
	// A loose file is refused and the message names the path, never the content.
	_ = os.Chmod(path, 0o644)
	_, err = LoadToken(env(map[string]string{"HCLOUD_TOKEN_FILE": path}), "")
	if err == nil || !strings.Contains(err.Error(), path) || strings.Contains(err.Error(), secret) {
		t.Fatalf("loose mode: %v", err)
	}
	// A named file that is missing is an error even when the home file would have worked.
	home := t.TempDir()
	hp := filepath.Join(home, TokenFileRel)
	_ = os.MkdirAll(filepath.Dir(hp), 0o700)
	_ = os.WriteFile(hp, []byte("other"), 0o600)
	if _, err := LoadToken(env(map[string]string{"HCLOUD_TOKEN_FILE": filepath.Join(dir, "missing")}), home); err == nil {
		t.Fatal("a missing named file must not fall back to the home file")
	}
	// An empty file is refused.
	empty := filepath.Join(dir, "empty")
	_ = os.WriteFile(empty, []byte(" \n"), 0o600)
	if _, err := LoadToken(env(map[string]string{"HCLOUD_TOKEN_FILE": empty}), ""); err == nil {
		t.Fatal("empty file must fail")
	}
}

func TestForeignServersUntouched(t *testing.T) {
	ours := `{"reelbolt-pool":"1","reelbolt-pool-instance":"stg"}`
	fleet := map[int]string{
		1: `{"id":1,"name":"control","status":"running","labels":{}}`,
		2: `{"id":2,"name":"media","status":"running","labels":{}}`,
		3: `{"id":3,"name":"rb-pool-lookalike","status":"running","labels":{}}`,
		4: `{"id":4,"name":"rb-pool-other","status":"running","labels":{"reelbolt-pool":"1","reelbolt-pool-instance":"prod"}}`,
		5: `{"id":5,"name":"worker-x","status":"running","labels":` + ours + `}`,
		6: `{"id":6,"name":"rb-pool-mine","status":"running","labels":` + ours + `}`,
	}
	var mu sync.Mutex
	var log []string
	p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		log = append(log, r.Method+" "+r.URL.Path)
		mu.Unlock()
		if strings.HasPrefix(r.URL.Path, "/server_types") {
			fmt.Fprint(w, `{"server_types":[]}`)
			return
		}
		if r.URL.Path == "/servers" {
			var all []string
			for i := 1; i <= 6; i++ {
				all = append(all, fleet[i]) // a sloppy API ignoring the selector
			}
			fmt.Fprint(w, `{"servers":[`+strings.Join(all, ",")+`],"meta":{"pagination":{}}}`)
			return
		}
		var id int
		_, _ = fmt.Sscanf(r.URL.Path, "/servers/%d", &id)
		if r.Method == http.MethodGet {
			fmt.Fprint(w, `{"server":`+fleet[id]+`}`)
			return
		}
		fmt.Fprint(w, `{}`)
	}), nil)
	ns, err := p.List(context.Background(), "stg")
	if err != nil || len(ns) != 1 || ns[0].ID != "6" {
		t.Fatalf("%v %+v", err, ns)
	}
	for _, id := range []string{"1", "2", "3", "4", "5"} {
		if err := p.Delete(context.Background(), id); !errors.Is(err, ErrNotOurs) {
			t.Fatalf("id %s: %v", id, err)
		}
	}
	mu.Lock()
	defer mu.Unlock()
	for _, l := range log {
		if strings.HasPrefix(l, "DELETE") {
			t.Fatalf("unexpected %s", l)
		}
	}
	if _, err := p.Create(context.Background(), poolwaker.NodeSpec{Name: "nope", ServerType: "x"}); err == nil {
		t.Fatal("prefix must be required")
	}
	if _, err := New(Config{Token: NewToken("t"), SnapshotID: "1"}); err == nil {
		t.Fatal("instance required")
	}
	if _, err := New(Config{Token: NewToken("t"), Instance: "A b", SnapshotID: "1"}); err == nil {
		t.Fatal("bad instance")
	}
}

func TestOnlyAllowedMutations(t *testing.T) {
	src, err := os.ReadFile("hetzner.go")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := parser.ParseFile(token.NewFileSet(), "hetzner.go", src, 0); err != nil {
		t.Fatal(err)
	}
	s := string(src)
	for _, bad := range []string{"http.MethodPut", "http.MethodPatch", "poweroff", "reset", "rebuild", "resize", "change_type", "shutdown"} {
		if strings.Contains(s, bad) {
			t.Fatalf("forbidden mutation reference %q", bad)
		}
	}
	if strings.Count(s, "http.MethodPost") != 3 || strings.Count(s, "http.MethodDelete") != 2 {
		t.Fatal("exactly three POST (create, attach_to_network, poweron) and two DELETE (delete, cleanup) call sites allowed")
	}
	if !strings.Contains(s, `http.MethodPost, "/servers"`) || !strings.Contains(s, `http.MethodDelete, "/servers/"`) {
		t.Fatal("mutations must be POST /servers and DELETE /servers/{id}")
	}
}

// netFake is a Hetzner stand-in for the attach flow; it records the call order.
type netFake struct {
	mu          sync.Mutex
	calls       []string
	createBody  map[string]any
	attachIPs   []string
	conflicts   int    // first N attaches answer ip_not_available
	failAttach  bool   // attach answers a hard error
	actionError string // attach action ends in error with this code
	deleted     bool
	used        string // ip already used by another server
}

func (f *netFake) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls = append(f.calls, r.Method+" "+r.URL.Path)
	switch {
	case r.Method == http.MethodPost && r.URL.Path == "/servers":
		_ = json.NewDecoder(r.Body).Decode(&f.createBody)
		fmt.Fprint(w, `{"server":{"id":7,"name":"rb-pool-a","status":"initializing","labels":{},"server_type":{"name":"cpx42"}},"action":{"id":1,"status":"running"}}`)
	case r.Method == http.MethodGet && r.URL.Path == "/actions/1":
		fmt.Fprint(w, `{"action":{"id":1,"status":"success"}}`)
	case r.Method == http.MethodGet && r.URL.Path == "/actions/2":
		if f.actionError != "" {
			fmt.Fprintf(w, `{"action":{"id":2,"status":"error","error":{"code":%q,"message":"x"}}}`, f.actionError)
			return
		}
		fmt.Fprint(w, `{"action":{"id":2,"status":"success"}}`)
	case r.Method == http.MethodGet && r.URL.Path == "/servers":
		if f.used != "" {
			fmt.Fprintf(w, `{"servers":[{"id":3,"name":"other","private_net":[{"ip":%q}]}],"meta":{"pagination":{}}}`, f.used)
			return
		}
		fmt.Fprint(w, `{"servers":[],"meta":{"pagination":{}}}`)
	case r.Method == http.MethodPost && r.URL.Path == "/servers/7/actions/attach_to_network":
		var b map[string]any
		_ = json.NewDecoder(r.Body).Decode(&b)
		f.attachIPs = append(f.attachIPs, fmt.Sprint(b["ip"]))
		if b["network"] != float64(55) {
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		if f.failAttach {
			w.WriteHeader(http.StatusInternalServerError)
			fmt.Fprint(w, `{"error":{"code":"server_error","message":"boom"}}`)
			return
		}
		if len(f.attachIPs) <= f.conflicts {
			w.WriteHeader(http.StatusUnprocessableEntity)
			fmt.Fprint(w, `{"error":{"code":"ip_not_available","message":"taken"}}`)
			return
		}
		fmt.Fprint(w, `{"action":{"id":2,"status":"running"}}`)
	case r.Method == http.MethodPost && r.URL.Path == "/servers/7/actions/poweron":
		fmt.Fprint(w, `{"action":{"id":3,"status":"running"}}`)
	case r.Method == http.MethodDelete && r.URL.Path == "/servers/7":
		f.deleted = true
		fmt.Fprint(w, `{}`)
	default:
		w.WriteHeader(http.StatusNotFound)
	}
}

func netProvider(t *testing.T, f *netFake) *Provider {
	return newP(t, f, func(c *Config) { c.Network = "55"; c.NetworkSubnet = "10.0.2.0/24" })
}

func createA(p *Provider) (poolwaker.Node, error) {
	return p.Create(context.Background(), poolwaker.NodeSpec{Name: "rb-pool-a", Instance: "stg", ServerType: "cpx42", UserData: "x"})
}

func inSubnet(t *testing.T, ip string) {
	t.Helper()
	_, n, _ := net.ParseCIDR("10.0.2.0/24")
	p := net.ParseIP(ip)
	if p == nil || !n.Contains(p) || p[15] < 4 || p[15] > 254 {
		t.Fatalf("ip %q not a usable address of 10.0.2.0/24", ip)
	}
}

func TestAttachFlowOrder(t *testing.T) {
	f := &netFake{}
	if _, err := createA(netProvider(t, f)); err != nil {
		t.Fatal(err)
	}
	if _, ok := f.createBody["networks"]; ok {
		t.Fatal("create must not carry networks")
	}
	if f.createBody["start_after_create"] != false {
		t.Fatal("start_after_create must be false")
	}
	var seq []string
	for _, c := range f.calls {
		if strings.Contains(c, "/actions/attach") || strings.Contains(c, "poweron") || c == "POST /servers" {
			seq = append(seq, c)
		}
	}
	want := []string{"POST /servers", "POST /servers/7/actions/attach_to_network", "POST /servers/7/actions/poweron"}
	if strings.Join(seq, ",") != strings.Join(want, ",") {
		t.Fatalf("order %v", seq)
	}
	if len(f.attachIPs) != 1 {
		t.Fatalf("attach ips %v", f.attachIPs)
	}
	inSubnet(t, f.attachIPs[0])
	if f.deleted {
		t.Fatal("server deleted on success")
	}
}

func TestAttachSkipsUsedAndRetriesConflict(t *testing.T) {
	f := &netFake{conflicts: 2, used: "10.0.2.4"}
	if _, err := createA(netProvider(t, f)); err != nil {
		t.Fatal(err)
	}
	if len(f.attachIPs) != 3 {
		t.Fatalf("attach ips %v", f.attachIPs)
	}
	seen := map[string]bool{}
	for _, ip := range f.attachIPs {
		inSubnet(t, ip)
		if ip == "10.0.2.4" || seen[ip] {
			t.Fatalf("reused or used ip in %v", f.attachIPs)
		}
		seen[ip] = true
	}
	if f.deleted {
		t.Fatal("deleted despite success")
	}
}

func TestAttachActionConflictRetried(t *testing.T) {
	f := &netFake{actionError: "ip_not_available"}
	if _, err := createA(netProvider(t, f)); err == nil {
		t.Fatal("persistent action conflict must fail")
	}
	if len(f.attachIPs) != attachAttempts || !f.deleted {
		t.Fatalf("attempts %d deleted %v", len(f.attachIPs), f.deleted)
	}
}

func TestAttachFailureDeletesServer(t *testing.T) {
	f := &netFake{failAttach: true}
	_, err := createA(netProvider(t, f))
	if err == nil || !strings.Contains(err.Error(), "server_error") {
		t.Fatalf("err %v", err)
	}
	if !f.deleted {
		t.Fatal("server leaked after attach failure")
	}
	for _, c := range f.calls {
		if strings.Contains(c, "poweron") {
			t.Fatal("powered on after failed attach")
		}
	}
}

func TestConflictExhaustionDeletesServer(t *testing.T) {
	f := &netFake{conflicts: 100}
	if _, err := createA(netProvider(t, f)); err == nil || !f.deleted {
		t.Fatalf("err %v deleted %v", err, f.deleted)
	}
	if len(f.attachIPs) != attachAttempts {
		t.Fatalf("attempts %d", len(f.attachIPs))
	}
}

func TestSizeRefusalStillSurfacesWithSubnet(t *testing.T) {
	p := newP(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusForbidden)
		fmt.Fprint(w, `{"error":{"code":"resource_limit_exceeded","message":"limit"}}`)
	}), func(c *Config) { c.Network = "55"; c.NetworkSubnet = "10.0.2.0/24" })
	_, err := createA(p)
	if !errors.Is(err, poolwaker.ErrSizeRefused) {
		t.Fatalf("err %v", err)
	}
}

func TestNetworkNeedsSubnet(t *testing.T) {
	if _, err := New(Config{Token: NewToken("t"), Instance: "stg", SnapshotID: "1", Network: "55"}); err == nil {
		t.Fatal("network without subnet must be refused")
	}
	if _, err := New(Config{Token: NewToken("t"), Instance: "stg", SnapshotID: "1", NetworkSubnet: "10.0.2.0/24"}); err == nil {
		t.Fatal("subnet without network must be refused")
	}
}

func TestSubnetValidation(t *testing.T) {
	for _, bad := range []string{"8.8.8.0/24", "1.2.3.0/24", "10.0.2.0/29", "10.0.2.0/32", "nonsense", "fd00::/64", "172.32.0.0/16", "172.0.0.0/8", "192.0.0.0/8", "10.0.0.0/16", "10.0.0.0/21"} {
		if _, err := ValidateSubnet(bad); err == nil {
			t.Fatalf("%q must be rejected", bad)
		}
	}
	for _, ok := range []string{"10.0.2.0/24", "172.16.5.0/28", "192.168.0.0/22"} {
		if _, err := ValidateSubnet(ok); err != nil {
			t.Fatalf("%q: %v", ok, err)
		}
	}
}

func TestCandidatesSkipReserved(t *testing.T) {
	p := netProvider(t, &netFake{})
	c := p.freeCandidates(map[string]bool{"10.0.2.10": true})
	if c[0] != "10.0.2.4" || c[len(c)-1] != "10.0.2.254" || len(c) != 250 {
		t.Fatalf("candidates %v..%v (%d)", c[0], c[len(c)-1], len(c))
	}
}
