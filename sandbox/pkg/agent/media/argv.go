package media

import (
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
)

// ArgvError is a refused argv. It is reported to the engine as
// argv_not_allowed.
type ArgvError struct {
	Token  string
	Reason string
}

func (e *ArgvError) Error() string {
	if e.Token == "" {
		return "argv_not_allowed: " + e.Reason
	}
	return fmt.Sprintf("argv_not_allowed: %s (%q)", e.Reason, e.Token)
}

func reject(token, reason string) error { return &ArgvError{Token: token, Reason: reason} }

// fileReadingFilterKeys are the filtergraph options that read a file or the
// network from inside a graph. The specification names movie=, amovie=,
// subtitles=, ass=, textfile=, fontfile= and lut3d= and says "and similar";
// this is that list. A value is accepted only when it is a session-relative
// path that already exists in the session directory.
var fileReadingFilterKeys = []string{
	"movie", "amovie", "subtitles", "ass", "textfile", "fontfile", "font",
	"lut3d", "haldclut", "haldclut3d", "lut1d", "lutrgb", "lutyuv",
	"afir", "sofalizer",
}

// ValidateArgv is the worker's own argv check, and it is the last thing between
// a bug anywhere upstream and a customer's machine.
//
// The engine builds argv from typed, first-party code; the worker validates it
// anyway, because the specification says so in as many words: "The worker MUST
// validate every argv itself, even though the engine builds it from typed,
// first-party code. The worker does not trust the gateway or the engine for
// safety, because a bug anywhere upstream must not let argv reach a customer's
// machine with an escape."
//
// It refuses, with argv_not_allowed:
//
//   - any argument in an input position (-i, or the list file of -f concat)
//     that is a URL or carries a protocol prefix, which is what makes http:,
//     concat:, subfile:, file: and data: unusable as an input; a token in any
//     other position is not a protocol reference — a stream specifier such as
//     v:0 or a filtergraph is not — so the scheme check is scoped to inputs;
//   - any absolute path, any path with a .. component, and any path that
//     resolves outside the session directory (through a symlink, included),
//     where it appears in the argv;
//   - a filtergraph option that reads a file (movie=, subtitles=, textfile=,
//     fontfile=, lut3d= and the rest of that family) unless the file it names
//     is a session-relative path that already exists in the session directory;
//   - -f concat with a list file whose entries are not session-relative, which
//     is the same escape wearing a different hat;
//   - -passlogfile, -attach and -dump_attachment, which no first-party argv
//     sends, and every other option outside the option table;
//   - -filter_script outright: a script file is a second-order file reference
//     the validator would have to parse, and the graph can be inlined.
//   - a host path embedded in a token: an absolute, home, volume, UNC or
//     upward-traversing path that follows a =, :, , or | (quoted or escaped,
//     included), which is how a filtergraph option names a file to read or
//     write (psnr=stats_file=, metadata=...:file=, sendcmd=f=);
//   - a protocol reference in a positional (tcp://, http://, file:, tee: and
//     the rest), because the whitelist is an input option and an ffmpeg
//     positional is an output;
//   - a -f, -fifo_format or -segment_format value outside the first-party
//     allowlist (tee, fifo, network muxers and capture devices included);
//   - -filter_complex_script whose graph breaks these rules, any -/option
//     (its value is loaded from a file), and the filters that listen, load a
//     library or replay commands (zmq, sendcmd, frei0r, ladspa and kin);
//   - -allowed_extensions and its relatives, which lift the HLS demuxer's
//     restriction on what a playlist may name;
//   - -safe anywhere but on an explicit -f concat input, whose list this
//     validator has read. WithSafeConcat then pins the value to 1.
//
// dir is the session directory every path resolves inside.
func ValidateArgv(argv []string, dir string) error {
	if len(argv) == 0 {
		return reject("", "empty argv")
	}
	positional, err := classifyArgv(argv)
	if err != nil {
		return err
	}
	for i, tok := range argv {
		name, inline, hasInline := splitOption(tok)
		if positional[i] {
			if err := checkPositional(argv, i, dir); err != nil {
				return err
			}
		}
		// An input position is the value token that follows a -i (or the list
		// file that follows -f concat), never the flag itself. That is the
		// one position where a protocol prefix is a real threat; in any other
		// position a name is an option, a stream specifier (v:0, a:0) or a
		// filtergraph, none of which is a protocol reference, so the scheme
		// check applies only there. A host path, wherever it appears, still
		// never reaches ffmpeg.
		isInput := false
		if i > 0 {
			prev, _, _ := splitOption(argv[i-1])
			isInput = prev == "-i"
		}
		if name == "-f" &&
			strings.EqualFold(nextArg(argv, i, inline, hasInline), "concat") &&
			i+1 < len(argv) {
			isInput = true
		}
		// A filtergraph is read by checkGraph at both of libavfilter's
		// unescape levels; the raw-token shape checks below would read it at
		// neither.
		graph := isGraphToken(argv, i)
		if graph {
			if isInput {
				if scheme, ok := schemePrefix(tok); ok {
					return reject(tok, "it carries the protocol prefix "+scheme+":")
				}
			}
		} else if err := checkTokenShape(tok, isInput); err != nil {
			return err
		}
		if strings.HasPrefix(tok, "-/") {
			// ffmpeg reads the value of an option spelled -/name from the file
			// its argument names: a second-order reference no check here sees.
			return reject(tok, "an option whose value is loaded from a file is not accepted")
		}
		if name == "" {
			// A positional is an output for ffmpeg and an input for ffprobe, and
			// -protocol_whitelist is an input option, so it does not restrain
			// an output (or -progress). Every scheme shape is refused there,
			// known or not, nested (crypto+udp:) or without //, except the
			// process's own descriptors (pipe:, pipe:1).
			if scheme, ok := protocolReference(tok); ok {
				return reject(tok, "it carries the protocol prefix "+scheme+":")
			}
		}
		if !graph && embeddedHostPath(tok) {
			return reject(tok, "it names a path outside the session directory")
		}
		for _, refused := range refusedOptions {
			if optionIs(name, refused) {
				return reject(tok, "-"+refused+" is not accepted")
			}
		}
		if optionIs(name, "safe") && (hasInline || !inConcatGroup(argv, i)) {
			return reject(tok, "-safe is only accepted on an explicit -f concat input")
		}
		if graph {
			// checked by checkGraph where its option names it
		} else if err := checkFilterKeys(tok, dir); err != nil {
			return err
		}
		switch {
		case name == "-filter_script" || strings.HasPrefix(name, "-filter_script:"):
			return reject(tok, "-filter_script is not accepted; inline the filtergraph")
		case strings.HasPrefix(name, "-filter_complex_script:"):
			return reject(tok, "-filter_complex_script takes no stream specifier")
		case name == "-filter_complex_script":
			// The engine writes a long graph into the session and names it
			// here. The graph is read and held to the same rules as an inline
			// one.
			if err := checkFilterScript(nextArg(argv, i, inline, hasInline), dir); err != nil {
				return err
			}
		case name == "-protocol_whitelist":
			for _, p := range strings.Split(nextArg(argv, i, inline, hasInline), ",") {
				if !allowedProtocols[strings.TrimSpace(p)] {
					return reject(tok, "the protocol "+p+" is not accepted")
				}
			}
		case name == "-progress":
			// -progress writes to its target like an output does.
			if v := nextArg(argv, i, inline, hasInline); !isPipe(v) {
				if _, err := resolveInside(dir, v); err != nil {
					return reject(v, "-progress must stay inside the session directory")
				}
			}
		case name == "-report":
			// -report writes ffmpeg-<pid>.log into the working directory, which
			// is the session directory. Allowed as-is.
		case name == "-i":
			value := nextArg(argv, i, inline, hasInline)
			// A -f lavfi input is a filtergraph, not a file.
			if inputFormat(argv, i) == "lavfi" {
				if err := checkGraph(value, dir); err != nil {
					return err
				}
			} else if err := checkInput(value, dir, false); err != nil {
				return err
			}
		case strings.HasPrefix(name, "-f:"):
			return reject(tok, "-f takes no stream specifier")
		case name == "-f" || name == "-fifo_format" || name == "-segment_format":
			// A format is an allowlist: the muxers and demuxers first-party
			// argv names. Everything else (tee, fifo, segment, hls, rtsp,
			// rtp, every capture and output device) is refused.
			if format := nextArg(argv, i, inline, hasInline); !allowedFormats[format] {
				return reject(tok, "the format "+format+" is not accepted")
			}
			if strings.EqualFold(nextArg(argv, i, inline, hasInline), "concat") {
				if err := checkConcat(argv, i, dir); err != nil {
					return err
				}
			}
		}
		if isFilterOption(name) {
			if err := checkGraph(nextArg(argv, i, inline, hasInline), dir); err != nil {
				return err
			}
		}

	}
	return nil
}

