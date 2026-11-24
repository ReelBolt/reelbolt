package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

type testCA struct {
	cert *x509.Certificate
	key  *ecdsa.PrivateKey
	pem  []byte
}

func newTestCA(t *testing.T, cn string) *testCA {
	t.Helper()
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	tpl := &x509.Certificate{
		SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: cn},
		NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour),
		IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign,
	}
	der, err := x509.CreateCertificate(rand.Reader, tpl, tpl, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	c, _ := x509.ParseCertificate(der)
	return &testCA{c, key, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})}
}

// issue returns PEM cert and key signed by the CA.
func (ca *testCA) issue(t *testing.T, cn string, serial int64, server bool) (certPEM, keyPEM []byte) {
	t.Helper()
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	tpl := &x509.Certificate{
		SerialNumber: big.NewInt(serial), Subject: pkix.Name{CommonName: cn},
		NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour),
		KeyUsage: x509.KeyUsageDigitalSignature,
	}
	if server {
		tpl.ExtKeyUsage = []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}
		tpl.IPAddresses = []net.IP{net.ParseIP("127.0.0.1")}
	} else {
		tpl.ExtKeyUsage = []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth}
	}
	der, err := x509.CreateCertificate(rand.Reader, tpl, ca.cert, &key.PublicKey, ca.key)
	if err != nil {
		t.Fatal(err)
	}
	kd, _ := x509.MarshalECPrivateKey(key)
	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: kd})
}

type tlsFixture struct {
	tg       *testGateway
	ca       *testCA
	certFile string
	keyFile  string
	url      string
}

const tlsTestToken = "tls-test-token"

func newTLSFixture(t *testing.T, withClientCA bool) *tlsFixture {
	t.Helper()
	dir := t.TempDir()
	ca := newTestCA(t, "server-ca")
	cp, kp := ca.issue(t, "gateway", 2, true)
	f := &tlsFixture{ca: ca, certFile: filepath.Join(dir, "c.pem"), keyFile: filepath.Join(dir, "k.pem")}
	must(t, os.WriteFile(f.certFile, cp, 0o600))
	must(t, os.WriteFile(f.keyFile, kp, 0o600))
	clientCAFile := ""
	if withClientCA {
		clientCAFile = filepath.Join(dir, "ca.pem")
		must(t, os.WriteFile(clientCAFile, ca.pem, 0o600))
	}
	f.tg = newTestGateway(t, func(c *appConfig) {
		c.InternalToken = tlsTestToken
		c.InternalTLSAddr = "127.0.0.1:0"
		c.InternalTLSCertFile, c.InternalTLSKeyFile, c.InternalTLSClientCAFile = f.certFile, f.keyFile, clientCAFile
		c.InternalTLSAllowNoClientCert = !withClientCA
	})
	s, err := f.tg.gateway.newInternalTLSServer()
	must(t, err)
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	must(t, err)
	go func() { _ = s.Serve(tls.NewListener(ln, s.TLSConfig)) }()
	t.Cleanup(func() { _ = s.Close() })
	f.url = "https://" + ln.Addr().String()
	return f
}

func must(t *testing.T, err error) {
	t.Helper()
	if err != nil {
		t.Fatal(err)
	}
}

func (f *tlsFixture) client(trust *testCA, clientCert *tls.Certificate, maxVer uint16) *http.Client {
	pool := x509.NewCertPool()
	pool.AddCert(trust.cert)
	tc := &tls.Config{RootCAs: pool, MaxVersion: maxVer}
	if clientCert != nil {
		tc.Certificates = []tls.Certificate{*clientCert}
	}
	return &http.Client{Transport: &http.Transport{TLSClientConfig: tc}, Timeout: 5 * time.Second}
}

func get(c *http.Client, url string, auth bool) (int, error) {
	req, _ := http.NewRequest(http.MethodGet, url, nil)
	if auth {
		req.Header.Set("Authorization", "Bearer "+tlsTestToken)
	}
	resp, err := c.Do(req)
	if err != nil {
		return 0, err
	}
	defer resp.Body.Close()
	return resp.StatusCode, nil
}

