package governor

import (
	"encoding/json"
	"errors"
	"fmt"
	"path"
	"strconv"
	"strings"
)

// ByteSize is a byte count that may be written either as a JSON number or as a
// string with a unit: "4g", "512m", "2GiB".
//
// The string form exists because that is how a container runtime's own flags
// are written and how a person thinks about a memory cap; the number form
// exists because a generated configuration writes numbers.
type ByteSize int64

// UnmarshalJSON accepts a number, a unit string, or null.
func (b *ByteSize) UnmarshalJSON(raw []byte) error {
	s := strings.TrimSpace(string(raw))
	if s == "null" || s == "" {
		return nil
	}
	if strings.HasPrefix(s, "\"") {
		var str string
		if err := json.Unmarshal(raw, &str); err != nil {
			return err
		}
		n, err := ParseByteSize(str)
		if err != nil {
			return err
		}
		*b = ByteSize(n)
		return nil
	}
	var n int64
	if err := json.Unmarshal(raw, &n); err != nil {
		return fmt.Errorf("a byte size must be a number or a string with a unit: %w", err)
	}
	if n < 0 {
		return fmt.Errorf("a byte size must not be negative, got %d", n)
	}
	*b = ByteSize(n)
	return nil
}

// MarshalJSON writes the number of bytes, so a round trip is lossless.
func (b ByteSize) MarshalJSON() ([]byte, error) { return json.Marshal(int64(b)) }

// Int64 returns the size in bytes.
func (b ByteSize) Int64() int64 { return int64(b) }

// String renders the size the way a person wrote it, using binary units.
func (b ByteSize) String() string {
	n := int64(b)
	if n == 0 {
		return "0"
	}
	for _, u := range []struct {
		suffix string
		scale  int64
	}{{"t", 1 << 40}, {"g", 1 << 30}, {"m", 1 << 20}, {"k", 1 << 10}} {
		if n%u.scale == 0 {
			return strconv.FormatInt(n/u.scale, 10) + u.suffix
		}
	}
	return strconv.FormatInt(n, 10)
}

// ParseByteSize reads "4g", "512m", "2GiB", "1073741824". Units are binary
// (1g = 1 GiB), matching the container runtimes' own convention.
func ParseByteSize(raw string) (int64, error) {
	s := strings.ToLower(strings.TrimSpace(raw))
	if s == "" {
		return 0, errors.New("empty size")
	}
	scale := int64(1)
	for _, u := range []struct {
		suffix string
		scale  int64
	}{
		{"kib", 1 << 10}, {"mib", 1 << 20}, {"gib", 1 << 30}, {"tib", 1 << 40},
		{"kb", 1 << 10}, {"mb", 1 << 20}, {"gb", 1 << 30}, {"tb", 1 << 40},
		{"k", 1 << 10}, {"m", 1 << 20}, {"g", 1 << 30}, {"t", 1 << 40},
		{"b", 1},
	} {
		if strings.HasSuffix(s, u.suffix) {
			scale = u.scale
			s = strings.TrimSuffix(s, u.suffix)
			break
		}
	}
	n, err := strconv.ParseFloat(strings.TrimSpace(s), 64)
	if err != nil {
		return 0, fmt.Errorf("%q is not a size such as 4g or 512m", raw)
	}
	if n < 0 {
		return 0, fmt.Errorf("%q is negative", raw)
	}
	return int64(n * float64(scale)), nil
}

// Priority is how hard a job is allowed to compete with the person using the
// machine. It maps onto the host process's own priority (nice on Unix, a
// priority class on Windows) and onto a container's CPU shares.
type Priority string

const (
	// PriorityNormal is the default: the job competes on equal terms.
	PriorityNormal Priority = "normal"
	// PriorityBelowNormal keeps a render behind interactive work without
	// starving it.
	PriorityBelowNormal Priority = "below-normal"
	// PriorityLow is for a machine that is only lending its idle time.
	PriorityLow Priority = "low"
)

