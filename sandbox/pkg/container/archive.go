package container

import (
	"archive/tar"
	"compress/gzip"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path"
	"sort"
	"strings"
	"time"
)

// Workspace archives move a whole Remotion project out of a sandbox and back
// into a fresh one (composition revisions). Every rule in this file assumes the
// workspace was written by hostile code: the walk and the extraction both go
// through os.Root, links of any kind are never archived or extracted, and every
// size is capped before the bytes it describes are trusted.

var (
	// ErrArchiveTooLarge means a workspace (on export) or an uploaded archive
	// (on import) exceeds one of the ArchiveLimits.
	ErrArchiveTooLarge = errors.New("workspace archive too large")
	// ErrInvalidArchive means an uploaded archive is not a gzip-compressed tar,
	// or carries an entry this service refuses to extract (a link, a device, an
	// absolute or escaping path, an excluded directory).
	ErrInvalidArchive = errors.New("invalid workspace archive")
)

// WorkspaceArchiveExcludedNames are directory or file names that are never
// archived and never extracted, at any depth.
var WorkspaceArchiveExcludedNames = []string{"node_modules"}

// WorkspaceArchiveExcludedTopLevel are top-level workspace entries that are
// never archived and never extracted: build output, caches and tool state.
// A nested directory of the same name (src/build) is ordinary source.
var WorkspaceArchiveExcludedTopLevel = []string{"out", "build", "dist", ".cache", ".remotion", ".git", ".npm", "tmp"}

// IsExcludedWorkspacePath reports whether a clean, slash-separated,
// workspace-relative path falls under the archive exclusions. Export skips such
// paths and import refuses them, so both directions apply the same rule.
func IsExcludedWorkspacePath(rel string) bool {
	parts := strings.Split(rel, "/")
	for _, p := range parts {
		for _, ex := range WorkspaceArchiveExcludedNames {
			if p == ex {
				return true
			}
		}
	}
	for _, ex := range WorkspaceArchiveExcludedTopLevel {
		if parts[0] == ex {
			return true
		}
	}
	return false
}

// Default archive caps, used for any zero field of ArchiveLimits.
const (
	DefaultArchiveMaxEntries      = 20000
	DefaultArchiveMaxTotalBytes   = int64(2 << 30)   // 2 GiB uncompressed
	DefaultArchiveMaxFileBytes    = int64(512 << 20) // 512 MiB per file
	DefaultArchiveMaxArchiveBytes = int64(1 << 30)   // 1 GiB compressed (import)
)

// DefaultTemplateDir is where the Remotion template lives in the runtime image,
// which the control-plane image is built FROM, so the path exists there too.
const DefaultTemplateDir = "/opt/remotion-template"

// maxManifestBytes caps how much of a package.json the dependency comparison
// reads. A real manifest is a few KiB.
const maxManifestBytes = 1 << 20

// ArchiveLimits caps a workspace archive in both directions. A zero (or
// negative) field means its Default* value.
type ArchiveLimits struct {
	// MaxEntries caps files plus directories.
	MaxEntries int
	// MaxTotalBytes caps the uncompressed content of all files together.
	MaxTotalBytes int64
	// MaxFileBytes caps a single file.
	MaxFileBytes int64
	// MaxArchiveBytes caps the compressed input of an import.
	MaxArchiveBytes int64
}

// DefaultArchiveLimits returns the default caps.
func DefaultArchiveLimits() ArchiveLimits {
	return ArchiveLimits{}.normalized()
}

func (l ArchiveLimits) normalized() ArchiveLimits {
	if l.MaxEntries <= 0 {
		l.MaxEntries = DefaultArchiveMaxEntries
	}
	if l.MaxTotalBytes <= 0 {
		l.MaxTotalBytes = DefaultArchiveMaxTotalBytes
	}
	if l.MaxFileBytes <= 0 {
		l.MaxFileBytes = DefaultArchiveMaxFileBytes
	}
	if l.MaxArchiveBytes <= 0 {
		l.MaxArchiveBytes = DefaultArchiveMaxArchiveBytes
	}
	return l
}

