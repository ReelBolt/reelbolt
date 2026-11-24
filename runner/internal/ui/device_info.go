package ui

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	goruntime "runtime"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/caps"
	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

// ---- the workspace's name ----
//
// The configuration knows the workspace by its id, which is the one thing a
// person never recognises. The device API's "who am I" call knows its name and
// who paired the computer, so the shell asks it: once at start, once after
// pairing, and again at most every deviceInfoTTL. The view never waits for it;
// until it has answered, the name is simply absent.

const (
	// deviceInfoTTL is how long an answer is kept before it is asked again.
	deviceInfoTTL = 10 * time.Minute
	// deviceInfoRetry is how long a failed ask waits before the next one, so an
	// offline computer does not ask every second.
	deviceInfoRetry = time.Minute
	// deviceInfoTimeout bounds one ask.
	deviceInfoTimeout = 15 * time.Second
)

// deviceInfo is the cached answer. It is guarded by the shell's mutex.
type deviceInfo struct {
	deviceID      string
	workspaceName string
	pairedByEmail string
	answeredAt    time.Time
	triedAt       time.Time
	inFlight      bool
}

// deviceClient builds the signed device client from the live configuration.
// It is the same client folder sync uses (see EngineFolders.client), built from
// this shell's own configuration and key store.
func (s *Shell) deviceClient() (*deviceapi.Client, error) {
	cfg := s.config()
	if cfg.DeviceID == "" {
		return nil, errors.New("not paired")
	}
	if cfg.APIBaseURL == "" {
		return nil, errors.New("no API origin is recorded for this device")
	}
	origin, err := config.CheckAPIBase(cfg.APIBaseURL, cfg.DevGateways)
	if err != nil {
		return nil, err
	}
	key, err := s.opts.Keys.Load(cfg.DeviceID)
	if err != nil {
		return nil, err
	}
	return deviceapi.New(origin, cfg.DeviceID, key), nil
}

// whoami asks ReelBolt who this device is.
func (s *Shell) whoami(ctx context.Context) (deviceapi.Me, error) {
	if s.opts.Whoami != nil {
		return s.opts.Whoami(ctx)
	}
	client, err := s.deviceClient()
	if err != nil {
		return deviceapi.Me{}, err
	}
	return client.Me(ctx)
}

// refreshDeviceInfo asks again when the answer is missing or old. It never
// blocks: the ask runs in the background and redraws when it lands. force asks
// even when the cached answer is fresh (after pairing).
func (s *Shell) refreshDeviceInfo(force bool) {
	s.mu.Lock()
	cfg := s.opts.Config
	if !cfg.IsPaired() {
		s.info = deviceInfo{}
		s.mu.Unlock()
		return
	}
	now := s.now()
	in := &s.info
	if in.deviceID != cfg.DeviceID {
		*in = deviceInfo{deviceID: cfg.DeviceID}
	}
	switch {
	case in.inFlight:
		s.mu.Unlock()
		return
	case force:
	case !in.answeredAt.IsZero() && now.Sub(in.answeredAt) < deviceInfoTTL:
		s.mu.Unlock()
		return
	case !in.triedAt.IsZero() && now.Sub(in.triedAt) < deviceInfoRetry:
		s.mu.Unlock()
		return
	}
	in.inFlight = true
	in.triedAt = now
	device := cfg.DeviceID
	parent := s.rootCtx
	s.mu.Unlock()
	if parent == nil {
		parent = context.Background()
	}

	go func() {
		defer telemetry.Recover(s.telemetry, "whoami")
		ctx, cancel := context.WithTimeout(parent, deviceInfoTimeout)
		me, err := s.whoami(ctx)
		cancel()
		s.mu.Lock()
		changed := false
		if s.info.deviceID == device {
			s.info.inFlight = false
			if err == nil {
				changed = s.info.workspaceName != me.OrganizationName || s.info.pairedByEmail != me.PairedByEmail
				s.info.workspaceName = me.OrganizationName
				s.info.pairedByEmail = me.PairedByEmail
				s.info.answeredAt = s.now()
			}
		}
		s.mu.Unlock()
		if err != nil {
			s.log.Printf("device info: %v", err)
		}
		if changed {
			s.notify()
		}
	}()
}

// ---- what this machine has ----

// HostView is what this computer has, so the Settings screen can offer sensible
// ranges for the per-job limits.
type HostView struct {
	CPUs        int   `json:"cpus"`
	MemoryBytes int64 `json:"memoryBytes"`
	// DiskFreeBytes is the free space where jobs work. Absent when it could not
	// be read.
	DiskFreeBytes int64 `json:"diskFreeBytes,omitempty"`
}

// hostTTL bounds how stale the free-space figure may be. Reading it is one
// statfs, but the view is rebuilt every second and that is not free.
const hostTTL = 30 * time.Second

type hostCache struct {
	view HostView
	at   time.Time
}

// hostView reports the machine's cores, memory and free disk, cached.
func (s *Shell) hostView() *HostView {
	s.mu.Lock()
	if !s.host.at.IsZero() && s.now().Sub(s.host.at) < hostTTL {
		v := s.host.view
		s.mu.Unlock()
		return &v
	}
	probe := s.probe
	s.mu.Unlock()

	facts := s.opts.HostFacts
	if facts == nil {
		facts = caps.SystemFacts
	}
	mem, disk := facts(existingDir(s.scratchRoot()))
	v := HostView{CPUs: goruntime.NumCPU(), MemoryBytes: clampInt64(mem), DiskFreeBytes: clampInt64(disk)}
	if probe != nil {
		if probe.Inventory.CPUCores > 0 {
			v.CPUs = probe.Inventory.CPUCores
		}
		if v.MemoryBytes == 0 && probe.Inventory.MemTotal > 0 {
			v.MemoryBytes = clampInt64(probe.Inventory.MemTotal)
		}
	}
	s.mu.Lock()
	s.host = hostCache{view: v, at: s.now()}
	s.mu.Unlock()
	return &v
}

// existingDir walks up from dir to the nearest directory that exists, because
// the scratch directory is created by the first job and free space is a
// property of the volume, not of the folder.
func existingDir(dir string) string {
	if dir == "" {
		return ""
	}
	for {
		if info, err := os.Stat(dir); err == nil && info.IsDir() {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return dir
		}
		dir = parent
	}
}

func clampInt64(n uint64) int64 {
	const maxInt64 = int64(^uint64(0) >> 1)
	if n > uint64(maxInt64) {
		return maxInt64
	}
	return int64(n)
}
