package main

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"os"
	"runtime"
	"strings"
	"time"

	"github.com/vecchiotom/reelbolt/runner/internal/caps"
	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/daemon"
	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
	"github.com/vecchiotom/reelbolt/runner/internal/foldersync"
	"github.com/vecchiotom/reelbolt/runner/internal/governor"
	"github.com/vecchiotom/reelbolt/runner/internal/pairing"
	"github.com/vecchiotom/reelbolt/runner/internal/runtimeimage"
	"github.com/vecchiotom/reelbolt/runner/internal/update"
)

// defaultAPIBase is the origin the pairing routes are reached at: the
// dashboard's own origin, which proxies /api/v1 to the Go API. --api overrides
// it, which is what a development stack needs.
var defaultAPIBase = config.Origin()

// Each command's flags are declared by a constructor so the flag surface is
// testable without running the command (a command parses argv, which a test
// cannot do without exiting the test process).

// ---- run ----

type runFlags struct {
	headless     bool
	gateway      string
	devGateway   string
	slots        int
	image        string
	containerCLI string
	logPath      string
	noAutoUpdate bool
	// folderSyncOnlyWhenAllowed makes folder sync follow the governor's
	// schedule. Off by default: a person who turned folder sync on meant it.
	folderSyncOnlyWhenAllowed bool
}

func newRunFlags(o *runFlags) *flag.FlagSet {
	fs := flagSet("run", "serve jobs until the process is asked to stop")
	fs.BoolVar(&o.headless, "headless", true, "run without a user interface")
	fs.StringVar(&o.gateway, "gateway", "", "gateway URL to dial (overrides runner.json)")
	fs.StringVar(&o.devGateway, "dev-gateway", "", "accept this gateway host (non-release builds only)")
	fs.IntVar(&o.slots, "slots", 0, "how many jobs to run at once")
	fs.StringVar(&o.image, "runtime-image", "", "container image jobs execute in")
	fs.StringVar(&o.containerCLI, "container-cli", "", "docker or podman (default: detect)")
	fs.StringVar(&o.logPath, "log-file", "", "also append the log to this file")
	fs.BoolVar(&o.noAutoUpdate, "no-auto-update", false, "do not check for a signed release while running")
	fs.BoolVar(&o.folderSyncOnlyWhenAllowed, "sync-only-when-allowed", false,
		"copy synced folders only while the governor says this machine may work")
	return fs
}

func cmdRun(args []string) error {
	var o runFlags
	fs := newRunFlags(&o)
	_ = fs.Parse(args)

	if !o.headless {
		return errors.New("this build has no user interface; the desktop shell is a separate binary (E6)")
	}

	e, err := loadEnv(func(c *config.Config) {
		if o.gateway != "" {
			c.GatewayURL = o.gateway
		}
		if o.slots > 0 {
			c.Slots = o.slots
		}
		if o.image != "" {
			c.RuntimeImage = o.image
		}
		if o.containerCLI != "" {
			c.ContainerCLI = o.containerCLI
		}
		if o.logPath != "" {
			c.LogFile = o.logPath
		}
		if err := applyDevGateway(c, o.devGateway); err != nil {
			fatal(err)
		}
	})
	if err != nil {
		return err
	}
	if !e.cfg.IsPaired() {
		return errNotPaired
	}
	if e.cfg.DevGateway && !config.DevGatewayAllowed() {
		return errors.New("runner.json names a development gateway; this is a release build")
	}

	logger, closer, err := openLogger(e.cfg.LogFile)
	if err != nil {
		return err
	}
	defer closer()

	opts := daemon.Options{
		Config:         e.cfg,
		Keys:           e.keys,
		Logger:         logger,
		Version:        Version,
		FontsetVersion: FontsetVersion,
		UpdateInterval: e.cfg.UpdateInterval(),
	}
	if e.cfg.UpdatesEnabled() && !o.noAutoUpdate {
		// An updater that cannot be built is not a reason to refuse to run: a
		// build with no release key compiled in is the normal state until the
		// release pipeline provisions one, and a runner that refuses to start
		// over it would be useless in the meantime.
		up, upErr := newUpdater(e)
		if upErr != nil {
			logger.Printf("auto-update is off: %v", upErr)
		} else {
			opts.Updater = up
		}
	} else {
		logger.Printf("auto-update is off")
	}

	d, err := daemon.New(opts)
	if err != nil {
		if errors.Is(err, daemon.ErrNotPaired) {
			return errNotPaired
		}
		return err
	}

	logger.Printf("reelbolt-runner %s starting (device %s)", Version, shortID(e.cfg.DeviceID))

	// Folder sync runs beside the job loop, not inside it: it signs its own
	// requests to the API, so it works whether or not the compute fabric is up,
	// and a broken folder link never stops this machine taking jobs.
	runCtx, stopFolders := context.WithCancel(context.Background())
	defer stopFolders()
	go runFolderSync(runCtx, e, logger)

	return d.Run(context.Background())
}