// WithProtocolWhitelist gives every input exactly one -protocol_whitelist,
// immediately before its -i.
//
// The specification requires the whitelist on every invocation, and ffmpeg
// applies the last one given for an input, so position matters: every
// caller-supplied pair is removed, and each -i gets the engine's own value for
// THAT input's option group (the options between the previous input and this
// -i, as the option table reads them), kept only as its intersection with
// file,pipe (file,concat on a -f concat input), or file,pipe when the group
// set none. A whitelist anywhere else, in an earlier input's group, among the
// output options or as another option's value, never stands in for it. An
// empty intersection is refused. With no -i (ffprobe's positional input) the
// one value, the engine's or the default, goes at the end.
func WithProtocolWhitelist(argv []string) ([]string, error) {
	const flag = "-protocol_whitelist"
	options, err := optionIndexes(argv)
	if err != nil {
		return nil, err
	}
	out := make([]string, 0, len(argv)+4)
	groupValue, groupConcat, lastValue := "", false, ""
	inserted := false
	for i := 0; i < len(argv); i++ {
		name := ""
		if options[i] {
			name = argv[i]
		}
		switch name {
		case flag:
			groupValue = argv[i+1]
			lastValue = groupValue
			i++
			continue
		case "-f":
			groupConcat = argv[i+1] == "concat"
		case "-i":
			v, err := whitelistFor(groupValue, groupConcat)
			if err != nil {
				return nil, err
			}
			out = append(out, flag, v)
			inserted = true
			groupValue, groupConcat = "", false
		}
		out = append(out, argv[i])
		if options[i] && optionsWithValue[optionBase(argv[i])] && i+1 < len(argv) {
			i++
			out = append(out, argv[i])
		}
	}
	if !inserted {
		v, err := whitelistFor(lastValue, false)
		if err != nil {
			return nil, err
		}
		out = append(out, flag, v)
	}
	return out, nil
}

// whitelistFor is the value an input gets: the engine's, intersected with
// what that input may use, or the default.
func whitelistFor(engine string, concat bool) (string, error) {
	allowed := []string{"file", "pipe"}
	if concat {
		allowed = []string{"file", "concat"}
	}
	if engine == "" {
		return "file,pipe", nil
	}
	var keep []string
	for _, p := range allowed {
		for _, e := range strings.Split(engine, ",") {
			if strings.TrimSpace(e) == p {
				keep = append(keep, p)
				break
			}
		}
	}
	if len(keep) == 0 {
		return "", reject(engine, "the protocol whitelist leaves this input no protocol")
	}
	return strings.Join(keep, ","), nil
}

// optionIndexes marks the tokens classifyArgv reads as options (not values,
// not positionals).
func optionIndexes(argv []string) ([]bool, error) {
	positional, err := classifyArgv(argv)
	if err != nil {
		return nil, err
	}
	options := make([]bool, len(argv))
	for i := 0; i < len(argv); i++ {
		if positional[i] {
			continue
		}
		options[i] = true
		if optionsWithValue[optionBase(argv[i])] {
			i++
		}
	}
	return options, nil
}

func optionBase(tok string) string {
	base, _, _ := strings.Cut(strings.TrimPrefix(tok, "-"), ":")
	return base
}

// WithSafeConcat pins the concat demuxer to its safe mode: every -safe the
// caller sent is dropped and -safe 1 follows each explicit -f concat.
//
// The engine sends -safe 0 with its own list of session-relative segment names,
// which safe mode reads just the same. Pinning it means a list that names an
// absolute path, a protocol or anything else safe mode refuses is refused by
// ffmpeg itself, whatever this validator's own reading of the list missed. The
// option is written out rather than left to the default because an ffmpeg
// older than 5.0 treats an explicit -f concat as unsafe.
func WithSafeConcat(argv []string) []string {
	out := make([]string, 0, len(argv)+2)
	for i := 0; i < len(argv); i++ {
		name, _, hasInline := splitOption(argv[i])
		if optionIs(name, "safe") {
			if !hasInline {
				i++
			}
			continue
		}
		out = append(out, argv[i])
		if name == "-f" && !hasInline && i+1 < len(argv) && strings.EqualFold(argv[i+1], "concat") {
			out = append(out, argv[i+1], "-safe", "1")
			i++
		}
	}
	return out
}

