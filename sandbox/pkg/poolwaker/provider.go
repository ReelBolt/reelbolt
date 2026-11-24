// Package poolwaker is the control loop that starts and stops CloudPool worker
// nodes on demand (initiative on-demand-compute, stage 1).
//
// This file is the seam between the loop and a cloud: a Provider creates,
// lists and deletes nodes and knows nothing about queues, caps or credits. The
// first implementation is Hetzner Cloud (package hetzner); tests use Fake.
package poolwaker

import (
	"context"
	"errors"
	"time"
)

// LabelPool is the label every server the waker creates carries. The waker only
// ever lists, reads or deletes servers that carry it.
const (
	LabelPool      = "reelbolt-pool"
	LabelPoolValue = "1"
	// LabelInstance names the deployment (staging, production) so two wakers on
	// one cloud project never touch each other's servers.
	LabelInstance = "reelbolt-pool-instance"
	// LabelNode carries the node name (also the worker name at the gateway).
	LabelNode = "reelbolt-pool-node"
)

// NodeSpec is what the loop asks a provider to create.
type NodeSpec struct {
	// Name is the node and worker name. It is unique per node and never reused.
	Name string
	// Instance is the deployment label value.
	Instance string
	// ServerType is the provider's size name (for example "cpx42").
	ServerType string
	// UserData is the cloud-init document. It carries the node's short-lived
	// gateway token and nothing else secret; the loop renders it.
	UserData string
	// Labels are extra labels; the pool labels are added by the provider.
	Labels map[string]string
}

// ErrSizeRefused is what a Provider returns from Create when it refused this server size for a
// reason another size may not share: the account's dedicated-core limit, no stock in the
// location. It is not a provider outage: the loop leaves that size alone for
// Config.SizeRefusalBackoff and tries the next size in its list.
var ErrSizeRefused = errors.New("server size refused by the provider")

// SizeRefusedError carries the provider's own words for a refused size.
type SizeRefusedError struct {
	ServerType string
	Reason     string
}

func (e *SizeRefusedError) Error() string {
	return "server size " + e.ServerType + " refused by the provider: " + e.Reason
}

// Is makes errors.Is(err, ErrSizeRefused) true.
func (e *SizeRefusedError) Is(target error) bool { return target == ErrSizeRefused }

// Node is a server as the provider reports it.
type Node struct {
	ID         string
	Name       string
	Instance   string
	ServerType string
	// Status is the provider's own word, normalised to one of the NodeStatus values.
	Status    NodeStatus
	CreatedAt time.Time
	// HourlyEUR is the provider's hourly price for ServerType, ex VAT, when known (0 when unknown).
	HourlyEUR float64
}

// NodeStatus is the normalised lifecycle word.
type NodeStatus string

const (
	StatusStarting NodeStatus = "starting"
	StatusRunning  NodeStatus = "running"
	StatusStopping NodeStatus = "stopping"
	StatusUnknown  NodeStatus = "unknown"
)

// Provider is the whole cloud surface the loop uses.
//
// Contract every implementation must keep:
//   - Create labels the server with LabelPool=1, LabelInstance and LabelNode, and
//     returns once the provider has accepted the request (not when the OS is up).
//   - List returns ONLY servers carrying LabelPool=1 AND the given instance.
//   - Delete is idempotent: deleting a server that is already gone is not an error.
//   - No method ever logs or returns a credential.
type Provider interface {
	Name() string
	Create(ctx context.Context, spec NodeSpec) (Node, error)
	List(ctx context.Context, instance string) ([]Node, error)
	Delete(ctx context.Context, id string) error
	// HourlyEUR is the price of one server type per started hour, ex VAT; false when unknown.
	HourlyEUR(ctx context.Context, serverType string) (float64, bool)
}