// Priorities lists the values Validate accepts.
func Priorities() []string {
	return []string{string(PriorityNormal), string(PriorityBelowNormal), string(PriorityLow)}
}

// IsKnown reports whether this build understands the priority.
func (p Priority) IsKnown() bool {
	switch p {
	case PriorityNormal, PriorityBelowNormal, PriorityLow:
		return true
	}
	return false
}

// UnixNice is the nice level to give a host process. Zero for normal.
func (p Priority) UnixNice() int {
	switch p {
	case PriorityBelowNormal:
		return 5
	case PriorityLow:
		return 10
	default:
		return 0
	}
}

// ContainerCPUShares is the --cpu-shares weight, relative to the runtime's
// default of 1024. Zero means "leave the runtime's default in place".
func (p Priority) ContainerCPUShares() int {
	switch p {
	case PriorityBelowNormal:
		return 512
	case PriorityLow:
		return 256
	default:
		return 0
	}
}

// Bounds a configured limit must sit inside. They are wide: the point is to
// catch a typo that would wedge a machine or starve a render, not to have an
// opinion about a sensible cap.
const (
	MinMemoryBytes    = 256 << 20
	MaxCPUs           = 64
	MinPIDs           = 16
	MaxPIDs           = 65536
	MaxFFmpegThreads  = 64
	MinDiskQuotaBytes = 512 << 20
	// HostMemoryFraction is the most of the machine's RAM one job may be given.
	// The runtime enforces a memory cap by killing the container, so a cap
	// above the machine's own memory is not a cap at all.
	HostMemoryFraction = 0.8
)

// LimitsConfig is the [limits] block of runner.json: what a user writes.
type LimitsConfig struct {
	// Memory caps one job's container. Empty means DefaultMemoryBytes.
	Memory ByteSize `json:"memory,omitempty"`
	// CPUs caps one job's CPU time. Empty means DefaultCPUs.
	CPUs float64 `json:"cpus,omitempty"`
	// PIDs caps the processes one job may create. Empty means DefaultPIDs.
	PIDs int `json:"pids,omitempty"`
	// FFmpegThreads caps the encoder's thread count.
	FFmpegThreads int `json:"ffmpegThreads,omitempty"`
	// Priority is how the job competes with the user's own work.
	Priority Priority `json:"priority,omitempty"`
	// DiskQuotaBytes caps one job's scratch directory.
	DiskQuotaBytes ByteSize `json:"diskQuotaBytes,omitempty"`
}

// The container caps E5 hard-coded in the daemon, now the defaults a user can
// change. 4 GiB and two cores are enough for a 1080p Remotion render and small
// enough that a laptop stays usable.
const (
	DefaultMemoryBytes    int64 = 4 << 30
	DefaultCPUs                 = 2
	DefaultPIDs                 = 512
	DefaultFFmpegThreads        = 2
	DefaultDiskQuotaBytes int64 = 8 << 30
)

// DefaultLimits is the configuration an unconfigured runner resolves to.
func DefaultLimits() LimitsConfig {
	return LimitsConfig{
		Memory:         ByteSize(DefaultMemoryBytes),
		CPUs:           DefaultCPUs,
		PIDs:           DefaultPIDs,
		FFmpegThreads:  DefaultFFmpegThreads,
		Priority:       PriorityBelowNormal,
		DiskQuotaBytes: ByteSize(DefaultDiskQuotaBytes),
	}
}

// ApplyDefaults fills in every unset limit.
func (c *LimitsConfig) ApplyDefaults() {
	d := DefaultLimits()
	if c.Memory <= 0 {
		c.Memory = d.Memory
	}
	if c.CPUs <= 0 {
		c.CPUs = d.CPUs
	}
	if c.PIDs <= 0 {
		c.PIDs = d.PIDs
	}
	if c.FFmpegThreads <= 0 {
		c.FFmpegThreads = d.FFmpegThreads
	}
	if c.Priority == "" {
		c.Priority = d.Priority
	}
	if c.DiskQuotaBytes <= 0 {
		c.DiskQuotaBytes = d.DiskQuotaBytes
	}
}