// refusedOptions are never accepted. They lift the HLS demuxer's restriction on
// the files a playlist may name, which is the only thing between a playlist in
// the session directory and any file the desktop user can read.
// -hls_key_info_file names a file whose lines name the key file the HLS muxer
// reads.
var refusedOptions = []string{"allowed_extensions", "allowed_segment_extensions", "extension_picky", "hls_key_info_file"}

// allowedFormats are the -f values the engine's argv builders send
// (WorkflowEngine FfmpegArgvBuilder, VideoCompileStepExecutor, TimelineRenderer,
// StillsArgvBuilder, MarketplacePreviewArgvBuilder, AudioCleanupArgv,
// GeneratedClipNormalizer, EnhanceBoundedCopy, MediaDerivativeService,
// VideoEventDetector, SceneCutFinder and the rest; srt from
// ModerationMediaSampler). The match is exact: ffmpeg's names are lowercase.
var allowedFormats = map[string]bool{
	"null": true, "concat": true, "lavfi": true, "mp4": true, "mov": true,
	"matroska": true, "image2": true, "wav": true, "s16le": true,
	"rawvideo": true, "srt": true,
}

// allowedFilters are the filters first-party graphs use: the names ffmpeg's
// filter list shares with the engine's graph builders (WorkflowEngine
// Services, Execution and Agents/Tools, Shared/Moderation; the intersection of
// `ffmpeg -filters` with the identifiers in those sources). Anything else,
// movie, amovie, zmq, sendcmd, ladspa, lv2, frei0r, ddagrab and gfxcapture
// included, is refused.
var allowedFilters = setOf(
	"acompressor", "acrossfade", "adelay", "afade", "afftdn", "aformat", "agate",
	"alimiter", "alphaextract", "alphamerge", "ametadata", "amix", "anlmdn", "anull",
	"anullsrc", "apad", "aresample", "areverse", "arnndn", "aselect", "asetnsamples",
	"asetpts", "ass", "astats", "atempo", "atrim", "bandreject", "bass", "blackdetect",
	"blend", "cas", "chromakey", "color", "colorbalance", "colorchannelmixer",
	"colorkey", "colorlevels", "colorspace", "colortemperature", "concat", "copy",
	"crop", "cue", "curves", "decimate", "deesser", "despill", "dilation", "drawbox",
	"drawtext", "dynaudnorm", "ebur128", "edgedetect", "eq", "equalizer", "exposure",
	"fade", "feedback", "field", "fillborders", "format", "fps", "framerate",
	"freezedetect", "gblur", "geq", "gradients", "guided", "hflip", "highpass",
	"histogram", "hqdn3d", "hstack", "hue", "hysteresis", "identity", "il",
	"inflate", "interleave", "join", "latency", "life", "limiter", "loop", "loudnorm",
	"lowpass", "lowshelf", "lut", "lut1d", "lut2", "lut3d", "lutrgb", "maskedmerge",
	"median", "metadata", "minterpolate", "mix", "monochrome", "multiply", "negate",
	"nlmeans", "noise", "normalize", "null", "nullsink", "overlay", "pad", "pan",
	"perspective", "phase", "pixelize", "random", "remap", "reverse", "rotate",
	"scale", "scdet", "segment", "select", "setparams", "setpts", "setsar", "shear",
	"showinfo", "sidechaincompress", "signalstats", "signature", "silencedetect",
	"sine", "smptehdbars", "speechnorm", "split", "subtitles", "surround", "testsrc",
	"testsrc2", "threshold", "thumbnail", "tile", "tmix", "tonemap", "tpad",
	"transpose", "trim", "unsharp", "vflip", "vidstabdetect", "vidstabtransform",
	"vignette", "volume", "waveform", "xfade", "zoompan", "zscale",
)

// filterPathKeys are filter options whose value names a file the filter
// reads or writes (metadata/ametadata file=, drawtext fontfile=/textfile=,
// ass f=/filename=, lut1d/lut3d file=, arnndn m=, vidstabdetect result=,
// vidstabtransform input=, signature filename=). Each must resolve inside the
// session, symlinks included; "-" is the process's stdout.
var filterPathKeys = setOf("file", "filename", "f", "m", "model", "input", "result",
	"fontfile", "fontsdir", "textfile", "stats_file", "psfile", "plot")

func setOf(names ...string) map[string]bool {
	m := make(map[string]bool, len(names))
	for _, n := range names {
		m[n] = true
	}
	return m
}

// isFilterOption reports the options whose value is a filtergraph.
func isFilterOption(name string) bool {
	for _, key := range []string{"vf", "af", "filter", "filter_complex", "lavfi"} {
		if optionIs(name, key) {
			return true
		}
	}
	return false
}

// avWhitespace is libavutil's WHITESPACES.
const avWhitespace = " \n\t\r"

// avGetToken is libavutil's av_get_token: leading whitespace is skipped, a
// backslash escapes the next byte, a quoted run is literal, the token ends at
// the first unquoted, unescaped byte of term, and trailing unescaped
// whitespace is dropped. It returns the token and the rest, which starts at
// the terminator.
func avGetToken(s, term string) (string, string) {
	i := 0
	for i < len(s) && strings.IndexByte(avWhitespace, s[i]) >= 0 {
		i++
	}
	var out []byte
	end := 0
	for i < len(s) && strings.IndexByte(term, s[i]) < 0 {
		c := s[i]
		i++
		switch {
		case c == '\\' && i < len(s):
			out = append(out, s[i])
			i++
			end = len(out)
		case c == '\'':
			for i < len(s) && s[i] != '\'' {
				out = append(out, s[i])
				i++
			}
			if i < len(s) {
				i++
				end = len(out)
			}
		default:
			out = append(out, c)
		}
	}
	for len(out) > end && strings.IndexByte(avWhitespace, out[len(out)-1]) >= 0 {
		out = out[:len(out)-1]
	}
	return string(out), s[i:]
}

func skipAVWhitespace(s string) string {
	return strings.TrimLeft(s, avWhitespace)
}

// skipLabels drops leading [label] pads and the whitespace around them.
func skipLabels(s string) (string, bool) {
	s = skipAVWhitespace(s)
	for strings.HasPrefix(s, "[") {
		end := strings.IndexByte(s, ']')
		if end < 0 || !isPadLabel(s[1:end]) {
			return s, false
		}
		s = skipAVWhitespace(s[end+1:])
	}
	return s, true
}

