// Command reelbolt-runner is the headless ReelBolt runner daemon.
//
// It pairs a machine into a workspace, then serves that workspace's render and
// media jobs until it is told to stop. It embeds the worker agent library
// (sandbox/pkg/agent) rather than reimplementing the protocol: this command owns
// configuration, the device key, the runtime image, logging and the stop
// sequence, and nothing else.
//
// The process listens on no port and holds no provider credential.
package main

import (
	"errors"
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/identity"
)

// Version is the build's version string, stamped by the release pipeline.
var Version = "dev"

// FontsetVersion is the bundled font set's tag, stamped by the release pipeline
// alongside Version. Empty means this build does not advertise one.
var FontsetVersion = ""

// Exit codes. A service manager distinguishes "this machine is not paired"
// from "the daemon failed", and so does a user reading a shell script.
const (
	exitOK        = 0
	exitFailure   = 1
	exitUsage     = 2
	exitNotPaired = 3
)

const usage = `reelbolt-runner - the headless ReelBolt runner

Usage:
  reelbolt-runner pair   [flags]   pair this machine into a ReelBolt workspace
  reelbolt-runner run    [flags]   serve jobs until stopped (SIGTERM drains first)
  reelbolt-runner status [flags]   show the local configuration and device state
  reelbolt-runner pause  [flags]   stop taking new work until resumed
  reelbolt-runner resume [flags]   take work again after a pause
  reelbolt-runner doctor [flags]   probe the machine and print its capabilities
  reelbolt-runner unpair [flags]   forget the device identity
  reelbolt-runner update [flags]   check for, verify and install a signed release
  reelbolt-runner folders <sub>    list, add, remove, pause or resume a synced folder
  reelbolt-runner config <sub>     set-api, or show the local configuration
  reelbolt-runner version          print the version

Folder sync:
  reelbolt-runner folders list                       what this computer syncs
  reelbolt-runner folders add <path> --project <id>  sync a folder into a project
  reelbolt-runner folders remove <linkId>            stop syncing one (files stay in the project)
  reelbolt-runner folders pause|resume <linkId>      stop or restart copying
  reelbolt-runner config set-api <url>               set the origin folder sync signs for

Run "reelbolt-runner <command> -h" for a command's flags.
`

func main() {
	log.SetFlags(log.LstdFlags | log.LUTC)
	log.SetPrefix("reelbolt-runner: ")
	if len(os.Args) < 2 {
		fmt.Fprint(os.Stderr, usage)
		os.Exit(exitUsage)
	}
	var err error
	switch os.Args[1] {
	case "pair":
		err = cmdPair(os.Args[2:])
	case "run":
		err = cmdRun(os.Args[2:])
	case "status":
		err = cmdStatus(os.Args[2:])
	case "pause":
		err = cmdPause(os.Args[2:])
	case "resume":
		err = cmdResume(os.Args[2:])
	case "doctor":
		err = cmdDoctor(os.Args[2:])
	case "unpair":
		err = cmdUnpair(os.Args[2:])
	case "update":
		err = cmdUpdate(os.Args[2:])
	case "folders":
		err = cmdFolders(os.Args[2:])
	case "config":
		err = cmdConfig(os.Args[2:])
	case "version", "-version", "--version":
		fmt.Printf("reelbolt-runner %s\n", Version)
	case "help", "-h", "-help", "--help":
		fmt.Print(usage)
	default:
		fmt.Fprintf(os.Stderr, "reelbolt-runner: unknown command %q\n\n%s", os.Args[1], usage)
		os.Exit(exitUsage)
	}
	if err != nil {
		fmt.Fprintf(os.Stderr, "reelbolt-runner: %v\n", err)
		os.Exit(exitFor(err))
	}
}

// exitFor maps an error to an exit code.
func exitFor(err error) int {
	if errors.Is(err, errNotPaired) {
		return exitNotPaired
	}
	return exitFailure
}

// errNotPaired mirrors daemon.ErrNotPaired as a value this package can compare
// without importing the daemon from the dispatch table.
var errNotPaired = fmt.Errorf("this machine is not paired: run \"reelbolt-runner pair\" first")

// ---- shared plumbing ----

// env is the shared command environment: the loaded configuration, the key
// store, and where they came from.
type env struct {
	cfg     *config.Config
	keys    *identity.Store
	cfgPath string
	dir     string
}

// loadEnv loads runner.json, applies flag overrides and reports where it read
// from.
func loadEnv(overrides func(*config.Config)) (*env, error) {
	path, err := config.Path()
	if err != nil {
		return nil, err
	}
	cfg, err := config.LoadFrom(path)
	if err != nil {
		return nil, err
	}
	if overrides != nil {
		overrides(cfg)
	}
	cfg.ApplyDefaults()
	if err := cfg.Validate(); err != nil {
		return nil, err
	}
	dir := filepath.Dir(path)
	return &env{cfg: cfg, keys: identity.Open(dir, log.Default()), cfgPath: path, dir: dir}, nil
}

// flagSet builds a flag set in the shared style: -h prints usage and exits 2.
func flagSet(name, synopsis string) *flag.FlagSet {
	fs := flag.NewFlagSet(name, flag.ExitOnError)
	fs.Usage = func() {
		fmt.Fprintf(os.Stderr, "reelbolt-runner %s - %s\n\nFlags:\n", name, synopsis)
		fs.PrintDefaults()
	}
	return fs
}

// applyDevGateway records a development gateway host in the configuration, so
// the allowlist accepts it. A release build refuses it later, at validation.
func applyDevGateway(cfg *config.Config, raw string) error {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil
	}
	host := hostOf(raw)
	if host == "" {
		return fmt.Errorf("--dev-gateway %q is not a URL or host", raw)
	}
	cfg.DevGateway = true
	if !contains(cfg.DevGateways, host) {
		cfg.DevGateways = append(cfg.DevGateways, host)
	}
	return nil
}

func hostOf(raw string) string {
	if i := strings.Index(raw, "://"); i >= 0 {
		rest := raw[i+3:]
		if j := strings.IndexAny(rest, "/?#"); j >= 0 {
			return rest[:j]
		}
		return rest
	}
	if strings.Contains(raw, "/") {
		return ""
	}
	return raw
}

func contains(list []string, want string) bool {
	for _, s := range list {
		if strings.EqualFold(s, want) {
			return true
		}
	}
	return false
}
