package poolwaker

import (
	"context"
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
)

// testCA is a throwaway certificate authority for these tests only.
type testCA struct {
	cert *x509.Certificate
	key  *ecdsa.PrivateKey
	pem  []byte
}

func newTestCA(t *testing.T, name string) testCA {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tmpl := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: name},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(time.Hour),
		IsCA:                  true,
		BasicConstraintsValid: true,
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature,
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	return testCA{cert: cert, key: key, pem: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})}
}

// leaf signs a server or client certificate and returns the certificate and key as PEM.
func (ca testCA) leaf(t *testing.T, name string, usage x509.ExtKeyUsage) (certPEM, keyPEM []byte) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tmpl := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: name},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{usage},
	}
	if usage == x509.ExtKeyUsageServerAuth {
		tmpl.IPAddresses = []net.IP{net.ParseIP("127.0.0.1")}
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, ca.cert, &key.PublicKey, ca.key)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})
}

func writeFile(t *testing.T, dir, name string, data []byte) string {
	t.Helper()
	p := filepath.Join(dir, name)
	if err := os.WriteFile(p, data, 0o600); err != nil {
		t.Fatal(err)
	}
	return p
}

// mtlsGateway is a stand-in gateway: TLS, client certificate required, signed by ca.
func mtlsGateway(t *testing.T, ca testCA, maxVersion uint16) *httptest.Server {
	t.Helper()
	certPEM, keyPEM := ca.leaf(t, "runner-gateway-internal", x509.ExtKeyUsageServerAuth)
	serverCert, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		t.Fatal(err)
	}
	clientCAs := x509.NewCertPool()
	clientCAs.AddCert(ca.cert)
	srv := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer fake-internal-token" {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		_, _ = w.Write([]byte(`{"runners":[{"name":"n1","kind":"CloudPool","running":0}]}`))
	}))
	srv.TLS = &tls.Config{
		Certificates: []tls.Certificate{serverCert},
		ClientAuth:   tls.RequireAndVerifyClientCert,
		ClientCAs:    clientCAs,
		MinVersion:   tls.VersionTLS12,
		MaxVersion:   maxVersion,
	}
	srv.StartTLS()
	t.Cleanup(srv.Close)
	return srv
}

func runners(t *testing.T, url string, cfg GatewayTLS) error {
	t.Helper()
	client, err := NewGatewayClient(url, cfg)
	if err != nil {
		t.Fatalf("client for %s: %v", url, err)
	}
	gw := &HTTPGateway{BaseURL: url, Token: "fake-internal-token", Client: client}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	got, err := gw.Runners(ctx)
	if err == nil && (len(got) != 1 || got[0].Name != "n1") {
		t.Fatalf("unexpected runners: %+v", got)
	}
	return err
}

func TestGatewayClientMutualTLS(t *testing.T) {
	ca := newTestCA(t, "test gateway CA")
	dir := t.TempDir()
	caFile := writeFile(t, dir, "ca.pem", ca.pem)
	certPEM, keyPEM := ca.leaf(t, "reelbolt-pool-waker", x509.ExtKeyUsageClientAuth)
	certFile := writeFile(t, dir, "client.pem", certPEM)
	keyFile := writeFile(t, dir, "client-key.pem", keyPEM)
	srv := mtlsGateway(t, ca, tls.VersionTLS13)

	t.Run("success", func(t *testing.T) {
		if err := runners(t, srv.URL, GatewayTLS{CAFile: caFile, ClientCertFile: certFile, ClientKeyFile: keyFile}); err != nil {
			t.Fatalf("mutual TLS call failed: %v", err)
		}
	})

	t.Run("wrong CA", func(t *testing.T) {
		other := newTestCA(t, "some other CA")
		otherFile := writeFile(t, dir, "other-ca.pem", other.pem)
		err := runners(t, srv.URL, GatewayTLS{CAFile: otherFile, ClientCertFile: certFile, ClientKeyFile: keyFile})
		if err == nil || !strings.Contains(err.Error(), "certificate") {
			t.Fatalf("a server signed by another CA must be refused, got %v", err)
		}
	})

	t.Run("client certificate from another CA", func(t *testing.T) {
		other := newTestCA(t, "some other CA")
		c, k := other.leaf(t, "intruder", x509.ExtKeyUsageClientAuth)
		err := runners(t, srv.URL, GatewayTLS{CAFile: caFile,
			ClientCertFile: writeFile(t, dir, "bad.pem", c), ClientKeyFile: writeFile(t, dir, "bad-key.pem", k)})
		if err == nil {
			t.Fatal("the gateway must refuse a client certificate it did not sign")
		}
	})

	t.Run("no client certificate", func(t *testing.T) {
		// NewGatewayClient refuses this configuration at start; strip the certificate from a
		// valid client to show the gateway refuses it on the wire as well.
		client, err := NewGatewayClient(srv.URL, GatewayTLS{CAFile: caFile, ClientCertFile: certFile, ClientKeyFile: keyFile})
		if err != nil {
			t.Fatal(err)
		}
		client.Transport.(*http.Transport).TLSClientConfig.Certificates = nil
		gw := &HTTPGateway{BaseURL: srv.URL, Token: "fake-internal-token", Client: client}
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if _, err := gw.Runners(ctx); err == nil {
			t.Fatal("the gateway must refuse a client with no certificate")
		}
	})

	t.Run("TLS 1.2 server refused", func(t *testing.T) {
		old := mtlsGateway(t, ca, tls.VersionTLS12)
		err := runners(t, old.URL, GatewayTLS{CAFile: caFile, ClientCertFile: certFile, ClientKeyFile: keyFile})
		if err == nil {
			t.Fatal("a server that only speaks TLS 1.2 must be refused")
		}
	})
}