// graphFilter is one filter of a graph as libavfilter's graph parser reads
// it: the name with its @instance cut off, and its option string after the
// graph-level av_get_token, which is still to be unescaped once more by the
// filter's own option parser.
type graphFilter struct {
	name, opts string
	// rawOpts is the option text as written, before the graph-level unescape.
	rawOpts string
	hasOpts bool
}

// parseGraph follows libavfilter's graph parser: pads, then the name as
// av_get_token(..., "=,;["), then after an = the options as
// av_get_token(..., "[],;"), then output pads, then , or ;. A graph it cannot
// walk to the end is reported as not parsed.
func parseGraph(g string) ([]graphFilter, bool) {
	var out []graphFilter
	p := g
	for {
		var ok bool
		if p, ok = skipLabels(p); !ok {
			return nil, false
		}
		if p == "" {
			return out, true
		}
		var f graphFilter
		f.name, p = avGetToken(p, "=,;[")
		if strings.HasPrefix(p, "=") {
			f.hasOpts = true
			before := p[1:]
			f.opts, p = avGetToken(before, "[],;")
			f.rawOpts = before[:len(before)-len(p)]
		}
		if at := strings.IndexByte(f.name, '@'); at >= 0 {
			f.name = f.name[:at]
		}
		out = append(out, f)
		if p, ok = skipLabels(p); !ok {
			return nil, false
		}
		if p == "" {
			return out, true
		}
		if p[0] != ',' && p[0] != ';' {
			return nil, false
		}
		p = p[1:]
	}
}

// filterOption is one key=value of a filter after the option parser's own
// av_get_token: key is empty for a positional (shorthand) value.
type filterOption struct{ key, value string }

func isAVKeyChar(c byte) bool {
	return (c|32) >= 'a' && (c|32) <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '/' || c == '.'
}

// splitFilterOptions follows av_opt_get_key_value(&args, "=", ":", ...): a
// key is a run of key characters followed (after whitespace) by =; without
// one the whole pair is a positional value. Each value goes through
// av_get_token(..., ":"), the second unescape.
func splitFilterOptions(opts string) []filterOption {
	var out []filterOption
	rest := opts
	for rest != "" {
		s := skipAVWhitespace(rest)
		k := 0
		for k < len(s) && isAVKeyChar(s[k]) {
			k++
		}
		after := skipAVWhitespace(s[k:])
		var o filterOption
		if k > 0 && strings.HasPrefix(after, "=") {
			o.key = s[:k]
			o.value, rest = avGetToken(after[1:], ":")
		} else {
			o.value, rest = avGetToken(rest, ":")
		}
		out = append(out, o)
		if rest != "" {
			rest = rest[1:]
		}
	}
	return out
}

// isPadLabel reports a pad label of the characters the engine's labels use
// (0:v, v0, vout, rmk3s1). libavfilter reads a label with av_get_token(buf,
// "]"), so a quote or a backslash would move where it ends; such a label is
// refused rather than emulated.
func isPadLabel(s string) bool {
	if s == "" {
		return false
	}
	for i := 0; i < len(s); i++ {
		if !isIdentifierByte(s[i]) && s[i] != ':' {
			return false
		}
	}
	return true
}

// positionalKeys map a keyless (shorthand) option of a file-taking filter to
// the option it sets, in libavfilter's option order with aliases skipped
// (`ffmpeg -h filter=X`, ffmpeg 8). Positions past the table, and filters not
// in it, keep no key; their values still get the scheme and host-path checks.
var positionalKeys = map[string][]string{
	"drawtext":         {"fontfile", "text", "textfile"},
	"metadata":         {"mode", "key", "value", "function", "expr", "file"},
	"ametadata":        {"mode", "key", "value", "function", "expr", "file"},
	"life":             {"filename"},
	"arnndn":           {"model"},
	"vidstabdetect":    {"result"},
	"vidstabtransform": {"input"},
	"curves":           {"preset", "master", "red", "green", "blue", "all", "psfile", "plot"},
	"signature":        {"detectmode", "nb_inputs", "filename"},
	"lut1d":            {"file"},
	"lut3d":            {"file"},
	"subtitles":        {"filename", "original_size", "fontsdir"},
	"ass":              {"filename", "original_size", "fontsdir"},
}

// withPositionalKeys names each keyless option of filter by its position, so
// a positional value gets the checks of its keyed spelling.
func withPositionalKeys(filter string, opts []filterOption) []filterOption {
	table := positionalKeys[filter]
	out := make([]filterOption, len(opts))
	pos := 0
	for i, o := range opts {
		if o.key == "" {
			if pos < len(table) {
				o.key = table[pos]
			}
			pos++
		}
		out[i] = o
	}
	return out
}

// readFileOptions are the options that read a file that must already be in
// the session: drawtext's fontfile and textfile in any filter that has them,
// and per filter the rest.
var readFileOptions = map[string][]string{
	"subtitles": {"filename", "f"},
	"ass":       {"filename", "f"},
	"lut3d":     {"file"},
	"lut1d":     {"file"},
	"curves":    {"psfile"},
	"life":      {"filename", "f"},
}

func readsFile(filter, key string) bool {
	if key == "textfile" || key == "fontfile" {
		return true
	}
	for _, k := range readFileOptions[filter] {
		if k == key {
			return true
		}
	}
	return false
}

// checkGraph reads a filtergraph the way libavfilter does, both unescape
// levels, and holds every filter to the rules: an allowlisted plain name;
// option values, fully unescaped, with no scheme shape and no host path
// (drawtext's text aside, and a font key naming the system font directory);
// path-valued keys inside the session, symlinks included; and file-reading
// options on a session file that exists.
func checkGraph(graph, dir string) error {
	filters, ok := parseGraph(graph)
	if !ok {
		return reject(graph, "the filtergraph cannot be read with certainty")
	}
	for _, f := range filters {
		if f.name == "" && !f.hasOpts {
			continue
		}
		if !isIdentifier(f.name) || !allowedFilters[f.name] {
			return reject(graph, "the filter "+f.name+" is not accepted")
		}
		// The same values read with the graph-level unescape skipped: a
		// reading that unescapes less than ffmpeg must not find a scheme or a
		// host path either. (A third reading, the graph-level text split at
		// every colon, was dropped: alone it refused only an escaped colon
		// inside drawtext's text or inside a system font path, both harmless,
		// and its other catch, a /key= option, is now refused by the key
		// rule below.)
		views := withPositionalKeys(f.name, splitFilterOptions(f.rawOpts))
		for _, o := range append(views, splitFilterOptions(f.opts)...) {
			// A key is a plain identifier. libavfilter's key grammar also
			// takes / . and -, and /key=path loads the value from that file.
			if o.key != "" && !isIdentifier(o.key) {
				return reject(graph, "the filter option "+o.key+" is not accepted")
			}
		}
		for _, o := range views {
			if err := checkFilterValue(graph, o); err != nil {
				return err
			}
		}
		for _, o := range withPositionalKeys(f.name, splitFilterOptions(f.opts)) {
			v := o.value
			if o.key == "text" || isSystemFont(o) {
				continue
			}
			if err := checkFilterValue(graph, o); err != nil {
				return err
			}
			if v == "" || v == "-" || dir == "" {
				continue
			}
			if filterPathKeys[o.key] {
				if _, err := resolveInside(dir, v); err != nil {
					return reject(v, "a filter path must stay inside the session directory")
				}
			}
			if readsFile(f.name, o.key) {
				if err := requireExisting(v, dir); err != nil {
					return err
				}
			}
		}
	}
	return nil
}