// Validate rejects a limit that would wedge a machine.
func (c LimitsConfig) Validate() error {
	if c.Memory < ByteSize(MinMemoryBytes) {
		return fmt.Errorf("memory must be at least %s, got %s", ByteSize(MinMemoryBytes), c.Memory)
	}
	if c.CPUs <= 0 || c.CPUs > MaxCPUs {
		return fmt.Errorf("cpus must be between 0 and %d, got %v", MaxCPUs, c.CPUs)
	}
	if c.PIDs < MinPIDs || c.PIDs > MaxPIDs {
		return fmt.Errorf("pids must be between %d and %d, got %d", MinPIDs, MaxPIDs, c.PIDs)
	}
	if c.FFmpegThreads < 0 || c.FFmpegThreads > MaxFFmpegThreads {
		return fmt.Errorf("ffmpegThreads must be between 0 and %d, got %d", MaxFFmpegThreads, c.FFmpegThreads)
	}
	if !c.Priority.IsKnown() {
		return fmt.Errorf("priority %q is not one of %s", c.Priority, strings.Join(Priorities(), ", "))
	}
	if c.DiskQuotaBytes < ByteSize(MinDiskQuotaBytes) {
		return fmt.Errorf("diskQuotaBytes must be at least %s, got %s", ByteSize(MinDiskQuotaBytes), c.DiskQuotaBytes)
	}
	return nil
}

// Host is what the machine can actually spare.
type Host struct {
	// NumCPU is the machine's core count. Zero means unknown.
	NumCPU int
	// MemTotalBytes is the machine's RAM. Zero means unknown.
	MemTotalBytes uint64
}

// Limits is a resolved, host-aware cap set. It is what the executors read: no
// executor holds a literal, and no limit is decided anywhere else.
type Limits struct {
	MemoryBytes    int64    `json:"memoryBytes"`
	CPUs           float64  `json:"cpus"`
	PIDs           int      `json:"pids"`
	FFmpegThreads  int      `json:"ffmpegThreads"`
	Priority       Priority `json:"priority"`
	DiskQuotaBytes int64    `json:"diskQuotaBytes"`
	// Notes records every value the host clamped, so "status" and the log can
	// say so instead of quietly running something other than what was asked for.
	Notes []string `json:"notes,omitempty"`
}

// Resolve turns a configuration into the caps to apply on this host.
//
// A limit larger than the machine is clamped rather than refused: asking for
// eight cores on a four-core laptop is a mistake, not a misconfiguration worth
// refusing to start over, and the clamp is recorded and reported.
func (c LimitsConfig) Resolve(host Host) Limits {
	c.ApplyDefaults()
	l := Limits{
		MemoryBytes:    c.Memory.Int64(),
		CPUs:           c.CPUs,
		PIDs:           c.PIDs,
		FFmpegThreads:  c.FFmpegThreads,
		Priority:       c.Priority,
		DiskQuotaBytes: c.DiskQuotaBytes.Int64(),
	}
	if host.NumCPU > 0 && l.CPUs > float64(host.NumCPU) {
		l.Notes = append(l.Notes, fmt.Sprintf("cpus %v clamped to the machine's %d cores", l.CPUs, host.NumCPU))
		l.CPUs = float64(host.NumCPU)
	}
	if host.MemTotalBytes > 0 {
		maxMem := int64(float64(host.MemTotalBytes) * HostMemoryFraction)
		if l.MemoryBytes > maxMem {
			l.Notes = append(l.Notes, fmt.Sprintf("memory %s clamped to %d%% of the machine's RAM (%s)",
				ByteSize(l.MemoryBytes), int(HostMemoryFraction*100), ByteSize(maxMem)))
			l.MemoryBytes = maxMem
		}
	}
	if l.FFmpegThreads < 1 {
		l.FFmpegThreads = 1
	}
	if l.FFmpegThreads > MaxFFmpegThreads {
		l.FFmpegThreads = MaxFFmpegThreads
	}
	return l
}

