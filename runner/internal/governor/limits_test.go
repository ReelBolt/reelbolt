package governor

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestByteSizeParsing(t *testing.T) {
	cases := []struct {
		in   string
		want int64
		bad  bool
	}{
		{"4g", 4 << 30, false},
		{"512m", 512 << 20, false},
		{"2GiB", 2 << 30, false},
		{"1t", 1 << 40, false},
		{"4096", 4096, false},
		{"1.5g", int64(1.5 * float64(1<<30)), false},
		{"", 0, true},
		{"lots", 0, true},
		{"-4g", 0, true},
	}
	for _, tc := range cases {
		t.Run(tc.in, func(t *testing.T) {
			got, err := ParseByteSize(tc.in)
			if tc.bad {
				if err == nil {
					t.Fatalf("ParseByteSize(%q) = %d, want an error", tc.in, got)
				}
				return
			}
			if err != nil {
				t.Fatalf("ParseByteSize(%q): %v", tc.in, err)
			}
			if got != tc.want {
				t.Fatalf("ParseByteSize(%q) = %d, want %d", tc.in, got, tc.want)
			}
		})
	}
}

// A configuration is written by hand and read by both a person and a program:
// the string form a person writes and the number form a generator writes must
// both survive, and a round trip must not change the meaning.
func TestByteSizeJSONAcceptsBothForms(t *testing.T) {
	var c LimitsConfig
	if err := json.Unmarshal([]byte(`{"memory":"4g","cpus":1.5,"pids":256,"ffmpegThreads":3,"priority":"low","diskQuotaBytes":10737418240}`), &c); err != nil {
		t.Fatalf("Unmarshal: %v", err)
	}
	if c.Memory != ByteSize(4<<30) {
		t.Errorf("memory = %s, want 4g", c.Memory)
	}
	if c.DiskQuotaBytes != ByteSize(10<<30) {
		t.Errorf("diskQuotaBytes = %d, want 10GiB", c.DiskQuotaBytes)
	}
	raw, err := json.Marshal(c)
	if err != nil {
		t.Fatal(err)
	}
	var back LimitsConfig
	if err := json.Unmarshal(raw, &back); err != nil {
		t.Fatal(err)
	}
	if back.Memory != c.Memory || back.Priority != c.Priority || back.CPUs != c.CPUs {
		t.Fatalf("round trip changed the configuration: %s -> %+v", raw, back)
	}
}

