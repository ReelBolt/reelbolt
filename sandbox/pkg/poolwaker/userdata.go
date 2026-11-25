package poolwaker

import (
	_ "embed"
	"fmt"
	"net"
	"net/url"
	"strings"
)

// GatewayEndpoint validates the URL a node dials and returns its host and port
// (default 443 for wss, 80 for ws). The scheme must be ws or wss; plain ws is
// accepted only when the host is a private (RFC 1918) or loopback IP literal,
// because that traffic never leaves the private network.
func GatewayEndpoint(raw string) (host, port string, err error) {
	u, perr := url.Parse(raw)
	if perr != nil || u.Host == "" {
		return "", "", fmt.Errorf("gateway URL %q is not a valid URL with a host", raw)
	}
	host, port = u.Hostname(), u.Port()
	switch strings.ToLower(u.Scheme) {
	case "wss":
		if port == "" {
			port = "443"
		}
	case "ws":
		ip := net.ParseIP(host)
		if ip == nil || ip.To4() == nil || (!ip.IsLoopback() && !ip.IsPrivate()) {
			return "", "", fmt.Errorf("gateway URL %q: plain ws:// is only allowed for a private or loopback IPv4 address; use wss://", raw)
		}
		if port == "" {
			port = "80"
		}
	default:
		return "", "", fmt.Errorf("gateway URL %q: scheme must be ws or wss", raw)
	}
	return host, port, nil
}

// UserDataParams is everything the cloud-init document carries. Token is the
// only secret and is short-lived and bound to Name.
type UserDataParams struct {
	GatewayURL string
	Name       string
	Token      string
	CPULimit   string
}

//go:embed cloud-init.yaml
var userDataTmpl string

// The document (systemd units, 6 h max-lifetime timer, env file) is cloud-init.yaml in this
// package, the single source of truth; the infra kit test checks it too.
//
// RenderUserData renders the node's cloud-init. SANDBOX_IMAGE comes from the
// baked image's /etc/reelbolt/node-image.env, not from here.
func RenderUserData(p UserDataParams) (string, error) {
	for k, v := range map[string]string{
		"gateway url": p.GatewayURL, "name": p.Name, "token": p.Token, "cpu limit": p.CPULimit,
	} {
		if v == "" {
			return "", fmt.Errorf("user-data: %s is empty", k)
		}
		if strings.ContainsAny(v, " \t\r\n\"'$`\\") {
			return "", fmt.Errorf("user-data: %s contains characters that are not allowed", k)
		}
	}
	gwHost, gwPort, err := GatewayEndpoint(p.GatewayURL)
	if err != nil {
		return "", fmt.Errorf("user-data: %w", err)
	}
	return strings.NewReplacer(
		"${gateway_url}", p.GatewayURL,
		"${gateway_host}", gwHost,
		"${gateway_port}", gwPort,
		"${worker_name}", p.Name,
		"${node_token}", p.Token,
		"${cpu_limit}", p.CPULimit,
	).Replace(userDataTmpl), nil
}
