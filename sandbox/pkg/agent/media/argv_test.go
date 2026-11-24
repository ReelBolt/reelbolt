package media

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// sessionDir builds a session directory with the files a legitimate argv may
// reference, so the "accepted" cases are accepted for the right reason.
func sessionDir(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	files := map[string]string{
		"clip.mp4":             "media",
		"sub.srt":              "1\n00:00:00,000 --> 00:00:01,000\nhi\n",
		"font.ttf":             "font",
		"list.txt":             "file 'clip.mp4'\nfile '/etc/passwd'\n",
		"safe-list.txt":        "file 'clip.mp4'\n# a comment\n",
		"nested/inner.mp4":     "media",
		"safe-list-nested.txt": "file 'nested/inner.mp4'\n",
		"concat.txt":           "file 'seg-0.mp4'\nfile 'seg-1.mp4'\n",
		"list.ffconcat":        "ffconcat version 1.0\nfile '/etc/passwd'\n",
		"play.m3u8":            "#EXTM3U\n#EXTINF:1,\n/etc/passwd\n",
		"stats-0.txt":          "",
	}
	for name, body := range files {
		full := filepath.Join(dir, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			t.Fatalf("mkdir: %v", err)
		}
		if err := os.WriteFile(full, []byte(body), 0o600); err != nil {
			t.Fatalf("write %s: %v", name, err)
		}
	}
	return dir
}

// The acceptance fixtures are named in the specification itself: "The full
// validator rules are tested by D4b with fixtures such as movie=/etc/passwd,
// -i http://example.com/x, and concat: with absolute paths, all of which MUST
// be rejected."
func TestValidateArgvRejects(t *testing.T) {
	dir := sessionDir(t)
	cases := []struct {
		name string
		argv []string
	}{
		{"movie= names a host file", []string{"-vf", "movie=/etc/passwd"}},
		{"amovie= names a host file", []string{"-filter_complex", "amovie=/etc/passwd"}},
		{"subtitles= names a host file", []string{"-vf", "subtitles=/etc/shadow"}},
		{"subtitles= names a file that is not there", []string{"-vf", "subtitles=missing.srt"}},
		{"ass= names a host file", []string{"-vf", "ass=/etc/passwd"}},
		{"textfile= names a host file", []string{"-vf", "drawtext=textfile=/etc/passwd"}},
		{"fontfile= names a host file", []string{"-vf", "drawtext=fontfile=/etc/passwd:text=x"}},
		{"lut3d= names a host file", []string{"-vf", "lut3d=/etc/passwd"}},
		{"fontfile= escapes upwards", []string{"-vf", "drawtext=fontfile=../../etc/passwd:text=x"}},
		{"http input", []string{"-i", "http://example.com/x.mp4"}},
		{"https input", []string{"-i", "https://example.com/x.mp4"}},
		{"ftp input", []string{"-i", "ftp://example.com/x.mp4"}},
		{"rtmp input", []string{"-i", "rtmp://example.com/x"}},
		{"tcp input", []string{"-i", "tcp://example.com:1"}},
		{"concat protocol", []string{"-i", "concat:clip.mp4"}},
		{"concat with an absolute path", []string{"-i", "concat:/etc/passwd"}},
		{"subfile protocol", []string{"-i", "subfile:clip.mp4"}},
		{"file protocol", []string{"-i", "file:/etc/passwd"}},
		{"data protocol", []string{"-i", "data:text/plain;base64,aGk="}},
		{"an absolute input", []string{"-i", "/etc/passwd"}},
		{"a traversing input", []string{"-i", "../../etc/passwd"}},
		{"a traversing output", []string{"-i", "clip.mp4", "../../out.mp4"}},
		{"an absolute output", []string{"-i", "clip.mp4", "/tmp/out.mp4"}},
		{"a windows volume", []string{"-i", "clip.mp4", "C:\\out.mp4"}},
		{"a UNC path", []string{"-i", "clip.mp4", "\\\\server\\share\\out.mp4"}},
		{"lavfi naming a host file", []string{"-f", "lavfi", "-i", "movie=/etc/passwd"}},
		{"lavfi reading a host file in a filter", []string{"-f", "lavfi", "-i", "color=c=black:s=1x1", "-vf", "movie=/etc/passwd"}},
		{"filter_script", []string{"-filter_script", "graph.txt"}},
		{"filter_script with a stream specifier", []string{"-filter_script:v", "graph.txt"}},
		{"filter_script with an absolute path", []string{"-filter_script", "/etc/passwd"}},
		{"filter_script as an inline value", []string{"-filter_script=/etc/passwd"}},
		{"passlogfile outside", []string{"-passlogfile", "/tmp/ffmpeg"}},
		{"dump_attachment outside", []string{"-dump_attachment:t", "/tmp/page"}},
		{"dump_attachment traversing", []string{"-dump_attachment", "../x"}},
		{"a concat list naming a host file", []string{"-f", "concat", "-i", "list.txt"}},
		{"a concat list that is not there", []string{"-f", "concat", "-i", "absent.txt"}},
		{"an empty argv", []string{}},
		{"a host path in an unexpected place", []string{"-i", "clip.mp4", "-attach", "/etc/passwd"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := ValidateArgv(tc.argv, dir)
			if err == nil {
				t.Fatalf("accepted %v", tc.argv)
			}
			var argvErr *ArgvError
			if !errors.As(err, &argvErr) {
				t.Fatalf("%v is not an ArgvError", err)
			}
			if !strings.HasPrefix(argvErr.Error(), "argv_not_allowed") {
				t.Fatalf("error = %q", argvErr.Error())
			}
		})
	}
}

// Everything the engine legitimately builds must still pass, or the validator
// would break the product rather than protect it.
func TestValidateArgvAccepts(t *testing.T) {
	dir := sessionDir(t)
	cases := [][]string{
		{"-y", "-i", "inputs/clip.mp4", "-c:v", "libx264", "-crf", "20", "out/seg-0.mp4"},
		{"-i", "clip.mp4", "-vf", "scale=1920:-2", "-an", "out.mp4"},
		{"-i", "clip.mp4", "-ss", "00:00:10", "-t", "5", "-c", "copy", "out.mp4"},
		{"-i", "clip.mp4", "-map", "0:v:0", "-map", "0:a:0", "out.mp4"},
		{"-i", "clip.mp4", "-vf", "subtitles=sub.srt", "out.mp4"},
		{"-i", "clip.mp4", "-vf", "subtitles=sub.srt:force_style='FontName=Arial'", "out.mp4"},
		{"-i", "clip.mp4", "-vf", "drawtext=fontfile=font.ttf:text='Hello World'", "out.mp4"},
		{"-i", "clip.mp4", "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease", "out.mp4"},
		{"-i", "clip.mp4", "-metadata", "title=ReelBolt: a demo", "out.mp4"},
		{"-i", "clip.mp4", "-metadata:s:v", "title=x", "out.mp4"},
		{"-f", "lavfi", "-i", "color=c=black:s=1920x1080:r=30", "-t", "1", "out.mp4"},
		{"-f", "concat", "-i", "safe-list.txt", "-c", "copy", "out.mp4"},
		{"-f", "concat", "-i", "safe-list-nested.txt", "-c", "copy", "out.mp4"},
		{"-report", "-i", "clip.mp4", "out.mp4"},
		{"-i", "-", "-f", "null", "-"},
		{"-i", "clip.mp4", "-af", "volume=0.5", "out.mp4"},
		{"-i", "clip.mp4", "-vf", "crop=in_w-2*20:in_h-2*20", "out.mp4"},
		// Stream specifiers are standard ffprobe/ffmpeg selectors; the bare
		// form (v:0, a:0) is not an input and must not read as a protocol
		// prefix.
		{"-v", "error", "-select_streams", "v:0", "clip.mp4"},
		{"-v", "error", "-select_streams", "a:0", "clip.mp4"},
		{"-v", "error", "-select_streams", "v:0", "-show_streams", "clip.mp4"},
		{"-v", "error", "-map", "v:0", "clip.mp4", "out.mp4"},
		{"-i", "clip.mp4", "-map", "v:0", "-map", "a:0", "out.mp4"},
	}
	for _, argv := range cases {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("rejected a legitimate argv %v: %v", argv, err)
		}
	}
}

// The whitelist has to sit before the input it applies to; a trailing input
// option applies to nothing and would silently not be in force.
func TestWithProtocolWhitelistAppliesToEveryInput(t *testing.T) {
	got, _ := WithProtocolWhitelist([]string{"-y", "-i", "a.mp4", "-i", "b.mp4", "out.mp4"})
	want := []string{"-y", "-protocol_whitelist", "file,pipe", "-i", "a.mp4", "-protocol_whitelist", "file,pipe", "-i", "b.mp4", "out.mp4"}
	if strings.Join(got, " ") != strings.Join(want, " ") {
		t.Fatalf("argv = %v, want %v", got, want)
	}
	// With no input at all it is still present: the specification requires it
	// on every invocation.
	got, _ = WithProtocolWhitelist([]string{"-hide_banner", "-filters"})
	if strings.Join(got, " ") != "-hide_banner -filters -protocol_whitelist file,pipe" {
		t.Fatalf("argv = %v", got)
	}
}

// A colon is not a scheme, and treating it as one would reject half the
// filtergraphs the engine legitimately builds.
func TestSchemeDetection(t *testing.T) {
	schemes := map[string]string{
		"http://x/y":        "http",
		"https://x/y":       "https",
		"concat:a|b":        "concat",
		"file:/etc/passwd":  "file",
		"data:text/plain,x": "data",
		"subfile:x":         "subfile",
	}
	for token, want := range schemes {
		if got, ok := schemePrefix(token); !ok || got != want {
			t.Errorf("schemePrefix(%q) = %q, %v; want %q, true", token, got, ok, want)
		}
	}
	notSchemes := []string{"0:v:0", "00:00:10", "scale=1920:1080", "drawtext=text='a:b'", "-c:v", "out.mp4"}
	for _, token := range notSchemes {
		if got, ok := schemePrefix(token); ok {
			t.Errorf("schemePrefix(%q) = %q, want no scheme", token, got)
		}
	}
}

// A stream selector is not a protocol reference, and the scheme check must
// stay a real boundary. The fix that lets v:0 through is scoped to input
// position; this pins both halves: the standard selectors go through, and
// every real scheme is still refused — in an input position, and (as a host
// path) wherever it appears in the argv.
func TestStreamSelectorsPassAndSchemesStayRefused(t *testing.T) {
	dir := sessionDir(t)
	// The standard ffprobe stream selectors, accepted in the positions the
	// engine uses them.
	selectors := [][]string{
		{"-v", "error", "-select_streams", "v:0", "clip.mp4"},
		{"-v", "error", "-select_streams", "a:0", "clip.mp4"},
		{"-v", "error", "-map", "v:0", "clip.mp4", "out.mp4"},
		{"-i", "clip.mp4", "-map", "v:0", "-map", "a:0", "out.mp4"},
	}
	for _, argv := range selectors {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("rejected a standard stream selector argv %v: %v", argv, err)
		}
	}
	// And the scheme check still blocks every real scheme, in an input
	// position (where it lives).
	inputs := []string{
		"http://example.com/x.mp4",
		"https://example.com/x.mp4",
		"file:/etc/passwd",
		"concat:clip.mp4",
		"subfile:clip.mp4",
		"data:text/plain;base64,aGk=",
		"ftp://example.com/x.mp4",
	}
	for _, in := range inputs {
		if err := ValidateArgv([]string{"-i", in}, dir); err == nil {
			t.Errorf("accepted a scheme input %q", in)
		}
	}
	// movie= is refused in every non-input position (the spec's own fixture),
	// in every token shape the filter can carry it in.
	for _, tok := range []string{"movie=/etc/passwd", "movie=/etc/passwd:s=1", "amovie=/etc/passwd"} {
		for _, opt := range []string{"-vf", "-filter_complex"} {
			if err := ValidateArgv([]string{opt, tok}, dir); err == nil {
				t.Errorf("accepted a host file in %q %q", opt, tok)
			}
		}
	}
	// A scheme-shaped token that is not in an input position is not a
	// protocol reference: the validator's job there is the host-path check,
	// which v:0 does not trip.
	if err := ValidateArgv([]string{"-select_streams", "v:0"}, dir); err != nil {
		t.Errorf("rejected the stream selector in a non-input position: %v", err)
	}
}

