package poolwaker

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// Demand is the gateway's demand signal reduced to what the loop needs.
type Demand struct {
	// DesiredNodes is the node count the gateway's slot demand translates to
	// (before the waker's own hard cap).
	DesiredNodes int
	// RunningNodes is the node count needed to hold the jobs running now.
	RunningNodes int
	// Queued is every job waiting for a pool worker, not only those with status
	// Queued: a requeued or retried job counts, and so does a job leased to a
	// worker that is gone. Running jobs a worker holds do not.
	Queued  int64
	Running int64
}

// Runner is one live worker connection as far as the loop cares.
type Runner struct {
	Name    string `json:"name"`
	Kind    string `json:"kind"`
	Running int    `json:"running"`
}

// Heartbeat is the body of PUT /internal/v1/pool/waker.
type Heartbeat struct {
	Healthy       bool    `json:"healthy"`
	Reason        string  `json:"reason"`
	Nodes         int     `json:"nodes"`
	MaxNodes      int     `json:"maxNodes"`
	Capped        bool    `json:"capped"`
	SpendTodayEUR float64 `json:"spendTodayEur"`
	Instance      string  `json:"instance"`
}

// NodeToken is a freshly minted node credential. Never log it.
type NodeToken struct {
	Name      string `json:"name"`
	Token     string `json:"token"`
	ExpiresAt string `json:"expiresAt"`
}

// Gateway is the whole gateway surface the loop uses.
type Gateway interface {
	Demand(ctx context.Context) (Demand, error)
	Runners(ctx context.Context) ([]Runner, error)
	MintNode(ctx context.Context, name string, ttl time.Duration) (NodeToken, error)
	RevokeNode(ctx context.Context, name string) error
	Heartbeat(ctx context.Context, hb Heartbeat) error
}

// HTTPGateway talks to the runner-gateway internal API. Demand is supplied by
// the caller (the scaler command reuses its Decide logic) through DemandFn.
type HTTPGateway struct {
	BaseURL  string
	Token    string
	Client   *http.Client
	DemandFn func(ctx context.Context) (Demand, error)
}

func (g *HTTPGateway) client() *http.Client {
	if g.Client != nil {
		return g.Client
	}
	return &http.Client{Timeout: 30 * time.Second}
}

func (g *HTTPGateway) do(ctx context.Context, method, path string, in, out any) error {
	var body io.Reader
	if in != nil {
		b, err := json.Marshal(in)
		if err != nil {
			return err
		}
		body = bytes.NewReader(b)
	}
	req, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(g.BaseURL, "/")+path, body)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+g.Token)
	if in != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := g.client().Do(req)
	if err != nil {
		// The URL carries no secret; the token is only in the header.
		return fmt.Errorf("%s %s: %w", method, path, err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, 4<<20))
	if err != nil {
		return fmt.Errorf("%s %s: %w", method, path, err)
	}
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		msg := strings.TrimSpace(string(data))
		if len(msg) > 200 {
			msg = msg[:200] + "..."
		}
		return fmt.Errorf("%s %s: HTTP %d: %s", method, path, resp.StatusCode, msg)
	}
	if out != nil && len(data) > 0 {
		if err := json.Unmarshal(data, out); err != nil {
			return fmt.Errorf("decode %s: %w", path, err)
		}
	}
	return nil
}

// Demand implements Gateway.
func (g *HTTPGateway) Demand(ctx context.Context) (Demand, error) {
	if g.DemandFn == nil {
		return Demand{}, fmt.Errorf("no demand source configured")
	}
	return g.DemandFn(ctx)
}

// Runners implements Gateway (GET /internal/v1/runners).
func (g *HTTPGateway) Runners(ctx context.Context) ([]Runner, error) {
	var out struct {
		Runners []Runner `json:"runners"`
	}
	if err := g.do(ctx, http.MethodGet, "/internal/v1/runners", nil, &out); err != nil {
		return nil, err
	}
	return out.Runners, nil
}

// MintNode implements Gateway (POST /internal/v1/pool/nodes).
func (g *HTTPGateway) MintNode(ctx context.Context, name string, ttl time.Duration) (NodeToken, error) {
	var out NodeToken
	err := g.do(ctx, http.MethodPost, "/internal/v1/pool/nodes",
		map[string]any{"name": name, "ttlSeconds": int(ttl.Seconds())}, &out)
	if err == nil && out.Token == "" {
		err = fmt.Errorf("gateway returned no token for %s", name)
	}
	return out, err
}

// RevokeNode implements Gateway (DELETE /internal/v1/pool/nodes/{name}).
func (g *HTTPGateway) RevokeNode(ctx context.Context, name string) error {
	return g.do(ctx, http.MethodDelete, "/internal/v1/pool/nodes/"+name, nil, nil)
}

// Heartbeat implements Gateway (PUT /internal/v1/pool/waker).
func (g *HTTPGateway) Heartbeat(ctx context.Context, hb Heartbeat) error {
	return g.do(ctx, http.MethodPut, "/internal/v1/pool/waker", hb, nil)
}
