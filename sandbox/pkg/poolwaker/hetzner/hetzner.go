// Package hetzner implements poolwaker.Provider over the Hetzner Cloud REST API
// v1 with plain net/http. It never logs or returns the token.
package hetzner

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/rand/v2"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/poolwaker"
)

// DefaultBaseURL is the Hetzner Cloud API root.
const DefaultBaseURL = "https://api.hetzner.cloud/v1"

// Config configures the provider.
type Config struct {
	Token      Token
	Instance   string // required; the only instance label this provider touches
	BaseURL    string // default DefaultBaseURL; non-https only for loopback hosts
	SnapshotID string // image id of the baked node snapshot; empty = the newest labelled snapshot (see LatestSnapshot)
	// LatestSnapshot makes Create use the newest available snapshot labelled
	// reelbolt-pool-image=1 instead of a pinned SnapshotID. The choice is cached for
	// SnapshotCacheTTL (default 5 min).
	LatestSnapshot   bool
	SnapshotCacheTTL time.Duration
	ServerType       string // default for prices lookups when a spec has none
	Location         string
	Network          string // optional network id; requires NetworkSubnet
	// NetworkSubnet is the dedicated node subnet inside Network (CIDR, a private range, at most
	// a /28). Nodes are attached with an address chosen from it so host firewall rules can isolate
	// them; Hetzner is never left to pick the subnet.
	NetworkSubnet string
	Firewall      string   // optional firewall id (should allow no inbound)
	SSHKeys       []string // nil = field omitted; non-nil (even empty) = sent as given
	HTTPClient    *http.Client
	MaxRetries    int // 429 retries, default 3
	// Sleep is replaced in tests.
	Sleep func(time.Duration)
}

// Provider is the Hetzner implementation of poolwaker.Provider.
type Provider struct {
	cfg    Config
	subnet *net.IPNet
	base   string
	client *http.Client
	sleep  func(time.Duration)

	mu     sync.Mutex
	prices map[string]float64

	snapID string
	snapAt time.Time
	now    func() time.Time
}

var _ poolwaker.Provider = (*Provider)(nil)

// New validates cfg and returns a provider. Wiring note: the program lead
// registers New (or Factory) with the poolwaker factory registry.
func New(cfg Config) (*Provider, error) {
	if cfg.Token.Empty() {
		return nil, errors.New("hetzner: token is required")
	}
	if !instanceRE.MatchString(cfg.Instance) {
		return nil, errors.New("hetzner: instance is required and must match [a-z0-9][a-z0-9-]{0,39}")
	}
	if cfg.SnapshotID == "" && !cfg.LatestSnapshot {
		return nil, errors.New("hetzner: snapshot id is required (or set LatestSnapshot to use the newest labelled snapshot)")
	}
	base := strings.TrimRight(cfg.BaseURL, "/")
	if base == "" {
		base = DefaultBaseURL
	}
	u, err := url.Parse(base)
	if err != nil || u.Host == "" {
		return nil, errors.New("hetzner: invalid base URL")
	}
	if u.Scheme != "https" {
		ip := net.ParseIP(u.Hostname())
		if u.Scheme != "http" || (u.Hostname() != "localhost" && (ip == nil || !ip.IsLoopback())) {
			return nil, errors.New("hetzner: base URL must be https unless the host is loopback")
		}
	}
	if cfg.Network != "" && cfg.NetworkSubnet == "" {
		return nil, errors.New("hetzner: a network needs a dedicated node subnet (set NetworkSubnet / HCLOUD_NETWORK_SUBNET): a node outside its own subnet cannot be isolated")
	}
	var subnet *net.IPNet
	if cfg.NetworkSubnet != "" {
		if cfg.Network == "" {
			return nil, errors.New("hetzner: a node subnet needs a network (HCLOUD_NETWORK)")
		}
		sn, err := ValidateSubnet(cfg.NetworkSubnet)
		if err != nil {
			return nil, err
		}
		subnet = sn
	}
	p := &Provider{cfg: cfg, subnet: subnet, base: base, client: cfg.HTTPClient, sleep: cfg.Sleep, prices: map[string]float64{}, now: time.Now}
	if p.cfg.SnapshotCacheTTL <= 0 {
		p.cfg.SnapshotCacheTTL = 5 * time.Minute
	}
	if p.client == nil {
		// Never follow a redirect: the Authorization header must not travel to another URL.
		p.client = &http.Client{
			Timeout:       30 * time.Second,
			CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
		}
	}
	if p.sleep == nil {
		p.sleep = time.Sleep
	}
	if p.cfg.MaxRetries <= 0 {
		p.cfg.MaxRetries = 3
	}
	return p, nil
}