// ArchiveStats describes an export, computed before the first byte is written.
type ArchiveStats struct {
	// Entries counts the files and directories in the archive.
	Entries int `json:"entries"`
	// Bytes is the uncompressed size of all archived files.
	Bytes int64 `json:"bytes"`
	// Skipped counts entries left out because they are not regular files or
	// directories (symlinks, devices, FIFOs, sockets). Excluded paths
	// (node_modules, build output) are not counted.
	Skipped int `json:"skipped"`
}

// ImportOptions controls ImportWorkspace.
type ImportOptions struct {
	// Replace removes every top-level workspace entry except the excluded ones
	// (node_modules, build output) before extracting, so the result is exactly
	// the archive on top of the template's node_modules. Without it the
	// archive is merged over what is there.
	Replace bool
}

// ImportResult describes a completed import.
type ImportResult struct {
	Files       int   `json:"files"`
	Directories int   `json:"directories"`
	Bytes       int64 `json:"bytes"`
	// TemplateCompared is false when either package.json (the restored one or
	// the template's) is missing or unreadable.
	TemplateCompared bool `json:"templateCompared"`
	// DependenciesMatchTemplate is TemplateCompared && no ExtraDependencies.
	DependenciesMatchTemplate bool `json:"dependenciesMatchTemplate"`
	// ExtraDependencies lists, as name@spec and sorted, every dependency or
	// devDependency of the restored manifest that the template lacks or
	// declares with a different spec. The sandbox has no egress, so these
	// usually cannot be installed. Never nil.
	ExtraDependencies []string `json:"extraDependencies"`
}

// pinWorkspace pins a sandbox's workspace for a file transfer without starting
// its container: archives only touch the host directory, and a stopped
// container must not be restarted just to read its files. The sandbox is
// counted as in flight, so the janitor neither stops nor deletes it until the
// returned release (endExec) runs. It does not wait on a stop/start
// transition, since the files are on disk either way.
func (d *Driver) pinWorkspace(workflowExecutionID string) (*Sandbox, func(), error) {
	if err := ValidateWorkflowExecutionID(workflowExecutionID); err != nil {
		return nil, nil, err
	}
	d.forgetIfTakenOver(workflowExecutionID)
	d.mu.Lock()
	sb, ok := d.lookupLocked(workflowExecutionID)
	if !ok || sb.deleting {
		d.mu.Unlock()
		return nil, nil, ErrNotFound
	}
	sb.inFlight++
	sb.LastActivity = time.Now().UTC()
	snapshot := *sb
	d.mu.Unlock()
	return &snapshot, func() { d.endExec(sb) }, nil
}

// archiveEntry is one file or directory found by the export pre-pass.
type archiveEntry struct {
	rel   string
	isDir bool
	size  int64
	exec  bool
	// info is the pre-pass Lstat; the copy refuses a handle that is not this
	// same object (a file swapped for a link or FIFO after the pre-pass).
	info fs.FileInfo
}

// errNotRegular reports a workspace path that is not the kind of object the
// caller expected (a FIFO, device, socket, or a swapped-in link target).
var errNotRegular = errors.New("not a regular file or directory")

// openVerifiedIn opens rel through the root without ever blocking, and
// refuses it unless it is a directory (wantDir) or a regular file and, when
// expect is non-nil, the very object an earlier Lstat described.
//
// The workspace is written by hostile code: a FIFO placed at a path this
// service opens would make a plain open (or the read after it) wait forever
// for a writer, so the open is O_NONBLOCK, the handle is fstat'ed before
// anything is read, and only then is blocking mode restored.
func openVerifiedIn(root *os.Root, rel string, wantDir bool, expect fs.FileInfo) (*os.File, fs.FileInfo, error) {
	f, err := root.OpenFile(rel, os.O_RDONLY|nonBlockingOpenFlag, 0)
	if err != nil {
		return nil, nil, err
	}
	info, err := f.Stat()
	if err != nil {
		_ = f.Close() // best-effort: the handle is abandoned either way.
		return nil, nil, err
	}
	if (wantDir && !info.IsDir()) || (!wantDir && !info.Mode().IsRegular()) {
		_ = f.Close() // best-effort: the handle is abandoned either way.
		return nil, nil, fmt.Errorf("%s: %w", rel, errNotRegular)
	}
	if expect != nil && !os.SameFile(expect, info) {
		_ = f.Close() // best-effort: the handle is abandoned either way.
		return nil, nil, fmt.Errorf("workspace changed during transfer: %s was replaced", rel)
	}
	if err := setBlocking(f); err != nil {
		_ = f.Close() // best-effort: the handle is abandoned either way.
		return nil, nil, err
	}
	return f, info, nil
}