// runFolderSync starts the folder-sync engine and keeps it running for as long
// as this process does. A failure to build it is logged and nothing else: a
// machine whose folder list cannot be read must still serve jobs.
func runFolderSync(ctx context.Context, e *env, logger *log.Logger) {
	engine, err := newFolderEngine(e, logger)
	if err != nil {
		logger.Printf("folder sync is off: %v", err)
		return
	}
	st := engine.Status()
	switch {
	case st.Problem != "":
		logger.Printf("folder sync: %s", st.Problem)
	case len(st.Links) == 0:
		logger.Printf("folder sync: no folders are synced on this computer")
	default:
		logger.Printf("folder sync: watching %d %s", len(st.Links), pluralWord(len(st.Links), "folder", "folders"))
	}
	if err := engine.Run(ctx); err != nil {
		logger.Printf("folder sync stopped: %v", err)
	}
}

// ---- pair ----

type pairFlags struct {
	api        string
	name       string
	devGateway string
	noBrowser  bool
	gateway    string
}

func newPairFlags(o *pairFlags) *flag.FlagSet {
	fs := flagSet("pair", "pair this machine into a ReelBolt workspace")
	fs.StringVar(&o.api, "api", "", "origin serving /api/v1/runners (default "+defaultAPIBase+")")
	fs.StringVar(&o.name, "name", "", "the name the approval screen shows")
	fs.StringVar(&o.devGateway, "dev-gateway", "", "accept this gateway host (non-release builds only)")
	fs.BoolVar(&o.noBrowser, "no-browser", false, "do not open a browser")
	fs.StringVar(&o.gateway, "gateway", "", "gateway URL to store instead of the one returned")
	return fs
}

func cmdPair(args []string) error {
	var o pairFlags
	fs := newPairFlags(&o)
	_ = fs.Parse(args)

	e, err := loadEnv(func(c *config.Config) {
		if o.name != "" {
			c.DeviceName = o.name
		}
		if err := applyDevGateway(c, o.devGateway); err != nil {
			fatal(err)
		}
	})
	if err != nil {
		return err
	}

	// A fresh key per attempt: the API refuses a public key that is already a
	// device ("one key, one device"), so reusing a stale one would make
	// re-pairing impossible.
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return fmt.Errorf("generate a device key: %w", err)
	}

	base := o.api
	if base == "" {
		base = defaultAPIBase
	}
	client := &pairing.Client{
		APIBaseURL: base,
		DeviceName: e.cfg.DeviceName,
		Version:    Version,
		Logger:     log.Default(),
	}
	if o.noBrowser {
		client.OpenBrowser = func(string) error { return nil }
	}

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Minute)
	defer cancel()

	_, res, err := client.Pair(ctx, pairing.Request{PublicKey: pub})
	switch {
	case errors.Is(err, pairing.ErrAccessDenied):
		return errors.New("the request was denied in the dashboard")
	case errors.Is(err, pairing.ErrExpired):
		return errors.New("the pairing code expired; run pair again")
	case errors.Is(err, pairing.ErrAlreadyPaired):
		return errors.New("this key is already paired; run unpair first, or pair from a fresh machine")
	case err != nil:
		return err
	}

	gatewayURL := res.GatewayURL
	if o.gateway != "" {
		gatewayURL = o.gateway
	}
	// The gateway the poll returned is checked before it is stored, not when it
	// is first dialled: a value that cannot be dialled should never reach the
	// configuration file.
	if err := config.CheckGateway(gatewayURL, e.cfg.DevGateways); err != nil {
		return fmt.Errorf("the pairing response named a gateway this runner will not dial: %w", err)
	}
	if err := e.keys.Save(res.DeviceID, priv); err != nil {
		return fmt.Errorf("store the device key: %w", err)
	}
	e.cfg.DeviceID = res.DeviceID
	e.cfg.GatewayURL = gatewayURL
	e.cfg.OrganizationID = res.OrganizationID
	// The origin the device was paired at is the origin the device API (folder
	// sync, the workspace name, "unpair") signs its requests for. It is checked
	// against the same allowlist as every load will check it, and a value the
	// runner would refuse is left out rather than stored.
	if origin, err := config.CheckAPIBase(base, e.cfg.DevGateways); err == nil {
		e.cfg.APIBaseURL = origin
	} else {
		fmt.Fprintf(os.Stderr, "note: not recording %s for folder sync: %v\n", base, err)
	}
	if err := e.cfg.Save(); err != nil {
		return fmt.Errorf("write %s: %w", e.cfgPath, err)
	}
	fmt.Printf("\nPaired.\n  device    %s\n  workspace %s\n  gateway   %s\n  key       %s\n  config    %s\n",
		res.DeviceID, res.OrganizationID, gatewayURL, e.keys.Describe(), e.cfgPath)
	fmt.Println("\nStart the runner with: reelbolt-runner run --headless")
	return nil
}