// A symlink inside the session directory is not a way out of it.
func TestValidateArgvRefusesASymlinkEscape(t *testing.T) {
	dir := t.TempDir()
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "secret.mp4"), []byte("x"), 0o600); err != nil {
		t.Fatalf("seed: %v", err)
	}
	if err := os.Symlink(outside, filepath.Join(dir, "escape")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	if err := ValidateArgv([]string{"-i", "escape/secret.mp4"}, dir); err == nil {
		t.Fatal("an input reached through a symlink out of the session was accepted")
	}
	if err := ValidateArgv([]string{"-vf", "subtitles=escape/secret.mp4"}, dir); err == nil {
		t.Fatal("a filter reached through a symlink out of the session was accepted")
	}
}

// SBX-01: the shapes the security assessment showed the validator accepting.
// Each one reaches a host file or the network from a token whose start looks
// innocent.
func TestValidateArgvRejectsEmbeddedPathsAndOutputs(t *testing.T) {
	dir := sessionDir(t)
	null := []string{"-f", "null", "-"}
	vf := func(graph string) []string {
		return append([]string{"-i", "clip.mp4", "-vf", graph}, null...)
	}
	cases := []struct {
		name string
		argv []string
	}{
		{"psnr stats_file writes a host file", vf("psnr=stats_file=/home/victim/.bashrc")},
		{"metadata file= writes a host file", vf("signalstats,metadata=mode=print:file=/home/victim/.profile")},
		{"sendcmd f= reads a host file", vf("sendcmd=f=/etc/passwd")},
		{"sendcmd f= reads an escaped volume path", vf("sendcmd=f=C\\:/Users/victim/secret.txt")},
		{"a volume path with a backslash", vf("sendcmd=f=C\\:\\Users\\victim\\secret.txt")},
		{"an unescaped volume path", vf("psnr=stats_file=D:/out.log")},
		{"a quoted host path", vf("metadata=mode=print:file='/home/victim/.profile'")},
		{"an escaped host path", vf("psnr=stats_file=\\/home/victim/.bashrc")},
		{"a home path", vf("metadata=mode=print:file=~/.bashrc")},
		{"a climbing path", vf("psnr=stats_file=../../.bashrc")},
		{"a UNC path", vf("psnr=stats_file=\\\\server\\share\\x")},
		{"a host path after a comma", vf("null,/etc/passwd")},
		{"a host path after a pipe", []string{"-i", "clip.mp4", "-f", "tee", "out.mkv|/tmp/out.mkv"}},
		{"ssim stats_file in a complex graph", []string{"-i", "clip.mp4", "-filter_complex", "[0:v]ssim=stats_file=/tmp/x[o]", "-map", "[o]", "out.mp4"}},
		{"a tcp output", []string{"-i", "clip.mp4", "-c", "copy", "-f", "mpegts", "tcp://192.168.1.1:9000"}},
		{"an http output", []string{"-i", "clip.mp4", "-c", "copy", "-f", "mpegts", "http://169.254.169.254/x"}},
		{"a udp output", []string{"-i", "clip.mp4", "-f", "mpegts", "udp://239.0.0.1:1234"}},
		{"an unknown scheme output", []string{"-i", "clip.mp4", "whatever://host/x"}},
		{"a file: output", []string{"-i", "clip.mp4", "file:out.mp4"}},
		{"a file: output on the host", []string{"-i", "clip.mp4", "file:/tmp/out.mp4"}},
		{"a tee: output", []string{"-i", "clip.mp4", "tee:out.mp4"}},
		{"an ffprobe network input", []string{"-v", "error", "-show_format", "http://169.254.169.254/x"}},
		{"-safe 0 on an auto-probed list", []string{"-safe", "0", "-i", "list.ffconcat", "-c", "copy", "out.mkv"}},
		{"-safe on the wrong input", []string{"-f", "concat", "-i", "safe-list.txt", "-safe", "0", "-i", "list.ffconcat", "out.mkv"}},
		{"-safe as an output option", []string{"-f", "concat", "-i", "safe-list.txt", "-safe", "0", "out.mkv"}},
		{"-safe with an inline value", []string{"-f", "concat", "-safe=0", "-i", "safe-list.txt", "out.mkv"}},
		{"-allowed_extensions", []string{"-allowed_extensions", "ALL", "-i", "play.m3u8", "-c", "copy", "out.mkv"}},
		{"-allowed_segment_extensions", []string{"-allowed_segment_extensions", "ALL", "-i", "play.m3u8", "out.mkv"}},
		{"-extension_picky", []string{"-extension_picky", "0", "-i", "play.m3u8", "out.mkv"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := ValidateArgv(tc.argv, dir)
			var argvErr *ArgvError
			if !errors.As(err, &argvErr) {
				t.Fatalf("ValidateArgv(%v) = %v, want an ArgvError", tc.argv, err)
			}
		})
	}
}

// The argv shapes the engine sends to a runner (RemoteMediaWorkspace forwards
// what VideoCompileStepExecutor, FfmpegArgvBuilder, SceneCutFinder,
// CaptionPlacementProbe, DrawtextFilterBuilder and ReviewAudioFacts build, with
// session-relative paths). The stricter rules must not refuse any of them.
func TestValidateArgvAcceptsEngineShapes(t *testing.T) {
	dir := sessionDir(t)
	cases := [][]string{
		// VideoCompile StreamCopy: cut a segment, then concat the list.
		{"-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file",
			"-ss", "1.5", "-to", "4.25", "-i", "clip.mp4", "-c", "copy", "seg-0.mp4"},
		{"-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file,concat",
			"-f", "concat", "-safe", "0", "-i", "concat.txt", "-c", "copy", "edited.mp4"},
		// Frame statistics printed to stdout and to session files.
		{"-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file", "-threads", "2",
			"-i", "clip.mp4", "-an", "-vf",
			"fps=2,scale=320:-2,signalstats,metadata=print:key=lavfi.signalstats.YDIF:file=-", "-f", "null", "-"},
		{"-i", "clip.mp4", "-an", "-vf",
			"signalstats,metadata=print:key=lavfi.signalstats.YAVG:file='stats-0.txt',metadata=print:key=lavfi.signalstats.YLOW:file='out/stats-1.txt'",
			"-f", "null", "-"},
		{"-i", "clip.mp4", "-vf", "crop=iw*0.8:ih*0.2:iw*0.1:ih*0.7,signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"},
		{"-i", "clip.mp4", "-af",
			"aresample=16000,asetnsamples=n=65536:p=0,astats=length=1:metadata=1:reset=0,ametadata=mode=print:key=lavfi.astats.1.RMS_peak:file=-",
			"-f", "null", "-"},
		{"-i", "clip.mp4", "-af", "ebur128=peak=true", "-f", "null", "-"},
		// Overlay text: the text and the font are session files.
		{"-i", "clip.mp4", "-filter_complex",
			"[0:v]drawbox=x=(iw-w)/2:y=ih*0.8:w=iw/2:h=80:color=black@0.5:t=fill[b];[b]drawtext=fontfile='font.ttf':textfile='sub.srt':expansion=none:fontsize=48:x=(w-text_w)/2:y=h*0.8[v]",
			"-map", "[v]", "-map", "0:a?", "-c:v", "libx264", "-pix_fmt", "yuv420p", "out.mp4"},
		{"-i", "clip.mp4", "-vf", "setpts=PTS/1.5,fps=30000/1001,scale=iw/2:-2", "-af", "atempo=1.5", "out.mp4"},
		// ffprobe.
		{"-v", "error", "-protocol_whitelist", "file", "-show_entries",
			"stream=index,codec_type,width,height,r_frame_rate:format=duration:stream_tags=rotate",
			"-of", "json", "clip.mp4"},
		{"-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time,pict_type", "-of", "csv=p=0", "clip.mp4"},
	}
	for _, argv := range cases {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("rejected an engine argv %v: %v", argv, err)
		}
	}
}