// ExportWorkspace writes a deterministic gzip-compressed tar of the
// workspace. A pre-pass walks the workspace, computes ArchiveStats and enforces
// limits before anything is written; only then is begin called for the
// destination writer, so a caller (the HTTP handler) can still answer an error
// with a status code when the pre-pass fails.
//
// The archive holds regular files and directories only, in lexicographic path
// order, with fixed metadata (mtime 0, uid/gid 0, mode 0644/0755), so two
// exports of an unchanged workspace are byte-identical. Symlinks, devices,
// FIFOs and sockets are skipped and counted, never followed. Excluded paths
// (IsExcludedWorkspacePath) are left out.
//
// An error returned after begin means the archive is incomplete: the gzip and
// tar trailers are deliberately not written, so the output is not a valid
// archive.
func (d *Driver) ExportWorkspace(workflowExecutionID string, limits ArchiveLimits, begin func(ArchiveStats) (io.Writer, error)) (ArchiveStats, error) {
	limits = limits.normalized()
	sb, release, err := d.pinWorkspace(workflowExecutionID)
	if err != nil {
		return ArchiveStats{}, err
	}
	defer release()

	root, err := openWorkspace(sb)
	if err != nil {
		return ArchiveStats{}, err
	}
	defer func() {
		_ = root.Close() // best-effort: every read is complete by now.
	}()

	entries, stats, err := scanWorkspace(root, limits)
	if err != nil {
		return stats, err
	}

	w, err := begin(stats)
	if err != nil {
		return stats, err
	}
	if err := writeWorkspaceArchive(root, entries, w); err != nil {
		return stats, err
	}
	d.Touch(workflowExecutionID)
	return stats, nil
}

// scanWorkspace is the export pre-pass. It walks through the root only, uses
// Lstat so no link is ever followed, and stops as soon as a cap is exceeded.
func scanWorkspace(root *os.Root, limits ArchiveLimits) ([]archiveEntry, ArchiveStats, error) {
	var (
		entries []archiveEntry
		stats   ArchiveStats
	)
	var walk func(dir string, expect fs.FileInfo) error
	walk = func(dir string, expect fs.FileInfo) error {
		// Opened once, verified to be the directory the Lstat saw (a
		// directory swapped for a link or FIFO is refused, never walked),
		// and listed from that same handle.
		df, _, err := openVerifiedIn(root, dir, true, expect)
		if err != nil {
			return err
		}
		dirents, readErr := df.ReadDir(-1)
		_ = df.Close() // best-effort: the listing is complete or readErr says why.
		if readErr != nil {
			return readErr
		}
		for _, de := range dirents {
			rel := path.Join(dir, de.Name())
			if IsExcludedWorkspacePath(rel) {
				continue
			}
			info, err := root.Lstat(rel)
			if err != nil {
				return err
			}
			mode := info.Mode()
			switch {
			case mode.IsDir():
				entries = append(entries, archiveEntry{rel: rel, isDir: true, info: info})
			case mode.IsRegular():
				if info.Size() > limits.MaxFileBytes {
					return fmt.Errorf("%w: %s is %d bytes (limit %d per file)", ErrArchiveTooLarge, rel, info.Size(), limits.MaxFileBytes)
				}
				stats.Bytes += info.Size()
				if stats.Bytes > limits.MaxTotalBytes {
					return fmt.Errorf("%w: workspace exceeds %d bytes", ErrArchiveTooLarge, limits.MaxTotalBytes)
				}
				entries = append(entries, archiveEntry{rel: rel, size: info.Size(), exec: mode.Perm()&0o111 != 0, info: info})
			default:
				stats.Skipped++
				continue
			}
			if len(entries) > limits.MaxEntries {
				return fmt.Errorf("%w: workspace has more than %d entries", ErrArchiveTooLarge, limits.MaxEntries)
			}
			if mode.IsDir() {
				if err := walk(rel, info); err != nil {
					return err
				}
			}
		}
		return nil
	}
	if err := walk(".", nil); err != nil {
		return nil, stats, err
	}
	// A directory is a prefix of its children, so a plain string sort puts it
	// before them.
	sort.Slice(entries, func(i, j int) bool { return entries[i].rel < entries[j].rel })
	stats.Entries = len(entries)
	return entries, stats, nil
}