// ---- status ----

// statusReport is the machine-readable form of "status".
type statusReport struct {
	ConfigPath     string `json:"configPath"`
	Paired         bool   `json:"paired"`
	DeviceID       string `json:"deviceId,omitempty"`
	DeviceName     string `json:"deviceName,omitempty"`
	OrganizationID string `json:"organizationId,omitempty"`
	GatewayURL     string `json:"gatewayUrl,omitempty"`
	Slots          int    `json:"slots"`
	RuntimeImage   string `json:"runtimeImage"`
	ImageOrigin    string `json:"runtimeImageSource"`
	KeyStore       string `json:"keyStore"`
	Version        string `json:"version"`
	// Limits and Schedule are the resolved governor configuration, so a user
	// can see what a job will be given without opening runner.json.
	Limits   governor.Limits   `json:"limits"`
	Schedule governor.Schedule `json:"schedule"`
	// Runner is the live state of the daemon running on this machine, absent
	// when there is none.
	Runner        *governor.Live `json:"runner,omitempty"`
	UpdateChannel string         `json:"updateChannel"`
	AutoUpdate    bool           `json:"autoUpdate"`
	TrustedKeys   string         `json:"trustedKeys"`
	// FolderSync is the folder-sync state "folders list" also shows, carried
	// here so one command answers "is this machine working" as a whole.
	FolderSync FolderSyncStatus `json:"folderSync"`
}

// FolderSyncStatus is what "status" reports about folder sync.
type FolderSyncStatus struct {
	// Ready is false when folder sync cannot run at all (no apiBaseUrl, not
	// paired), and Problem says why in one sentence.
	Ready   bool   `json:"ready"`
	Problem string `json:"problem,omitempty"`
	Folder  string `json:"folder,omitempty"`
	Links   int    `json:"links"`
	Paused  int    `json:"paused,omitempty"`
	// Running is true when a live status document is fresh.
	Running bool   `json:"running"`
	Summary string `json:"summary,omitempty"`
}

type statusFlags struct {
	asJSON bool
}

func newStatusFlags(o *statusFlags) *flag.FlagSet {
	fs := flagSet("status", "show the local configuration and device state")
	fs.BoolVar(&o.asJSON, "json", false, "print JSON")
	return fs
}

