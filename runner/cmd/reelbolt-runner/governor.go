package main

import (
	"errors"
	"flag"
	"fmt"
	"path/filepath"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// The governor's two command-line triggers.
//
// They write a small request document next to the runner's other state and wait
// for the running daemon to act on it. That is the whole local control surface:
// no listening socket (the runner holds none by design), no signal that only one
// platform has, and no file a user has to edit by hand.
//
// A request left behind by a crash is deleted before the next daemon starts its
// watcher, so a pause can never outlive the process that honoured it.

// controlWait is how long the command waits for the running daemon to
// acknowledge a request before saying so. The daemon polls once a second.
const controlWait = 6 * time.Second

func controlChannel() (*governor.Control, error) {
	cache, err := config.CacheDir()
	if err != nil {
		return nil, fmt.Errorf("find this machine's cache directory: %w", err)
	}
	return &governor.Control{
		RequestPath: filepath.Join(cache, "governor.json"),
		LivePath:    filepath.Join(cache, "runner.live.json"),
	}, nil
}

// runningRunner returns the live document, or an error a person can act on.
func runningRunner(c *governor.Control) (governor.Live, error) {
	live, err := c.ReadLive()
	if err != nil {
		return governor.Live{}, errors.New(
			"no runner is running on this machine (start one with \"reelbolt-runner run --headless\")")
	}
	if !live.Fresh(time.Now()) {
		return governor.Live{}, errors.New(
			"the runner's state is stale: it has probably stopped (check \"reelbolt-runner status\")")
	}
	return live, nil
}

type pauseFlags struct {
	reason string
	by     string
}

func newPauseFlags(o *pauseFlags) *flag.FlagSet {
	fs := flagSet("pause", "stop this machine taking new work until it is resumed")
	fs.StringVar(&o.reason, "reason", "", "why, shown in status and in the tray")
	fs.StringVar(&o.by, "by", "", "who asked (default: the command line)")
	return fs
}

func cmdPause(args []string) error {
	var o pauseFlags
	fs := newPauseFlags(&o)
	_ = fs.Parse(args)

	c, err := controlChannel()
	if err != nil {
		return err
	}
	if _, err := runningRunner(c); err != nil {
		return err
	}
	by := o.by
	if by == "" {
		by = "the command line"
	}
	if err := c.Request(governor.ActionPause, by, o.reason); err != nil {
		return err
	}
	// Wait for the daemon to act rather than claiming success the moment the
	// request is written: "I asked" and "it happened" are different claims.
	status, err := awaitStatus(c, func(s governor.Status) bool { return s.Held })
	if err != nil {
		return err
	}
	fmt.Printf("paused: %s\n", status.Detail)
	fmt.Println("this machine is not taking new work. Resume it with \"reelbolt-runner resume\".")
	return nil
}

type resumeFlags struct {
	by string
}

func newResumeFlags(o *resumeFlags) *flag.FlagSet {
	fs := flagSet("resume", "let this machine take work again")
	fs.StringVar(&o.by, "by", "", "who asked (default: the command line)")
	return fs
}

func cmdResume(args []string) error {
	var o resumeFlags
	fs := newResumeFlags(&o)
	_ = fs.Parse(args)

	c, err := controlChannel()
	if err != nil {
		return err
	}
	live, err := runningRunner(c)
	if err != nil {
		return err
	}
	if !live.Status.Held {
		fmt.Println("this runner is not paused; nothing to resume")
		printGovernorStatus(live.Status)
		return nil
	}
	by := o.by
	if by == "" {
		by = "the command line"
	}
	if err := c.Request(governor.ActionResume, by, ""); err != nil {
		return err
	}
	status, err := awaitStatus(c, func(s governor.Status) bool { return !s.Held })
	if err != nil {
		return err
	}
	fmt.Println("resumed")
	printGovernorStatus(status)
	return nil
}

// awaitStatus polls the live document until the running daemon reports the
// state the request asked for.
func awaitStatus(c *governor.Control, want func(governor.Status) bool) (governor.Status, error) {
	deadline := time.Now().Add(controlWait)
	for {
		if live, err := c.ReadLive(); err == nil && want(live.Status) {
			return live.Status, nil
		}
		if time.Now().After(deadline) {
			return governor.Status{}, fmt.Errorf(
				"the running runner did not acknowledge the request within %s; check \"reelbolt-runner status\"",
				controlWait)
		}
		time.Sleep(200 * time.Millisecond)
	}
}

// printGovernorStatus explains what the governor is doing, including the case a
// user is most likely to hit: resumed, and still not working.
func printGovernorStatus(s governor.Status) {
	if s.Allowed {
		fmt.Printf("governor    working (%s)\n", s.Reason)
	} else {
		fmt.Printf("governor    NOT working (%s): %s\n", s.Reason, s.Detail)
		if s.NextChange != "" {
			fmt.Printf("            work resumes at %s\n", s.NextChange)
		}
		if s.Held {
			fmt.Printf("            an explicit pause is in force (by %s)\n", s.HeldBy)
		} else {
			fmt.Println("            the schedule is holding it: change [schedule] in runner.json, or run \"reelbolt-runner resume\"")
		}
	}
}