// epoch is the fixed modification time of every archived entry.
var epoch = time.Unix(0, 0)

func writeWorkspaceArchive(root *os.Root, entries []archiveEntry, w io.Writer) error {
	zw, err := gzip.NewWriterLevel(w, gzip.DefaultCompression)
	if err != nil {
		return err
	}
	// gzip.Writer's Header is zero by default (no name, no mtime), which is
	// what determinism needs; nothing sets it.
	tw := tar.NewWriter(zw)
	for _, e := range entries {
		hdr := &tar.Header{
			Name:    e.rel,
			ModTime: epoch,
			Mode:    0o644,
		}
		if e.isDir {
			hdr.Typeflag = tar.TypeDir
			hdr.Name += "/"
			hdr.Mode = 0o755
			if err := tw.WriteHeader(hdr); err != nil {
				return err
			}
			continue
		}
		hdr.Typeflag = tar.TypeReg
		hdr.Size = e.size
		if e.exec {
			hdr.Mode = 0o755
		}
		if err := copyWorkspaceFile(root, e, hdr, tw); err != nil {
			return err
		}
	}
	// Only a fully written archive gets its trailers: on any error above the
	// output stays truncated and invalid rather than a short valid archive.
	if err := tw.Close(); err != nil {
		return err
	}
	return zw.Close()
}

func copyWorkspaceFile(root *os.Root, e archiveEntry, hdr *tar.Header, tw *tar.Writer) error {
	// Non-blocking, fstat'ed, and required to be the file the pre-pass saw:
	// one swapped for a FIFO or a link since then is refused, not read.
	f, _, err := openVerifiedIn(root, e.rel, false, e.info)
	if err != nil {
		return fmt.Errorf("workspace changed during export: %w", err)
	}
	defer func() {
		_ = f.Close() // best-effort: read-only handle, the copy result is what matters.
	}()
	if err := tw.WriteHeader(hdr); err != nil {
		return err
	}
	// Copy exactly the size the header promised: a file that grew is cut at
	// the recorded size, one that shrank is an error.
	n, err := io.Copy(tw, io.LimitReader(f, e.size))
	if err != nil {
		return err
	}
	if n != e.size {
		return fmt.Errorf("workspace changed during export: %s shrank from %d to %d bytes", e.rel, e.size, n)
	}
	return nil
}

// limitedCountingReader fails with ErrArchiveTooLarge once more than max bytes
// have been read from r.
type limitedCountingReader struct {
	r   io.Reader
	n   int64
	max int64
}

func (l *limitedCountingReader) Read(p []byte) (int, error) {
	if l.n > l.max {
		return 0, fmt.Errorf("%w: compressed archive exceeds %d bytes", ErrArchiveTooLarge, l.max)
	}
	// Allow one byte past the cap so exceeding it is observable.
	if remaining := l.max + 1 - l.n; int64(len(p)) > remaining {
		p = p[:remaining]
	}
	n, err := l.r.Read(p)
	l.n += int64(n)
	if l.n > l.max {
		return n, fmt.Errorf("%w: compressed archive exceeds %d bytes", ErrArchiveTooLarge, l.max)
	}
	return n, err
}

