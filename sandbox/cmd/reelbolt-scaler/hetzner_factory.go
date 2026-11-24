package main

import (
	"errors"
	"os"
	"strings"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/poolwaker"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/poolwaker/hetzner"
)

func init() { providerFactories["hetzner"] = hetznerFactory }

// hetznerFactory reads HCLOUD_TOKEN (or ~/.config/reelbolt/hcloud.token),
// HCLOUD_SNAPSHOT_ID, HCLOUD_LOCATION, HCLOUD_NETWORK,
// HCLOUD_NETWORK_SUBNET and HCLOUD_FIREWALL. Errors never include the token.
func hetznerFactory(getenv func(string) string) (poolwaker.Provider, error) {
	home := getenv("HOME")
	if home == "" {
		home, _ = os.UserHomeDir()
	}
	tok, err := hetzner.LoadToken(getenv, home)
	if err != nil {
		return nil, err
	}
	inst := strings.TrimSpace(getenv("POOL_INSTANCE"))
	if inst == "" {
		return nil, errors.New("POOL_INSTANCE is required for the hetzner provider")
	}
	snap := strings.TrimSpace(getenv("HCLOUD_SNAPSHOT_ID"))
	if snap == "" {
		return nil, errors.New("HCLOUD_SNAPSHOT_ID is required for the hetzner provider (an image id, or \"latest\" for the newest snapshot labelled reelbolt-pool-image=1)")
	}
	latest := strings.EqualFold(snap, "latest")
	if latest {
		snap = ""
	}
	// A node holds the Docker socket and runs customer media: it must sit behind a
	// firewall with no inbound rules. Refuse to start without one.
	if strings.TrimSpace(getenv("HCLOUD_FIREWALL")) == "" {
		return nil, errors.New("HCLOUD_FIREWALL is required (a firewall with no inbound rules)")
	}
	// A network without a dedicated node subnet cannot be isolated by host firewall rules.
	if strings.TrimSpace(getenv("HCLOUD_NETWORK")) != "" && strings.TrimSpace(getenv("HCLOUD_NETWORK_SUBNET")) == "" {
		return nil, errors.New("HCLOUD_NETWORK_SUBNET is required with HCLOUD_NETWORK (a CIDR such as 10.0.2.0/24 inside the network, reserved for pool nodes)")
	}
	return hetzner.Factory(hetzner.Config{
		Token: tok, Instance: inst, SnapshotID: snap, LatestSnapshot: latest,
		Location:      strings.TrimSpace(getenv("HCLOUD_LOCATION")),
		Network:       strings.TrimSpace(getenv("HCLOUD_NETWORK")),
		NetworkSubnet: strings.TrimSpace(getenv("HCLOUD_NETWORK_SUBNET")),
		Firewall:      strings.TrimSpace(getenv("HCLOUD_FIREWALL")),
	})
}