func cmdStatus(args []string) error {
	var o statusFlags
	fs := newStatusFlags(&o)
	_ = fs.Parse(args)

	e, err := loadEnv(nil)
	if err != nil {
		return err
	}
	img := runtimeimage.Resolve(e.cfg.RuntimeImage, os.Getenv(daemon.RuntimeImageEnv))
	mem, _ := caps.SystemFacts(e.cfg.ScratchRoot)
	limits := e.cfg.Limits.Resolve(governor.Host{NumCPU: runtime.NumCPU(), MemTotalBytes: mem})
	rep := statusReport{
		ConfigPath:     e.cfgPath,
		Paired:         e.cfg.IsPaired(),
		DeviceID:       e.cfg.DeviceID,
		DeviceName:     e.cfg.DeviceName,
		OrganizationID: e.cfg.OrganizationID,
		GatewayURL:     e.cfg.GatewayURL,
		Slots:          e.cfg.Slots,
		RuntimeImage:   img.Ref,
		ImageOrigin:    img.Origin,
		KeyStore:       e.keys.Describe(),
		Version:        Version,
		Limits:         limits,
		Schedule:       e.cfg.Schedule,
		UpdateChannel:  e.cfg.UpdateChannelOr(),
		AutoUpdate:     e.cfg.UpdatesEnabled(),
		TrustedKeys:    trustedKeyNames(),
	}
	rep.FolderSync = readFolderSync(e)
	if c, err := controlChannel(); err == nil {
		if live, err := c.ReadLive(); err == nil && live.Fresh(time.Now()) {
			rep.Runner = &live
		}
	}
	if o.asJSON {
		return printJSON(rep)
	}
	fmt.Printf("config      %s\n", rep.ConfigPath)
	fmt.Printf("version     %s\n", rep.Version)
	if rep.Paired {
		fmt.Printf("paired      yes\n")
		fmt.Printf("device      %s (%s)\n", rep.DeviceID, rep.DeviceName)
		fmt.Printf("workspace   %s\n", rep.OrganizationID)
		fmt.Printf("gateway     %s\n", rep.GatewayURL)
	} else {
		fmt.Printf("paired      no (run \"reelbolt-runner pair\")\n")
	}
	fmt.Printf("slots       %d\n", rep.Slots)
	fmt.Printf("image       %s (%s)\n", rep.RuntimeImage, rep.ImageOrigin)
	fmt.Printf("limits      %s\n", rep.Limits.Describe())
	fmt.Printf("schedule    %s\n", rep.Schedule.Describe())
	if rep.Runner != nil {
		fmt.Printf("running     yes (pid %d, since %s)\n", rep.Runner.PID, rep.Runner.StartedAt)
		printGovernorStatus(rep.Runner.Status)
	} else {
		fmt.Printf("running     no\n")
	}
	fmt.Printf("key store   %s\n", rep.KeyStore)
	printFolderSyncStatus(rep.FolderSync)
	fmt.Printf("updates     %s channel, auto-update %s, keys %s\n",
		rep.UpdateChannel, onOff(rep.AutoUpdate), rep.TrustedKeys)
	return nil
}

// readFolderSync assembles the folder-sync half of "status". It never fails:
// "folder sync is off, and here is why" is a fact worth printing.
func readFolderSync(e *env) FolderSyncStatus {
	fs := FolderSyncStatus{}
	if problem := syncUnavailable(e); problem != nil {
		fs.Problem = problem.Error()
	} else {
		fs.Ready = true
	}
	store, err := folderStore()
	if err != nil {
		if fs.Problem == "" {
			fs.Problem = err.Error()
		}
		return fs
	}
	links, err := store.Load()
	if err != nil {
		fs.Problem = "the list of synced folders could not be read: " + err.Error()
		return fs
	}
	fs.Links = len(links)
	for _, l := range links {
		if l.Paused {
			fs.Paused++
		}
	}
	if path, err := folderStatusPath(); err == nil {
		if st, fresh, err := foldersync.ReadStatus(path, 3*time.Minute); err == nil && fresh {
			fs.Running = true
			fs.Summary = st.Summary()
			if st.Problem != "" {
				fs.Problem = st.Problem
				fs.Ready = false
			}
		}
	}
	return fs
}

func printFolderSyncStatus(fs FolderSyncStatus) {
	switch {
	case fs.Problem != "":
		fmt.Printf("folders     not running: %s\n", fs.Problem)
	case fs.Links == 0:
		fmt.Printf("folders     none synced (reelbolt-runner folders add <path> --project <id>)\n")
	case fs.Running && fs.Summary != "":
		fmt.Printf("folders     %d synced, %s\n", fs.Links, strings.TrimPrefix(fs.Summary, "Folder sync: "))
	case !fs.Running:
		fmt.Printf("folders     %d synced, not running\n", fs.Links)
	default:
		fmt.Printf("folders     %d synced\n", fs.Links)
	}
}

// trustedKeyNames names the keys this build can verify a release with. It never
// fails: "none" is a fact worth printing, not an error in a status command.
func trustedKeyNames() string {
	keys, err := update.TrustedKeys()
	if err != nil {
		return "none (this build refuses to update)"
	}
	return update.DescribeKeys(keys)
}

func onOff(on bool) string {
	if on {
		return "on"
	}
	return "off"
}

// ---- doctor ----

