package ui

import "sync"

// fakeAutostart is the login-item manager the cross-platform tests use.
//
// The real manager is per-platform, so a test that used LinuxAutostartFor would
// stop this package type-checking for windows - and "go vet ./..." for
// GOOS=windows is a gate on this module. The Linux manager has its own test in
// autostart_linux_test.go, where it can be exercised for real.
type fakeAutostart struct {
	mu      sync.Mutex
	on      bool
	command []string
	err     error
}

func (f *fakeAutostart) Enabled() (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.on, f.err
}

func (f *fakeAutostart) Enable(command []string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.err != nil {
		return f.err
	}
	f.on = true
	f.command = append([]string(nil), command...)
	return nil
}

func (f *fakeAutostart) Disable() error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.err != nil {
		return f.err
	}
	f.on = false
	return nil
}

func (f *fakeAutostart) Describe() string { return "fake login item" }
