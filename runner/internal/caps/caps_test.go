package caps

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/runtime"
)

const digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
const image = "ghcr.io/reelbolt/reelbolt-sandbox-runtime@" + digest

func ready(kind runtime.Kind, mode runtime.Mode) *runtime.Report {
	return &runtime.Report{
		Candidate: runtime.Candidate{Kind: kind}, Mode: mode, Version: "27.3.1",
		Usable: true, RemotionReady: true,
	}
}

func TestBuild(t *testing.T) {
	base := Inputs{
		AgentVersion: "0.1.0", OS: "linux", Arch: "amd64", Slots: 2,
		FontsetVersion: "1", MaxOutputBytes: 4294967296, FfmpegVersion: "7.1", Image: image,
	}
	tests := []struct {
		name          string
		mut           func(*Inputs)
		container     string
		mode          string
		digestSet     bool
		jobs          []string
		wantNoRuntime bool
	}{
		{
			name:      "docker rootless remotion ready",
			mut:       func(in *Inputs) { in.Runtime = ready(runtime.KindDocker, runtime.ModeRootless) },
			container: "docker", mode: "rootless", digestSet: true,
			jobs: []string{JobRemotion, JobVideoCompile, JobVideoAnalyzeExt},
		},
		{
			name:      "podman vm remotion ready",
			mut:       func(in *Inputs) { in.Runtime = ready(runtime.KindPodman, runtime.ModeVM) },
			container: "podman", mode: "vm", digestSet: true,
			jobs: []string{JobRemotion, JobVideoCompile, JobVideoAnalyzeExt},
		},
		{
			name:      "no container runtime: ffmpeg only, no error",
			mut:       func(in *Inputs) { in.Runtime = nil },
			container: "none", jobs: []string{JobVideoCompile, JobVideoAnalyzeExt},
		},
		{
			name: "runtime usable but image not pulled: no remotion, null digest",
			mut: func(in *Inputs) {
				r := ready(runtime.KindDocker, runtime.ModeRootful)
				r.RemotionReady, r.Failure = false, runtime.FailImageMissing
				in.Runtime = r
			},
			container: "docker", mode: "rootful", jobs: []string{JobVideoCompile, JobVideoAnalyzeExt},
		},
		{
			name: "remotion ready but image ref is not digest-pinned",
			mut: func(in *Inputs) {
				in.Runtime = ready(runtime.KindDocker, runtime.ModeRootless)
				in.Image = "ghcr.io/reelbolt/reelbolt-sandbox-runtime:1.0.0"
			},
			container: "docker", mode: "rootless", jobs: []string{JobVideoCompile, JobVideoAnalyzeExt},
		},
		{
			name: "runtime failed probe is treated as none",
			mut: func(in *Inputs) {
				in.Runtime = &runtime.Report{Candidate: runtime.Candidate{Kind: runtime.KindDocker}, Failure: runtime.FailUnreachable}
			},
			container: "none", jobs: []string{JobVideoCompile, JobVideoAnalyzeExt},
		},
		{
			name: "no ffmpeg: no job types at all",
			mut: func(in *Inputs) {
				in.FfmpegVersion = ""
				in.Runtime = ready(runtime.KindDocker, runtime.ModeRootless)
			},
			container: "docker", mode: "rootless", digestSet: true, jobs: []string{JobRemotion},
		},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			in := base
			tc.mut(&in)
			d := Build(in)
			if d.Container != tc.container || d.ContainerMode != tc.mode {
				t.Errorf("container/mode = %q/%q, want %q/%q", d.Container, d.ContainerMode, tc.container, tc.mode)
			}
			if (d.RuntimeImageDigest != nil) != tc.digestSet {
				t.Errorf("digest set = %v, want %v", d.RuntimeImageDigest != nil, tc.digestSet)
			}
			if d.RuntimeImageDigest != nil && *d.RuntimeImageDigest != digest {
				t.Errorf("digest = %q", *d.RuntimeImageDigest)
			}
			if !reflect.DeepEqual(d.JobTypes, tc.jobs) {
				t.Errorf("jobTypes = %v, want %v", d.JobTypes, tc.jobs)
			}
			// D1 invariant: container none must never advertise Remotion.
			if d.Container == "none" {
				for _, j := range d.JobTypes {
					if j == JobRemotion {
						t.Error("container none advertises remotion")
					}
				}
			}
			if d.JobTypes == nil {
				t.Error("jobTypes must marshal as [], not null")
			}
		})
	}
}