// doctorReport is the machine-readable form of "doctor".
type doctorReport struct {
	ConfigPath   string              `json:"configPath"`
	Paired       bool                `json:"paired"`
	KeyStore     string              `json:"keyStore"`
	KeyPresent   bool                `json:"deviceKeyPresent"`
	Image        runtimeimage.Source `json:"runtimeImage"`
	Capabilities json.RawMessage     `json:"capabilities"`
	Inventory    json.RawMessage     `json:"inventory"`
	JobTypes     []string            `json:"jobTypes"`
	Warnings     []string            `json:"warnings,omitempty"`
}

type doctorFlags struct {
	asJSON bool
	slots  int
}

func newDoctorFlags(o *doctorFlags) *flag.FlagSet {
	fs := flagSet("doctor", "probe this machine and print what it can run")
	fs.BoolVar(&o.asJSON, "json", false, "print JSON")
	fs.IntVar(&o.slots, "slots", 0, "slots to report in the capability document")
	return fs
}

func cmdDoctor(args []string) error {
	var o doctorFlags
	fs := newDoctorFlags(&o)
	_ = fs.Parse(args)

	e, err := loadEnv(nil)
	if err != nil {
		return err
	}
	if o.slots > 0 {
		e.cfg.Slots = o.slots
	}

	prober := &daemon.SystemProber{Log: func(format string, args ...any) { log.Printf(format, args...) }}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	res, err := prober.Probe(ctx, daemon.ProbeRequest{
		Version:        Version,
		FontsetVersion: FontsetVersion,
		Slots:          e.cfg.Slots,
		Image:          e.cfg.RuntimeImage,
		ContainerCLI:   e.cfg.ContainerCLI,
		FfmpegPath:     daemon.DefaultFfmpegPath,
		ScratchRoot:    e.cfg.ScratchRoot,
	})
	if err != nil {
		return err
	}

	var warnings []string
	if res.Image.Origin == "development default" {
		warnings = append(warnings, "no runtime image is pinned or configured; using "+res.Image.Ref)
	}
	for _, r := range res.Reports {
		if !r.Usable {
			warnings = append(warnings, fmt.Sprintf("%s %s: %s %s",
				r.Candidate.Kind, r.Candidate.Source, r.Failure, r.Detail))
		} else if !r.RemotionReady {
			warnings = append(warnings, fmt.Sprintf("%s is usable but Remotion is not ready: %s %s",
				r.Candidate.Kind, r.Failure, r.Detail))
		}
	}
	keyPresent := false
	if e.cfg.IsPaired() {
		if _, err := e.keys.Load(e.cfg.DeviceID); err != nil {
			warnings = append(warnings, "the device key is missing: "+err.Error())
		} else {
			keyPresent = true
		}
	}

	capsJSON, err := json.Marshal(res.Capabilities)
	if err != nil {
		return err
	}
	invJSON, err := json.Marshal(res.Inventory)
	if err != nil {
		return err
	}
	rep := doctorReport{
		ConfigPath:   e.cfgPath,
		Paired:       e.cfg.IsPaired(),
		KeyStore:     e.keys.Describe(),
		KeyPresent:   keyPresent,
		Image:        res.Image,
		Capabilities: capsJSON,
		Inventory:    invJSON,
		JobTypes:     res.Capabilities.JobTypes,
		Warnings:     warnings,
	}

	if o.asJSON {
		if err := printJSON(rep); err != nil {
			return err
		}
	} else {
		fmt.Printf("config       %s\n", rep.ConfigPath)
		fmt.Printf("paired       %t\n", rep.Paired)
		fmt.Printf("key store    %s\n", rep.KeyStore)
		if rep.Paired {
			fmt.Printf("device key   %s\n", presentOrMissing(rep.KeyPresent))
		}
		fmt.Printf("image        %s (%s)\n", res.Image.Ref, res.Image.Origin)
		fmt.Printf("ffmpeg       %s\n", res.Capabilities.FfmpegVersion)
		fmt.Printf("runtime      %s\n", describeRuntimeFull(res))
		fmt.Printf("job types    %s\n", joinOrNone(res.Capabilities.JobTypes))
		fmt.Printf("capabilities %s\n", capsJSON)
		fmt.Printf("inventory    %s\n", invJSON)
		if len(warnings) > 0 {
			fmt.Println("warnings:")
			for _, w := range warnings {
				fmt.Printf("  - %s\n", w)
			}
		}
	}
	if len(res.Capabilities.JobTypes) == 0 {
		return errors.New("this machine can run no job type: no usable ffmpeg and no verified container runtime")
	}
	return nil
}