func clientPair(t *testing.T, ca *testCA) *tls.Certificate {
	t.Helper()
	c, k := ca.issue(t, "control", 3, false)
	p, err := tls.X509KeyPair(c, k)
	must(t, err)
	return &p
}

func TestInternalTLS_MutualTLS(t *testing.T) {
	f := newTLSFixture(t, true)
	good := clientPair(t, f.ca)

	if code, err := get(f.client(f.ca, good, 0), f.url+"/internal/v1/runners", true); err != nil || code != http.StatusOK {
		t.Fatalf("valid client+token: code=%d err=%v", code, err)
	}
	if code, err := get(f.client(f.ca, good, 0), f.url+"/internal/v1/runners", false); err != nil || code != http.StatusUnauthorized {
		t.Fatalf("no token: code=%d err=%v", code, err)
	}
	if _, err := get(f.client(f.ca, nil, 0), f.url+"/internal/v1/runners", true); err == nil {
		t.Fatal("client without a certificate was accepted")
	}
	other := newTestCA(t, "other-ca")
	if _, err := get(f.client(f.ca, clientPair(t, other), 0), f.url+"/internal/v1/runners", true); err == nil {
		t.Fatal("client certificate from another CA was accepted")
	}
	if _, err := get(f.client(f.ca, good, tls.VersionTLS12), f.url+"/internal/v1/runners", true); err == nil {
		t.Fatal("TLS 1.2 handshake succeeded")
	}
}

func TestInternalTLS_OnlyInternalRoutes(t *testing.T) {
	f := newTLSFixture(t, false)
	c := f.client(f.ca, nil, 0)
	if code, err := get(c, f.url+"/internal/v1/runners", true); err != nil || code != http.StatusOK {
		t.Fatalf("internal without client CA: code=%d err=%v", code, err)
	}
	if code, _ := get(c, f.url+"/healthz", false); code != http.StatusOK {
		t.Fatalf("healthz: %d", code)
	}
	for _, p := range []string{protocol.ConnectPath, "/metrics"} {
		if code, _ := get(c, f.url+p, true); code != http.StatusNotFound {
			t.Fatalf("%s on the TLS listener: %d, want 404", p, code)
		}
	}
	// The plain listener still serves the same route.
	if code, err := get(http.DefaultClient, f.tg.server.URL+"/internal/v1/runners", true); err != nil || code != http.StatusOK {
		t.Fatalf("plain listener: code=%d err=%v", code, err)
	}
}

func TestInternalTLS_ConfigErrors(t *testing.T) {
	g := newTestGateway(t, func(c *appConfig) { c.InternalTLSAddr = ":0"; c.InternalTLSAllowNoClientCert = true }).gateway
	if _, err := g.newInternalTLSServer(); err == nil || !strings.Contains(err.Error(), "CERT_FILE") {
		t.Fatalf("missing cert/key: %v", err)
	}
	off := newTestGateway(t, nil).gateway
	if s, err := off.newInternalTLSServer(); s != nil || err != nil {
		t.Fatalf("feature off: %v %v", s, err)
	}
}

func TestCertReloader_PicksUpRewrite(t *testing.T) {
	f := newTLSFixture(t, false)
	rl, err := newCertReloader(f.certFile, f.keyFile)
	must(t, err)
	now := time.Now()
	rl.now = func() time.Time { return now }
	first, _ := rl.GetCertificate(nil)

	cp, kp := f.ca.issue(t, "gateway2", 9, true)
	must(t, os.WriteFile(f.certFile, cp, 0o600))
	must(t, os.WriteFile(f.keyFile, kp, 0o600))
	future := time.Now().Add(time.Minute)
	must(t, os.Chtimes(f.certFile, future, future))

	if same, _ := rl.GetCertificate(nil); same != first {
		t.Fatal("reloaded before the 30 s check interval")
	}
	now = now.Add(31 * time.Second)
	if next, _ := rl.GetCertificate(nil); next == first {
		t.Fatal("rewritten certificate was not picked up")
	}
}

