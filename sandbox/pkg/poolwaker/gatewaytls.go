package poolwaker

import (
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"
)

// GatewayComposeHost is the gateway's service name on its own compose network. A plain http://
// URL to it never leaves the host the gateway runs on.
const GatewayComposeHost = "runner-gateway"

// GatewayTLS is how the scaler reaches the gateway's internal API. Across hosts the link is the
// gateway's TLS 1.3 listener with a required client certificate (RUNNER_GATEWAY_INTERNAL_TLS_ADDR
// on the gateway); these are the client's half of that, named like the Inference API's
// Compute:GatewayCaFile / GatewayClientCertFile / GatewayClientKeyFile.
type GatewayTLS struct {
	// CAFile verifies the gateway's certificate (RUNNER_GATEWAY_CA_FILE). Required with an
	// https:// URL: there is no fallback to the system roots.
	CAFile string
	// ClientCertFile and ClientKeyFile are what the scaler presents
	// (RUNNER_GATEWAY_CLIENT_CERT_FILE, RUNNER_GATEWAY_CLIENT_KEY_FILE). Both are required with an
	// https:// URL.
	ClientCertFile string
	ClientKeyFile  string
	// AllowPlainHTTP lifts the refusal of a plain http:// URL to another host
	// (RUNNER_GATEWAY_ALLOW_PLAIN_HTTP=true). The internal token then crosses the network in clear.
	AllowPlainHTTP bool
}

// GatewayTLSFromEnv reads the four settings. It reads paths only, never a key.
func GatewayTLSFromEnv(getenv func(string) string) GatewayTLS {
	get := func(k string) string { return strings.TrimSpace(getenv(k)) }
	return GatewayTLS{
		CAFile:         get("RUNNER_GATEWAY_CA_FILE"),
		ClientCertFile: get("RUNNER_GATEWAY_CLIENT_CERT_FILE"),
		ClientKeyFile:  get("RUNNER_GATEWAY_CLIENT_KEY_FILE"),
		AllowPlainHTTP: strings.EqualFold(get("RUNNER_GATEWAY_ALLOW_PLAIN_HTTP"), "true"),
	}
}

func (t GatewayTLS) anyFile() bool {
	return t.CAFile != "" || t.ClientCertFile != "" || t.ClientKeyFile != ""
}

// plainHostAllowed reports whether a plain http:// URL to host stays on this machine: loopback,
// or the gateway's compose service name.
func plainHostAllowed(host string) bool {
	if host == GatewayComposeHost || strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

// NewGatewayClient builds the HTTP client for the gateway at baseURL, or says why the settings
// cannot be used. It refuses:
//   - a plain http:// URL to anything but loopback or the compose service name, unless
//     AllowPlainHTTP is set;
//   - TLS files together with a plain http:// URL (they would be silently unused);
//   - a client certificate without its key, or the reverse;
//   - an https:// URL without CAFile (the link uses a private CA; the system roots are never used);
//   - an https:// URL without the client certificate and key (the gateway requires them);
//   - files that are missing or do not parse.
//
// An https:// URL always negotiates TLS 1.3 or newer. Error messages name settings and paths,
// never file content.
func NewGatewayClient(baseURL string, t GatewayTLS) (*http.Client, error) {
	u, err := url.Parse(strings.TrimSpace(baseURL))
	if err != nil {
		return nil, errors.New("RUNNER_GATEWAY_URL is not a valid URL")
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return nil, fmt.Errorf("RUNNER_GATEWAY_URL must start with http:// or https:// (got scheme %q)", u.Scheme)
	}
	if u.Hostname() == "" {
		return nil, errors.New("RUNNER_GATEWAY_URL has no host")
	}

	transport := http.DefaultTransport.(*http.Transport).Clone()
	// The internal API is a direct private link: never send the internal token through a proxy
	// picked up from the environment.
	transport.Proxy = nil
	client := &http.Client{Timeout: 30 * time.Second, Transport: transport}

	if u.Scheme == "http" {
		if t.anyFile() {
			return nil, errors.New("RUNNER_GATEWAY_CA_FILE / RUNNER_GATEWAY_CLIENT_CERT_FILE / RUNNER_GATEWAY_CLIENT_KEY_FILE " +
				"are set but RUNNER_GATEWAY_URL is plain http://: use the gateway's https:// internal TLS address, or unset the files")
		}
		if !plainHostAllowed(u.Hostname()) && !t.AllowPlainHTTP {
			return nil, fmt.Errorf("RUNNER_GATEWAY_URL is plain http:// to %q, which would send the internal token across the "+
				"network in clear. Use the gateway's https:// internal TLS address with RUNNER_GATEWAY_CA_FILE, "+
				"RUNNER_GATEWAY_CLIENT_CERT_FILE and RUNNER_GATEWAY_CLIENT_KEY_FILE, or set RUNNER_GATEWAY_ALLOW_PLAIN_HTTP=true "+
				"to accept that", u.Hostname())
		}
		return client, nil
	}

	if (t.ClientCertFile == "") != (t.ClientKeyFile == "") {
		return nil, errors.New("RUNNER_GATEWAY_CLIENT_CERT_FILE and RUNNER_GATEWAY_CLIENT_KEY_FILE must be set together")
	}
	if t.CAFile == "" {
		return nil, errors.New("RUNNER_GATEWAY_CA_FILE is required with an https:// RUNNER_GATEWAY_URL: " +
			"the gateway's certificate is checked against its private CA, never the system roots")
	}
	cfg := &tls.Config{MinVersion: tls.VersionTLS13}
	pemBytes, err := os.ReadFile(t.CAFile)
	if err != nil {
		return nil, fmt.Errorf("RUNNER_GATEWAY_CA_FILE %s is not readable", t.CAFile)
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(pemBytes) {
		return nil, fmt.Errorf("RUNNER_GATEWAY_CA_FILE %s holds no PEM certificate", t.CAFile)
	}
	cfg.RootCAs = pool
	if t.ClientCertFile == "" {
		return nil, errors.New("RUNNER_GATEWAY_CLIENT_CERT_FILE and RUNNER_GATEWAY_CLIENT_KEY_FILE are required with an " +
			"https:// RUNNER_GATEWAY_URL: the gateway's internal TLS listener requires a client certificate")
	}
	cert, err := tls.LoadX509KeyPair(t.ClientCertFile, t.ClientKeyFile)
	if err != nil {
		// The cause can quote nothing secret (a path or a parse failure), but keep it generic anyway.
		return nil, fmt.Errorf("the gateway client certificate (%s) and key (%s) could not be loaded: "+
			"missing, unreadable by this user, or not a matching pair", t.ClientCertFile, t.ClientKeyFile)
	}
	cfg.Certificates = []tls.Certificate{cert}
	transport.TLSClientConfig = cfg
	return client, nil
}
