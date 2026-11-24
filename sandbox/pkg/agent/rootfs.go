package agent

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path"
	"sort"
	"strings"
)

// ErrUnsafePath marks a path that is absolute, escapes the session root, or is
// otherwise not a session-relative virtual path.
var ErrUnsafePath = errors.New("agent: unsafe path")

// VirtualPath validates one op's path argument and returns it normalized.
//
// This is the worker's own check, independent of whatever the engine did. The
// specification is explicit that the worker does not trust its upstream: "Any
// job or op field that looks like a host path MUST be rejected with
// invalid_path". So an absolute path, a drive letter, a home-directory prefix,
// a NUL byte and any `..` component are refused here, before the path
// reaches a filesystem call.
func VirtualPath(p string) (string, error) {
	if p == "" {
		return "", fmt.Errorf("%w: empty path", ErrUnsafePath)
	}
	if strings.ContainsRune(p, 0) {
		return "", fmt.Errorf("%w: path contains a NUL byte", ErrUnsafePath)
	}
	// Backslashes are separators on Windows, so they are treated as separators
	// everywhere: a path that means one thing on one runner must not mean
	// another on the next.
	slashed := strings.ReplaceAll(p, "\\", "/")
	if strings.HasPrefix(slashed, "/") {
		return "", fmt.Errorf("%w: %q is absolute", ErrUnsafePath, p)
	}
	if strings.HasPrefix(slashed, "~") {
		return "", fmt.Errorf("%w: %q looks like a home directory", ErrUnsafePath, p)
	}
	if isDrivePath(slashed) {
		return "", fmt.Errorf("%w: %q names a volume", ErrUnsafePath, p)
	}
	for _, part := range strings.Split(slashed, "/") {
		if part == ".." {
			return "", fmt.Errorf("%w: %q traverses upwards", ErrUnsafePath, p)
		}
	}
	clean := path.Clean(slashed)
	if clean == "." || clean == "" {
		return "", fmt.Errorf("%w: %q names no file", ErrUnsafePath, p)
	}
	if clean == ".." || strings.HasPrefix(clean, "../") {
		return "", fmt.Errorf("%w: %q escapes the session root", ErrUnsafePath, p)
	}
	return clean, nil
}

func isDrivePath(p string) bool {
	if len(p) < 2 || p[1] != ':' {
		return false
	}
	c := p[0]
	return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
}

// LooksLikeHostPath reports whether a free-form string names a host path. It is
// applied to job and op fields that are not paths by name, so that a field
// which quietly carries a host path is refused rather than handed to a tool.
func LooksLikeHostPath(s string) bool {
	if s == "" {
		return false
	}
	if strings.HasPrefix(s, "/") || strings.HasPrefix(s, "\\") || isDrivePath(s) {
		return true
	}
	for _, part := range strings.Split(strings.ReplaceAll(s, "\\", "/"), "/") {
		if part == ".." {
			return true
		}
	}
	return false
}

// Entry is one listing row, matching the protocol's {path, kind, bytes}.
type Entry struct {
	Path  string `json:"path"`
	Kind  string `json:"kind"` // file | dir
	Bytes int64  `json:"bytes"`
}

// RootFS is a directory that every file operation of one session is confined
// to, plus the disk quota that session is allowed.
//
// Containment is os.Root, never a lexical prefix test: a lexical test cannot see
// symlinks, and the contents of a session directory are written by
// model-authored code and by media files nobody controls. This is the same
// containment sandbox/pkg/container applies to the executor's own workspace.
type RootFS struct {
	dir   string
	root  *os.Root
	quota int64
	used  int64
}

// OpenRootFS opens dir confined. A quota of zero or less means no quota.
func OpenRootFS(dir string, quota int64) (*RootFS, error) {
	root, err := os.OpenRoot(dir)
	if err != nil {
		return nil, err
	}
	return &RootFS{dir: dir, root: root, quota: quota}, nil
}

// Dir is the real directory every virtual path resolves inside.
func (r *RootFS) Dir() string { return r.dir }

// Close releases the directory handle.
func (r *RootFS) Close() error { return r.root.Close() }

// WriteFile writes data at a virtual path, creating parents.
func (r *RootFS) WriteFile(virtual string, data []byte) (int64, error) {
	rel, err := VirtualPath(virtual)
	if err != nil {
		return 0, err
	}
	if err := r.reserve(int64(len(data))); err != nil {
		return 0, err
	}
	if err := r.mkdirAll(path.Dir(rel)); err != nil {
		return 0, err
	}
	f, err := r.root.OpenFile(rel, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o600)
	if err != nil {
		return 0, err
	}
	if _, err := f.Write(data); err != nil {
		_ = f.Close()
		return 0, err
	}
	if err := f.Close(); err != nil {
		return 0, err
	}
	r.commit(int64(len(data)))
	return int64(len(data)), nil
}