// The concat demuxer runs in safe mode whatever the caller asked for, so a list
// naming an absolute path or a protocol is refused by ffmpeg itself.
func TestWithSafeConcatPinsSafeMode(t *testing.T) {
	cases := []struct{ in, want string }{
		{"-y -f concat -safe 0 -i concat.txt -c copy out.mp4", "-y -f concat -safe 1 -i concat.txt -c copy out.mp4"},
		{"-safe 0 -f concat -i concat.txt out.mp4", "-f concat -safe 1 -i concat.txt out.mp4"},
		{"-f concat -i concat.txt out.mp4", "-f concat -safe 1 -i concat.txt out.mp4"},
		{"-i clip.mp4 -f mp4 out.mp4", "-i clip.mp4 -f mp4 out.mp4"},
	}
	for _, tc := range cases {
		got := strings.Join(WithSafeConcat(strings.Fields(tc.in)), " ")
		if got != tc.want {
			t.Errorf("WithSafeConcat(%q) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

// ffmpeg parses more than this validator's first reading did: the tee muxer
// splits its target on | and lets each slave open with a [options] block, and a
// filtergraph value is unescaped twice and may be padded with any whitespace.
// None of those forms is a way round the host-path and protocol rules.
func TestValidateArgvRejectsTeeAndEscapedForms(t *testing.T) {
	dir := sessionDir(t)
	null := []string{"-f", "null", "-"}
	vf := func(graph string) []string {
		return append([]string{"-i", "clip.mp4", "-vf", graph}, null...)
	}
	tee := func(target string) []string {
		return []string{"-i", "clip.mp4", "-c", "copy", "-f", "tee", target}
	}
	cases := []struct {
		name string
		argv []string
	}{
		{"tee slaves with option blocks", tee("[f=mpegts]tcp://10.0.0.1:9|[onfail=ignore]/home/u/.bashrc")},
		{"a tee slave naming a host file after its options", tee("[onfail=ignore]/home/u/.bashrc")},
		{"a tee slave naming a network target", tee("[f=mpegts]udp:10.0.0.1:9")},
		{"tee with session-relative slaves", tee("a.mkv|b.mkv")},
		{"the tee protocol form", []string{"-i", "clip.mp4", "tee:[f=mpegts]out.ts"}},
		{"a host path after an option block", []string{"-i", "clip.mp4", "[onfail=ignore]/home/u/.bashrc"}},
		{"a quoted and escaped host path", vf("metadata=mode=print:file='\\/home/u/.profile'")},
		{"a doubly escaped host path", vf("sendcmd=f=\\\\/etc/passwd")},
		{"a host path behind a tab", vf("sendcmd=f=\t/etc/passwd")},
		{"a host path behind a newline", vf("sendcmd=f=\r\n/etc/passwd")},
		{"a host path behind an empty quote", vf("sendcmd=f=''\\''/etc/passwd")},
		{"a host path behind mixed quotes and escapes", vf("sendcmd=f=' '\\\\' '/etc/passwd")},
		{"a quoted volume path", vf("sendcmd=f='C'\\:'/Users/u/x'")},
		{"a quoted climb", vf("psnr=stats_file='..'/'..'/.bashrc")},
		{"a quoted climb mid-path", vf("psnr=stats_file=out/'..'/'..'/.bashrc")},
		{"an escaped climb mid-path", vf("psnr=stats_file=out/\\.\\./\\.\\./.bashrc")},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := ValidateArgv(tc.argv, dir)
			var argvErr *ArgvError
			if !errors.As(err, &argvErr) {
				t.Fatalf("ValidateArgv(%q) = %v, want an ArgvError", tc.argv, err)
			}
		})
	}
}

// reviewSessionDir adds the session files the review shapes name: an option
// value file, a filtergraph script the engine would write, and scripts whose
// content is the escape.
func reviewSessionDir(t *testing.T) string {
	t.Helper()
	dir := sessionDir(t)
	files := map[string]string{
		"opt.txt":            "tee",
		"filter_complex.txt": "[0:v]trim=start=0:end=1.5,setpts=PTS-STARTPTS,scale=1280:-2[v0];\n[v0]drawtext=fontfile='font.ttf':textfile='sub.srt':x=(w-text_w)/2[vout]",
		"graph-movie.txt":    "movie=/etc/passwd[vout]",
		"graph-stats.txt":    "[0:v]psnr=stats_file='/home/victim/.bashrc'[vout]",
		"graph-zmq.txt":      "[0:v]null,\n zmq [vout]",
		"f=pulse":            "",
		"format_opts=x":      "",
		"default":            "",
	}
	for name, body := range files {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o600); err != nil {
			t.Fatalf("write %s: %v", name, err)
		}
	}
	return dir
}

// The security review of PR #1183 (T-105 probe table): ffmpeg has more
// spellings than a denylist of names. Formats are an allowlist, an option whose
// value is loaded from a file is refused, a scheme in an output is refused
// unless it is pipe:, and the filters that listen, load a library or take
// commands from a file are refused in every filter option.
func TestValidateArgvRejectsReviewShapes(t *testing.T) {
	dir := reviewSessionDir(t)
	in := []string{"-i", "clip.mp4"}
	null := []string{"-f", "null", "-"}
	join := func(parts ...[]string) []string {
		var out []string
		for _, p := range parts {
			out = append(out, p...)
		}
		return out
	}
	cases := []struct {
		name string
		argv []string
	}{
		// Option-file loaders.
		{"01 a filter script naming a host file", join(in, []string{"-filter_complex_script", "graph-movie.txt", "-map", "[vout]"}, null)},
		{"01 a filter script writing a host file", join(in, []string{"-filter_complex_script", "graph-stats.txt", "-map", "[vout]"}, null)},
		{"01 a filter script with a refused filter", join(in, []string{"-filter_complex_script", "graph-zmq.txt", "-map", "[vout]"}, null)},
		{"01 a filter script that is not there", join(in, []string{"-filter_complex_script", "absent.txt"}, null)},
		{"01 a filter script on the host", join(in, []string{"-filter_complex_script", "/etc/passwd"}, null)},
		{"01 a filter script with a specifier", join(in, []string{"-filter_complex_script:v", "filter_complex.txt"}, null)},
		{"02 -/filter_complex", join(in, []string{"-/filter_complex", "filter_complex.txt", "-map", "[vout]"}, null)},
		{"03 -/vf", join(in, []string{"-/vf", "graph-movie.txt"}, null)},
		{"05 -/safe", []string{"-/safe", "opt.txt", "-i", "list.ffconcat", "-c", "copy", "out05.mkv"}},
		{"06 -/allowed_extensions", []string{"-/allowed_extensions", "opt.txt", "-/extension_picky", "opt.txt", "-i", "play.m3u8", "-c", "copy", "out06.bin"}},
		{"08 -/f", join(in, []string{"-c", "copy", "-/f", "opt.txt", "out08.mkv|[f=mpegts]udp:127.0.0.1:59999"})},
		{"21 -/vstats_file", join(in, []string{"-/vstats_file", "opt.txt"}, null)},
		{"34 -/safe after -f concat", []string{"-f", "concat", "-/safe", "opt.txt", "-i", "safe-list.txt", "-c", "copy", "e.mp4"}},
		{"36 -/dump_attachment:t", []string{"-/dump_attachment:t", "opt.txt", "-i", "clip.mp4"}},
		{"45 -/segment_list", join(in, []string{"-/segment_list", "opt.txt", "seg%d.ts"})},
		// Formats outside the first-party list.
		{"09 fifo wrapping tee", join(in, []string{"-c", "copy", "-map", "0", "-f", "fifo", "-fifo_format", "tee", "a.mkv|b.mkv"})},
		{"09 -fifo_format tee alone", join(in, []string{"-fifo_format", "tee", "out.mkv"})},
		{"-segment_format tee", join(in, []string{"-segment_format", "tee", "out.mkv"})},
		{"-f segment", join(in, []string{"-f", "segment", "-segment_format", "mp4", "seg%d.mp4"})},
		{"10 the rtsp muxer", join(in, []string{"-c", "copy", "-f", "rtsp", "out.sdp"})},
		{"12 x11grab", []string{"-f", "x11grab", "-i", "default", "-frames:v", "1", "shot.png"}},
		{"13 pulse", []string{"-f", "pulse", "-i", "default", "-t", "1", "mic.wav"}},
		{"14 alsa", []string{"-f", "alsa", "-i", "default", "-t", "1", "mic.wav"}},
		{"15 avfoundation", []string{"-f", "avfoundation", "-i", "default", "-t", "1", "cam.mp4"}},
		{"16 dshow", []string{"-f", "dshow", "-i", "video=Integrated Camera", "-t", "1", "cam.mp4"}},
		{"17 gdigrab", []string{"-f", "gdigrab", "-i", "desktop", "-frames:v", "1", "shot.png"}},
		{"48 kmsgrab", []string{"-f", "kmsgrab", "-i", "-", "-frames:v", "1", "o.png"}},
		{"v4l2", []string{"-f", "v4l2", "-i", "default", "o.mp4"}},
		{"fbdev output", join(in, []string{"-f", "fbdev", "default"})},
		{"43 sdl output", join(in, []string{"-f", "sdl", "win"})},
		{"27 -f:v tee", join(in, []string{"-c", "copy", "-f:v", "tee", "a.mkv|b.mkv"})},
		{"-f with a specifier and a first-party format", join(in, []string{"-f:v", "mp4", "out.mp4"})},
		{"a format in another case", join(in, []string{"-f", "TEE", "a.mkv"})},
		{"31 the hls muxer", join(in, []string{"-f", "hls", "o.m3u8"})},
		{"31 -hls_key_info_file", join(in, []string{"-hls_key_info_file", "opt.txt", "o.m3u8"})},
		{"40 mpegts to a pipe", join(in, []string{"-f", "mpegts", "pipe:1"})},
		{"movie= naming a capture format", []string{"-f", "lavfi", "-i", "movie=default:f=pulse", "-t", "1", "mic.wav"}},
		{"movie= naming a capture format by position", []string{"-f", "lavfi", "-i", "movie=default:pulse", "-t", "1", "mic.wav"}},
		{"movie= with the format key first", []string{"-f", "lavfi", "-i", "movie=f=pulse:filename=default", "-t", "1", "mic.wav"}},
		{"amovie= with format_opts first", join(in, []string{"-filter_complex", "amovie=format_opts=x:filename=default[a]", "out.mp4"})},
		{"movie@instance with the format key", []string{"-f", "lavfi", "-i", "movie@x=f=pulse:filename=default", "-t", "1", "mic.wav"}},
		{"amovie@instance with format_opts", join(in, []string{"-filter_complex", "amovie@a=format_opts=x:filename=default[a]", "out.mp4"})},
		{"movie@instance naming a missing file", join(in, []string{"-vf", "movie@x=absent.mp4"}, null)},
		{"subtitles@instance naming a missing file", join(in, []string{"-vf", "subtitles@x=absent.srt"}, null)},
		{"lut3d@instance naming a missing file", join(in, []string{"-vf", "lut3d@x=file=absent.cube"}, null)},
		{"azmq@instance", join(in, []string{"-af", "azmq@x"}, null)},
		{"sendcmd@instance", join(in, []string{"-vf", "sendcmd@x=f=opt.txt"}, null)},
		{"asendcmd@instance", join(in, []string{"-af", "asendcmd@x=c=x"}, null)},
		{"frei0r@instance", join(in, []string{"-vf", "frei0r@x=glow"}, null)},
		{"ladspa@instance", []string{"-f", "lavfi", "-i", "ladspa@x=file=cmt", "-t", "1", "o.wav"}},
		{"lv2@instance", join(in, []string{"-af", "lv2@x=p=urn:x"}, null)},
		{"amovie= naming a capture format", join(in, []string{"-filter_complex", "amovie=default:format_name=alsa[a]", "out.mp4"})},
		// Schemes without //.
		{"10 an rtsp target without slashes", join(in, []string{"-c", "copy", "-f", "mp4", "rtsp:127.0.0.1:59999/x"})},
		{"11 a nested scheme", join(in, []string{"-c", "copy", "-f", "mp4", "crypto+udp:127.0.0.1:59999"})},
		{"a doubly nested scheme", join(in, []string{"a+b+c:out.mp4"})},
		{"an unknown scheme without slashes", join(in, []string{"whatever:out.mp4"})},
		{"a scheme after a flag that takes no value", join(in, []string{"-an", "rtsp:127.0.0.1:59999/x"})},
		{"29 -progress to an unknown scheme", join(in, []string{"-progress", "rtsp:127.0.0.1:59999"}, null)},
		{"-progress to a nested scheme", join(in, []string{"-progress", "crypto+udp:127.0.0.1:59999"}, null)},
		{"a pipe with a path", join(in, []string{"-f", "mp4", "pipe:out.mp4"})},
		{"30 the subfile comma form", []string{"-i", "subfile,,start,0,end,100,,:clip.mp4", "out.mkv"}},
		{"the subfile comma form as an output", join(in, []string{"subfile,,start,0,end,100,,:clip.mp4"})},
		// Filters.
		{"18 zmq in -vf", join(in, []string{"-vf", "zmq"}, null)},
		{"zmq with options", join(in, []string{"-vf", "zmq=bind_address=tcp\\://*\\:5555"}, null)},
		{"zmq after another filter", join(in, []string{"-vf", "null,zmq"}, null)},
		{"zmq quoted", join(in, []string{"-vf", "null, 'zmq'"}, null)},
		{"zmq with an instance name", join(in, []string{"-vf", "zmq@a"}, null)},
		{"azmq in -af", join(in, []string{"-af", "azmq"}, null)},
		{"zmq in -filter:v", join(in, []string{"-filter:v", "zmq"}, null)},
		{"zmq in -filter_complex", join(in, []string{"-filter_complex", "[0:v]zmq[o]", "-map", "[o]"}, null)},
		{"zmq in -lavfi", join(in, []string{"-lavfi", "[0:v]null[a];[a]zmq[o]", "-map", "[o]"}, null)},
		{"zmq in a lavfi input", []string{"-f", "lavfi", "-i", "color=c=black:s=16x16,zmq", "-t", "1", "out.mp4"}},
		{"sendcmd with a session file", join(in, []string{"-vf", "sendcmd=f=opt.txt,drawtext=text=x"}, null)},
		{"asendcmd with a session file", join(in, []string{"-af", "asendcmd=filename=opt.txt,volume=1"}, null)},
		{"46 ladspa", []string{"-f", "lavfi", "-i", "ladspa=file=cmt:plugin=sine_fcac", "-t", "1", "o.wav"}},
		{"47 frei0r", join(in, []string{"-vf", "frei0r=glow"}, null)},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := ValidateArgv(tc.argv, dir)
			var argvErr *ArgvError
			if !errors.As(err, &argvErr) {
				t.Fatalf("ValidateArgv(%q) = %v, want an ArgvError", tc.argv, err)
			}
		})
	}
}

// Every format and shape the engine's argv builders name (the WorkflowEngine's
// "-f" values: null, concat, lavfi, mp4, mov, matroska, image2, wav, s16le and
// rawvideo; -progress pipe:1; a filtergraph script the engine wrote into the
// session) is still accepted under the allowlist.
func TestValidateArgvAcceptsFirstPartyFormats(t *testing.T) {
	dir := reviewSessionDir(t)
	cases := [][]string{
		// MarketplacePreviewArgvBuilder, StagedVideoProxy, EnhanceDuration.
		{"-y", "-i", "clip.mp4", "-c:v", "libx264", "-movflags", "+faststart", "-f", "mp4", "out/preview.mp4"},
		// GeneratedClipNormalizer, AudioCleanupArgv.
		{"-y", "-i", "clip.mp4", "-c:v", "prores_ks", "-c:a", "pcm_s16le", "-f", "mov", "out/clip.mov"},
		// EnhanceBoundedCopy.
		{"-y", "-i", "clip.mp4", "-c", "copy", "-f", "matroska", "out/clip.mkv"},
		// StillsArgvBuilder, PlacementStills, AvatarStillThenAnimate.
		{"-y", "-ss", "1.5", "-i", "clip.mp4", "-frames:v", "1", "-c:v", "mjpeg", "-q:v", "4", "-f", "image2", "out/still-%03d.jpg"},
		// SegmentationMaskArgvBuilder: an image sequence as an input.
		{"-y", "-i", "clip.mp4", "-framerate", "30", "-f", "image2", "-i", "nested/mask-%05d.png", "-filter_complex", "[0:v][1:v]alphamerge[o]", "-map", "[o]", "-f", "mp4", "out/masked.mp4"},
		// AudioCleanupArgv, LocalAudioCleaner, AvatarStillThenAnimate.
		{"-y", "-i", "clip.mp4", "-vn", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "wav", "out/audio.wav"},
		// MediaDerivativeService, VideoEventDetector, MainsFrequencyProbe.
		{"-i", "clip.mp4", "-vn", "-ac", "1", "-ar", "8000", "-c:a", "pcm_s16le", "-f", "s16le", "out/audio.pcm"},
		// FfmpegArgvBuilder: raw frames to a session file and to stdout.
		{"-i", "clip.mp4", "-vf", "fps=2,scale=64:36:flags=area", "-pix_fmt", "rgb24", "-f", "rawvideo", "out/frames.rgb"},
		{"-i", "clip.mp4", "-vf", "scale=64:36", "-pix_fmt", "gray", "-f", "rawvideo", "-"},
		// VideoCompileStepExecutor.AddInsertInput: a synthesized colour plate.
		{"-i", "clip.mp4", "-f", "lavfi", "-i", "color=c=0x101010:s=16x16:r=30000/1001", "-filter_complex", "[0:v][1:v]overlay[o]", "-map", "[o]", "-map", "0:a?", "out.mp4"},
		// VideoCompileStepExecutor, ProxyArgvBuilder, StabilizedProxyArgvBuilder:
		// progress on the process's own stdout.
		{"-y", "-i", "clip.mp4", "-c:v", "libx264", "-progress", "pipe:1", "-nostats", "out/proxy.mp4"},
		// VideoCompileStepExecutor, SpeedRampApplier, RegionFilterGraphBuilder: a
		// long graph is written into the session and named as a script.
		{"-y", "-i", "clip.mp4", "-filter_complex_script", "filter_complex.txt", "-map", "[vout]", "-map", "0:a:0?", "-c:v", "libx264", "out/edited.mp4"},
		// ffprobe (MediaProbe): two sections in one -show_entries.
		{"-v", "error", "-show_entries", "stream=sample_rate,channels:format=duration", "-of", "json", "clip.mp4"},
	}
	for _, argv := range cases {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("rejected a first-party argv %v: %v", argv, err)
		}
	}
}