// fixtureCaps loads payload.capabilities from a protocol fixture, so the Go
// document is held to the same bytes the gateway's own tests use.
func fixtureCaps(t *testing.T, name string) map[string]any {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join("..", "..", "..", "sandbox", "protocol", "fixtures", name))
	if err != nil {
		t.Fatal(err)
	}
	var env struct {
		Payload struct {
			Capabilities map[string]any `json:"capabilities"`
		} `json:"payload"`
	}
	if err := json.Unmarshal(raw, &env); err != nil {
		t.Fatal(err)
	}
	return env.Payload.Capabilities
}

func asMap(t *testing.T, d Document) map[string]any {
	t.Helper()
	raw, err := json.Marshal(d)
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatal(err)
	}
	return m
}

func TestDocumentMatchesProtocolFixtures(t *testing.T) {
	t.Run("hello.desktop.json", func(t *testing.T) {
		d := Build(Inputs{
			AgentVersion: "0.1.0", OS: "linux", Arch: "amd64", Slots: 2, FontsetVersion: "1",
			MaxOutputBytes: 4294967296, FfmpegVersion: "7.1", Image: image,
			Runtime: ready(runtime.KindDocker, runtime.ModeRootless),
		})
		if got, want := asMap(t, d), fixtureCaps(t, "hello.desktop.json"); !reflect.DeepEqual(got, want) {
			t.Errorf("document differs from fixture\n got: %v\nwant: %v", got, want)
		}
	})
	t.Run("hello.no-container.json", func(t *testing.T) {
		d := Build(Inputs{
			AgentVersion: "0.1.0", OS: "darwin", Arch: "arm64", Slots: 2, FontsetVersion: "1",
			MaxOutputBytes: 4294967296, FfmpegVersion: "7.1", Image: image,
		})
		if got, want := asMap(t, d), fixtureCaps(t, "hello.no-container.json"); !reflect.DeepEqual(got, want) {
			t.Errorf("document differs from fixture\n got: %v\nwant: %v", got, want)
		}
	})
}

func TestParseFFmpegVersion(t *testing.T) {
	for in, want := range map[string]string{
		"ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers\nbuilt with gcc": "7.1",
		"ffmpeg version n7.0.2-static https://johnvansickle.com/ffmpeg/":                   "n7.0.2-static",
		"":                          "",
		"something else entirely\n": "",
	} {
		if got := ParseFFmpegVersion(in); got != want {
			t.Errorf("ParseFFmpegVersion(%q) = %q, want %q", in, got, want)
		}
	}
}

const encodersOut = `Encoders:
 V..... = Video
 A..... = Audio
 ------
 V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)
 V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
 V....D h264_vaapi           H.264/AVC (VAAPI) (codec h264)
 A....D libmp3lame           libmp3lame MP3 (MPEG audio layer 3) (codec mp3)
 A....D aac                  AAC (Advanced Audio Coding)
 V....D h264_mf              H264 via MediaFoundation
`

func TestParseEncoders(t *testing.T) {
	all, hw := ParseEncoders(encodersOut)
	if want := []string{"libx264", "h264_nvenc", "h264_vaapi", "libmp3lame", "aac", "h264_mf"}; !reflect.DeepEqual(all, want) {
		t.Errorf("all = %v, want %v", all, want)
	}
	if want := []string{"h264_nvenc", "h264_vaapi", "h264_mf"}; !reflect.DeepEqual(hw, want) {
		t.Errorf("hardware = %v, want %v", hw, want)
	}
}

type scriptRunner map[string]struct {
	out string
	err error
}