func isIdentifier(s string) bool {
	for i := 0; i < len(s); i++ {
		if !isIdentifierByte(s[i]) {
			return false
		}
	}
	return s != ""
}

// checkFilterScript reads a -filter_complex_script graph from the session and
// holds it to the rules of an inline one.
func checkFilterScript(value, dir string) error {
	if err := requireExisting(value, dir); err != nil {
		return err
	}
	path, err := resolveInside(dir, value)
	if err != nil {
		return reject(value, err.Error())
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return reject(value, "the filter script could not be read")
	}
	if len(data) > 1<<20 {
		return reject(value, "the filter script is too large to validate")
	}
	return checkGraph(string(data), dir)
}

// optionIs reports whether name is the option -key, with or without a stream
// specifier (-key:v).
func optionIs(name, key string) bool {
	return name == "-"+key || strings.HasPrefix(name, "-"+key+":")
}

// inConcatGroup reports whether the option at index at belongs to an input
// that is explicitly -f concat: the options between the previous input and the
// next -i include -f concat. That is the input whose list checkConcat reads.
func inConcatGroup(argv []string, at int) bool {
	start := 0
	for i := at - 1; i >= 0; i-- {
		if argv[i] == "-i" {
			start = i + 2
			break
		}
	}
	for i := start; i < len(argv); i++ {
		if argv[i] == "-i" {
			return false
		}
		if argv[i] == "-f" && i+1 < len(argv) && strings.EqualFold(argv[i+1], "concat") {
			for j := i + 2; j < len(argv); j++ {
				if argv[j] == "-i" {
					return j > at
				}
			}
			return false
		}
	}
	return false
}

// protocolReference reports the scheme of a positional that names a protocol:
// every token schemePrefix matches, known or not, with or without //, nested
// (crypto+udp:) included, plus subfile's comma-option form. A stream specifier
// (v:0) is not one, and pipe: with an optional descriptor number is the one
// protocol a positional may name.
func protocolReference(tok string) (string, bool) {
	if strings.HasPrefix(tok, "subfile,") && strings.Contains(tok, ":") {
		return "subfile", true
	}
	scheme, ok := schemePrefix(tok)
	if !ok || isStreamSpecifier(tok) {
		return "", false
	}
	if scheme == "pipe" && (len(tok) == len("pipe:") || isDecimal(tok[len("pipe:"):])) {
		return "", false
	}
	return scheme, true
}

// embeddedHostPath reports whether a host path sits inside tok: after a =, :,
// , or |, after the ] that closes a tee slave's option block, or as a climb
// anywhere in it. LooksLikeHostPath tests only the start of a token, and a
// filtergraph names its files in the middle of one: psnr=stats_file=/home/u/x,
// metadata=mode=print:file=/home/u/.profile, sendcmd=f=C\:/Users/u/x.
func embeddedHostPath(tok string) bool {
	for i := 0; i < len(tok); i++ {
		switch tok[i] {
		case '=', ':', ',', '|', ']':
			if hostPathAt(tok[i+1:]) {
				return true
			}
		}
	}
	return climbs(tok)
}

// isPadding reports a byte that ffmpeg's own parsing drops from the front of a
// value or that only groups it: whitespace of every kind and both quotes.
func isPadding(c byte) bool {
	switch c {
	case '\'', '"', ' ', '\t', '\r', '\n', '\v', '\f':
		return true
	}
	return false
}

// hostPathAt reports whether s begins with a host path: an absolute path, a
// home directory, a volume or a UNC path.
//
// ffmpeg unescapes a filtergraph value twice (the graph, then the option) and
// trims whitespace, so the same path can be written with any mix of quotes,
// backslashes and padding in front of it and between its first characters.
// This does not re-implement that parser. It sets all of them aside and looks
// at what is left, which refuses every spelling of a host path at the price of
// refusing a few values no first-party argv contains.
func hostPathAt(s string) bool {
	i, slashes := 0, 0
	skip := func() {
		for i < len(s) && (isPadding(s[i]) || s[i] == '\\') {
			if s[i] == '\\' {
				slashes++
			}
			i++
		}
	}
	skip()
	if i >= len(s) {
		return false
	}
	c := s[i]
	switch {
	case c == '/', c == '~':
		return true
	case slashes >= 2 && isIdentifierByte(c):
		return true
	}
	if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') {
		i++
		skip()
		if i < len(s) && s[i] == ':' {
			i++
			slashes = 0
			skip()
			return slashes > 0 || (i < len(s) && s[i] == '/')
		}
	}
	return false
}

// climbs reports whether tok carries a .. path component once quotes, padding
// and escapes are set aside. A backslash is read both as an escape and as a
// path separator, since it is one or the other depending on where the token
// ends up.
func climbs(tok string) bool {
	var escaped, separated strings.Builder
	for i := 0; i < len(tok); i++ {
		c := tok[i]
		switch {
		case isPadding(c):
		case c == '\\':
			separated.WriteByte('/')
		default:
			escaped.WriteByte(c)
			separated.WriteByte(c)
		}
	}
	return hasClimb(escaped.String()) || hasClimb(separated.String())
}

func hasClimb(s string) bool {
	for i := 0; i+1 < len(s); i++ {
		if s[i] != '.' || s[i+1] != '.' {
			continue
		}
		before := i == 0 || strings.IndexByte("/=:,|]", s[i-1]) >= 0
		after := i+2 == len(s) || strings.IndexByte("/:,|[;", s[i+2]) >= 0
		if before && after {
			return true
		}
	}
	return false
}