// An ffmpeg output with no -f gets the muxer ffmpeg picks from its extension,
// so the extension is held to the same allowlist as -f.
func TestValidateFFmpegOutputsRejectsGuessedMuxers(t *testing.T) {
	in := []string{"-i", "clip.mp4", "-c", "copy"}
	for _, out := range []string{"out.m3u8", "out.mpd", "x.sdp", "out.ts", "seg%d.ts", "list.ffconcat", "out.ISM", "-an out.m3u8"} {
		argv := append(append([]string{}, in...), strings.Fields(out)...)
		var argvErr *ArgvError
		if err := ValidateFFmpegOutputs(argv); !errors.As(err, &argvErr) {
			t.Errorf("ValidateFFmpegOutputs(%q) = %v, want an ArgvError", argv, err)
		}
	}
}

// The output shapes the engine builders write: a first-party extension, or an
// allowlisted -f in front of the output, and file-valued options that are not
// outputs.
func TestValidateFFmpegOutputsAcceptsFirstPartyOutputs(t *testing.T) {
	cases := [][]string{
		{"-y", "-i", "clip.mp4", "-c:v", "libx264", "-crf", "20", "out/seg-0.mp4"},
		{"-i", "clip.webm", "-c", "copy", "out.webm"},
		{"-i", "clip.mp4", "-c", "copy", "out.mkv"},
		{"-i", "clip.mp4", "-c:v", "prores_ks", "out.mov"},
		{"-i", "a.mp3", "-af", "volume=0.5", "-b:a", "192k", "out.mp3"},
		{"-i", "a.wav", "out.aac"}, {"-i", "a.wav", "out.m4a"}, {"-i", "a.wav", "out.flac"},
		{"-i", "a.wav", "out.ogg"}, {"-i", "a.wav", "out.opus"}, {"-i", "a.wav", "out.wav"},
		{"-ss", "1.5", "-i", "clip.mp4", "-frames:v", "1", "out/still-%03d.jpg"},
		{"-i", "clip.mp4", "-frames:v", "1", "o.png"}, {"-i", "clip.mp4", "-frames:v", "1", "o.jpeg"},
		{"-i", "clip.mp4", "-frames:v", "1", "o.webp"}, {"-i", "clip.mp4", "o.gif"},
		{"-i", "clip.mp4", "-map", "0:s:0", "subs.srt"}, {"-i", "clip.mp4", "subs.vtt"}, {"-i", "clip.mp4", "subs.ass"},
		{"-i", "clip.mp4", "-f", "s16le", "out/audio.pcm"},
		{"-i", "clip.mp4", "-f", "rawvideo", "out/frames.rgb"},
		{"-i", "clip.mp4", "-f", "mp4", "out.tmp"},
		{"-f", "concat", "-safe", "0", "-i", "concat.txt", "-c", "copy", "edited.mp4"},
		{"-f", "lavfi", "-i", "color=c=black:s=16x16", "-i", "clip.mp4", "out.mp4"},
		{"-i", "clip.mp4", "-filter_complex_script", "filter_complex.txt", "-map", "[vout]", "-r", "29.97", "-b:v", "2.5M", "out.mp4"},
		{"-i", "clip.mp4", "-progress", "pipe:1", "-f", "null", "-"},
	}
	for _, argv := range cases {
		if err := ValidateFFmpegOutputs(argv); err != nil {
			t.Errorf("refused a first-party output %v: %v", argv, err)
		}
	}
}

// libavfilter reads a filter name up to the first unquoted, unescaped =, [, ,
// or ; and cuts it at the first @, whatever the instance holds. Every spelling
// of a refused or path-checked filter, crossed with every instance form,
// quoting and separator, is refused (local ffmpeg 8: movie@=, movie@a-b=,
// 'movie'@x=, mo\vie=, movie\@x= and 'movie@x'= all instantiate movie).
func TestValidateArgvFilterNameGrammar(t *testing.T) {
	dir := reviewSessionDir(t)
	filters := map[string]string{
		"zmq": "=a=1", "azmq": "=a=1", "sendcmd": "=c=x", "asendcmd": "=c=x",
		"frei0r": "=glow", "ladspa": "=file=cmt", "lv2": "=p=x",
		"movie": "=f=pulse:filename=default", "amovie": "=format_opts=x:filename=default",
		"subtitles": "=absent.srt", "lut3d": "=file=absent.cube",
	}
	instances := []string{"", "@", "@x", "@a-b", "@a.b+c%é"}
	quotings := []func(name, inst string) string{
		func(n, i string) string { return n + i },
		func(n, i string) string { return "'" + n + "'" + i },
		func(n, i string) string { return "'" + n + i + "'" },
		func(n, i string) string { return "\\" + n[:1] + n[1:] + strings.ReplaceAll(i, "@", "\\@") },
	}
	seps := []string{"", "null,", "null;", "[0:v]", "null, "}
	count := 0
	for name, opts := range filters {
		for _, inst := range instances {
			for qi, quote := range quotings {
				for _, sep := range seps {
					graph := sep + quote(name, inst) + opts
					for _, opt := range []string{"-vf", "-filter_complex"} {
						count++
						if err := ValidateArgv([]string{"-i", "clip.mp4", opt, graph, "-f", "null", "-"}, dir); err == nil {
							t.Errorf("accepted %s %q (quoting %d)", opt, graph, qi)
						}
					}
				}
			}
		}
	}
	t.Logf("%d combinations", count)
}

// Round 5: no first-party graph uses movie or amovie, so every spelling of
// them is refused, whatever its options look like; and every ffmpeg output is
// found by the option table, not by its shape, and needs an allowlisted
// extension or an -f in front of it.
func TestValidateRound5Shapes(t *testing.T) {
	dir := reviewSessionDir(t)
	var graphs []string
	for _, name := range []string{"movie", "amovie", "'movie'", "mo\\vie", "movie@x", "'amovie@a-b'", "amovie\\@x"} {
		for _, opts := range []string{"=default", "='f'=pulse:filename=default", "=\\f=pulse:filename=default", "=f\\=pulse", "=filename=default:'format_name'=alsa", "=default:si=0"} {
			graphs = append(graphs, name+opts)
		}
	}
	for _, g := range graphs {
		for _, argv := range [][]string{
			{"-i", "clip.mp4", "-vf", g, "-f", "null", "-"},
			{"-f", "lavfi", "-i", g, "-t", "1", "mic.wav"},
		} {
			if err := ValidateArgv(argv, dir); err == nil {
				t.Errorf("ValidateArgv accepted %q", argv)
			}
		}
	}
	outputs := [][]string{
		{"-i", "clip.mp4", "-c", "copy", "a=b.m3u8"},
		{"-i", "clip.mp4", "-c", "copy", "x,y.mpd"},
		{"-i", "clip.mp4", "-c", "copy", "o'q.sdp"},
		{"-i", "clip.mp4", "-c", "copy", "a b.ts"},
		{"-i", "clip.mp4", "-c", "copy", "[x].m3u8"},
		{"-i", "clip.mp4", "-c", "copy", "noext"},
		{"-i", "clip.mp4", "-c", "copy", "dir.d/noext"},
		{"-i", "clip.mp4", "-f", "mp4", "a.mp4", "b.m3u8"},
		{"-i", "clip.mp4", "-f", "mp4", "pipe:1", "x,y.mpd"},
		{"-i", "clip.mp4", "-unknown_bool", "out.m3u8"},
		{"-i", "clip.mp4", "-an", "-sn", "out.M3U8"},
	}
	for _, argv := range outputs {
		if err := ValidateFFmpegOutputs(argv); err == nil {
			t.Errorf("ValidateFFmpegOutputs accepted %q", argv)
		}
	}
	t.Logf("%d graph cases, %d output cases", len(graphs)*2, len(outputs))
}