// WriteStream copies rd into a virtual path, refusing to write past maxBytes.
// A partial copy is removed, so a failed download never looks like a complete
// file.
func (r *RootFS) WriteStream(virtual string, rd io.Reader, maxBytes int64) (int64, error) {
	rel, err := VirtualPath(virtual)
	if err != nil {
		return 0, err
	}
	if maxBytes <= 0 {
		maxBytes = MaxInlineBytes
	}
	if err := r.mkdirAll(path.Dir(rel)); err != nil {
		return 0, err
	}
	f, err := r.root.OpenFile(rel, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o600)
	if err != nil {
		return 0, err
	}
	// One byte past the cap is read so a stream that grew while being read is
	// reported as too large rather than silently truncated.
	written, err := io.Copy(f, io.LimitReader(rd, maxBytes+1))
	if err == nil && written > maxBytes {
		err = ErrTooLarge
	}
	// The quota is checked against the bytes that actually arrived rather than
	// against maxBytes, which is only an upper bound the engine offered: a
	// 100-byte object staged into a session with 1 MiB free must not be refused
	// because its op allowed 2 GiB.
	if err == nil && r.quota > 0 && r.used+written > r.quota {
		err = fmt.Errorf("%w: this session has %d of %d bytes left", ErrNoSpace, r.quota-r.used, r.quota)
	}
	if closeErr := f.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		_ = r.root.Remove(rel)
		return written, err
	}
	r.commit(written)
	return written, nil
}

// ReadFile reads at most maxBytes. A file larger than the cap is an error,
// never a silent short read.
func (r *RootFS) ReadFile(virtual string, maxBytes int64) ([]byte, error) {
	rel, err := VirtualPath(virtual)
	if err != nil {
		return nil, err
	}
	f, err := r.root.Open(rel)
	if err != nil {
		return nil, err
	}
	defer func() {
		// Best-effort: the read above is complete by the time the deferred
		// close runs (on the success path the caller has the bytes already; on
		// an error path the file is abandoned either way), so a failed close
		// has nothing to add.
		_ = f.Close()
	}()
	info, err := f.Stat()
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() {
		return nil, fmt.Errorf("%w: %q is not a regular file", ErrUnsafePath, virtual)
	}
	if maxBytes > 0 && info.Size() > maxBytes {
		return nil, fmt.Errorf("%w: %q is %d bytes, the cap is %d", ErrTooLarge, virtual, info.Size(), maxBytes)
	}
	data, err := io.ReadAll(io.LimitReader(f, maxBytes+1))
	if err != nil {
		return nil, err
	}
	if maxBytes > 0 && int64(len(data)) > maxBytes {
		return nil, fmt.Errorf("%w: %q grew past the cap while being read", ErrTooLarge, virtual)
	}
	return data, nil
}

// OpenStream opens a regular file for streaming and returns it with its size.
func (r *RootFS) OpenStream(virtual string) (*os.File, int64, error) {
	rel, err := VirtualPath(virtual)
	if err != nil {
		return nil, 0, err
	}
	f, err := r.root.Open(rel)
	if err != nil {
		return nil, 0, err
	}
	info, err := f.Stat()
	if err != nil {
		_ = f.Close()
		return nil, 0, err
	}
	if !info.Mode().IsRegular() {
		_ = f.Close()
		return nil, 0, fmt.Errorf("%w: %q is not a regular file", ErrUnsafePath, virtual)
	}
	return f, info.Size(), nil
}

// Stat reports whether a virtual path exists and how large it is.
func (r *RootFS) Stat(virtual string) (bool, int64, error) {
	rel, err := VirtualPath(virtual)
	if err != nil {
		return false, 0, err
	}
	info, err := r.root.Stat(rel)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return false, 0, nil
		}
		return false, 0, err
	}
	return true, info.Size(), nil
}

// Delete removes a virtual path and anything under it. A symlink is unlinked,
// never followed: deleting a link must not delete what it points at.
func (r *RootFS) Delete(virtual string) (bool, error) {
	rel, err := VirtualPath(virtual)
	if err != nil {
		return false, err
	}
	info, err := r.root.Lstat(rel)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return false, nil
		}
		return false, err
	}
	if info.IsDir() {
		if err := removeAllIn(r.root, rel); err != nil {
			return false, err
		}
		return true, nil
	}
	if err := r.root.Remove(rel); err != nil {
		return false, err
	}
	return true, nil
}