func TestGatewayClientPlainHTTP(t *testing.T) {
	for _, url := range []string{"http://127.0.0.1:8090", "http://localhost:8090", "http://[::1]:8090", "http://runner-gateway:8090"} {
		if _, err := NewGatewayClient(url, GatewayTLS{}); err != nil {
			t.Errorf("%s must be allowed over plain HTTP: %v", url, err)
		}
	}
	for _, url := range []string{"http://10.0.0.3:8090", "http://gateway.example.test:8090", "http://runner-gateway.example.test"} {
		_, err := NewGatewayClient(url, GatewayTLS{})
		if err == nil || !strings.Contains(err.Error(), "RUNNER_GATEWAY_ALLOW_PLAIN_HTTP") {
			t.Errorf("%s must be refused over plain HTTP, got %v", url, err)
		}
		if _, err := NewGatewayClient(url, GatewayTLS{AllowPlainHTTP: true}); err != nil {
			t.Errorf("%s with the opt-out must be allowed: %v", url, err)
		}
	}
}

func TestGatewayClientRefusesInconsistentSettings(t *testing.T) {
	ca := newTestCA(t, "test gateway CA")
	dir := t.TempDir()
	caFile := writeFile(t, dir, "ca.pem", ca.pem)
	certPEM, keyPEM := ca.leaf(t, "reelbolt-pool-waker", x509.ExtKeyUsageClientAuth)
	certFile := writeFile(t, dir, "client.pem", certPEM)
	keyFile := writeFile(t, dir, "client-key.pem", keyPEM)
	const tlsURL = "https://10.0.0.3:8443"

	cases := []struct {
		name string
		url  string
		cfg  GatewayTLS
		want string
	}{
		{"certificate without key", tlsURL, GatewayTLS{CAFile: caFile, ClientCertFile: certFile}, "together"},
		{"key without certificate", tlsURL, GatewayTLS{CAFile: caFile, ClientKeyFile: keyFile}, "together"},
		{"certificate without CA", tlsURL, GatewayTLS{ClientCertFile: certFile, ClientKeyFile: keyFile}, "RUNNER_GATEWAY_CA_FILE"},
		{"TLS files with a plain URL", "http://runner-gateway:8090", GatewayTLS{CAFile: caFile, ClientCertFile: certFile, ClientKeyFile: keyFile}, "https"},
		{"TLS files with a plain URL and the opt-out", "http://10.0.0.3:8090", GatewayTLS{CAFile: caFile, AllowPlainHTTP: true}, "https"},
		{"missing CA file", tlsURL, GatewayTLS{CAFile: filepath.Join(dir, "absent.pem")}, "RUNNER_GATEWAY_CA_FILE"},
		{"CA file with no certificate", tlsURL, GatewayTLS{CAFile: writeFile(t, dir, "empty.pem", []byte("not a certificate"))}, "RUNNER_GATEWAY_CA_FILE"},
		{"key that does not match", tlsURL, GatewayTLS{CAFile: caFile, ClientCertFile: certFile, ClientKeyFile: caFile}, "client certificate"},
		{"https without any TLS file (no system-trust fallback)", tlsURL, GatewayTLS{}, "RUNNER_GATEWAY_CA_FILE"},
		{"https with the CA but no client certificate", tlsURL, GatewayTLS{CAFile: caFile}, "RUNNER_GATEWAY_CLIENT_CERT_FILE"},
		{"unsupported scheme", "ftp://10.0.0.3", GatewayTLS{}, "http"},
		{"no host", "https://", GatewayTLS{}, "host"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			_, err := NewGatewayClient(c.url, c.cfg)
			if err == nil || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("want an error naming %q, got %v", c.want, err)
			}
		})
	}
}

func TestGatewayTLSFromEnv(t *testing.T) {
	env := map[string]string{
		"RUNNER_GATEWAY_CA_FILE":          " /run/gateway-tls/ca.pem ",
		"RUNNER_GATEWAY_CLIENT_CERT_FILE": "/run/gateway-tls/client.pem",
		"RUNNER_GATEWAY_CLIENT_KEY_FILE":  "/run/gateway-tls/client-key.pem",
		"RUNNER_GATEWAY_ALLOW_PLAIN_HTTP": "TRUE",
	}
	got := GatewayTLSFromEnv(func(k string) string { return env[k] })
	want := GatewayTLS{CAFile: "/run/gateway-tls/ca.pem", ClientCertFile: "/run/gateway-tls/client.pem",
		ClientKeyFile: "/run/gateway-tls/client-key.pem", AllowPlainHTTP: true}
	if got != want {
		t.Fatalf("got %+v, want %+v", got, want)
	}
	if (GatewayTLSFromEnv(func(string) string { return "" }) != GatewayTLS{}) {
		t.Fatal("an empty environment must give the zero value")
	}
}