func TestByteSizeString(t *testing.T) {
	for _, tc := range []struct {
		in   int64
		want string
	}{{4 << 30, "4g"}, {512 << 20, "512m"}, {1 << 40, "1t"}, {4096, "4k"}, {1234, "1234"}} {
		if got := ByteSize(tc.in).String(); got != tc.want {
			t.Errorf("ByteSize(%d) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

func TestLimitsValidateRefusesWhatWouldWedgeAMachine(t *testing.T) {
	base := func() LimitsConfig {
		c := LimitsConfig{}
		c.ApplyDefaults()
		return c
	}
	cases := []struct {
		name string
		mut  func(*LimitsConfig)
	}{
		{"memory too small", func(c *LimitsConfig) { c.Memory = 1 << 20 }},
		{"no cpus", func(c *LimitsConfig) { c.CPUs = 0 }},
		{"absurd cpus", func(c *LimitsConfig) { c.CPUs = 4096 }},
		{"no pids", func(c *LimitsConfig) { c.PIDs = 1 }},
		{"absurd pids", func(c *LimitsConfig) { c.PIDs = 1 << 20 }},
		{"negative threads", func(c *LimitsConfig) { c.FFmpegThreads = -1 }},
		{"unknown priority", func(c *LimitsConfig) { c.Priority = "urgent" }},
		{"tiny disk quota", func(c *LimitsConfig) { c.DiskQuotaBytes = 1 << 20 }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := base()
			tc.mut(&c)
			if err := c.Validate(); err == nil {
				t.Fatalf("Validate accepted %+v", c)
			}
		})
	}
	if err := base().Validate(); err != nil {
		t.Fatalf("the defaults must validate: %v", err)
	}
}

func TestLimitsAreClampedToWhatTheMachineHas(t *testing.T) {
	c := LimitsConfig{Memory: ByteSize(64 << 30), CPUs: 16}
	total := 8 << 30
	l := c.Resolve(Host{NumCPU: 4, MemTotalBytes: uint64(total)})
	if l.CPUs != 4 {
		t.Errorf("cpus = %v, want the machine's 4", l.CPUs)
	}
	if l.MemoryBytes > int64(float64(total)*HostMemoryFraction) {
		t.Errorf("memory = %d, want at most 80%% of the machine's RAM", l.MemoryBytes)
	}
	if len(l.Notes) != 2 {
		t.Fatalf("every clamp must be recorded, got %v", l.Notes)
	}
	if !strings.Contains(l.Describe(), "clamped") {
		t.Errorf("Describe must report the clamps: %s", l.Describe())
	}
}

func TestLimitsOnAnUnknownHostAreLeftAlone(t *testing.T) {
	c := LimitsConfig{}
	l := c.Resolve(Host{})
	if len(l.Notes) != 0 {
		t.Fatalf("an unknown host must not produce clamps: %v", l.Notes)
	}
	if l.MemoryArg() != "4g" || l.CPUArg() != "2" {
		t.Fatalf("defaults were not applied: %s %s", l.MemoryArg(), l.CPUArg())
	}
}

func TestCPUArgHasNoTrailingDecimal(t *testing.T) {
	if got := (Limits{CPUs: 2}).CPUArg(); got != "2" {
		t.Errorf("CPUArg(2) = %q", got)
	}
	if got := (Limits{CPUs: 1.5}).CPUArg(); got != "1.5" {
		t.Errorf("CPUArg(1.5) = %q", got)
	}
}

// ffmpeg -threads is the cap a container's --cpus cannot express, and ffprobe
// has no such option: adding one to a probe would turn every analyse step into
// an argument error.
func TestWithFFmpegThreads(t *testing.T) {
	l := Limits{FFmpegThreads: 2}
	got := l.WithFFmpegThreads([]string{"ffmpeg", "-i", "in.mp4", "out.mp4"})
	want := []string{"ffmpeg", "-threads", "2", "-i", "in.mp4", "out.mp4"}
	if strings.Join(got, " ") != strings.Join(want, " ") {
		t.Fatalf("got %v, want %v", got, want)
	}
	if got := l.WithFFmpegThreads([]string{"ffprobe", "-i", "in.mp4"}); len(got) != 3 {
		t.Fatalf("ffprobe must be left alone: %v", got)
	}
	explicit := []string{"ffmpeg", "-threads", "8", "-i", "in.mp4"}
	if got := l.WithFFmpegThreads(explicit); strings.Join(got, " ") != strings.Join(explicit, " ") {
		t.Fatalf("an explicit thread count wins: %v", got)
	}
	if got := l.WithFFmpegThreads([]string{"ffmpeg"}); len(got) != 1 {
		t.Fatalf("a bare program is left alone: %v", got)
	}
}

// The media package's container runner sets no resource flags of its own, and
// the workspace driver sets three of the four: the flags already present must
// be left alone, not duplicated.
func TestWithContainerResourceFlags(t *testing.T) {
	l := LimitsConfig{}.Resolve(Host{})
	args := []string{"run", "--rm", "--network", "none", "image"}
	got := l.WithContainerResourceFlags(args)
	joined := strings.Join(got, " ")
	for _, want := range []string{"--memory 4g", "--cpus 2", "--pids-limit 512", "--cpu-shares 512"} {
		if !strings.Contains(joined, want) {
			t.Errorf("missing %q in %q", want, joined)
		}
	}
	if got[0] != "run" {
		t.Errorf("the subcommand must stay first: %v", got)
	}

	// Idempotent: applying them twice changes nothing.
	again := l.WithContainerResourceFlags(got)
	if strings.Join(again, " ") != joined {
		t.Fatalf("not idempotent: %q", strings.Join(again, " "))
	}

	// The workspace driver's own vector keeps its flags.
	workspace := []string{"run", "-d", "--name", "c", "--memory", "2g", "--cpus", "1", "--pids-limit", "64", "image"}
	got = l.WithContainerResourceFlags(workspace)
	if strings.Count(strings.Join(got, " "), "--memory") != 1 {
		t.Fatalf("the driver's own memory flag was overridden: %v", got)
	}
	if !strings.Contains(strings.Join(got, " "), "--cpu-shares 512") {
		t.Fatalf("the CPU-shares weight is the one flag the driver lacks: %v", got)
	}

	// Anything that is not a create/run is untouched.
	other := []string{"ps", "-a"}
	if got := l.WithContainerResourceFlags(other); strings.Join(got, " ") != "ps -a" {
		t.Fatalf("unexpected rewrite: %v", got)
	}
}

func TestPriorityMappings(t *testing.T) {
	if (PriorityNormal).UnixNice() != 0 || (PriorityBelowNormal).UnixNice() != 5 || (PriorityLow).UnixNice() != 10 {
		t.Error("unix nice levels are wrong")
	}
	if (PriorityNormal).ContainerCPUShares() != 0 {
		t.Error("a normal priority must leave the runtime default alone")
	}
	if !(Priority("nonsense")).IsKnown() == false {
		t.Error("an unknown priority must not be known")
	}
}