// List walks a virtual path, at most depth levels deep (1 means the
// directory's own entries).
func (r *RootFS) List(virtual string, depth int) ([]Entry, error) {
	rel, err := VirtualPath(virtual)
	if err != nil {
		return nil, err
	}
	if depth < 1 {
		depth = 1
	}
	if depth > 16 {
		// A cap, because a listing travels inline: an unbounded walk of a deep
		// node_modules would be a 4 MiB payload of its own.
		depth = 16
	}
	out := []Entry{}
	if err := r.walk(rel, depth, &out); err != nil {
		return nil, err
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Path < out[j].Path })
	return out, nil
}

func (r *RootFS) walk(rel string, depth int, out *[]Entry) error {
	dir, err := r.root.Open(rel)
	if err != nil {
		return err
	}
	entries, err := dir.ReadDir(-1)
	_ = dir.Close()
	if err != nil {
		return err
	}
	for _, e := range entries {
		info, err := e.Info()
		if err != nil {
			return err
		}
		child := rel
		if child == "." {
			child = e.Name()
		} else {
			child = child + "/" + e.Name()
		}
		entry := Entry{Path: child, Kind: "file", Bytes: info.Size()}
		if e.IsDir() {
			entry.Kind, entry.Bytes = "dir", 0
		}
		*out = append(*out, entry)
		if e.IsDir() && depth > 1 {
			if err := r.walk(child, depth-1, out); err != nil {
				return err
			}
		}
	}
	return nil
}

// ListRoot walks the session directory from its root, for a listFiles op that
// names no path.
func (r *RootFS) ListRoot(depth int) ([]Entry, error) {
	if depth < 1 {
		depth = 1
	}
	if depth > 16 {
		depth = 16
	}
	out := []Entry{}
	if err := r.walk(".", depth, &out); err != nil {
		return nil, err
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Path < out[j].Path })
	return out, nil
}

// Digest hashes one session file, lower-case hex.
func (r *RootFS) Digest(virtual string) (string, error) { return digestOfFile(r, virtual) }

// DiskUsed is the bytes this session has written.
func (r *RootFS) DiskUsed() int64 { return r.used }

// CheckQuota reports an error when adding size bytes would exceed the quota.
func (r *RootFS) CheckQuota(size int64) error { return r.reserve(size) }

// Commit records bytes actually written by a streamed transfer.
func (r *RootFS) Commit(size int64) { r.commit(size) }

func (r *RootFS) reserve(size int64) error {
	if r.quota <= 0 || size <= 0 {
		return nil
	}
	if r.used+size > r.quota {
		return fmt.Errorf("%w: this session has %d of %d bytes left", ErrNoSpace, r.quota-r.used, r.quota)
	}
	return nil
}

func (r *RootFS) commit(size int64) {
	if size > 0 {
		r.used += size
	}
}

// mkdirAll is os.MkdirAll confined to the root: every component is created
// through the root, so each step is re-checked for containment.
func (r *RootFS) mkdirAll(dir string) error {
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
		if err := r.root.Mkdir(current, 0o700); err != nil && !errors.Is(err, fs.ErrExist) {
			return err
		}
	}
	return nil
}

// removeAllIn is os.RemoveAll confined to root, using Lstat so a symlink is
// unlinked rather than followed.
func removeAllIn(root *os.Root, rel string) error {
	info, err := root.Lstat(rel)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return nil
		}
		return err
	}
	if info.IsDir() {
		dir, err := root.Open(rel)
		if err != nil {
			return err
		}
		names, readErr := dir.Readdirnames(-1)
		_ = dir.Close()
		if readErr != nil {
			return readErr
		}
		for _, name := range names {
			if err := removeAllIn(root, path.Join(rel, name)); err != nil {
				return err
			}
		}
	}
	return root.Remove(rel)
}

// ErrTooLarge marks content past a size cap; ErrNoSpace marks a spent disk
// quota. Both map onto a protocol error code at the op boundary.
var (
	ErrTooLarge = errors.New("agent: content is larger than the cap allows")
	ErrNoSpace  = errors.New("agent: the session's disk quota is exhausted")
)

// MaxInlineBytes is the largest content the protocol moves inline (4 MiB).
const MaxInlineBytes = 4 << 20

// SHA256File hashes an open file from its current position and returns the
// digest in the lower-case hex the protocol's sha256 fields use.
func SHA256File(f *os.File) (string, error) {
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// SHA256Bytes hashes a byte slice.
func SHA256Bytes(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}