// ImportWorkspace extracts a gzip-compressed tar (as written by
// ExportWorkspace) into the workspace, through the os.Root.
//
// Every entry is validated before anything is written for it: only regular
// files and directories, no absolute paths, no "..", no backslashes or NULs,
// nothing under an excluded path. Sizes are capped on the bytes actually
// written, not on header claims, and the compressed input is capped too.
//
// A failure midway leaves the workspace partially restored (and, with
// Replace, with its previous source tree already removed). That is accepted:
// the caller treats a failed import as a failed restore, and the sandbox is
// disposable.
func (d *Driver) ImportWorkspace(workflowExecutionID string, r io.Reader, limits ArchiveLimits, opts ImportOptions) (ImportResult, error) {
	limits = limits.normalized()
	result := ImportResult{ExtraDependencies: []string{}}
	sb, release, err := d.pinWorkspace(workflowExecutionID)
	if err != nil {
		return result, err
	}
	defer release()

	root, err := openWorkspace(sb)
	if err != nil {
		return result, err
	}
	defer func() {
		_ = root.Close() // best-effort: every write is closed by now.
	}()

	if opts.Replace {
		if err := clearWorkspaceSource(root); err != nil {
			return result, err
		}
	}

	if err := extractArchive(root, r, limits, &result); err != nil {
		return result, err
	}

	compareDependencies(root, d.templateDir(), &result)
	d.Touch(workflowExecutionID)
	return result, nil
}

func (d *Driver) templateDir() string {
	if d.cfg.TemplateDir != "" {
		return d.cfg.TemplateDir
	}
	return DefaultTemplateDir
}

// clearWorkspaceSource removes every top-level entry except the excluded ones
// (node_modules from the template copy, build output). removeAllIn uses Lstat,
// so a planted link is unlinked, never followed.
func clearWorkspaceSource(root *os.Root) error {
	df, _, err := openVerifiedIn(root, ".", true, nil)
	if err != nil {
		return err
	}
	names, readErr := df.Readdirnames(-1)
	_ = df.Close() // best-effort: the listing is complete or readErr says why.
	if readErr != nil {
		return readErr
	}
	for _, name := range names {
		if IsExcludedWorkspacePath(name) {
			continue
		}
		if err := removeAllIn(root, name); err != nil {
			return asPathError(err)
		}
	}
	return nil
}

func invalidArchive(format string, args ...any) error {
	return fmt.Errorf("%w: %s", ErrInvalidArchive, fmt.Sprintf(format, args...))
}

// classifyReadErr maps a gzip/tar decoding error onto ErrInvalidArchive,
// leaving a cap violation (ErrArchiveTooLarge) and transport errors visible.
func classifyReadErr(err error) error {
	if errors.Is(err, ErrArchiveTooLarge) {
		return err
	}
	return fmt.Errorf("%w: %w", ErrInvalidArchive, err)
}