// Factory builds a poolwaker.Provider from a Config.
func Factory(cfg Config) (poolwaker.Provider, error) { return New(cfg) }

func (p *Provider) Name() string { return "hetzner" }

// apiError carries status and the API's own error code/message, nothing else.
type apiError struct {
	Status int
	Code   string
	Msg    string
}

func (e *apiError) Error() string {
	return fmt.Sprintf("hetzner: API status %d code=%q message=%q", e.Status, e.Code, e.Msg)
}

// NamePrefix is required on every server this provider creates, lists or deletes.
const NamePrefix = "rb-pool-"

var instanceRE = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{0,39}$`)

// ErrNotOurs is returned by Delete when the server is not one this provider created.
var ErrNotOurs = errors.New("hetzner: server is not managed by this pool (labels/instance/name mismatch)")

func (p *Provider) ours(s server) bool {
	return s.Labels[poolwaker.LabelPool] == poolwaker.LabelPoolValue &&
		s.Labels[poolwaker.LabelInstance] == p.cfg.Instance &&
		strings.HasPrefix(s.Name, NamePrefix)
}

const maxRetryWait = 30 * time.Second

// do performs one API call with bounded 429 backoff. It is the only place the
// Authorization header is set.
func (p *Provider) do(ctx context.Context, method, path string, body any, out any) (int, error) {
	var payload []byte
	if body != nil {
		b, err := json.Marshal(body)
		if err != nil {
			return 0, errors.New("hetzner: encode request failed")
		}
		payload = b
	}
	for attempt := 0; ; attempt++ {
		req, err := http.NewRequestWithContext(ctx, method, p.base+path, bytes.NewReader(payload))
		if err != nil {
			return 0, errors.New("hetzner: build request failed")
		}
		req.Header.Set("Authorization", "Bearer "+p.cfg.Token.reveal())
		req.Header.Set("Accept", "application/json")
		if payload != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		resp, err := p.client.Do(req)
		if err != nil {
			if ctx.Err() != nil {
				return 0, ctx.Err()
			}
			return 0, fmt.Errorf("hetzner: %s %s failed: transport error", method, path)
		}
		data, _ := io.ReadAll(io.LimitReader(resp.Body, 4<<20))
		_ = resp.Body.Close()
		if resp.StatusCode == http.StatusTooManyRequests && attempt < p.cfg.MaxRetries {
			wait := time.Duration(1<<attempt) * time.Second
			if s, err := strconv.Atoi(strings.TrimSpace(resp.Header.Get("Retry-After"))); err == nil && s >= 0 {
				wait = time.Duration(s) * time.Second
			}
			if wait > maxRetryWait {
				wait = maxRetryWait
			}
			p.sleep(wait)
			if ctx.Err() != nil {
				return 0, ctx.Err()
			}
			continue
		}
		if resp.StatusCode >= 300 {
			ae := &apiError{Status: resp.StatusCode}
			var e struct {
				Error struct{ Code, Message string } `json:"error"`
			}
			if json.Unmarshal(data, &e) == nil {
				ae.Code, ae.Msg = e.Error.Code, e.Error.Message
			}
			return resp.StatusCode, ae
		}
		if out != nil && len(data) > 0 {
			if err := json.Unmarshal(data, out); err != nil {
				return resp.StatusCode, errors.New("hetzner: decode response failed")
			}
		}
		return resp.StatusCode, nil
	}
}

type server struct {
	ID         int64             `json:"id"`
	Name       string            `json:"name"`
	Status     string            `json:"status"`
	Created    time.Time         `json:"created"`
	Labels     map[string]string `json:"labels"`
	PrivateNet []struct {
		IP string `json:"ip"`
	} `json:"private_net"`
	ServerType struct {
		Name string `json:"name"`
	} `json:"server_type"`
}

func (p *Provider) toNode(ctx context.Context, s server) poolwaker.Node {
	n := poolwaker.Node{
		ID:         strconv.FormatInt(s.ID, 10),
		Name:       s.Name,
		Instance:   s.Labels[poolwaker.LabelInstance],
		ServerType: s.ServerType.Name,
		Status:     mapStatus(s.Status),
		CreatedAt:  s.Created,
	}
	if v := s.Labels[poolwaker.LabelNode]; v != "" {
		n.Name = v
	}
	if h, ok := p.HourlyEUR(ctx, n.ServerType); ok {
		n.HourlyEUR = h
	}
	return n
}

func mapStatus(s string) poolwaker.NodeStatus {
	switch s {
	case "initializing", "starting", "migrating":
		return poolwaker.StatusStarting
	case "running":
		return poolwaker.StatusRunning
	case "stopping", "off", "deleting":
		return poolwaker.StatusStopping
	}
	return poolwaker.StatusUnknown
}

func (p *Provider) Create(ctx context.Context, spec poolwaker.NodeSpec) (poolwaker.Node, error) {
	if !strings.HasPrefix(spec.Name, NamePrefix) || spec.ServerType == "" {
		return poolwaker.Node{}, errors.New("hetzner: node name must start with " + NamePrefix + " and a server type is required")
	}
	if spec.Instance != "" && spec.Instance != p.cfg.Instance {
		return poolwaker.Node{}, errors.New("hetzner: spec instance differs from the configured instance")
	}
	labels := map[string]string{}
	for k, v := range spec.Labels {
		labels[k] = v
	}
	labels[poolwaker.LabelPool] = poolwaker.LabelPoolValue
	labels[poolwaker.LabelInstance] = p.cfg.Instance
	labels[poolwaker.LabelNode] = spec.Name
	snapshot, err := p.snapshotRef(ctx)
	if err != nil {
		return poolwaker.Node{}, err
	}
	var image any = snapshot
	if id, err := strconv.ParseInt(snapshot, 10, 64); err == nil {
		image = id
	}
	body := map[string]any{
		"name":               spec.Name,
		"server_type":        spec.ServerType,
		"image":              image,
		"labels":             labels,
		"user_data":          spec.UserData,
		"start_after_create": p.subnet == nil,
		"public_net":         map[string]any{"enable_ipv4": true, "enable_ipv6": false},
	}
	if p.cfg.Location != "" {
		body["location"] = p.cfg.Location
	}
	if p.cfg.Firewall != "" {
		body["firewalls"] = []map[string]any{{"firewall": idValue(p.cfg.Firewall)}}
	}
	if p.cfg.SSHKeys != nil {
		body["ssh_keys"] = p.cfg.SSHKeys
	}
	var out struct {
		Server server `json:"server"`
		Action *struct {
			ID int64 `json:"id"`
		} `json:"action"`
	}
	if _, err := p.do(ctx, http.MethodPost, "/servers", body, &out); err != nil {
		var ae *apiError
		p.mu.Lock()
		p.snapID = "" // the cached latest snapshot may be the cause; look it up again next time
		p.mu.Unlock()
		if errors.As(err, &ae) && ae.sizeRefusal() {
			return poolwaker.Node{}, &poolwaker.SizeRefusedError{ServerType: spec.ServerType, Reason: ae.Code + ": " + ae.Msg}
		}
		return poolwaker.Node{}, err
	}
	if p.subnet != nil {
		var createAction int64
		if out.Action != nil {
			createAction = out.Action.ID
		}
		if err := p.attachAndStart(ctx, out.Server.ID, createAction); err != nil {
			// Never leave a billed server behind: delete it on a context that outlives a cancelled call.
			dctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()
			if _, derr := p.do(dctx, http.MethodDelete, "/servers/"+strconv.FormatInt(out.Server.ID, 10), nil, nil); derr != nil {
				return poolwaker.Node{}, fmt.Errorf("%w (cleanup delete of server %d also failed: %w)", err, out.Server.ID, derr)
			}
			return poolwaker.Node{}, err
		}
	}
	return p.toNode(ctx, out.Server), nil
}

// sizeRefusal reports whether the API refused a create for a reason tied to the account or the
// size (dedicated-core or server limit, no stock in the location, a size not offered there) rather
// than a fault in the request or an outage. The caller falls back to another size.
func (e *apiError) sizeRefusal() bool {
	switch e.Code {
	case "resource_limit_exceeded", "resource_unavailable", "placement_error":
		return true
	}
	if e.Status == http.StatusTooManyRequests || e.Status >= 500 {
		return false // rate limits and outages are the breaker's business, never a size refusal
	}
	msg := strings.ToLower(e.Msg)
	return strings.Contains(msg, "limit exceeded") || strings.Contains(msg, "core limit")
}

// SnapshotLabel is the label every baked pool snapshot carries.
const SnapshotLabel = "reelbolt-pool-image=1"

// snapshotRef returns the image to boot: the pinned SnapshotID, or the newest available snapshot
// labelled reelbolt-pool-image=1 (cached for SnapshotCacheTTL). A pinned id always wins.
func (p *Provider) snapshotRef(ctx context.Context) (string, error) {
	if p.cfg.SnapshotID != "" {
		return p.cfg.SnapshotID, nil
	}
	p.mu.Lock()
	if p.snapID != "" && p.now().Sub(p.snapAt) < p.cfg.SnapshotCacheTTL {
		id := p.snapID
		p.mu.Unlock()
		return id, nil
	}
	p.mu.Unlock()
	var out struct {
		Images []struct {
			ID      int64             `json:"id"`
			Created time.Time         `json:"created"`
			Status  string            `json:"status"`
			Type    string            `json:"type"`
			Labels  map[string]string `json:"labels"`
		} `json:"images"`
	}
	path := "/images?type=snapshot&status=available&per_page=50&label_selector=" + url.QueryEscape(SnapshotLabel)
	if _, err := p.do(ctx, http.MethodGet, path, nil, &out); err != nil {
		return "", err
	}
	var best int64
	var bestAt time.Time
	for _, im := range out.Images {
		if im.Type != "snapshot" || im.Status != "available" || im.Labels["reelbolt-pool-image"] != "1" || im.ID <= 0 {
			continue
		}
		if best == 0 || im.Created.After(bestAt) {
			best, bestAt = im.ID, im.Created
		}
	}
	if best == 0 {
		return "", errors.New("hetzner: no available snapshot carries the label " + SnapshotLabel + "; bake one (infra/worker/bake-snapshot.sh)")
	}
	id := strconv.FormatInt(best, 10)
	p.mu.Lock()
	p.snapID, p.snapAt = id, p.now()
	p.mu.Unlock()
	return id, nil
}

func idValue(s string) any {
	if id, err := strconv.ParseInt(s, 10, 64); err == nil {
		return id
	}
	return s
}

func (p *Provider) List(ctx context.Context, instance string) ([]poolwaker.Node, error) {
	if instance != p.cfg.Instance {
		return nil, errors.New("hetzner: list instance differs from the configured instance")
	}
	sel := url.QueryEscape(fmt.Sprintf("%s=%s,%s=%s", poolwaker.LabelPool, poolwaker.LabelPoolValue, poolwaker.LabelInstance, instance))
	var nodes []poolwaker.Node
	for page := 1; page < 1000; {
		var out struct {
			Servers []server `json:"servers"`
			Meta    struct {
				Pagination struct {
					NextPage *int `json:"next_page"`
				} `json:"pagination"`
			} `json:"meta"`
		}
		if _, err := p.do(ctx, http.MethodGet, fmt.Sprintf("/servers?label_selector=%s&per_page=50&page=%d", sel, page), nil, &out); err != nil {
			return nil, err
		}
		for _, s := range out.Servers {
			if !p.ours(s) {
				continue
			}
			nodes = append(nodes, p.toNode(ctx, s))
		}
		if out.Meta.Pagination.NextPage == nil || *out.Meta.Pagination.NextPage <= page {
			break
		}
		page = *out.Meta.Pagination.NextPage
	}
	return nodes, nil
}

func (p *Provider) Delete(ctx context.Context, id string) error {
	if _, err := strconv.ParseInt(id, 10, 64); err != nil {
		return errors.New("hetzner: delete refused: server id must be numeric")
	}
	var out struct {
		Server server `json:"server"`
	}
	if code, err := p.do(ctx, http.MethodGet, "/servers/"+id, nil, &out); err != nil {
		if code == http.StatusNotFound {
			return nil
		}
		return err
	}
	if !p.ours(out.Server) || out.Server.ID <= 0 {
		return ErrNotOurs
	}
	if code, err := p.do(ctx, http.MethodDelete, "/servers/"+strconv.FormatInt(out.Server.ID, 10), nil, nil); err != nil && code != http.StatusNotFound {
		return err
	}
	return nil
}

// HourlyEUR returns the hourly net price for the type at the configured
// location (first listed location when unmatched). Results are cached.
func (p *Provider) HourlyEUR(ctx context.Context, serverType string) (float64, bool) {
	p.mu.Lock()
	if v, ok := p.prices[serverType]; ok {
		p.mu.Unlock()
		return v, true
	}
	p.mu.Unlock()
	var out struct {
		ServerTypes []struct {
			Name   string `json:"name"`
			Prices []struct {
				Location    string `json:"location"`
				PriceHourly struct {
					Net string `json:"net"`
				} `json:"price_hourly"`
			} `json:"prices"`
		} `json:"server_types"`
	}
	if _, err := p.do(ctx, http.MethodGet, "/server_types?name="+url.QueryEscape(serverType), nil, &out); err != nil {
		return 0, false
	}
	for _, st := range out.ServerTypes {
		if st.Name != serverType {
			continue
		}
		best, found := 0.0, false
		for _, pr := range st.Prices {
			v, err := strconv.ParseFloat(pr.PriceHourly.Net, 64)
			if err != nil {
				continue
			}
			if pr.Location == p.cfg.Location {
				best, found = v, true
				break
			}
			if !found {
				best, found = v, true
			}
		}
		if found {
			p.mu.Lock()
			p.prices[serverType] = best
			p.mu.Unlock()
			return best, true
		}
	}
	return 0, false
}

// ValidateSubnet parses the node subnet: an IPv4 CIDR inside 10/8, 172.16/12 or 192.168/16 and at
// most a /28 (it must hold addresses beyond the reserved ones) and at least a /22 (the free-address scan
// walks the whole range).
func ValidateSubnet(cidr string) (*net.IPNet, error) {
	ip, n, err := net.ParseCIDR(strings.TrimSpace(cidr))
	if err != nil || ip.To4() == nil {
		return nil, errors.New("hetzner: node subnet must be an IPv4 CIDR such as 10.0.2.0/24")
	}
	if !n.IP.IsPrivate() {
		return nil, errors.New("hetzner: node subnet must lie inside a private range (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16)")
	}
	ones, bits := n.Mask.Size()
	if bits != 32 || ones > 28 {
		return nil, errors.New("hetzner: node subnet is too small, use /28 or larger")
	}
	if ones < 22 {
		return nil, errors.New("hetzner: node subnet is too large, use /22 or smaller")
	}
	// IsPrivate checked only the network address; the whole range must be private too.
	last := lastIP(n)
	if !last.IsPrivate() {
		return nil, errors.New("hetzner: node subnet must lie inside a private range")
	}
	return n, nil
}

func lastIP(n *net.IPNet) net.IP {
	ip := n.IP.To4()
	out := make(net.IP, 4)
	for i := range out {
		out[i] = ip[i] | ^n.Mask[i]
	}
	return out
}

const (
	attachAttempts  = 5
	actionPollLimit = 90
)

// attachAndStart waits for the create action, attaches the server to the network at an address
// from the node subnet (retrying another address when one is taken) and powers it on.
func (p *Provider) attachAndStart(ctx context.Context, id, createAction int64) error {
	sid := strconv.FormatInt(id, 10)
	if createAction != 0 {
		if err := p.waitAction(ctx, createAction); err != nil {
			return err
		}
	}
	used, err := p.usedIPs(ctx)
	if err != nil {
		return err
	}
	cands := p.freeCandidates(used)
	if len(cands) == 0 {
		return errors.New("hetzner: node subnet has no free address")
	}
	rand.Shuffle(len(cands), func(i, j int) { cands[i], cands[j] = cands[j], cands[i] })
	var lastErr error
	for i := 0; i < len(cands) && i < attachAttempts; i++ {
		var out struct {
			Action struct {
				ID int64 `json:"id"`
			} `json:"action"`
		}
		_, err := p.do(ctx, http.MethodPost, "/servers/"+sid+"/actions/attach_to_network",
			map[string]any{"network": idValue(p.cfg.Network), "ip": cands[i]}, &out)
		if err == nil {
			err = p.waitAction(ctx, out.Action.ID)
		}
		if err == nil {
			lastErr = nil
			break
		}
		lastErr = err
		if !ipConflict(err) {
			return err
		}
	}
	if lastErr != nil {
		return lastErr
	}
	var out struct {
		Action struct {
			ID int64 `json:"id"`
		} `json:"action"`
	}
	if _, err := p.do(ctx, http.MethodPost, "/servers/"+sid+"/actions/poweron", nil, &out); err != nil {
		return err
	}
	return nil
}

// ipConflict reports an "address already taken" answer, from the request or from the action.
func ipConflict(err error) bool {
	var ae *apiError
	if !errors.As(err, &ae) {
		return false
	}
	switch ae.Code {
	case "ip_not_available", "uniqueness_error", "conflict":
		return true
	}
	return ae.Status == http.StatusConflict
}

// waitAction polls an action until it succeeds. An errored action becomes an apiError carrying its code.
func (p *Provider) waitAction(ctx context.Context, id int64) error {
	for i := 0; i < actionPollLimit; i++ {
		var out struct {
			Action struct {
				Status string `json:"status"`
				Error  *struct {
					Code    string `json:"code"`
					Message string `json:"message"`
				} `json:"error"`
			} `json:"action"`
		}
		if _, err := p.do(ctx, http.MethodGet, "/actions/"+strconv.FormatInt(id, 10), nil, &out); err != nil {
			return err
		}
		switch out.Action.Status {
		case "success":
			return nil
		case "error":
			ae := &apiError{Status: http.StatusUnprocessableEntity}
			if e := out.Action.Error; e != nil {
				ae.Code, ae.Msg = e.Code, e.Message
			}
			return ae
		}
		p.sleep(time.Second)
		if ctx.Err() != nil {
			return ctx.Err()
		}
	}
	return errors.New("hetzner: action did not finish in time")
}

// usedIPs lists the private addresses of every server in the project, ours or not.
func (p *Provider) usedIPs(ctx context.Context) (map[string]bool, error) {
	used := map[string]bool{}
	for page := 1; ; {
		var out struct {
			Servers []server `json:"servers"`
			Meta    struct {
				Pagination struct {
					NextPage *int `json:"next_page"`
				} `json:"pagination"`
			} `json:"meta"`
		}
		if _, err := p.do(ctx, http.MethodGet, "/servers?per_page=50&page="+strconv.Itoa(page), nil, &out); err != nil {
			return nil, err
		}
		for _, s := range out.Servers {
			for _, n := range s.PrivateNet {
				used[n.IP] = true
			}
		}
		if out.Meta.Pagination.NextPage == nil || *out.Meta.Pagination.NextPage <= page {
			return used, nil
		}
		page = *out.Meta.Pagination.NextPage
	}
}

// freeCandidates returns the subnet addresses not in used, skipping the network address, the first
// three addresses (reserved by Hetzner) and the broadcast address.
func (p *Provider) freeCandidates(used map[string]bool) []string {
	base := binary.BigEndian.Uint32(p.subnet.IP.To4())
	last := binary.BigEndian.Uint32(lastIP(p.subnet))
	var out []string
	for a := base + 4; a < last; a++ {
		ip := make(net.IP, 4)
		binary.BigEndian.PutUint32(ip, a)
		if !used[ip.String()] {
			out = append(out, ip.String())
		}
	}
	return out
}
