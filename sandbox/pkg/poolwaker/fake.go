package poolwaker

import (
	"context"
	"fmt"
	"strconv"
	"sync"
	"time"
)

// Fake is an in-memory Provider for tests and for `POOL_PROVIDER=fake`.
type Fake struct {
	mu       sync.Mutex
	Clock    Clock
	Prices   map[string]float64 // server type -> hourly EUR; absent = unknown
	CreateFn func(NodeSpec) error
	ListErr  error
	DelErr   error
	// servers are the labelled servers by ID; Foreign are unlabelled IDs that
	// Delete must refuse.
	servers map[string]*Node
	Foreign map[string]bool
	// Asked records every ID passed to Delete (labelled or not); Created every
	// created ID; ForeignTouched the Delete calls that hit an unrelated server.
	Asked          []string
	Created        []string
	ForeignTouched []string
	unrelated      map[string]Node
	next           int
}

// NewFake returns a Fake with a default price for cpx42.
func NewFake(clock Clock) *Fake {
	if clock == nil {
		clock = RealClock{}
	}
	return &Fake{Clock: clock, Prices: map[string]float64{"cpx42": 0.05}, servers: map[string]*Node{}, Foreign: map[string]bool{}, unrelated: map[string]Node{}}
}

// Name implements Provider.
func (f *Fake) Name() string { return "fake" }

// Create implements Provider.
func (f *Fake) Create(_ context.Context, spec NodeSpec) (Node, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.CreateFn != nil {
		if err := f.CreateFn(spec); err != nil {
			return Node{}, err
		}
	}
	f.next++
	n := Node{
		ID: "fake-" + strconv.Itoa(f.next), Name: spec.Name, Instance: spec.Instance, ServerType: spec.ServerType,
		Status: StatusStarting, CreatedAt: f.Clock.Now(), HourlyEUR: f.Prices[spec.ServerType],
	}
	f.servers[n.ID] = &n
	f.Created = append(f.Created, n.ID)
	return n, nil
}

// List implements Provider: only labelled servers of the instance.
func (f *Fake) List(_ context.Context, instance string) ([]Node, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.ListErr != nil {
		return nil, f.ListErr
	}
	var out []Node
	for _, s := range f.servers {
		if s.Instance == instance {
			out = append(out, *s)
		}
	}
	return out, nil
}

// Delete implements Provider. It refuses ids of servers without the pool labels.
func (f *Fake) Delete(_ context.Context, id string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Asked = append(f.Asked, id)
	if f.DelErr != nil {
		return f.DelErr
	}
	if _, ok := f.unrelated[id]; ok {
		f.ForeignTouched = append(f.ForeignTouched, id)
		return fmt.Errorf("fake: refusing to delete unrelated server %s", id)
	}
	if _, ok := f.servers[id]; !ok {
		if f.Foreign[id] {
			return fmt.Errorf("fake: refusing to delete unlabelled server %s", id)
		}
		return nil // already gone
	}
	delete(f.servers, id)
	return nil
}

// HourlyEUR implements Provider.
func (f *Fake) HourlyEUR(_ context.Context, serverType string) (float64, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	p, ok := f.Prices[serverType]
	return p, ok
}

// SetReady marks a node as running at the provider.
func (f *Fake) SetReady(name string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, s := range f.servers {
		if s.Name == name {
			s.Status = StatusRunning
		}
	}
}

// AddOrphan inserts a labelled server unknown to any controller.
func (f *Fake) AddOrphan(name, instance string, created time.Time) string {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.next++
	id := "fake-" + strconv.Itoa(f.next)
	f.servers[id] = &Node{ID: id, Name: name, Instance: instance, ServerType: "cpx42",
		Status: StatusRunning, CreatedAt: created, HourlyEUR: f.Prices["cpx42"]}
	return id
}

// Count returns the number of labelled servers.
func (f *Fake) Count() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.servers)
}

// FakeGateway is an in-memory Gateway.
type FakeGateway struct {
	mu         sync.Mutex
	DemandVal  Demand
	DemandErr  error
	RunnersErr error
	MintErr    error
	connected  map[string]int // name -> running jobs
	Minted     []string
	Revoked    []string
	Heartbeats []Heartbeat
}

// NewFakeGateway returns an empty FakeGateway.
func NewFakeGateway() *FakeGateway { return &FakeGateway{connected: map[string]int{}} }

// Connect makes a node appear in the runner list with the given running jobs.
func (g *FakeGateway) Connect(name string, running int) {
	g.mu.Lock()
	g.connected[name] = running
	g.mu.Unlock()
}

// Disconnect removes a node from the runner list.
func (g *FakeGateway) Disconnect(name string) {
	g.mu.Lock()
	delete(g.connected, name)
	g.mu.Unlock()
}

// Demand implements Gateway.
func (g *FakeGateway) Demand(context.Context) (Demand, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.DemandVal, g.DemandErr
}

// Runners implements Gateway.
func (g *FakeGateway) Runners(context.Context) ([]Runner, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.RunnersErr != nil {
		return nil, g.RunnersErr
	}
	var out []Runner
	for n, r := range g.connected {
		out = append(out, Runner{Name: n, Kind: "CloudPool", Running: r})
	}
	return out, nil
}

// MintNode implements Gateway.
func (g *FakeGateway) MintNode(_ context.Context, name string, _ time.Duration) (NodeToken, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.MintErr != nil {
		return NodeToken{}, g.MintErr
	}
	g.Minted = append(g.Minted, name)
	return NodeToken{Name: name, Token: "rbn1.fake" + name, ExpiresAt: "2099-01-01T00:00:00Z"}, nil
}

// RevokeNode implements Gateway.
func (g *FakeGateway) RevokeNode(_ context.Context, name string) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.Revoked = append(g.Revoked, name)
	return nil
}

// Heartbeat implements Gateway.
func (g *FakeGateway) Heartbeat(_ context.Context, hb Heartbeat) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.Heartbeats = append(g.Heartbeats, hb)
	return nil
}

// Last returns the most recent heartbeat.
func (g *FakeGateway) Last() Heartbeat {
	g.mu.Lock()
	defer g.mu.Unlock()
	if len(g.Heartbeats) == 0 {
		return Heartbeat{}
	}
	return g.Heartbeats[len(g.Heartbeats)-1]
}

// AddUnrelated stores a server the waker must never see or touch (it is not
// returned by List). Returns its id.
func (f *Fake) AddUnrelated(name, instance string) string {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.next++
	id := "foreign-" + strconv.Itoa(f.next)
	f.unrelated[id] = Node{ID: id, Name: name, Instance: instance, ServerType: "cx22", Status: StatusRunning, CreatedAt: f.Clock.Now().Add(-48 * time.Hour)}
	return id
}

// UnrelatedCount returns how many unrelated servers still exist.
func (f *Fake) UnrelatedCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.unrelated)
}

// Unrelated returns copies of the unrelated servers (for adversarial wrappers).
func (f *Fake) Unrelated() []Node {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []Node
	for _, n := range f.unrelated {
		out = append(out, n)
	}
	return out
}