// validateArchiveName returns the clean workspace-relative path for a tar
// entry name, or an ErrInvalidArchive error.
func validateArchiveName(name string) (string, error) {
	switch {
	case name == "":
		return "", invalidArchive("empty entry name")
	case strings.ContainsRune(name, 0):
		return "", invalidArchive("entry name contains a NUL byte")
	case strings.Contains(name, `\`):
		return "", invalidArchive("entry name %q contains a backslash", name)
	case strings.HasPrefix(name, "/"):
		return "", invalidArchive("entry name %q is absolute", name)
	}
	for _, part := range strings.Split(name, "/") {
		if part == ".." {
			return "", invalidArchive("entry name %q contains '..'", name)
		}
	}
	clean := path.Clean(name)
	if clean == "." || clean == "" || clean == ".." || strings.HasPrefix(clean, "../") || strings.HasPrefix(clean, "/") {
		return "", invalidArchive("entry name %q does not name a workspace path", name)
	}
	if IsExcludedWorkspacePath(clean) {
		return "", invalidArchive("entry %q is under an excluded path", name)
	}
	return clean, nil
}

func extractArchive(root *os.Root, r io.Reader, limits ArchiveLimits, result *ImportResult) error {
	counted := &limitedCountingReader{r: r, max: limits.MaxArchiveBytes}
	zr, err := gzip.NewReader(counted)
	if err != nil {
		return classifyReadErr(err)
	}
	defer func() {
		_ = zr.Close() // best-effort: Close only releases the decompressor.
	}()
	tr := tar.NewReader(zr)
	entries := 0
	for {
		hdr, err := tr.Next()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return classifyReadErr(err)
		}
		// archive/tar already rewrote legacy TypeRegA into TypeReg/TypeDir and
		// consumed PAX/GNU long-name records; anything else is refused.
		if hdr.Typeflag != tar.TypeReg && hdr.Typeflag != tar.TypeDir {
			return invalidArchive("entry %q has type %q; only regular files and directories are accepted", hdr.Name, string(hdr.Typeflag))
		}
		clean, err := validateArchiveName(hdr.Name)
		if err != nil {
			return err
		}
		entries++
		if entries > limits.MaxEntries {
			return fmt.Errorf("%w: archive has more than %d entries", ErrArchiveTooLarge, limits.MaxEntries)
		}

		if hdr.Typeflag == tar.TypeDir {
			if err := ensureDirIn(root, clean); err != nil {
				return asPathError(err)
			}
			_ = root.Lchown(clean, sandboxUID, sandboxGID) // best-effort: needs privileges.
			result.Directories++
			continue
		}

		if hdr.Size > limits.MaxFileBytes {
			return fmt.Errorf("%w: %s is %d bytes (limit %d per file)", ErrArchiveTooLarge, clean, hdr.Size, limits.MaxFileBytes)
		}
		written, err := extractFile(root, clean, hdr, tr, limits.MaxTotalBytes-result.Bytes)
		result.Bytes += written
		if err != nil {
			return err
		}
		result.Files++
	}
	// Drain to the end of the gzip stream so a corrupt trailer (checksum,
	// length) is reported rather than silently accepted.
	if _, err := io.Copy(io.Discard, zr); err != nil {
		return classifyReadErr(err)
	}
	return nil
}

// extractFile writes one regular file through the root and returns the bytes
// actually written. remaining is what is left of MaxTotalBytes.
func extractFile(root *os.Root, clean string, hdr *tar.Header, tr io.Reader, remaining int64) (int64, error) {
	if err := ensureDirIn(root, path.Dir(clean)); err != nil {
		return 0, asPathError(err)
	}
	// A link (or device, FIFO) already at this path is replaced, not written
	// through: os.Root would keep a link inside the workspace, but a FIFO
	// would block and a link would redirect the write.
	if info, err := root.Lstat(clean); err == nil && !info.Mode().IsRegular() && !info.IsDir() {
		if err := root.Remove(clean); err != nil {
			return 0, asPathError(err)
		}
	}
	// O_NONBLOCK as well: something swapped in for a FIFO between the Lstat
	// above and this open must fail (ENXIO) or be refused below, never block.
	f, err := root.OpenFile(clean, os.O_WRONLY|os.O_CREATE|os.O_TRUNC|nonBlockingOpenFlag, 0o644)
	if err != nil {
		return 0, asPathError(err)
	}
	n, err := writeExtractedFile(f, clean, hdr, tr, remaining)
	closeErr := f.Close()
	if err != nil {
		return n, err
	}
	return n, closeErr
}

// writeExtractedFile fills a freshly opened handle. Mode and owner are set on
// the handle itself, never by path, so nothing is changed through a link.
func writeExtractedFile(f *os.File, clean string, hdr *tar.Header, tr io.Reader, remaining int64) (int64, error) {
	info, err := f.Stat()
	if err != nil {
		return 0, err
	}
	if !info.Mode().IsRegular() {
		return 0, fmt.Errorf("%w: %s: %w", ErrInvalidPath, clean, errNotRegular)
	}
	if err := setBlocking(f); err != nil {
		return 0, err
	}
	// Count what is really decompressed: header sizes are claims, and a
	// gzip bomb is exactly a small input that claims nothing alarming.
	src := &readErrRecorder{r: io.LimitReader(tr, remaining+1)}
	n, copyErr := io.Copy(f, src)
	if copyErr != nil {
		if src.err != nil {
			// The archive stream broke (corrupt gzip, truncated tar, a cap).
			return n, classifyReadErr(src.err)
		}
		return n, copyErr // the write side failed: not the archive's fault.
	}
	if n > remaining {
		return n, fmt.Errorf("%w: archive content exceeds %d bytes", ErrArchiveTooLarge, remaining)
	}
	mode := os.FileMode(0o644)
	if hdr.Mode&0o111 != 0 {
		mode = 0o755
	}
	if err := f.Chmod(mode); err != nil {
		return n, err
	}
	_ = f.Chown(sandboxUID, sandboxGID) // best-effort: needs privileges.
	return n, nil
}

// readErrRecorder remembers the error its reader returned, so a failed copy
// can be blamed on the archive (read side) or on the workspace (write side).
type readErrRecorder struct {
	r   io.Reader
	err error
}

func (e *readErrRecorder) Read(p []byte) (int, error) {
	n, err := e.r.Read(p)
	if err != nil && !errors.Is(err, io.EOF) {
		e.err = err
	}
	return n, err
}

// ensureDirIn creates dir and its parents inside root. Unlike mkdirAllIn, any
// component that exists but is not a directory (typically a symlink the
// sandbox planted) is removed and replaced by a real directory, so the
// extraction never writes through a link.
func ensureDirIn(root *os.Root, dir string) error {
	if dir == "." || dir == "" {
		return nil
	}
	current := ""
	for _, part := range strings.Split(dir, "/") {
		if part == "" || part == "." {
			continue
		}
		if current == "" {
			current = part
		} else {
			current += "/" + part
		}
		info, err := root.Lstat(current)
		switch {
		case err == nil && info.IsDir():
			continue
		case err == nil:
			if err := root.Remove(current); err != nil {
				return err
			}
		case !errors.Is(err, fs.ErrNotExist):
			return err
		}
		if err := root.Mkdir(current, 0o755); err != nil && !errors.Is(err, fs.ErrExist) {
			return err
		}
	}
	return nil
}

type packageManifest struct {
	Dependencies    map[string]json.RawMessage `json:"dependencies"`
	DevDependencies map[string]json.RawMessage `json:"devDependencies"`
}

// specs flattens dependencies and devDependencies into name -> spec. A
// non-string spec is kept as its raw JSON so it still compares.
func (m packageManifest) specs() map[string]string {
	out := make(map[string]string, len(m.Dependencies)+len(m.DevDependencies))
	add := func(deps map[string]json.RawMessage) {
		for name, raw := range deps {
			var s string
			if err := json.Unmarshal(raw, &s); err != nil {
				s = string(raw)
			}
			if _, seen := out[name]; !seen {
				out[name] = s
			}
		}
	}
	add(m.Dependencies)
	add(m.DevDependencies)
	return out
}

func parseManifest(r io.Reader) (packageManifest, error) {
	var m packageManifest
	data, err := io.ReadAll(io.LimitReader(r, maxManifestBytes+1))
	if err != nil {
		return m, err
	}
	if len(data) > maxManifestBytes {
		return m, errors.New("package.json too large")
	}
	err = json.Unmarshal(data, &m)
	return m, err
}

// readManifestIn reads root/package.json without blocking on a FIFO (the
// workspace's copy was written by hostile code) and within maxManifestBytes.
func readManifestIn(root *os.Root) (packageManifest, error) {
	f, _, err := openVerifiedIn(root, "package.json", false, nil)
	if err != nil {
		return packageManifest{}, err
	}
	m, err := parseManifest(f)
	_ = f.Close() // best-effort: read-only handle, already fully read.
	return m, err
}

// compareDependencies fills the dependency fields of result. Any failure to
// read either manifest leaves TemplateCompared false; it is never an error.
func compareDependencies(root *os.Root, templateDir string, result *ImportResult) {
	result.ExtraDependencies = []string{}

	workspace, err := readManifestIn(root)
	if err != nil {
		return
	}

	// The template belongs to the trusted control-plane image, but it is read
	// the same careful way: through a root, non-blocking, regular files only.
	troot, err := os.OpenRoot(templateDir)
	if err != nil {
		return
	}
	template, err := readManifestIn(troot)
	_ = troot.Close() // best-effort: the manifest is read by now.
	if err != nil {
		return
	}

	tmpl := template.specs()
	extra := make([]string, 0)
	for name, spec := range workspace.specs() {
		if have, ok := tmpl[name]; !ok || have != spec {
			extra = append(extra, name+"@"+spec)
		}
	}
	sort.Strings(extra)
	result.TemplateCompared = true
	result.ExtraDependencies = extra
	result.DependenciesMatchTemplate = len(extra) == 0
}