func TestPlainLocalOnly(t *testing.T) {
	_, lan, _ := net.ParseCIDR("172.20.0.0/16")
	tg := newTestGateway(t, func(c *appConfig) {
		c.InternalToken = tlsTestToken
		c.InternalPlainLocalOnly = true
	})
	tg.gateway.interfaceAddrs = func() ([]net.Addr, error) { return []net.Addr{lan}, nil }
	h := mux2(tg)
	do := func(remote, path string) *httptest.ResponseRecorder {
		req := httptest.NewRequest(http.MethodGet, path, nil)
		req.RemoteAddr = remote
		req.Header.Set("Authorization", "Bearer "+tlsTestToken)
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		return rec
	}
	if rec := do("203.0.113.9:5000", "/internal/v1/runners"); rec.Code != http.StatusForbidden ||
		rec.Header().Get(llmRelayErrorHeader) != "internal_tls_required" || !strings.Contains(rec.Body.String(), "internal_tls_required") {
		t.Fatalf("non-local peer: %d %s", rec.Code, rec.Body.String())
	}
	for _, peer := range []string{"172.20.5.4:1", "127.0.0.1:1", "[::1]:1"} {
		if rec := do(peer, "/internal/v1/runners"); rec.Code != http.StatusOK {
			t.Fatalf("local peer %s: %d", peer, rec.Code)
		}
	}
	if rec := do("203.0.113.9:5000", "/healthz"); rec.Code == http.StatusForbidden {
		t.Fatal("healthz must stay unaffected")
	}
}

func mux2(tg *testGateway) http.Handler {
	r := mux.NewRouter()
	tg.gateway.RegisterRoutes(r)
	return r
}

func TestValidateInternalTLSConfig(t *testing.T) {
	ok := appConfig{InternalTLSAddr: ":8443", InternalTLSCertFile: "c", InternalTLSKeyFile: "k", InternalTLSClientCAFile: "ca"}
	with := func(f func(*appConfig)) appConfig { c := ok; f(&c); return c }
	cases := []struct {
		name    string
		cfg     appConfig
		wantErr string
	}{
		{"all off", appConfig{}, ""},
		{"full mTLS", ok, ""},
		{"cert without addr", appConfig{InternalTLSCertFile: "c"}, "CERT_FILE"},
		{"key without addr", appConfig{InternalTLSKeyFile: "k"}, "KEY_FILE"},
		{"ca without addr", appConfig{InternalTLSClientCAFile: "ca"}, "CLIENT_CA_FILE"},
		{"addr without cert", with(func(c *appConfig) { c.InternalTLSCertFile = "" }), "both required"},
		{"addr without key", with(func(c *appConfig) { c.InternalTLSKeyFile = "" }), "both required"},
		{"no client CA", with(func(c *appConfig) { c.InternalTLSClientCAFile = "" }), "ALLOW_NO_CLIENT_CERT"},
		{"opt-out without addr", appConfig{InternalTLSAllowNoClientCert: true}, "ALLOW_NO_CLIENT_CERT"},
		{"opt-out contradicts client CA", with(func(c *appConfig) { c.InternalTLSAllowNoClientCert = true }), "contradicts"},
		{"no client CA opted out", with(func(c *appConfig) { c.InternalTLSClientCAFile = ""; c.InternalTLSAllowNoClientCert = true }), ""},
	}
	for _, tc := range cases {
		err := validateInternalTLSConfig(tc.cfg)
		if tc.wantErr == "" && err != nil || tc.wantErr != "" && (err == nil || !strings.Contains(err.Error(), tc.wantErr)) {
			t.Errorf("%s: err=%v want %q", tc.name, err, tc.wantErr)
		}
	}
}

func TestParseStrictBool(t *testing.T) {
	for v, want := range map[string]bool{"": false, "true": true, "TRUE": true, "1": true, "Yes": true, "false": false, "0": false, "NO": false, " true ": true} {
		if got, err := parseStrictBool("X", v); err != nil || got != want {
			t.Errorf("%q: %v %v", v, got, err)
		}
	}
	for _, v := range []string{"ture", "2", "on", "enabled"} {
		if _, err := parseStrictBool("X", v); err == nil {
			t.Errorf("%q accepted", v)
		}
	}
}