// MemoryArg is the value for a container's --memory flag.
func (l Limits) MemoryArg() string { return ByteSize(l.MemoryBytes).String() }

// CPUArg is the value for a container's --cpus flag. A whole number is written
// without a decimal point, because some runtimes are fussier about "2.0" than
// they should be.
func (l Limits) CPUArg() string {
	if l.CPUs == float64(int64(l.CPUs)) {
		return strconv.FormatInt(int64(l.CPUs), 10)
	}
	return strconv.FormatFloat(l.CPUs, 'f', -1, 64)
}

// Describe is the one-line rendering "status" prints.
func (l Limits) Describe() string {
	s := fmt.Sprintf("memory=%s cpus=%s pids=%d ffmpegThreads=%d priority=%s disk=%s",
		l.MemoryArg(), l.CPUArg(), l.PIDs, l.FFmpegThreads, l.Priority, ByteSize(l.DiskQuotaBytes))
	if len(l.Notes) > 0 {
		s += " (" + strings.Join(l.Notes, "; ") + ")"
	}
	return s
}

// WithFFmpegThreads inserts "-threads N" into an ffmpeg invocation.
//
// argv[0] is the program, as the worker's own CommandRunner contract requires.
// A probe is left alone (ffprobe takes no -threads) and so is an argv that
// already names a thread count: the caller's explicit choice wins. The thread
// count is the one cap a container's --cpus cannot express. The cgroup bounds
// CPU TIME, but ffmpeg still spawns a slice per detected core, and a machine
// whose cores are all busy scheduling 32 encoder threads renders slower than
// one scheduling two.
func (l Limits) WithFFmpegThreads(argv []string) []string {
	if l.FFmpegThreads <= 0 || len(argv) < 2 {
		return argv
	}
	if !isFFmpegProgram(argv[0]) {
		return argv
	}
	for _, a := range argv[1:] {
		if a == "-threads" {
			return argv
		}
	}
	out := make([]string, 0, len(argv)+2)
	out = append(out, argv[0], "-threads", strconv.Itoa(l.FFmpegThreads))
	return append(out, argv[1:]...)
}

// isFFmpegProgram reports whether a program path names ffmpeg (not ffprobe).
func isFFmpegProgram(program string) bool {
	base := strings.ToLower(path.Base(strings.ReplaceAll(program, "\\", "/")))
	return base == "ffmpeg" || base == "ffmpeg.exe"
}

// WithContainerResourceFlags adds this runner's caps to a container invocation.
//
// args is a container CLI's argument vector AFTER the binary name, which is the
// shape both of the daemon's CLI seams already speak. A flag that is already
// present is left alone: the workspace driver sets its own limits from the same
// configuration and the media driver sets none, so this inserts what is missing
// rather than overriding what is there.
func (l Limits) WithContainerResourceFlags(args []string) []string {
	if len(args) == 0 || (args[0] != "run" && args[0] != "create") {
		return args
	}
	out := make([]string, 0, len(args)+10)
	out = append(out, args[0])
	if l.MemoryBytes > 0 && !hasFlag(args, "--memory") {
		out = append(out, "--memory", l.MemoryArg())
	}
	if l.CPUs > 0 && !hasFlag(args, "--cpus") {
		out = append(out, "--cpus", l.CPUArg())
	}
	if l.PIDs > 0 && !hasFlag(args, "--pids-limit") {
		out = append(out, "--pids-limit", strconv.Itoa(l.PIDs))
	}
	// A container has no nice level. CPU shares are the runtime's own version
	// of one: a relative weight against every other container on the machine.
	if shares := l.Priority.ContainerCPUShares(); shares > 0 && !hasFlag(args, "--cpu-shares") {
		out = append(out, "--cpu-shares", strconv.Itoa(shares))
	}
	return append(out, args[1:]...)
}

// hasFlag reports whether a flag token already appears in an argument vector.
func hasFlag(args []string, flag string) bool {
	for _, a := range args {
		if a == flag {
			return true
		}
	}
	return false
}
