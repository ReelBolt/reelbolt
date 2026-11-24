package ui

import (
	"context"
	"errors"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
)

// forgetTimeout bounds the "remove me" call. Unpairing never waits long on the
// network: the local unpair goes ahead whatever the server says.
const forgetTimeout = 10 * time.Second

// UnpairResult says whether ReelBolt also removed this computer from the
// workspace, which decides what the person is told.
type UnpairResult struct {
	// Removed is true when ReelBolt confirmed this computer is gone from the
	// workspace (or already was).
	Removed bool
}

// Message is the sentence the window shows after an unpair.
func (r UnpairResult) Message() string {
	if r.Removed {
		return "Unpaired. This computer was removed from your ReelBolt workspace and won't take any more work."
	}
	return "Unpaired on this computer. It may still be listed in your ReelBolt workspace; you can remove it there."
}

// Unpair forgets this machine's device identity.
//
// It first asks ReelBolt to remove the device from its workspace (the device
// API's "forget me", signed with this computer's own key), so the dashboard
// stops listing it. That call cannot stop the unpair: an answer of "not
// connected" means it is already removed, and an older server or a failed
// network leaves only a list entry behind, which the result says.
//
// Then it stops the runner and wipes the identity. A runner that kept dialling
// with an identity the user just removed would be the opposite of what the
// button says.
//
// forgetKey deletes the device key as well. The default keeps it: re-pairing
// generates a fresh key either way, but a user handing the machine on is
// entitled to remove it.
func (s *Shell) Unpair(ctx context.Context, forgetKey bool) (UnpairResult, error) {
	s.mu.Lock()
	device := s.opts.Config.DeviceID
	s.mu.Unlock()
	if device == "" {
		return UnpairResult{}, say("This computer isn't paired.")
	}

	res := UnpairResult{Removed: s.forgetMe(ctx)}

	s.StopRunner()
	s.mu.Lock()
	next := *s.opts.Config
	next.DeviceID = ""
	next.OrganizationID = ""
	s.revoked = false
	s.info = deviceInfo{}
	s.mu.Unlock()

	// Through saveConfig, like every other write: it is the path this shell
	// reads, which in a test is the test's own file and not the person's.
	if err := s.saveConfig(&next); err != nil {
		s.log.Printf("unpair: write %s: %v", s.ConfigPath(), err)
		return res, sayWrap("Couldn't save your settings. Check that your user folder isn't full, then try again.", err)
	}
	s.mu.Lock()
	s.opts.Config = &next
	s.mu.Unlock()
	if forgetKey {
		if err := s.opts.Keys.Delete(device); err != nil {
			s.log.Printf("unpair: delete the device key: %v", err)
			return res, sayWrap("Unpaired, but this computer's old sign-in couldn't be deleted. Try again.", err)
		}
	}
	s.notify()
	return res, nil
}

// forgetMe asks ReelBolt to remove this device, and reports whether it is
// known to be gone.
func (s *Shell) forgetMe(parent context.Context) bool {
	ctx, cancel := context.WithTimeout(parent, forgetTimeout)
	defer cancel()
	var err error
	if s.opts.ForgetMe != nil {
		err = s.opts.ForgetMe(ctx)
	} else {
		var client *deviceapi.Client
		client, err = s.deviceClient()
		if err == nil {
			err = client.ForgetMe(ctx)
		}
	}
	return forgetMeRemoved(err, s.log.Printf)
}

// forgetMeRemoved reads ForgetMe's answer: nil is removed, "not connected" is
// already removed, and anything else (an older server's 404, a network
// failure) leaves the dashboard entry in place.
func forgetMeRemoved(err error, logf func(string, ...any)) bool {
	switch {
	case err == nil:
		return true
	case errors.Is(err, deviceapi.ErrNotConnected):
		return true
	default:
		logf("unpair: ReelBolt did not remove this device: %v", err)
		return false
	}
}