// checkTokenShape rejects the argument shapes that are never legitimate: a
// protocol prefix in an input position, and a host path anywhere.
//
// Only in an input position is a scheme-shaped token a protocol reference: a
// token in any other position is an option, a stream specifier (v:0, a:0 —
// valid ffmpeg stream-specifier prefixes, and a bare "v:" is exactly the
// scheme shape a URL prefix would have) or a filtergraph, and none of them
// can open a connection. Keeping the check scoped to inputs is what lets a
// standard ffprobe stream selector through without loosening it: an input of
// the form http:, file:, concat: or the like is still refused, and every
// host path, wherever it sits in the argv, is still refused too.
//
// A stream specifier is excluded from the host-path check as well: it carries
// the shape that LooksWithHostPath's drive-letter test would read as a
// volume (v:0, a:0), but it names no path at all.
func checkTokenShape(tok string, isInput bool) error {
	if isInput {
		if scheme, ok := schemePrefix(tok); ok {
			return reject(tok, "it carries the protocol prefix "+scheme+":")
		}
	}
	if !isStreamSpecifier(tok) && agent.LooksLikeHostPath(tok) {
		return reject(tok, "it names a path outside the session directory")
	}
	return nil
}

// isStreamSpecifier reports whether tok is a stream specifier, the selector a
// stream in an input: a stream id and an optional type letter (v, a, s, d, p
// for video, audio, subtitle, data and program streams), plus an optional
// index within that type. The fields are separated by colons; the stream id
// may be omitted (v:0, a:0), and so may the index (v, a, 0:v). A stream
// specifier names no path: it selects a stream within the input ffmpeg is
// given, so neither the protocol check nor the host-path check may read it
// as one. The empty token and a bare colon are not specifiers.
func isStreamSpecifier(tok string) bool {
	if tok == "" {
		return false
	}
	parts := strings.Split(tok, ":")
	for i, part := range parts {
		if part == "" {
			return false
		}
		switch i {
		case 0:
			// Stream id (a decimal) or stream type (v, a, s, d, p).
			if !isDecimal(part) && !isStreamType(part) {
				return false
			}
		case 1:
			// Stream type (v, a, s, d, p) or index (a decimal).
			if !isStreamType(part) && !isDecimal(part) {
				return false
			}
		default:
			// Index: a decimal.
			if !isDecimal(part) {
				return false
			}
		}
	}
	return true
}

func isDecimal(s string) bool {
	if s == "" {
		return false
	}
	for _, c := range s {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

func isStreamType(s string) bool {
	switch s {
	case "v", "a", "s", "d", "p":
		return true
	}
	return false
}

// schemePrefix reports the scheme of a token that looks like a URL or a
// protocol-prefixed input, such as http://host/x, concat:a|b or file:/etc/x.
//
// A token is only a candidate when everything before its first colon is a
// scheme: a letter, then letters, digits, +, - or . That deliberately excludes
// the colons ffmpeg uses for other things — 0:v:0 is a stream specifier,
// 00:00:10 is a timestamp, scale=1920:1080 is a filter argument and
// drawtext=text='a:b' is a quoted value — while still catching every real
// prefix, including one this code has never heard of.
func schemePrefix(tok string) (string, bool) {
	i := strings.IndexByte(tok, ':')
	if i <= 0 {
		return "", false
	}
	scheme := tok[:i]
	for j := 0; j < len(scheme); j++ {
		c := scheme[j]
		switch {
		case c >= 'a' && c <= 'z', c >= 'A' && c <= 'Z':
		case c >= '0' && c <= '9', c == '+', c == '-', c == '.':
			if j == 0 {
				return "", false
			}
		default:
			return "", false
		}
	}
	return scheme, true
}

// checkInput validates an input path: relative, inside the session directory,
// and existing when existence is required.
func checkInput(value, dir string, mustExist bool) error {
	if value == "" {
		return reject("", "-i needs an input")
	}
	if value == "-" {
		// ffmpeg's stdin. The protocol's whitelist includes pipe, and a
		// session has no writer at the other end of it, so it is accepted and
		// useless rather than special-cased.
		return nil
	}
	if _, err := resolveInside(dir, value); err != nil {
		return reject(value, err.Error())
	}
	if mustExist {
		if err := requireExisting(value, dir); err != nil {
			return err
		}
	}
	return nil
}

// checkConcat validates -f concat: the input must be a session-relative file,
// and every entry inside it must be too. A concat list used to be the standard
// way to make ffmpeg read a file the caller could not name directly.
func checkConcat(argv []string, at int, dir string) error {
	input := ""
	for i := at + 1; i < len(argv); i++ {
		name, inline, hasInline := splitOption(argv[i])
		if name == "-i" {
			input = nextArg(argv, i, inline, hasInline)
			break
		}
	}
	if input == "" {
		return reject("", "-f concat without an -i input")
	}
	if err := requireExisting(input, dir); err != nil {
		return err
	}
	path, err := resolveInside(dir, input)
	if err != nil {
		return reject(input, err.Error())
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return reject(input, "the concat list could not be read")
	}
	if len(data) > 1<<20 {
		return reject(input, "the concat list is too large to validate")
	}
	for _, line := range strings.Split(string(data), "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		entry := strings.TrimSpace(strings.TrimPrefix(line, "file"))
		entry = strings.Trim(entry, "'\"")
		if entry == "" {
			continue
		}
		if _, err := resolveInside(dir, entry); err != nil {
			return reject(entry, "a concat list entry is not inside the session directory")
		}
	}
	return nil
}

// checkFilterKeys scans one token for the filtergraph options that read files.
func checkFilterKeys(tok, dir string) error {
	for _, key := range fileReadingFilterKeys {
		for _, value := range findKeyValues(tok, key) {
			if value == "" {
				continue
			}
			// ass=filename=x, subtitles=f=x: the filter's own key form names
			// the file after its key.
			if k, v, ok := strings.Cut(value, "="); ok && isIdentifier(k) {
				value = v
			}
			if err := requireExisting(value, dir); err != nil {
				return err
			}
		}
	}
	return nil
}

// findKeyValues returns every value of key= inside a filtergraph token,
// honouring the escaping ffmpeg uses (\: and \, inside a value) and the
// separators that end one (: , ; [ ] and a quote).
func findKeyValues(tok, key string) []string {
	var out []string
	for start := 0; ; {
		i := strings.Index(tok[start:], key+"=")
		if i < 0 {
			return out
		}
		at := start + i
		// The key must start a term: nothing identifier-ish may precede it, or
		// "class=" would read as "ass=".
		if at > 0 && isIdentifierByte(tok[at-1]) {
			start = at + len(key)
			continue
		}
		value, end := readFilterValue(tok, at+len(key)+1)
		if value != "" {
			out = append(out, value)
		}
		if end <= at {
			return out
		}
		start = end
	}
}

func isIdentifierByte(c byte) bool {
	return c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
}

// readFilterValue reads one filtergraph value, returning it unescaped and the
// index just past it.
func readFilterValue(tok string, i int) (string, int) {
	var b strings.Builder
	quote := byte(0)
	for ; i < len(tok); i++ {
		c := tok[i]
		if c == '\\' && i+1 < len(tok) {
			b.WriteByte(tok[i+1])
			i++
			continue
		}
		if quote != 0 {
			if c == quote {
				quote = 0
				continue
			}
			b.WriteByte(c)
			continue
		}
		switch c {
		case '\'':
			quote = c
			continue
		case '"':
			quote = c
			continue
		case ':', ',', ';', '[', ']':
			return strings.TrimSpace(b.String()), i
		}
		b.WriteByte(c)
	}
	return strings.TrimSpace(b.String()), len(tok)
}

// splitOption reports whether tok is an option, its name, and any inline value
// (-vf=x).
func splitOption(tok string) (name, value string, hasInline bool) {
	if len(tok) < 2 || tok[0] != '-' {
		return "", "", false
	}
	if i := strings.IndexByte(tok, '='); i > 0 {
		return tok[:i], tok[i+1:], true
	}
	return tok, "", false
}

// nextArg returns an option's value, inline or as the following argument.
func nextArg(argv []string, i int, inline string, hasInline bool) string {
	if hasInline {
		return inline
	}
	if i+1 < len(argv) {
		return argv[i+1]
	}
	return ""
}

// resolveInside proves a caller-supplied path stays inside dir, symlinks
// included.
//
// The lexical part is agent.VirtualPath's: no absolute path, no volume, no ..
// component. The part that matters here is what a lexical check cannot see — a
// symlink inside the session directory pointing out of it — so the deepest
// existing ancestor is resolved and compared against the resolved session
// directory itself, which is what makes the check correct on a macOS temp
// directory reached through /var.
func resolveInside(dir, rel string) (string, error) {
	clean, err := agent.VirtualPath(rel)
	if err != nil {
		return "", errors.New("the path must be session-relative")
	}
	full := filepath.Join(dir, filepath.FromSlash(clean))
	inside, err := within(dir, full)
	if err != nil {
		return "", err
	}
	if !inside {
		return "", errors.New("the path resolves outside the session directory")
	}
	return full, nil
}

func within(dir, candidate string) (bool, error) {
	realDir, err := filepath.EvalSymlinks(dir)
	if err != nil {
		realDir = dir
	}
	probe := candidate
	for {
		if resolved, err := filepath.EvalSymlinks(probe); err == nil {
			if resolved == realDir {
				return true, nil
			}
			ok, err := isUnder(realDir, resolved)
			return ok, err
		}
		parent := filepath.Dir(probe)
		if parent == probe {
			return false, nil
		}
		probe = parent
	}
}

func isUnder(dir, candidate string) (bool, error) {
	rel, err := filepath.Rel(dir, candidate)
	if err != nil {
		return false, err
	}
	if rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		return false, nil
	}
	return true, nil
}

// requireExisting refuses a referenced file unless it is a session-relative
// path that already exists inside the session directory.
func requireExisting(value, dir string) error {
	full, err := resolveInside(dir, value)
	if err != nil {
		return reject(value, err.Error())
	}
	info, err := os.Stat(full)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return reject(value, "the referenced file does not exist in the session directory")
		}
		return reject(value, "the referenced file could not be inspected")
	}
	if info.IsDir() {
		return reject(value, "the referenced path is a directory")
	}
	return nil
}