func (s scriptRunner) Run(_ context.Context, _ []string, name string, args ...string) ([]byte, error) {
	line := name + " " + strings.Join(args, " ")
	for k, v := range s {
		if strings.Contains(line, k) {
			return []byte(v.out), v.err
		}
	}
	return nil, errors.New("unexpected: " + line)
}

func TestProbeFFmpeg(t *testing.T) {
	t.Run("present", func(t *testing.T) {
		r := scriptRunner{
			"-version":  {out: "ffmpeg version 7.1 Copyright\n"},
			"-encoders": {out: encodersOut},
		}
		f := ProbeFFmpeg(context.Background(), r, "ffmpeg")
		if f.Version != "7.1" || len(f.Encoders) != 6 || len(f.HardwareEncoders) != 3 {
			t.Errorf("got %+v", f)
		}
	})
	t.Run("missing binary", func(t *testing.T) {
		r := scriptRunner{"-version": {err: errors.New("not found")}}
		if f := ProbeFFmpeg(context.Background(), r, "ffmpeg"); f.Version != "" || f.Encoders != nil {
			t.Errorf("got %+v, want zero", f)
		}
	})
	t.Run("version unparsable", func(t *testing.T) {
		r := scriptRunner{"-version": {out: "garbage"}}
		if f := ProbeFFmpeg(context.Background(), r, "ffmpeg"); f.Version != "" {
			t.Errorf("got %+v, want zero", f)
		}
	})
	t.Run("encoders fail but version ok", func(t *testing.T) {
		r := scriptRunner{
			"-version":  {out: "ffmpeg version 6.1\n"},
			"-encoders": {err: errors.New("boom")},
		}
		if f := ProbeFFmpeg(context.Background(), r, "ffmpeg"); f.Version != "6.1" || f.Encoders != nil {
			t.Errorf("got %+v", f)
		}
	})
}

func TestBuildInventory(t *testing.T) {
	inv := BuildInventory(InventoryInputs{CPUCores: 8, MemTotal: 16 << 30, DiskFree: 100 << 30,
		Runtime: ready(runtime.KindPodman, runtime.ModeVM)})
	if inv.RuntimeKind != "podman" || inv.RuntimeMode != "vm" || inv.CPUCores != 8 {
		t.Errorf("got %+v", inv)
	}
	inv = BuildInventory(InventoryInputs{Reports: []runtime.Report{{Failure: runtime.FailUnreachable}}})
	if inv.RuntimeKind != "none" || inv.RuntimeFault != runtime.FailUnreachable {
		t.Errorf("got %+v", inv)
	}
}

func TestSystemFactsSmoke(t *testing.T) {
	// Values are platform-specific; only assert the call is safe, including
	// for a directory that does not exist.
	_, _ = SystemFacts(t.TempDir())
	_, _ = SystemFacts(filepath.Join(t.TempDir(), "missing"))
}

func TestLocalLLMIsAdvertisedOnlyWhenEnabled(t *testing.T) {
	base := Build(Inputs{Slots: 1, OS: "linux", Arch: "amd64"})
	if base.LocalLLM != nil {
		t.Fatal("a runner that never opted in advertises localLlm")
	}
	off := Build(Inputs{Slots: 1, LocalLLM: &protocol.LocalLLMCapabilities{Enabled: false, Models: []string{"x"}}})
	if off.LocalLLM != nil {
		t.Fatal("a disabled share was advertised")
	}
	on := Build(Inputs{Slots: 1, LocalLLM: &protocol.LocalLLMCapabilities{Enabled: true, ServerKind: "ollama", Models: []string{"x"}, MaxConcurrent: 2}})
	if on.LocalLLM == nil || !on.LocalLLM.Enabled || on.LocalLLM.Models[0] != "x" {
		t.Fatalf("enabled share missing: %+v", on.LocalLLM)
	}
	raw, _ := json.Marshal(base)
	if strings.Contains(string(raw), "localLlm") {
		t.Fatalf("absent share still serialised: %s", raw)
	}
}