func describeRuntimeFull(res daemon.ProbeResult) string {
	if res.Runtime == nil {
		return "none"
	}
	s := fmt.Sprintf("%s %s (%s) at %s", res.Runtime.Candidate.Kind, res.Runtime.Version,
		res.Runtime.Mode, res.Runtime.Candidate.Binary)
	if !res.Runtime.RemotionReady {
		s += " - Remotion not ready: " + res.Runtime.Failure + " " + res.Runtime.Detail
	}
	return s
}

// ---- unpair ----

type unpairFlags struct {
	forgetKey bool
}

func newUnpairFlags(o *unpairFlags) *flag.FlagSet {
	fs := flagSet("unpair", "forget the device identity")
	fs.BoolVar(&o.forgetKey, "forget-key", false, "also delete the device key from the key store")
	return fs
}

func cmdUnpair(args []string) error {
	var o unpairFlags
	fs := newUnpairFlags(&o)
	_ = fs.Parse(args)

	e, err := loadEnv(nil)
	if err != nil {
		return err
	}
	if e.cfg.DeviceID == "" {
		fmt.Println("this machine is not paired; nothing to do")
		return nil
	}
	device := e.cfg.DeviceID

	// Ask ReelBolt to remove this device from its workspace first, signed with
	// the device's own key, so the dashboard stops listing it. Nothing it says
	// stops the local unpair.
	removed := forgetDevice(e)

	e.cfg.DeviceID = ""
	e.cfg.OrganizationID = ""
	if err := e.cfg.Save(); err != nil {
		return fmt.Errorf("write %s: %w", e.cfgPath, err)
	}
	if removed {
		fmt.Printf("unpaired device %s; ReelBolt removed it from the workspace\n", device)
	} else {
		fmt.Printf("unpaired device %s on this machine; it may still be listed in the workspace, so remove it there if it is\n", device)
	}
	if o.forgetKey {
		if err := e.keys.Delete(device); err != nil {
			return fmt.Errorf("delete the device key: %w", err)
		}
		fmt.Println("device key deleted")
		return nil
	}
	fmt.Println("the device key was kept; pairing again creates a new one either way")
	return nil
}

// forgetUnpairTimeout bounds the "remove me" call: an unpair never waits long
// on the network.
const forgetUnpairTimeout = 10 * time.Second

// forgetDevice calls the device API's "forget me" and reports whether the
// device is known to be gone from its workspace. "Not connected" means it
// already was; an older server's 404, a missing API origin or a network
// failure mean nothing is known to have changed.
func forgetDevice(e *env) bool {
	origin, err := apiOrigin(e.cfg)
	if err != nil {
		fmt.Fprintf(os.Stderr, "note: cannot ask ReelBolt to remove this device: %v\n", err)
		return false
	}
	key, err := deviceKey(e)
	if err != nil {
		fmt.Fprintf(os.Stderr, "note: cannot ask ReelBolt to remove this device: %v\n", err)
		return false
	}
	ctx, cancel := context.WithTimeout(context.Background(), forgetUnpairTimeout)
	defer cancel()
	err = deviceapi.New(origin, e.cfg.DeviceID, key).ForgetMe(ctx)
	switch {
	case err == nil, errors.Is(err, deviceapi.ErrNotConnected):
		return true
	default:
		fmt.Fprintf(os.Stderr, "note: ReelBolt did not remove this device: %v\n", err)
		return false
	}
}

// ---- helpers ----

// openLogger returns the daemon's logger: always stderr, plus a file when one
// is configured. The file is appended to and created 0600, because a runner log
// names paths on the user's disk.
func openLogger(path string) (*log.Logger, func(), error) {
	if path == "" {
		return log.New(os.Stderr, "", log.LstdFlags|log.LUTC), func() {}, nil
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return nil, nil, fmt.Errorf("open the log file: %w", err)
	}
	return log.New(io.MultiWriter(os.Stderr, f), "", log.LstdFlags|log.LUTC),
		func() { _ = f.Close() }, nil
}

// fatal reports a flag-level mistake and exits with the usage code.
func fatal(err error) {
	fmt.Fprintf(os.Stderr, "reelbolt-runner: %v\n", err)
	os.Exit(exitUsage)
}

func printJSON(v any) error {
	raw, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	fmt.Println(string(raw))
	return nil
}

