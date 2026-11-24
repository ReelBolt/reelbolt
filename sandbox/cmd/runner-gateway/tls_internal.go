package main

import (
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"sync"
	"time"

	"github.com/gorilla/mux"
)

// certReloadCheck is the most often the certificate files are stat'ed.
const certReloadCheck = 30 * time.Second

// certReloader serves a certificate/key pair and re-reads it when either file's
// mtime changes, checking at most every certReloadCheck, so rotation needs no
// restart. A failed reload keeps the previous certificate.
type certReloader struct {
	certFile, keyFile string
	now               func() time.Time

	mu        sync.Mutex
	cert      *tls.Certificate
	certMod   time.Time
	keyMod    time.Time
	lastCheck time.Time
}

func newCertReloader(certFile, keyFile string) (*certReloader, error) {
	r := &certReloader{certFile: certFile, keyFile: keyFile, now: time.Now}
	if err := r.load(); err != nil {
		return nil, err
	}
	r.lastCheck = r.now()
	return r, nil
}

func (r *certReloader) load() error {
	cs, err := os.Stat(r.certFile)
	if err != nil {
		return fmt.Errorf("stat certificate file: %w", err)
	}
	ks, err := os.Stat(r.keyFile)
	if err != nil {
		return fmt.Errorf("stat key file: %w", err)
	}
	c, err := tls.LoadX509KeyPair(r.certFile, r.keyFile)
	if err != nil {
		return fmt.Errorf("load certificate/key pair: %w", err)
	}
	r.cert, r.certMod, r.keyMod = &c, cs.ModTime(), ks.ModTime()
	return nil
}

// GetCertificate is the tls.Config hook.
func (r *certReloader) GetCertificate(*tls.ClientHelloInfo) (*tls.Certificate, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if now := r.now(); now.Sub(r.lastCheck) >= certReloadCheck {
		r.lastCheck = now
		cs, cerr := os.Stat(r.certFile)
		ks, kerr := os.Stat(r.keyFile)
		if cerr == nil && kerr == nil && (!cs.ModTime().Equal(r.certMod) || !ks.ModTime().Equal(r.keyMod)) {
			_ = r.load() // keep the old pair on a half-written rotation; the next check retries
		}
	}
	return r.cert, nil
}

// tlsClock is the CA reloader clock; tests replace it.
var tlsClock = time.Now

// caReloader serves the client CA pool and re-reads the bundle when its mtime
// changes, checking at most every certReloadCheck. A failed reload (unreadable,
// no PEM certificate) keeps the previous pool.
type caReloader struct {
	file string
	now  func() time.Time

	mu        sync.Mutex
	p         *x509.CertPool
	mod       time.Time
	lastCheck time.Time
}

func newCAReloader(file string) (*caReloader, error) {
	r := &caReloader{file: file, now: func() time.Time { return tlsClock() }}
	if err := r.load(); err != nil {
		return nil, err
	}
	r.lastCheck = r.now()
	return r, nil
}

func (r *caReloader) load() error {
	st, err := os.Stat(r.file)
	if err != nil {
		return fmt.Errorf("stat client CA file: %w", err)
	}
	pem, err := os.ReadFile(r.file)
	if err != nil {
		return fmt.Errorf("read client CA file: %w", err)
	}
	p := x509.NewCertPool()
	if !p.AppendCertsFromPEM(pem) {
		return errors.New("client CA file holds no PEM certificate")
	}
	r.p, r.mod = p, st.ModTime()
	return nil
}

func (r *caReloader) pool() *x509.CertPool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if now := r.now(); now.Sub(r.lastCheck) >= certReloadCheck {
		r.lastCheck = now
		if st, err := os.Stat(r.file); err == nil && !st.ModTime().Equal(r.mod) {
			_ = r.load() // keep the old pool on a bad replacement; the next check retries
		}
	}
	return r.p
}

// newInternalTLSServer builds the TLS listener's server, or nil when
// RUNNER_GATEWAY_INTERNAL_TLS_ADDR is empty. It serves only /internal/** and a
// bare /healthz, TLS 1.3 minimum; with a client CA it requires a verified client
// certificate. There is no WriteTimeout, so SSE streams are not cut off.
func (g *gateway) newInternalTLSServer() (*http.Server, error) {
	if g.cfg.InternalTLSAddr == "" {
		return nil, nil
	}
	if err := validateInternalTLSConfig(g.cfg); err != nil {
		return nil, err
	}
	rl, err := newCertReloader(g.cfg.InternalTLSCertFile, g.cfg.InternalTLSKeyFile)
	if err != nil {
		return nil, err
	}
	tc := &tls.Config{MinVersion: tls.VersionTLS13, GetCertificate: rl.GetCertificate}
	if g.cfg.InternalTLSClientCAFile != "" {
		cr, err := newCAReloader(g.cfg.InternalTLSClientCAFile)
		if err != nil {
			return nil, err
		}
		tc.ClientAuth = tls.RequireAndVerifyClientCert
		tc.ClientCAs = cr.pool() // initial value; per-handshake config below carries the fresh pool
		tc.GetConfigForClient = func(*tls.ClientHelloInfo) (*tls.Config, error) {
			c := tc.Clone()
			c.GetConfigForClient = nil
			c.ClientCAs = cr.pool()
			return c, nil
		}
	} else {
		log.Printf("WARNING: internal TLS listener %s has NO client-certificate verification "+
			"(RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT=true): any holder of the internal token can connect",
			g.cfg.InternalTLSAddr)
	}
	router := mux.NewRouter()
	router.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	}).Methods(http.MethodGet)
	g.registerInternalRoutes(router, false)
	return &http.Server{
		Addr:      g.cfg.InternalTLSAddr,
		Handler:   router,
		TLSConfig: tc,
		// No WriteTimeout: relay responses stream (SSE) and long-polls run up to
		// maxWait; a write deadline would cut them off.
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       2 * time.Minute,
	}, nil
}

// requireLocalPeer admits /internal/** on the plain listener only from loopback
// or a subnet of one of the gateway's own interfaces. That set includes the
// Docker bridge subnet and its gateway address (the host). It relies on default
// rootful Docker (iptables on) preserving the caller's real source address; with
// docker-proxy, rootless Docker or iptables=false remote peers can appear local.
// Peer replicas on another node are refused: use the TLS listener for them or
// turn RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY off. The token is the real control.
func (g *gateway) requireLocalPeer(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !g.peerIsLocal(r.RemoteAddr) {
			writeLLMError(w, http.StatusForbidden, "internal_tls_required",
				"the internal API on the plain listener is limited to the local network; use the TLS listener")
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (g *gateway) peerIsLocal(remote string) bool {
	host, _, err := net.SplitHostPort(remote)
	if err != nil {
		host = remote
	}
	ip := net.ParseIP(host)
	if ip == nil {
		return false
	}
	if ip.IsLoopback() {
		return true
	}
	lookup := g.interfaceAddrs
	if lookup == nil {
		lookup = net.InterfaceAddrs
	}
	addrs, err := lookup()
	if err != nil {
		return false
	}
	for _, a := range addrs {
		if n, ok := a.(*net.IPNet); ok && n.Contains(ip) {
			return true
		}
	}
	return false
}