// inputFormat returns the -f value given to the input whose -i is at index at.
func inputFormat(argv []string, at int) string {
	format := ""
	for i := 0; i < at; i++ {
		name, inline, hasInline := splitOption(argv[i])
		switch name {
		case "-i":
			format = ""
		case "-f":
			format = nextArg(argv, i, inline, hasInline)
		}
	}
	return format
}

// allowedOutputExtensions are the output extensions the engine writes with no
// -f in front (VideoCompileStepExecutor's .mp4/.mov/.webm/.mkv outputs,
// AudioCleanupStepExecutor's .mp3/.wav/.aac/.flac/.ogg/.m4a, the still and
// frame builders' .jpg/.jpeg/.png/.webp, caption sidecars .srt/.vtt/.ass, and
// .gif, .opus, .m4v, .bmp, .tiff from the same media sets). Each maps to a
// file muxer; nothing here maps to hls, dash, rtp/sdp, segment, mpegts or a
// device.
var allowedOutputExtensions = map[string]bool{
	"mp4": true, "m4v": true, "mov": true, "webm": true, "mkv": true,
	"mp3": true, "wav": true, "aac": true, "flac": true, "ogg": true,
	"opus": true, "m4a": true, "jpg": true, "jpeg": true, "png": true,
	"webp": true, "gif": true, "bmp": true, "tiff": true, "srt": true,
	"vtt": true, "ass": true,
}

// optionFlags are the options first-party argv sends that take no value, and
// optionsWithValue the ones that take exactly one (the -name literals in the
// WorkflowEngine and Shared ffmpeg/ffprobe builders, plus the session ops'
// own -report). An option is
// looked up by its name before any :specifier. Any other option is refused:
// a private demuxer or muxer option (-pattern_type, -graph_file,
// -enable_drefs, -hls_*) is unknown here, and an unknown option could also
// hide whether the next token is its value or an output.
var optionFlags = setOf("y", "n", "hide_banner", "nostdin", "nostats", "an", "vn",
	"sn", "dn", "copyts", "report", "filters", "show_format", "show_streams")

var optionsWithValue = setOf("i", "f", "c", "codec", "acodec", "vcodec", "af", "vf",
	"filter", "filter_complex", "filter_complex_script", "lavfi", "format_whitelist",
	"protocol_whitelist", "fps_mode", "framerate", "frames", "fs", "g", "keyint_min",
	"level", "loglevel", "v", "loop", "map", "map_chapters", "map_metadata",
	"maxrate", "bufsize", "metadata", "movflags", "of", "print_format", "pix_fmt",
	"preset", "profile", "progress", "q", "r", "read_intervals", "safe",
	"sample_fmt", "sc_threshold", "select_streams", "show_entries", "skip_frame",
	"ss", "sseof", "start_number", "stream_loop", "t", "to", "tag", "threads",
	"update", "x264-params", "color_primaries", "color_range", "colorspace",
	"color_trc", "crf", "ac", "ar", "b", "h", "filter_script", "allowed_extensions",
	"allowed_segment_extensions", "extension_picky", "hls_key_info_file")