func shortID(id string) string {
	if len(id) <= 8 {
		return id
	}
	return id[:8]
}

func presentOrMissing(ok bool) string {
	if ok {
		return "present"
	}
	return "MISSING"
}

func joinOrNone(list []string) string {
	if len(list) == 0 {
		return "(none)"
	}
	out := ""
	for i, s := range list {
		if i > 0 {
			out += ", "
		}
		out += s
	}
	return out
}

// ---- update ----

// updateFlags configure "update". The command is the manual half of E9; the
// daemon's background half uses the same package with the job-activity gate
// installed.
type updateFlags struct {
	check    bool
	apply    bool
	rollback bool
	channel  string
	baseURL  string
	devHost  string
	asJSON   bool
}

func newUpdateFlags(o *updateFlags) *flag.FlagSet {
	fs := flagSet("update", "check for, verify and install a signed runner release")
	fs.BoolVar(&o.check, "check", false, "report what is available without downloading or installing anything")
	fs.BoolVar(&o.apply, "apply", false, "download, verify and install the release")
	fs.BoolVar(&o.rollback, "rollback", false, "restore the binary the last update replaced")
	fs.StringVar(&o.channel, "channel", "", "release channel: stable or beta")
	fs.StringVar(&o.baseURL, "update-base-url", "", "download host to use instead of the product's")
	fs.StringVar(&o.devHost, "dev-update-host", "", "also accept this update host (non-release builds only)")
	fs.BoolVar(&o.asJSON, "json", false, "print JSON")
	return fs
}

// updateReport is the machine-readable form of "update".
type updateReport struct {
	ConfigPath     string `json:"configPath"`
	Channel        string `json:"channel"`
	Host           string `json:"updateHost"`
	TrustedKeys    string `json:"trustedKeys"`
	CurrentVersion string `json:"currentVersion"`
	InstallPath    string `json:"installPath,omitempty"`
	Action         string `json:"action"`
	ReleaseVersion string `json:"releaseVersion,omitempty"`
	Reason         string `json:"reason"`
	Applied        bool   `json:"applied,omitempty"`
	RestartNeeded  bool   `json:"restartRequired,omitempty"`
	Backup         string `json:"backupPath,omitempty"`
	Notify         string `json:"notify,omitempty"`
	StopTakingJobs bool   `json:"stopTakingJobs,omitempty"`
	RolloutBucket  int    `json:"rolloutBucket,omitempty"`
	RolloutPercent int    `json:"rolloutPercent,omitempty"`
	RolledBack     bool   `json:"rolledBack,omitempty"`
}

func cmdUpdate(args []string) error {
	var o updateFlags
	fs := newUpdateFlags(&o)
	_ = fs.Parse(args)

	if o.check && o.apply {
		return errors.New("--check and --apply are opposites; pick one")
	}
	e, err := loadEnv(func(c *config.Config) {
		if o.channel != "" {
			c.UpdateChannel = o.channel
		}
		if o.baseURL != "" {
			c.UpdateBaseURL = o.baseURL
		}
		if o.devHost != "" {
			if err := applyDevUpdateHost(c, o.devHost); err != nil {
				fatal(err)
			}
		}
	})
	if err != nil {
		return err
	}

	// The rollback path needs no key and no network: it restores a file that
	// was already verified when it was installed.
	if o.rollback {
		install, err := update.CurrentInstall()
		if err != nil {
			return err
		}
		up := &update.Updater{Install: install}
		if err := up.Rollback(); err != nil {
			return err
		}
		rep := updateReport{
			ConfigPath:  e.cfgPath,
			Channel:     e.cfg.UpdateChannelOr(),
			InstallPath: install.Path,
			Action:      "rolled-back",
			Reason:      "the previous binary was restored; restart the runner to run it",
			RolledBack:  true,
		}
		return printUpdateReport(rep, o.asJSON)
	}

	channel, err := update.ParseChannel(e.cfg.UpdateChannelOr())
	if err != nil {
		return err
	}
	// The trust root is checked before the network is touched: a build with no
	// release key cannot verify anything, and saying so is more useful than a
	// failed fetch.
	keys, err := update.TrustedKeys()
	if err != nil {
		return err
	}
	install, err := update.CurrentInstall()
	if err != nil {
		return err
	}
	host := &update.Host{
		BaseURL:  e.cfg.UpdateBaseURL,
		Channel:  channel,
		DevHosts: e.cfg.DevUpdateHosts,
	}
	manifestURL, err := host.ManifestURL()
	if err != nil {
		return err
	}
	deviceID := e.cfg.DeviceID
	up := &update.Updater{
		Host:     host,
		Keys:     keys,
		Platform: update.CurrentPlatform(Version, deviceID),
		Install:  install,
		Log:      func(format string, args ...any) { log.Printf(format, args...) },
	}

	rep := updateReport{
		ConfigPath:     e.cfgPath,
		Channel:        channel,
		Host:           manifestURL,
		TrustedKeys:    update.DescribeKeys(keys),
		CurrentVersion: Version,
		InstallPath:    install.Path,
	}

	if o.apply {
		out, err := up.RunOnce(context.Background())
		if err != nil {
			return err
		}
		rep.Action = string(out.Action)
		rep.ReleaseVersion = out.Version
		rep.Reason = out.Reason
		rep.Applied = out.Applied
		rep.RestartNeeded = out.RestartRequired
		rep.Backup = out.Backup
		rep.Notify = out.Notify
		return printUpdateReport(rep, o.asJSON)
	}

	_, d, err := up.Check(context.Background())
	if err != nil {
		return err
	}
	rep.Action = string(d.Action)
	rep.ReleaseVersion = d.Version
	rep.Reason = d.Reason
	rep.StopTakingJobs = d.StopTakingJobs
	rep.RolloutBucket = d.Bucket
	rep.RolloutPercent = d.RolloutPercent
	return printUpdateReport(rep, o.asJSON)
}