func TestLoadConfig_InternalTLSFatal(t *testing.T) {
	t.Setenv("RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY", "ture")
	if _, err := loadConfig(); err == nil {
		t.Fatal("bad boolean accepted")
	}
	t.Setenv("RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY", "")
	t.Setenv("RUNNER_GATEWAY_INTERNAL_TLS_CERT_FILE", "/x")
	if _, err := loadConfig(); err == nil {
		t.Fatal("stray cert file accepted")
	}
}

func TestInternalTLS_UnreadableFilesFailAtBoot(t *testing.T) {
	f := newTLSFixture(t, true)
	orig := f.tg.gateway.cfg
	missing := filepath.Join(t.TempDir(), "missing")
	bad := filepath.Join(t.TempDir(), "ca.pem")
	must(t, os.WriteFile(bad, []byte("junk"), 0o600))
	for name, mut := range map[string]func(*appConfig){
		"missing CA":   func(c *appConfig) { c.InternalTLSClientCAFile = missing },
		"missing cert": func(c *appConfig) { c.InternalTLSCertFile = missing },
		"non-PEM CA":   func(c *appConfig) { c.InternalTLSClientCAFile = bad },
	} {
		g := newTestGateway(t, func(c *appConfig) { *c = orig; mut(c) }).gateway
		if _, err := g.newInternalTLSServer(); err == nil {
			t.Fatalf("%s accepted at boot", name)
		}
	}
}

func TestInternalTLS_ClientCAReload(t *testing.T) {
	f := newTLSFixture(t, true)
	caFile := f.tg.gateway.cfg.InternalTLSClientCAFile
	now := time.Now()
	tlsClock = func() time.Time { return now }
	t.Cleanup(func() { tlsClock = time.Now })
	s, err := f.tg.gateway.newInternalTLSServer()
	must(t, err)
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	must(t, err)
	go func() { _ = s.Serve(tls.NewListener(ln, s.TLSConfig)) }()
	t.Cleanup(func() { _ = s.Close() })
	url := "https://" + ln.Addr().String() + "/internal/v1/runners"

	oldClient := clientPair(t, f.ca)
	if code, err := get(f.client(f.ca, oldClient, 0), url, true); err != nil || code != http.StatusOK {
		t.Fatalf("old CA client before rotation: %d %v", code, err)
	}

	// Corrupt replacement keeps the old pool.
	must(t, os.WriteFile(caFile, []byte("garbage"), 0o600))
	future := time.Now().Add(time.Minute)
	must(t, os.Chtimes(caFile, future, future))
	now = now.Add(31 * time.Second)
	if code, err := get(f.client(f.ca, oldClient, 0), url, true); err != nil || code != http.StatusOK {
		t.Fatalf("old CA client after corrupt replacement: %d %v", code, err)
	}

	// Real replacement: new CA accepted, old CA refused.
	newCA := newTestCA(t, "new-ca")
	must(t, os.WriteFile(caFile, newCA.pem, 0o600))
	future = future.Add(time.Minute)
	must(t, os.Chtimes(caFile, future, future))
	now = now.Add(31 * time.Second)
	if code, err := get(f.client(f.ca, clientPair(t, newCA), 0), url, true); err != nil || code != http.StatusOK {
		t.Fatalf("new CA client after rotation: %d %v", code, err)
	}
	if _, err := get(f.client(f.ca, oldClient, 0), url, true); err == nil {
		t.Fatal("old CA client accepted after rotation")
	}
}

func TestCertReloader_FailedReloadKeepsOldCert(t *testing.T) {
	f := newTLSFixture(t, false)
	rl, err := newCertReloader(f.certFile, f.keyFile)
	must(t, err)
	now := time.Now()
	rl.now = func() time.Time { return now }
	first, _ := rl.GetCertificate(nil)
	must(t, os.WriteFile(f.certFile, []byte("garbage"), 0o600))
	future := time.Now().Add(time.Minute)
	must(t, os.Chtimes(f.certFile, future, future))
	now = now.Add(31 * time.Second)
	if got, err := rl.GetCertificate(nil); err != nil || got != first {
		t.Fatalf("failed reload did not keep the old certificate: %v", err)
	}
}