// allowedProtocols are the -protocol_whitelist entries the engine sends
// (file, and file,concat on its concat input) plus pipe, the worker's own.
var allowedProtocols = setOf("file", "pipe", "concat")

// specifierOptions are the options first-party argv writes with a stream
// specifier (-c:v, -b:a, -q:v, -frames:v, -filter:a, -tag:v, -profile:v,
// -metadata:s:v). Any other option with a :suffix is refused, so a specifier
// never takes an option past the check its plain spelling gets.
var specifierOptions = setOf("c", "codec", "b", "q", "frames", "filter", "tag", "profile", "metadata")

// classifyArgv walks argv with the option table and reports which tokens are
// positionals: not an option and not an option's value. For ffmpeg those are
// the outputs, for ffprobe the input. An unknown option, or an option written
// with an inline =value (ffmpeg reads -x=y as an option named x=y), is
// refused.
func classifyArgv(argv []string) ([]bool, error) {
	positional := make([]bool, len(argv))
	for i := 0; i < len(argv); i++ {
		tok := argv[i]
		if len(tok) < 2 || tok[0] != '-' {
			positional[i] = true
			continue
		}
		base, _, hasSpec := strings.Cut(tok[1:], ":")
		if hasSpec && !specifierOptions[base] {
			return nil, reject(tok, "the option takes no specifier")
		}
		switch {
		case optionFlags[base]:
		case optionsWithValue[base]:
			if i+1 >= len(argv) {
				return nil, reject(tok, "the option has no value")
			}
			i++
		default:
			if strings.HasPrefix(tok, "-/") {
				return nil, reject(tok, "an option whose value is loaded from a file is not accepted")
			}
			return nil, reject(tok, "the option is not accepted")
		}
	}
	return positional, nil
}

// checkPositional holds a positional to the session: pipe:N and - are the
// process's own descriptors; anything else resolves inside the session
// directory, symlinks included. A positional right after an -f lavfi with no
// -i between (ffprobe's form) is a filtergraph and is checked as one.
func checkPositional(argv []string, at int, dir string) error {
	tok := argv[at]
	if at > 0 && inputFormat(argv, at+1) == "lavfi" && !hasInputAfterFormat(argv, at) {
		if err := checkGraph(tok, dir); err != nil {
			return err
		}
		return nil
	}
	if tok == "-" || isPipe(tok) {
		return nil
	}
	if _, err := resolveInside(dir, tok); err != nil {
		return reject(tok, "it names a path outside the session directory")
	}
	return nil
}

// hasInputAfterFormat reports whether an -i follows the last -f before at
// and precedes at.
func hasInputAfterFormat(argv []string, at int) bool {
	for i := at - 1; i >= 0; i-- {
		switch argv[i] {
		case "-i":
			return true
		case "-f":
			return false
		}
	}
	return false
}

func isPipe(tok string) bool {
	return tok == "pipe:" || (strings.HasPrefix(tok, "pipe:") && isDecimal(tok[len("pipe:"):]))
}

// ValidateFFmpegOutputs holds ffmpeg's outputs to the format allowlist. Every
// positional is an output (classifyArgv). An -f applies to the next output,
// whatever its shape, and must be on the allowlist (ValidateArgv); an output
// with no -f gets the muxer ffmpeg guesses from the text after its last '.',
// as av_guess_format does, and that extension must be on
// allowedOutputExtensions. No extension and no -f is refused. Only for
// ffmpeg: an ffprobe positional is an input.
func ValidateFFmpegOutputs(argv []string) error {
	positional, err := classifyArgv(argv)
	if err != nil {
		return err
	}
	pending := false
	for i, tok := range argv {
		switch {
		case tok == "-f":
			pending = true
		case tok == "-i":
			pending = false
		case positional[i]:
			if pending {
				pending = false
				continue
			}
			dot := strings.LastIndexByte(tok, '.')
			if dot < 0 || !allowedOutputExtensions[strings.ToLower(tok[dot+1:])] {
				return reject(tok, "the output's format is not accepted; name an allowed extension or -f")
			}
		}
	}
	return nil
}

// systemFontPrefix is the one place outside the session a filter may name: the
// read-only system font directory, for the font keys only. The engine sends
// drawtext fontfile=VideoEditingOptions.FontFilePath
// (/usr/share/fonts/dejavu/DejaVuSans.ttf) and ass fontsdir=CaptionFontsDir
// (/usr/share/fonts/reelbolt-captions).
// It is a variable only so a test can point it at a temporary directory.
var systemFontPrefix = "/usr/share/fonts"

// systemFontPath reports whether value names a path strictly under
// systemFontPrefix with no .. component, and still resolves there once every
// existing symlink on the way is followed.
func systemFontPath(value string) bool {
	if !strings.HasPrefix(value, systemFontPrefix+"/") {
		return false
	}
	for _, part := range strings.FieldsFunc(value, func(r rune) bool { return r == '/' || r == '\\' }) {
		if part == ".." {
			return false
		}
	}
	// A host without the directory fails closed: within() then finds no
	// resolved ancestor under the prefix.
	inside, err := within(systemFontPrefix, filepath.Clean(value))
	return err == nil && inside
}

// isGraphToken reports whether argv[at] is a filtergraph: the value of a
// filter option, or of an -i whose input format is lavfi.
func isGraphToken(argv []string, at int) bool {
	if at == 0 {
		return false
	}
	prev, _, hasInline := splitOption(argv[at-1])
	if hasInline {
		return false
	}
	if isFilterOption(prev) {
		return true
	}
	return prev == "-i" && inputFormat(argv, at) == "lavfi"
}

// isSystemFont reports a font key naming the system font directory.
func isSystemFont(o filterOption) bool {
	return (o.key == "fontfile" || o.key == "fontsdir") && systemFontPath(o.value)
}

// checkFilterValue refuses a filter option value with a scheme shape or a
// host path; drawtext's text and a system font are left alone.
func checkFilterValue(graph string, o filterOption) error {
	v := o.value
	if o.key == "text" || isSystemFont(o) {
		return nil
	}
	if scheme, ok := schemePrefix(v); ok && !isStreamSpecifier(v) {
		return reject(graph, "a filter option carries the protocol prefix "+scheme+":")
	}
	if hostPathAt(v) || agent.LooksLikeHostPath(v) || embeddedHostPath(v) {
		return reject(graph, "a filter option names a path outside the session directory")
	}
	return nil
}