func printUpdateReport(rep updateReport, asJSON bool) error {
	if asJSON {
		return printJSON(rep)
	}
	fmt.Printf("version      %s\n", rep.CurrentVersion)
	fmt.Printf("channel      %s\n", rep.Channel)
	if rep.Host != "" {
		fmt.Printf("host         %s\n", rep.Host)
	}
	if rep.InstallPath != "" {
		fmt.Printf("install      %s\n", rep.InstallPath)
	}
	fmt.Printf("trusted keys %s\n", rep.TrustedKeys)
	fmt.Printf("action       %s\n", rep.Action)
	if rep.ReleaseVersion != "" {
		fmt.Printf("release      %s\n", rep.ReleaseVersion)
	}
	if rep.RolloutPercent > 0 {
		fmt.Printf("rollout      %d%% (this device is bucket %d)\n", rep.RolloutPercent, rep.RolloutBucket)
	}
	fmt.Printf("%s\n", rep.Reason)
	if rep.Notify != "" {
		fmt.Printf("%s\n", rep.Notify)
	}
	if rep.Applied && rep.RestartNeeded {
		fmt.Println("restart the runner to run the new version")
	}
	if rep.RolledBack {
		fmt.Println("rolled back")
	}
	if rep.StopTakingJobs {
		fmt.Println("WARNING: this runner is below the supported version and must not take jobs until it is updated")
	}
	return nil
}

// applyDevUpdateHost records an update host a development build may use.
func applyDevUpdateHost(cfg *config.Config, raw string) error {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil
	}
	host := hostOf(raw)
	if host == "" {
		return fmt.Errorf("--dev-update-host %q is not a URL or host", raw)
	}
	if !contains(cfg.DevUpdateHosts, host) {
		cfg.DevUpdateHosts = append(cfg.DevUpdateHosts, host)
	}
	return nil
}

// newUpdater builds the daemon's auto-updater.
//
// A build with no release key compiled in cannot verify anything, so it gets no
// updater: the daemon logs why and keeps serving jobs. That is the state of
// every build made before the release pipeline (E10b) stamps a key.
func newUpdater(e *env) (*update.Updater, error) {
	channel, err := update.ParseChannel(e.cfg.UpdateChannelOr())
	if err != nil {
		return nil, err
	}
	keys, err := update.TrustedKeys()
	if err != nil {
		return nil, err
	}
	install, err := update.CurrentInstall()
	if err != nil {
		return nil, err
	}
	return &update.Updater{
		Host: &update.Host{
			BaseURL:  e.cfg.UpdateBaseURL,
			Channel:  channel,
			DevHosts: e.cfg.DevUpdateHosts,
		},
		Keys:     keys,
		Platform: update.CurrentPlatform(Version, e.cfg.DeviceID),
		Install:  install,
		Log:      func(format string, args ...any) { log.Printf(format, args...) },
	}, nil
}