// The security auditor's leads on 1c566aff1: private demuxer options (glob,
// graph_file, drefs), a scheme in a filter's file option, capture sources, odd
// output names, and session symlinks under outputs and filter write paths.
func TestValidateAuditorLeads(t *testing.T) {
	dir := reviewSessionDir(t)
	outside := t.TempDir()
	if err := os.Symlink(outside, filepath.Join(dir, "lnk")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	in := []string{"-i", "clip.mp4"}
	null := []string{"-f", "null", "-"}
	join := func(parts ...[]string) []string {
		var out []string
		for _, p := range parts {
			out = append(out, p...)
		}
		return out
	}
	cases := map[string][]string{
		"1 image2 glob braces":           {"-f", "image2", "-pattern_type", "glob", "-i", "{.,}./outside/*.png", "-frames:v", "1", "g.png"},
		"1 image2 glob dot-question":     {"-pattern_type", "glob", "-f", "image2", "-i", ".?/outside/*.png", "o.png"},
		"2 lavfi graph_file":             {"-f", "lavfi", "-graph_file", "graph-movie.txt", "-i", "x", "o.mp4"},
		"3 metadata file tcp no slash":   join(in, []string{"-vf", "metadata=mode=print:file=tcp\\:127.0.0.1\\:9"}, null),
		"3 metadata file tcp slashes":    join(in, []string{"-vf", "metadata=mode=print:file=tcp\\://127.0.0.1\\:9"}, null),
		"3 ametadata file udp quoted":    join(in, []string{"-af", "ametadata=mode=print:file='udp:127.0.0.1:9'"}, null),
		"3 drawtext textfile scheme":     join(in, []string{"-vf", "drawtext=textfile=data\\:,x"}, null),
		"4 ddagrab lavfi":                {"-f", "lavfi", "-i", "ddagrab", "-frames:v", "1", "o.png"},
		"4 gfxcapture lavfi":             {"-f", "lavfi", "-i", "gfxcapture", "-frames:v", "1", "o.png"},
		"4 ddagrab in a graph":           join(in, []string{"-filter_complex", "ddagrab[s];[0:v][s]overlay[o]", "-map", "[o]"}, null),
		"4 an unknown filter":            join(in, []string{"-vf", "nosuchfilter=1"}, null),
		"5 output with a space":          join(in, []string{"-c", "copy", "o x.m3u8"}),
		"5 output with a comma":          join(in, []string{"-c", "copy", "o,x.m3u8"}),
		"5 output that is an extension":  join(in, []string{"-c", "copy", ".mpd"}),
		"5 output with brackets":         join(in, []string{"-c", "copy", "[o].m3u8"}),
		"6 output through a symlink":     join(in, []string{"-c", "copy", "lnk/o.mp4"}),
		"6 filter write through symlink": join(in, []string{"-vf", "metadata=mode=print:file=lnk/x.txt"}, null),
		"6 vidstab result via symlink":   join(in, []string{"-vf", "vidstabdetect=result=lnk/x.trf"}, null),
		"8 mov drefs":                    {"-enable_drefs", "1", "-use_absolute_path", "1", "-i", "clip.mp4", "o.mp4"},
		"ffprobe lavfi positional":       {"-f", "lavfi", "movie=clip.mp4"},
		"an unknown option":              join(in, []string{"-nosuchoption", "1", "o.mp4"}),
		"an inline option value":         join(in, []string{"-vf=null", "o.mp4"}),
	}
	for name, argv := range cases {
		t.Run(name, func(t *testing.T) {
			err := ValidateArgv(argv, dir)
			if err == nil {
				err = ValidateFFmpegOutputs(argv)
			}
			var argvErr *ArgvError
			if !errors.As(err, &argvErr) {
				t.Fatalf("accepted %q", argv)
			}
		})
	}
}

// One accept row per argv shape the engine's builders emit (session-relative
// paths substituted for the engine's own). Each goes through ValidateArgv and,
// as an ffmpeg op does, ValidateFFmpegOutputs; ffprobe rows only the former.
func TestValidateArgvAcceptsBuilderShapes(t *testing.T) {
	dir := reviewSessionDir(t)
	const wl = "mov,matroska,mp3,wav,aac,flac,ogg,avi,mpegts,mpeg,flv,asf,aiff,caf,w64,mxf"
	ffmpeg := map[string][]string{
		"FfmpegArgvBuilder frame diff stats":  {"-protocol_whitelist", "file", "-threads", "2", "-i", "clip.mp4", "-an", "-vf", "fps=2,scale=320:-2,signalstats,metadata=print:key=lavfi.signalstats.YDIF:file=-", "-f", "null", "-"},
		"FfmpegArgvBuilder silencedetect":     {"-protocol_whitelist", "file", "-i", "clip.mp4", "-vn", "-af", "silencedetect=noise=-30dB:d=0.5", "-f", "null", "-"},
		"FfmpegArgvBuilder scene select":      {"-protocol_whitelist", "file", "-threads", "2", "-i", "clip.mp4", "-an", "-vf", "select='gt(scene,0.3)',showinfo", "-f", "null", "-"},
		"FfmpegArgvBuilder.cs:161 rgb raw":    {"-protocol_whitelist", "file", "-threads", "2", "-i", "clip.mp4", "-an", "-sn", "-vf", "fps=2,scale=64:36:flags=area", "-frames:v", "120", "-f", "rawvideo", "-pix_fmt", "rgb24", "out/frames.raw"},
		"FfmpegArgvBuilder.cs:205 gray raw":   {"-protocol_whitelist", "file", "-format_whitelist", wl, "-threads", "2", "-i", "clip.mp4", "-an", "-sn", "-vf", "fps=1,scale=32:18", "-frames:v", "60", "-fs", "34560", "-f", "rawvideo", "-pix_fmt", "gray", "out/gray.raw"},
		"FfmpegArgvBuilder wav extract":       {"-protocol_whitelist", "file", "-ss", "1.5", "-i", "clip.mp4", "-t", "10", "-vn", "-ac", "1", "-ar", "16000", "-sample_fmt", "s16", "-f", "wav", "out/a.wav"},
		"FfmpegArgvBuilder hdr still":         {"-protocol_whitelist", "file", "-ss", "2", "-i", "clip.mp4", "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p", "-f", "image2", "-c:v", "mjpeg", "-q:v", "4", "out/s.jpg"},
		"FfmpegArgvBuilder contact sheet":     {"-protocol_whitelist", "file", "-ss", "2", "-i", "clip.mp4", "-filter_complex", "[0:v]split[a][b];[a]scale=320:-2[o1];[b]scale=160:-2,pad=320:180[o2];[o1][o2]hstack[out]", "-map", "[out]", "-frames:v", "1", "-f", "image2", "-c:v", "mjpeg", "-q:v", "4", "out/c.jpg"},
		"FfmpegArgvBuilder last frame":        {"-protocol_whitelist", "file", "-ss", "0", "-sseof", "-1", "-i", "clip.mp4", "-update", "1", "-frames:v", "1", "-vf", "scale=320:-2", "-f", "image2", "-c:v", "png", "-q:v", "2", "out/last.png"},
		"FfmpegArgvBuilder thumbnail text":    {"-protocol_whitelist", "file", "-i", "clip.mp4", "-frames:v", "1", "-vf", "drawtext=fontfile='font.ttf':text='Episode 3\\: the end, part 2':fontsize=48:fontcolor=white:x=(w-text_w)/2:y=h-80", "-f", "image2", "-c:v", "mjpeg", "-q:v", "3", "out/t.jpg"},
		"MediaDerivativeService.cs:871 s16le": {"-y", "-nostdin", "-hide_banner", "-protocol_whitelist", "file", "-format_whitelist", wl, "-i", "clip.mp4", "-vn", "-ac", "1", "-ar", "8000", "-sample_fmt", "s16", "-f", "s16le", "out/peaks.raw"},
		"VideoEventDetector.cs:160 beat pcm":  {"-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file", "-format_whitelist", wl, "-threads", "2", "-i", "clip.mp4", "-vn", "-ac", "1", "-ar", "22050", "-f", "s16le", "out/beat.pcm"},
		"GeneratedClipNormalizer tmp + -f":    {"-y", "-i", "clip.mp4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata", "comment=AI generated: disclosure", "-metadata", "ai_generated=true", "-metadata", "ai_generator=ReelBolt", "-movflags", "+faststart+use_metadata_tags", "-f", "mp4", "out/clip.tmp"},
		"TimelineRenderer region concat":      {"-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", "concat.txt", "-c", "copy", "-movflags", "+faststart", "program-video.mp4"},
		"VideoCompile reencode":               {"-y", "-i", "clip.mp4", "-filter_complex", "[0:v]trim=start=0:end=1.5,setpts=PTS-STARTPTS[v0];[0:a]atrim=start=0:end=1.5,asetpts=PTS-STARTPTS[a0]", "-map", "[v0]", "-map", "[a0]", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.1", "-crf", "20", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0", "-maxrate", "8M", "-bufsize", "16M", "-c:a", "aac", "-b:a", "192k", "-tag:v", "avc1", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv", "-fps_mode", "cfr", "-r", "30", "-progress", "pipe:1", "-nostats", "-movflags", "+faststart", "out/edited.mp4"},
		"VideoCompile overlay loop":           {"-stream_loop", "-1", "-i", "clip.mp4", "-loop", "1", "-framerate", "30", "-i", "font.ttf", "-filter_complex", "[1:v]scale=100:100[l];[0:v][l]overlay=10:10:enable='between(t,1,2)'[o]", "-map", "[o]", "-map_metadata", "-1", "-map_chapters", "-1", "-t", "5", "out.webm"},
		"SceneCutFinder stat files":           {"-i", "clip.mp4", "-an", "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file='stats-0.txt'", "-f", "null", "-"},
		"ModerationMediaSampler astats":       {"-i", "clip.mp4", "-vn", "-af", "aresample=16000,asetnsamples=n=65536:p=0,astats=length=1:metadata=1:reset=0,ametadata=mode=print:key=lavfi.astats.1.RMS_peak:file=-", "-f", "null", "-"},
		"AudioCleanup chain":                  {"-y", "-i", "clip.mp4", "-af", "afftdn=nf=-25,highpass=f=80,lowpass=f=12000,arnndn=m='model.rnnn',loudnorm=I=-16:TP=-1.5:LRA=11", "-c:a", "pcm_s16le", "-f", "wav", "out/clean.wav"},
		"AudioCleanup mp3 out":                {"-y", "-i", "clip.mp4", "-af", "dynaudnorm", "-c:a", "libmp3lame", "-b:a", "192k", "out/clean.mp3"},
		"Stabilize detect":                    {"-i", "clip.mp4", "-vf", "vidstabdetect=shakiness=5:result='out/x.trf'", "-f", "null", "-"},
		"Stabilize transform":                 {"-i", "clip.mp4", "-vf", "vidstabtransform=input='out/x.trf':smoothing=10,unsharp=5:5:0.8:3:3:0.4", "out/stable.mp4"},
		"Captions ass":                        {"-i", "clip.mp4", "-vf", "ass=filename='sub.srt'", "-c:a", "copy", "out/captioned.mp4"},
		"AvatarStillThenAnimate wav":          {"-i", "clip.mp4", "-filter_complex", "[0:a]apad=pad_dur=1,atrim=0:5[out]", "-map", "[out]", "-c:a", "pcm_s16le", "-f", "wav", "out/pad.wav"},
		"capability probe":                    {"-hide_banner", "-filters"},
	}
	for name, argv := range ffmpeg {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("%s: ValidateArgv refused %q: %v", name, argv, err)
			continue
		}
		if err := ValidateFFmpegOutputs(argv); err != nil {
			t.Errorf("%s: ValidateFFmpegOutputs refused %q: %v", name, argv, err)
		}
	}
	ffprobe := map[string][]string{
		"probe streams":   {"-v", "error", "-protocol_whitelist", "file", "-format_whitelist", wl, "-show_entries", "stream=index,codec_type:format=duration", "-of", "json", "clip.mp4"},
		"probe keyframes": {"-v", "error", "-read_intervals", "%+#1", "-skip_frame", "nokey", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-print_format", "csv=p=0", "clip.mp4"},
		"probe format":    {"-v", "error", "-show_format", "-show_streams", "-print_format", "json", "clip.mp4"},
	}
	for name, argv := range ffprobe {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("%s: ValidateArgv refused %q: %v", name, argv, err)
		}
	}
}

// -filter_complex_script is content-checked: an unsafe graph in a session file
// is refused, and so is a script path outside the session.
func TestValidateArgvFilterScriptContentAndPath(t *testing.T) {
	dir := reviewSessionDir(t)
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "g.txt"), []byte("[0:v]null[vout]"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(dir, "lnk")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	scripts := map[string]string{
		"s-zmq.txt":     "[0:v]zmq[vout]",
		"s-movie.txt":   "movie=default:f=pulse[vout]",
		"s-path.txt":    "[0:v]metadata=mode=print:file=/tmp/x.log[vout]",
		"s-scheme.txt":  "[0:v]metadata=mode=print:file=tcp\\:127.0.0.1\\:9[vout]",
		"s-unknown.txt": "[0:v]nosuchfilter[vout]",
	}
	for name, body := range scripts {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	paths := []string{"s-zmq.txt", "s-movie.txt", "s-path.txt", "s-scheme.txt", "s-unknown.txt", "/etc/passwd", "../g.txt", "lnk/g.txt"}
	for _, p := range paths {
		if err := ValidateArgv([]string{"-i", "clip.mp4", "-filter_complex_script", p, "-map", "[vout]", "out.mp4"}, dir); err == nil {
			t.Errorf("accepted -filter_complex_script %q", p)
		}
	}
}

// Round 6 rejects: path-valued output options and inputs through a session
// symlink, an input scheme only the input check sees, and the font-path
// exception held to font keys under the system font prefix.
func TestValidateArgvRound6Rejects(t *testing.T) {
	dir := reviewSessionDir(t)
	outside := t.TempDir()
	if err := os.Symlink(outside, filepath.Join(dir, "lnk")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	in := []string{"-i", "clip.mp4"}
	null := []string{"-f", "null", "-"}
	vf := func(g string) []string { return append(append(append([]string{}, in...), "-vf", g), null...) }
	cases := map[string][]string{
		"M14 -passlogfile through a symlink": {"-i", "clip.mp4", "-passlogfile", "lnk/x", "-c:v", "libx264", "out.mp4"},
		"M21 concat: input":                  {"-i", "concat:clip.mp4|clip.mp4", "out.mp4"},
		"M21 subfile: input":                 {"-i", "subfile:clip.mp4", "out.mp4"},
		// The positional scheme check lets pipe:N through; on an input only the
		// input scheme check refuses it.
		"M21 pipe: input":                     {"-i", "pipe:0", "out.mp4"},
		"fontfile climbing out of the prefix": vf("drawtext=fontfile='/usr/share/fonts/../../etc/passwd':textfile='sub.srt'"),
		"fontfile escaped climb":              vf("drawtext=fontfile=/usr/share/fonts/..\\/..\\/etc/passwd:textfile=sub.srt"),
		"fontfile through a session symlink":  vf("drawtext=fontfile='lnk/x.ttf':textfile='sub.srt'"),
		"fontsdir through a session symlink":  vf("ass=filename='captions.ass':fontsdir='lnk'"),
		"fontsdir outside the prefix":         vf("ass=filename='captions.ass':fontsdir='/etc'"),
		"a lookalike prefix":                  vf("ass=filename='captions.ass':fontsdir='/usr/share/fontsX/y'"),
		"the prefix itself on fontfile":       vf("drawtext=fontfile='/usr/share/fonts':textfile='sub.srt'"),
		"the font prefix on a non-font key":   vf("metadata=mode=print:file='/usr/share/fonts/x.log'"),
		"the font prefix on textfile":         vf("drawtext=fontfile='font.ttf':textfile='/usr/share/fonts/x.txt'"),
		"the font prefix on ass filename":     vf("ass=filename='/usr/share/fonts/x.ass'"),
		"the font prefix as an output":        {"-i", "clip.mp4", "/usr/share/fonts/x.mp4"},
	}
	if err := os.WriteFile(filepath.Join(dir, "captions.ass"), []byte("[Script Info]\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	for name, argv := range cases {
		t.Run(name, func(t *testing.T) {
			err := ValidateArgv(argv, dir)
			var argvErr *ArgvError
			if !errors.As(err, &argvErr) {
				t.Fatalf("accepted %q", argv)
			}
		})
	}
}

// Real builder output for the engine's caption fonts, strings copied from the
// C# builders: CaptionFilterBuilder.BuildAssFilter with
// VideoEditingOptions.CaptionFontsDir (VideoCompileStepExecutor.cs:8564),
// and DrawtextFilterBuilder's drawtext with VideoEditingOptions.FontFilePath,
// both through EscapeFilterPath ('->'\”).
func TestValidateArgvAcceptsSystemFontPaths(t *testing.T) {
	if _, err := os.Stat("/usr/share/fonts"); err != nil {
		t.Skip("no /usr/share/fonts on this machine")
	}
	dir := reviewSessionDir(t)
	if err := os.WriteFile(filepath.Join(dir, "captions.ass"), []byte("[Script Info]\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	rows := map[string][]string{
		"real: ass captions with fontsdir": {"-i", "clip.mp4", "-filter_complex",
			"[vbase]ass=filename='captions.ass':fontsdir='/usr/share/fonts/reelbolt-captions'[vout]", "-map", "[vout]", "out.mp4"},
		"real: drawtext with the default fontfile": {"-i", "clip.mp4", "-filter_complex",
			"[v0]drawbox=x=10:y=20:w=200:h=80:color=black@0.5:t=fill[box0];[box0]drawtext=fontfile='/usr/share/fonts/dejavu/DejaVuSans.ttf':textfile='sub.srt':expansion=none:fontsize=48:fontcolor=white:x=10+(200-text_w)/2:y=20+(80-text_h)/2:alpha='if(lt(t,0.2),t/0.2,1)':enable='between(t,0,2)'[vout]",
			"-map", "[vout]", "out.mp4"},
		"real: EscapeFilterPath of a quoted font dir": {"-i", "clip.mp4", "-vf",
			"ass=filename='captions.ass':fontsdir='/usr/share/fonts/it'\\''s'", "out.mp4"},
	}
	for name, argv := range rows {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("%s: refused %q: %v", name, argv, err)
		}
		if err := ValidateFFmpegOutputs(argv); err != nil {
			t.Errorf("%s: output check refused %q: %v", name, argv, err)
		}
	}
}

// Real rows for the builder options the accept table lacked, then a
// synthetic table: every allowlisted filter name and every allowlisted option
// is accepted in a minimal valid form, so the allowlists and the accept tests
// cannot drift apart.
func TestValidateArgvAcceptsEveryAllowlistedName(t *testing.T) {
	dir := reviewSessionDir(t)
	for _, name := range []string{"grade/shaper.cube", "grade/look.cube"} {
		if err := os.MkdirAll(filepath.Join(dir, "grade"), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(dir, name), []byte("LUT_3D_SIZE 2\n"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	const mif = "mov,matroska,mp3,wav,aac,ac3,flac,ogg,avi,mpegts,mpeg,flv,asf,aiff,caf,w64,mxf"
	real := map[string][]string{
		"real: ModerationMediaSampler.BuildAudioArgs (-acodec)":    {"-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", "file", "-format_whitelist", mif, "-threads", "2", "-i", "clip.mp4", "-vn", "-af", "pan=mono|c0=0.5*c0+0.5*c1", "-ac", "1", "-ar", "16000", "-acodec", "pcm_s16le", "-t", "1800", "out/audio.wav"},
		"real: ModerationMediaSampler.BuildChannelArgs (-dn)":      {"-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", "file", "-format_whitelist", mif, "-threads", "2", "-i", "clip.mp4", "-vn", "-sn", "-dn", "-af", "pan=mono|c0=c2", "-ar", "16000", "-acodec", "pcm_s16le", "-t", "1800", "out/ch2.wav"},
		"real: ModerationMediaSampler frame (-copyts)":             {"-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-protocol_whitelist", "file", "-format_whitelist", mif, "-ss", "12.5", "-copyts", "-threads", "2", "-i", "clip.mp4", "-map", "0:0", "-an", "-sn", "-dn", "-vf", "scale='min(768,iw)':-2:flags=lanczos,metadata=mode=add:key=rb:value=1,metadata=mode=print:key=rb:file=-", "-frames:v", "1", "-f", "image2", "-c:v", "mjpeg", "out/frame.jpg"},
		"real: SegmentationMaskArgvBuilder (-start_number)":        {"-hide_banner", "-nostdin", "-y", "-f", "image2", "-start_number", "1", "-framerate", "29.97", "-i", "segmentation/mask-%06d.pbm", "-an", "-sn", "-dn", "-c:v", "ffv1", "-pix_fmt", "gray", "-r", "29.97", "segmentation/mask.mkv"},
		"real: SegmentationMaskPreviewArgvBuilder (-x264-params)":  {"-hide_banner", "-nostdin", "-y", "-i", "clip.mp4", "-an", "-sn", "-map", "0:v:0", "-vf", "scale=640:-2", "-fps_mode", "cfr", "-r", "30", "-frames:v", "90", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-color_range", "pc", "-x264-params", "fullrange=on", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-movflags", "+faststart", "-f", "mp4", "out/preview.mp4"},
		"real: VoiceoverStepExecutor (-filter:a)":                  {"-y", "-i", "clip.mp4", "-filter:a", "atempo=1.08", "-c:a", "pcm_s16le", "out/line.wav"},
		"real: GeneratedClipNormalizer disclosure label (text=)":   {"-y", "-i", "clip.mp4", "-vf", "drawtext=fontfile='font.ttf':text='AI-generated':fontsize=h/30:fontcolor=white@0.9:box=1:boxcolor=black@0.5:boxborderw=8:x=w-tw-24:y=h-th-24", "-c:v", "libx264", "-f", "mp4", "out/clip.tmp"},
		"real: PlacementStills band label (text=)":                 {"-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-protocol_whitelist", "file", "-i", "clip.mp4", "-frames:v", "1", "-vf", "drawbox=x=12:y=40:w=200:h=60:color=yellow@0.95:t=4,drawtext=fontfile='font.ttf':text='p12':expansion=none:x=20:y=48:fontsize=18:fontcolor=black:box=1:boxcolor=yellow@0.95:boxborderw=4", "-f", "image2", "-c:v", "mjpeg", "out/placement.jpg"},
		"real: GradeFilterBuilder shaper LUT (lut1d/lut3d file=)":  {"-i", "clip.mp4", "-vf", "lut1d=file='grade/shaper.cube':interp=linear,lut3d=file='grade/look.cube':interp=tetrahedral", "out/graded.mp4"},
		"real: FfmpegArgvBuilder thumbnail (boxcolor/fontcolor #)": {"-protocol_whitelist", "file", "-i", "clip.mp4", "-frames:v", "1", "-vf", "drawtext=fontfile='font.ttf':textfile='sub.srt':expansion=none:fontsize=64:fontcolor=#FFFFFF:box=1:boxborderw=14:boxcolor=black@0.6:x=(w-text_w)/2:y=h*0.8-text_h/2", "-f", "image2", "-c:v", "mjpeg", "-q:v", "3", "out/thumb.jpg"},
		"real: VideoCompile amix probe (-h)":                       {"-hide_banner", "-h", "filter=amix"},
	}
	for name, argv := range real {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("%s: refused %q: %v", name, argv, err)
		}
		if err := ValidateFFmpegOutputs(argv); err != nil {
			t.Errorf("%s: output check refused %q: %v", name, argv, err)
		}
	}
	for name := range allowedFilters {
		argv := []string{"-i", "clip.mp4", "-filter_complex", name, "out.mp4"}
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("synthetic: filter %s refused: %v", name, err)
		}
	}
	values := map[string]string{
		"i": "clip.mp4", "f": "mp4", "filter_complex_script": "filter_complex.txt",
		"vf": "null", "af": "anull", "filter": "null", "filter_complex": "null", "lavfi": "null",
		"progress": "pipe:1", "passlogfile": "pass", "attach": "font.ttf", "dump_attachment": "att",
		"protocol_whitelist": "file", "format_whitelist": "mov", "h": "filter=amix",
	}
	refused := setOf("filter_script", "allowed_extensions", "allowed_segment_extensions", "extension_picky", "hls_key_info_file", "safe")
	for opt := range optionsWithValue {
		v, ok := values[opt]
		if !ok {
			v = "1"
		}
		argv := []string{"-i", "clip.mp4", "-" + opt, v, "out.mp4"}
		err := ValidateArgv(argv, dir)
		if refused[opt] {
			if err == nil {
				t.Errorf("synthetic: -%s outside its allowed use was accepted", opt)
			}
			continue
		}
		if err != nil {
			t.Errorf("synthetic: option -%s refused: %v", opt, err)
		}
	}
	for opt := range optionFlags {
		if err := ValidateArgv([]string{"-i", "clip.mp4", "-" + opt, "out.mp4"}, dir); err != nil {
			t.Errorf("synthetic: flag -%s refused: %v", opt, err)
		}
	}
	if err := ValidateArgv([]string{"-f", "concat", "-safe", "0", "-i", "safe-list.txt", "-c", "copy", "out.mp4"}, dir); err != nil {
		t.Errorf("synthetic: -safe on a concat input refused: %v", err)
	}
}

// The system font exception follows symlinks: a link inside the font
// directory that points out of it is refused, a real font under it is
// accepted.
func TestValidateArgvSystemFontSymlinkOut(t *testing.T) {
	fonts := filepath.Join(t.TempDir(), "fonts")
	if err := os.MkdirAll(fonts, 0o755); err != nil {
		t.Fatal(err)
	}
	saved := systemFontPrefix
	systemFontPrefix = fonts
	t.Cleanup(func() { systemFontPrefix = saved })
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "secret.ttf"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(fonts, "real.ttf"), []byte("font"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(fonts, "out")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	dir := reviewSessionDir(t)
	vf := func(font string) []string {
		return []string{"-i", "clip.mp4", "-vf", "drawtext=fontfile='" + font + "':textfile='sub.srt'", "-f", "null", "-"}
	}
	if err := ValidateArgv(vf(fonts+"/out/secret.ttf"), dir); err == nil {
		t.Error("accepted a fontfile through a symlink out of the font directory")
	}
	if err := ValidateArgv(vf(fonts+"/real.ttf"), dir); err != nil {
		t.Errorf("refused a real font under the font directory: %v", err)
	}
}

// libavfilter unescapes a filter option twice: av_get_token at the graph level,
// then again when the filter splits key=value pairs. Every faithful two-level
// encoding of a scheme or a host path, in every path-valued option, through
// every way a graph reaches ffmpeg, is refused. (Local ffmpeg 8:
// metadata=mode=print:file=tcp\\:127.0.0.1\\:P and file=\'tcp:...\' connect.)
func TestValidateArgvDoubleUnescapeTable(t *testing.T) {
	dir := reviewSessionDir(t)
	outside := t.TempDir()
	if err := os.Symlink(outside, filepath.Join(dir, "lnk")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	esc := func(s, chars string) string {
		var b strings.Builder
		for i := 0; i < len(s); i++ {
			if strings.IndexByte(chars, s[i]) >= 0 {
				b.WriteByte('\\')
			}
			b.WriteByte(s[i])
		}
		return b.String()
	}
	quote := func(s string) string { return "'" + strings.ReplaceAll(s, "'", `'\''`) + "'" }
	l2b := func(v string) string { return esc(v, `\':`) }
	l1b := func(s string) string { return esc(s, `\'[],;=`) }
	encodings := map[string]func(string) string{
		"L1 backslash, L2 backslash": func(v string) string { return l1b(l2b(v)) },
		"L1 backslash, L2 quote":     func(v string) string { return l1b(quote(v)) },
		"L1 quote, L2 backslash":     func(v string) string { return quote(l2b(v)) },
		"L1 quote, L2 quote":         func(v string) string { return quote(quote(v)) },
		"three backslashes":          func(v string) string { return strings.ReplaceAll(v, ":", `\\\:`) },
	}
	values := []string{"tcp://127.0.0.1:9", "tcp:127.0.0.1:9", "/etc/hostname", "../../etc/hostname", "lnk/x.txt"}
	contexts := []struct {
		audio  bool
		filter string
		key    string
	}{
		{false, "metadata=mode=print", "file"},
		{true, "ametadata=mode=print", "file"},
		{false, "drawtext=fontfile=font.ttf", "textfile"},
		{false, "drawtext=textfile=sub.srt", "fontfile"},
		{false, "subtitles=filename=sub.srt", "fontsdir"},
		{false, "vidstabdetect=shakiness=5", "result"},
		{false, "signature=detectmode=full", "filename"},
	}
	if err := os.WriteFile(filepath.Join(dir, "default.txt"), nil, 0o600); err != nil {
		t.Fatal(err)
	}
	count := 0
	for encName, enc := range encodings {
		for _, v := range values {
			// A three-backslash variant only differs from the others where the
			// value has a colon.
			if encName == "three backslashes" && !strings.Contains(v, ":") {
				continue
			}
			for _, c := range contexts {
				graph := c.filter + ":" + c.key + "=" + enc(v)
				opt, src := "-vf", "color=c=black:s=16x16"
				if c.audio {
					opt, src = "-af", "anullsrc"
				}
				script := filepath.Join(dir, fmt.Sprintf("s%d.txt", count))
				if err := os.WriteFile(script, []byte("[0]"+graph+"[o]"), 0o600); err != nil {
					t.Fatal(err)
				}
				ways := map[string][]string{
					opt:               {"-i", "clip.mp4", opt, graph, "-f", "null", "-"},
					"-filter:v":       {"-i", "clip.mp4", "-filter:" + map[bool]string{false: "v", true: "a"}[c.audio], graph, "-f", "null", "-"},
					"-filter_complex": {"-i", "clip.mp4", "-filter_complex", "[0]" + graph + "[o]", "-map", "[o]", "-f", "null", "-"},
					"lavfi input":     {"-f", "lavfi", "-i", src + "," + graph, "-t", "1", "-f", "null", "-"},
					"script":          {"-i", "clip.mp4", "-filter_complex_script", filepath.Base(script), "-map", "[o]", "-f", "null", "-"},
					"instance name":   {"-i", "clip.mp4", opt, strings.Replace(graph, "=", "@x=", 1), "-f", "null", "-"},
				}
				for way, argv := range ways {
					count++
					if err := ValidateArgv(argv, dir); err == nil {
						t.Errorf("%s / %s / %s: accepted %q", encName, way, c.key, argv)
					}
				}
			}
		}
	}
	t.Logf("%d combinations", count)
}

// Security round: the font exemption may not swallow other options hidden in
// a quoted span, an empty or specifier-suffixed -dump_attachment may not
// write to the attachment's own filename tag, and a script carrying either
// shape is refused.
func TestValidateArgvSecurityRound7(t *testing.T) {
	dir := reviewSessionDir(t)
	scripts := map[string]string{
		"s-metadata-tcp.txt": `[0:v]metadata=mode=print:file=tcp\\:127.0.0.1\\:9[o]`,
		"s-font-span.txt":    `[0:v]drawtext=fontfile='/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf:textfile=/etc/hostname'[o]`,
	}
	for name, body := range scripts {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	vf := func(g string) []string { return []string{"-i", "clip.mp4", "-vf", g, "-f", "null", "-"} }
	cases := map[string][]string{
		"font span hides textfile":         vf(`drawtext=fontfile='/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf:textfile=/etc/hostname'`),
		"font span hides f= on subtitles":  vf(`subtitles=f=sub.srt:fontsdir='/usr/share/fonts/truetype:f=tcp\\://127.0.0.1\\:9'`),
		"escaped colon after a font path":  vf(`drawtext=fontfile=/usr/share/fonts/a\\:textfile=/etc/hostname`),
		"fontsdir span hides textfile":     vf(`ass=filename=sub.srt:fontsdir='/usr/share/fonts/x:textfile=/etc/hostname'`),
		"dump_attachment:t empty":          {"-dump_attachment:t", "", "-i", "clip.mp4", "-f", "null", "-"},
		"dump_attachment:t blank":          {"-dump_attachment:t", " ", "-i", "clip.mp4", "-f", "null", "-"},
		"dump_attachment empty":            {"-dump_attachment", "", "-i", "clip.mp4", "-f", "null", "-"},
		"attach":                           {"-i", "clip.mp4", "-attach", "font.ttf", "out.mkv"},
		"passlogfile":                      {"-i", "clip.mp4", "-passlogfile", "pass", "-c:v", "libx264", "out.mp4"},
		"script with a double-escaped tcp": {"-i", "clip.mp4", "-filter_complex_script", "s-metadata-tcp.txt", "-map", "[o]", "-f", "null", "-"},
		"script with a font span":          {"-i", "clip.mp4", "-filter_complex_script", "s-font-span.txt", "-map", "[o]", "-f", "null", "-"},
	}
	for name, argv := range cases {
		t.Run(name, func(t *testing.T) {
			if err := ValidateArgv(argv, dir); err == nil {
				t.Fatalf("accepted %q", argv)
			}
		})
	}
}

// libavfilter loads an option spelled /key=path from that file (local ffmpeg
// 8: metadata=mode=print:/file=fval.txt, fval.txt naming a loopback tcp URL,
// connects). A filter option key is a plain identifier at every unescape
// level, in every way a graph arrives; and the single-backslash font span.
func TestValidateArgvSlashKeysAndFontSpan(t *testing.T) {
	dir := reviewSessionDir(t)
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "x.txt"), []byte("tcp://127.0.0.1:9"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(dir, "lnk")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	for name, body := range map[string]string{
		"fval.txt":       "tcp://127.0.0.1:9",
		"t.txt":          "hello",
		"s-slashkey.txt": "[0:v]metadata=mode=print:/file=fval.txt[o]",
	} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	graphs := map[string]string{
		"/file= from a session file":       "metadata=mode=print:/file=fval.txt",
		"/text= from a session file":       "drawtext=fontfile=font.ttf:/text=t.txt",
		"/textfile= through a symlink":     "drawtext=fontfile=font.ttf:/textfile=lnk/x.txt",
		"/fontfile= through a symlink":     "drawtext=/fontfile=lnk/x.txt:textfile=sub.srt",
		"graph-level escaped slash":        `metadata=mode=print:\/file=fval.txt`,
		"graph-level quoted slash key":     `metadata=mode=print:'/file'=fval.txt`,
		"two-level escaped slash":          `metadata=mode=print:\\/file=fval.txt`,
		"a dotted key":                     "metadata=mode=print:file.x=fval.txt",
		"a dashed key":                     "metadata=mode=print:-file=fval.txt",
		"single-backslash font span (Q13)": `drawtext=fontfile=/usr/share/fonts/a\:textfile=/etc/hostname`,
	}
	count := 0
	for name, g := range graphs {
		ways := map[string][]string{
			"-vf":             {"-i", "clip.mp4", "-vf", g, "-f", "null", "-"},
			"-filter_complex": {"-i", "clip.mp4", "-filter_complex", "[0:v]" + g + "[o]", "-map", "[o]", "-f", "null", "-"},
			"lavfi":           {"-f", "lavfi", "-i", "color=c=black:s=16x16," + g, "-t", "1", "-f", "null", "-"},
		}
		for way, argv := range ways {
			count++
			if err := ValidateArgv(argv, dir); err == nil {
				t.Errorf("%s via %s: accepted %q", name, way, argv)
			}
		}
	}
	count++
	if err := ValidateArgv([]string{"-i", "clip.mp4", "-filter_complex_script", "s-slashkey.txt", "-map", "[o]", "-f", "null", "-"}, dir); err == nil {
		t.Error("script with a /file= key: accepted")
	}
	t.Logf("%d cases", count)
}

// The inputs only the dropped colon-split reading refused, pinned at their
// intended result: an escaped colon inside drawtext's display text, and inside
// a literal path in the system font directory, are accepted; ffmpeg reads
// each as one value.
func TestValidateArgvEscapedColonsInTextAndFonts(t *testing.T) {
	if _, err := os.Stat("/usr/share/fonts"); err != nil {
		t.Skip("no /usr/share/fonts on this machine")
	}
	dir := reviewSessionDir(t)
	for _, g := range []string{
		`drawtext=fontfile=font.ttf:text='a\:/etc/passwd'`,
		`drawtext=fontfile=font.ttf:text='x\:tcp\://h'`,
		`drawtext=fontfile='/usr/share/fonts/a\:/etc/passwd':textfile=sub.srt`,
	} {
		if err := ValidateArgv([]string{"-i", "clip.mp4", "-vf", g, "-f", "null", "-"}, dir); err != nil {
			t.Errorf("refused %q: %v", g, err)
		}
	}
}

// libavfilter reads a pad label with av_get_token(buf, "]"), so a quote or a
// backslash moves where ffmpeg thinks a label ends. Labels are held to the
// characters the engine's labels use ([A-Za-z0-9_:]) instead of emulating
// that, in argv, lavfi and script forms; curves psfile/plot are path keys;
// and positional values of file-taking filters resolve inside the session.
func TestValidateArgvPadLabelsAndPositionalPaths(t *testing.T) {
	dir := reviewSessionDir(t)
	outside := t.TempDir()
	if err := os.Symlink(outside, filepath.Join(dir, "lnk")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	fileFilters := []string{
		"metadata=mode=print:file=stats-0.txt",
		"curves=psfile=stats-0.txt",
		"curves=plot=stats-0.txt",
		"drawtext=fontfile=font.ttf:textfile=sub.srt",
		"subtitles=filename=sub.srt",
		"arnndn=model=stats-0.txt",
		"vidstabdetect=result=stats-0.txt",
		"vidstabtransform=input=stats-0.txt",
	}
	labels := map[string]string{
		"escaped label": `[0:v]null[x\],%s[o]`,
		"quoted label":  `[0:v]null[x'],%s'[o]`,
		"quoted close":  `[0:v]null[x']'],%s[o]`,
	}
	count := 0
	for lname, shape := range labels {
		for _, ff := range fileFilters {
			g := fmt.Sprintf(shape, ff)
			if err := os.WriteFile(filepath.Join(dir, "lbl.txt"), []byte(g), 0o600); err != nil {
				t.Fatal(err)
			}
			for way, argv := range map[string][]string{
				"-filter_complex": {"-i", "clip.mp4", "-filter_complex", g, "-map", "[o]", "-f", "null", "-"},
				"script":          {"-i", "clip.mp4", "-filter_complex_script", "lbl.txt", "-map", "[o]", "-f", "null", "-"},
			} {
				count++
				if err := ValidateArgv(argv, dir); err == nil {
					t.Errorf("%s / %s: accepted %q", lname, way, g)
				}
			}
		}
	}
	for _, g := range []string{
		"curves=psfile=lnk/x.acv",
		"curves=plot=lnk/x.plt",
		"drawtext=lnk/x.ttf:textfile=sub.srt",
		"metadata=print:k:v:same_str:e:lnk/x.txt",
		"arnndn=lnk/x.rnnn",
		"vidstabdetect=lnk/x.trf",
		"vidstabtransform=lnk/x.trf",
		"life=lnk/x.txt",
	} {
		count++
		if err := ValidateArgv([]string{"-i", "clip.mp4", "-vf", g, "-f", "null", "-"}, dir); err == nil {
			t.Errorf("accepted %q", g)
		}
	}
	t.Logf("%d cases", count)
}

// Options with a specifier are either the engine's own specifier uses (-c:v,
// -b:a, -q:v, -frames:v, -filter:a, -tag:v, -profile:v, -metadata:s:v) or
// refused, so a specifier never skips the check its base option gets; and
// -progress resolves inside the session like any other written path.
func TestValidateArgvSpecifiersAndProgress(t *testing.T) {
	dir := reviewSessionDir(t)
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "x.mp4"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(dir, "lnk")); err != nil {
		t.Skipf("symlinks are not available here: %v", err)
	}
	cases := map[string][]string{
		"-i:v through a symlink":        {"-i:v", "lnk/x.mp4", "out.mp4"},
		"-i:0 on a host path":           {"-i:0", "lnk/x.mp4", "-f", "null", "-"},
		"-progress through a symlink":   {"-i", "clip.mp4", "-progress", "lnk/p.txt", "-f", "null", "-"},
		"-progress:x through a symlink": {"-i", "clip.mp4", "-progress:x", "lnk/p.txt", "-f", "null", "-"},
		"-filter_complex_script:x":      {"-i", "clip.mp4", "-filter_complex_script:x", "filter_complex.txt", "out.mp4"},
		"-vf:x with an unknown filter":  {"-i", "clip.mp4", "-vf:x", "zmq", "out.mp4"},
		"-safe:x":                       {"-f", "concat", "-safe:x", "0", "-i", "safe-list.txt", "out.mp4"},
		"-format_whitelist:x":           {"-format_whitelist:x", "mov", "-i", "clip.mp4", "out.mp4"},
		"-protocol_whitelist:x":         {"-protocol_whitelist:x", "file,tcp", "-i", "clip.mp4", "out.mp4"},
		"-ss:x":                         {"-ss:x", "1", "-i", "clip.mp4", "out.mp4"},
	}
	for name, argv := range cases {
		t.Run(name, func(t *testing.T) {
			if err := ValidateArgv(argv, dir); err == nil {
				t.Fatalf("accepted %q", argv)
			}
		})
	}
	for _, argv := range [][]string{
		{"-i", "clip.mp4", "-c:v", "libx264", "-b:a", "192k", "-q:v", "4", "-frames:v", "1", "-tag:v", "avc1", "-profile:v", "high", "-metadata:s:v", "title=x", "-filter:a", "anull", "out.mp4"},
		{"-i", "clip.mp4", "-progress", "pipe:1", "-f", "null", "-"},
		{"-i", "clip.mp4", "-progress", "out/progress.txt", "-f", "null", "-"},
	} {
		if err := ValidateArgv(argv, dir); err != nil {
			t.Errorf("refused %q: %v", argv, err)
		}
	}
}

// The font exception fails closed: with no system font directory on the
// host, a font key naming it is refused like any other host path.
func TestValidateArgvSystemFontMissingFailsClosed(t *testing.T) {
	saved := systemFontPrefix
	systemFontPrefix = filepath.Join(t.TempDir(), "no-such-fonts")
	t.Cleanup(func() { systemFontPrefix = saved })
	dir := reviewSessionDir(t)
	g := "drawtext=fontfile='" + systemFontPrefix + "/x.ttf':textfile='sub.srt'"
	if err := ValidateArgv([]string{"-i", "clip.mp4", "-vf", g, "-f", "null", "-"}, dir); err == nil {
		t.Fatal("accepted a font path under a font directory the host does not have")
	}
}

// Pins: a tcp target or a host path given positionally at each file-taking
// filter's file position is refused, as its keyed spelling is.
func TestValidateArgvPositionalFileValues(t *testing.T) {
	dir := reviewSessionDir(t)
	shapes := []string{
		"drawtext=%s", "metadata=print:k:v:same_str:e:%s", "ametadata=print:k:v:same_str:e:%s",
		"life=%s", "arnndn=%s", "vidstabdetect=%s", "vidstabtransform=%s",
		"curves=none:m:r:g:b:a:%s", "curves=none:m:r:g:b:a:x.acv:%s", "signature=full:1:%s",
		"lut1d=%s", "lut3d=%s", "subtitles=%s", "ass=%s", "subtitles=sub.srt:640x360:%s", "ass=sub.srt:640x360:%s",
	}
	count := 0
	for _, shape := range shapes {
		for _, v := range []string{`tcp\\:127.0.0.1\\:9`, "/etc/hostname", "../../etc/hostname"} {
			g := fmt.Sprintf(shape, v)
			opt := "-vf"
			if strings.HasPrefix(g, "ametadata") || strings.HasPrefix(g, "arnndn") {
				opt = "-af"
			}
			count++
			if err := ValidateArgv([]string{"-i", "clip.mp4", opt, g, "-f", "null", "-"}, dir); err == nil {
				t.Errorf("accepted %q", g)
			}
		}
	}
	t.Logf("%d cases", count)
}

// ffmpeg applies the LAST -protocol_whitelist given for an input (local
// ffmpeg 8: "-protocol_whitelist file,pipe -protocol_whitelist file" refuses
// a pipe input, the reverse order opens it). The inserted file,pipe must not
// land after a narrower value the engine already set for that input, and an
// engine value may not be wider than file, pipe and concat.
func TestWithProtocolWhitelistNeverWidensTheEnginesValue(t *testing.T) {
	cases := map[string]struct{ in, want []string }{
		"engine sets file for its input": {
			[]string{"-protocol_whitelist", "file", "-i", "clip.mp4", "out.mp4"},
			[]string{"-protocol_whitelist", "file", "-i", "clip.mp4", "out.mp4"},
		},
		"engine sets file,concat on a concat input": {
			[]string{"-protocol_whitelist", "file,concat", "-f", "concat", "-i", "list.txt", "out.mp4"},
			[]string{"-f", "concat", "-protocol_whitelist", "file,concat", "-i", "list.txt", "out.mp4"},
		},
		"only the second input carries a value": {
			[]string{"-i", "a.mp4", "-protocol_whitelist", "file", "-i", "b.mp4", "out.mp4"},
			[]string{"-protocol_whitelist", "file,pipe", "-i", "a.mp4", "-protocol_whitelist", "file", "-i", "b.mp4", "out.mp4"},
		},
		"ffprobe with the engine's value and no -i": {
			[]string{"-v", "error", "-protocol_whitelist", "file", "-show_format", "clip.mp4"},
			[]string{"-v", "error", "-show_format", "clip.mp4", "-protocol_whitelist", "file"},
		},
		"no value anywhere": {
			[]string{"-i", "clip.mp4", "out.mp4"},
			[]string{"-protocol_whitelist", "file,pipe", "-i", "clip.mp4", "out.mp4"},
		},
	}
	for name, tc := range cases {
		if got, _ := WithProtocolWhitelist(tc.in); strings.Join(got, " ") != strings.Join(tc.want, " ") {
			t.Errorf("%s: %q, want %q", name, got, tc.want)
		}
	}
	dir := reviewSessionDir(t)
	for _, v := range []string{"file,tcp", "file,pipe,http", "crypto,file", "tcp"} {
		if err := ValidateArgv([]string{"-protocol_whitelist", v, "-i", "clip.mp4", "out.mp4"}, dir); err == nil {
			t.Errorf("accepted -protocol_whitelist %s", v)
		}
	}
	for _, v := range []string{"file", "file,concat", "file,pipe"} {
		if err := ValidateArgv([]string{"-protocol_whitelist", v, "-i", "clip.mp4", "out.mp4"}, dir); err != nil {
			t.Errorf("refused -protocol_whitelist %s: %v", v, err)
		}
	}
}

// The engine's real remote argv (testdata/engine-remote-argv.json, written by
// the .NET side from its builders, T-029) passes the validator and the ffmpeg
// op's output check, names no absolute font path, and a -filter_complex graph
// also passes when the engine sends it as a -filter_complex_script file.
func TestValidateArgvAcceptsTheEnginesRemoteArgvFixture(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("testdata", "engine-remote-argv.json"))
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		Shapes []struct {
			Shape string   `json:"shape"`
			Argv  []string `json:"argv"`
		} `json:"shapes"`
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	if len(fixture.Shapes) == 0 {
		t.Fatal("the fixture has no shapes")
	}
	fileRef := regexp.MustCompile(`(?:filename|textfile|fontfile|file)='([^']+)'`)
	absFont := regexp.MustCompile(`(?:fontfile|fontsdir)=\\?'?\s*/`)
	for _, s := range fixture.Shapes {
		dir := t.TempDir()
		for _, tok := range s.Argv {
			if absFont.MatchString(tok) {
				t.Errorf("%s: an absolute font path in %q", s.Shape, tok)
			}
			for _, m := range fileRef.FindAllStringSubmatch(tok, -1) {
				full := filepath.Join(dir, filepath.FromSlash(m[1]))
				if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
					t.Fatal(err)
				}
				if err := os.WriteFile(full, []byte("x"), 0o600); err != nil {
					t.Fatal(err)
				}
			}
		}
		if err := ValidateArgv(s.Argv, dir); err != nil {
			t.Errorf("%s: ValidateArgv refused: %v", s.Shape, err)
		}
		if err := ValidateFFmpegOutputs(s.Argv); err != nil {
			t.Errorf("%s: output check refused: %v", s.Shape, err)
		}
		// Every input of the engine's argv gets one whitelist right before
		// its -i, and the engine's narrower file is kept, not widened.
		final, err := WithProtocolWhitelist(WithSafeConcat(s.Argv))
		if err != nil {
			t.Errorf("%s: whitelist refused: %v", s.Shape, err)
		}
		for j, tok := range final {
			if tok != "-i" {
				continue
			}
			if j < 2 || final[j-2] != "-protocol_whitelist" || final[j-1] != "file" {
				t.Errorf("%s: input at %d is not preceded by the engine's -protocol_whitelist file: %q", s.Shape, j, final)
			}
		}
		if n := strings.Count(strings.Join(final, "\x00"), "-protocol_whitelist"); n != strings.Count(strings.Join(final, "\x00"), "\x00-i\x00") {
			t.Errorf("%s: %d whitelists for the inputs in %q", s.Shape, n, final)
		}
		for i, tok := range s.Argv {
			if tok != "-filter_complex" || i+1 >= len(s.Argv) {
				continue
			}
			if err := os.WriteFile(filepath.Join(dir, "filter_complex.txt"), []byte(s.Argv[i+1]), 0o600); err != nil {
				t.Fatal(err)
			}
			script := append(append(append([]string{}, s.Argv[:i]...), "-filter_complex_script", "filter_complex.txt"), s.Argv[i+2:]...)
			if err := ValidateArgv(script, dir); err != nil {
				t.Errorf("%s as a script: ValidateArgv refused: %v", s.Shape, err)
			}
		}
	}
}

// Every -i gets exactly one whitelist, immediately before it: the engine's
// own value for THAT input's option group, kept as its intersection with
// file,pipe (file,concat on a -f concat input), or the default. A whitelist
// anywhere else, an earlier input's, an output option or another option's
// value, never stands in for it. (ffmpeg applies the last of two whitelists
// before one -i.)
func TestWithProtocolWhitelistPerInputGroup(t *testing.T) {
	cases := map[string]struct{ in, want []string }{
		"only the first input carries the engine's value": {
			[]string{"-protocol_whitelist", "file", "-i", "a.mp4", "-i", "b.mp4", "out.mp4"},
			[]string{"-protocol_whitelist", "file", "-i", "a.mp4", "-protocol_whitelist", "file,pipe", "-i", "b.mp4", "out.mp4"},
		},
		"a whitelist after the last input": {
			[]string{"-i", "a.mp4", "-protocol_whitelist", "file", "out.mp4"},
			[]string{"-protocol_whitelist", "file,pipe", "-i", "a.mp4", "out.mp4"},
		},
		"a whitelist as another option's value": {
			[]string{"-metadata", "-protocol_whitelist", "-i", "a.mp4", "out.mp4"},
			[]string{"-metadata", "-protocol_whitelist", "-protocol_whitelist", "file,pipe", "-i", "a.mp4", "out.mp4"},
		},
		"-i as another option's value": {
			[]string{"-metadata", "-i", "-i", "a.mp4", "out.mp4"},
			[]string{"-metadata", "-i", "-protocol_whitelist", "file,pipe", "-i", "a.mp4", "out.mp4"},
		},
		"concat input with file,concat": {
			[]string{"-protocol_whitelist", "file,concat", "-f", "concat", "-i", "list.txt", "out.mp4"},
			[]string{"-f", "concat", "-protocol_whitelist", "file,concat", "-i", "list.txt", "out.mp4"},
		},
		"concat on a plain input narrows to file": {
			[]string{"-protocol_whitelist", "file,concat", "-i", "a.mp4", "out.mp4"},
			[]string{"-protocol_whitelist", "file", "-i", "a.mp4", "out.mp4"},
		},
		"two whitelists before one input: the last, intersected": {
			[]string{"-protocol_whitelist", "file", "-protocol_whitelist", "file,pipe", "-i", "a.mp4", "out.mp4"},
			[]string{"-protocol_whitelist", "file,pipe", "-i", "a.mp4", "out.mp4"},
		},
		"ffprobe: the engine's value, no -i": {
			[]string{"-v", "error", "-protocol_whitelist", "file", "-show_format", "clip.mp4"},
			[]string{"-v", "error", "-show_format", "clip.mp4", "-protocol_whitelist", "file"},
		},
	}
	for name, tc := range cases {
		got, err := pwl(tc.in)
		if err != nil || strings.Join(got, " ") != strings.Join(tc.want, " ") {
			t.Errorf("%s: %q, %v; want %q", name, got, err, tc.want)
		}
	}
	for _, in := range [][]string{
		{"-protocol_whitelist", "concat", "-i", "a.mp4", "out.mp4"},
		{"-protocol_whitelist", "pipe", "-f", "concat", "-i", "list.txt", "out.mp4"},
	} {
		if got, err := pwl(in); err == nil {
			t.Errorf("an empty intersection was accepted: %q", got)
		}
	}
}

func pwl(in []string) ([]string, error) { return WithProtocolWhitelist(in) }
