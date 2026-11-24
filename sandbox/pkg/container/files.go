package container

import (
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path"
	"strings"
	"time"
)

// by code running inside the sandbox, so its file sizes are attacker-chosen and
// the whole body is buffered in memory and base64-encoded before it is sent.
const MaxReadFileBytes = 64 << 20 // 64 MiB

// DefaultMaxRenderBytes is the default SANDBOX_MAX_RENDER_BYTES cap for the
// streaming /files/raw endpoint.
const DefaultMaxRenderBytes int64 = 4 << 30 // 4 GiB

// NormalizePath turns a caller-supplied path into a path relative to the
// workspace root. Lexical cleaning here only neutralizes the easy cases; real
// containment is enforced by os.Root in openWorkspace, which is what makes
// symlinks safe.
func NormalizePath(userPath string) string {
	clean := path.Clean("/" + strings.TrimSpace(strings.ReplaceAll(userPath, `\`, "/")))
	rel := strings.TrimPrefix(clean, "/")
	if rel == "" {
		return "."
	}
	return rel
}

// openWorkspace opens the sandbox workspace as an os.Root. Every file operation
// this service performs must go through the returned root.
//
// This is a security boundary, not a convenience. The previous implementation
// resolved paths lexically (filepath.Clean + a HasPrefix check), which cannot
// see symlinks: code running inside the sandbox container — which is fully
// attacker-controlled, see ValidateExec — could create /workspace/esc -> / and
// this service would then follow it while reading, writing, or deleting. Those
// operations run in *this* container's mount namespace, which has the Docker
// socket bind-mounted, so a write through such a link escalates from "arbitrary
// code in the sandbox" to "arbitrary code in the control plane" and from there
// to root on the host. os.Root refuses any traversal that leaves the root,
// including via symlink, and re-checks on every path component.
func openWorkspace(sb *Sandbox) (*os.Root, error) {
	return os.OpenRoot(sb.WorkspacePath)
}

// mkdirAllIn is os.MkdirAll confined to root. os.Root gained MkdirAll in Go
// 1.25; this walks the components by hand so the service still builds on 1.24.
func mkdirAllIn(root *os.Root, dir string) error {
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
		if err := root.Mkdir(current, 0o755); err != nil && !errors.Is(err, fs.ErrExist) {
			return err
		}
	}
	return nil
}

// removeAllIn is os.RemoveAll confined to root (os.Root gained RemoveAll in Go
// 1.25). It uses Lstat so a symlink is unlinked rather than followed — deleting
// a link must never delete what it points at.
func removeAllIn(root *os.Root, rel string) error {
	info, err := root.Lstat(rel)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return nil
		}
		return err
	}
	if info.IsDir() {
		// Non-blocking and checked against the Lstat: a directory swapped for
		// a FIFO (which would block a plain open) or for a link to another
		// directory between the two calls is refused, not listed.
		dir, _, err := openVerifiedIn(root, rel, true, info)
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

// asPathError maps an os.Root containment refusal onto ErrInvalidPath so the
// caller answers 400 rather than leaking the underlying filesystem error.
func asPathError(err error) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, fs.ErrNotExist) {
		return err
	}
	return fmt.Errorf("%w: %w", ErrInvalidPath, err)
}

func (d *Driver) ListFiles(workflowExecutionID, relPath string) ([]FileEntry, error) {
	sb, err := d.GetByExecution(workflowExecutionID)
	if err != nil {
		return nil, err
	}
	root, err := openWorkspace(sb)
	if err != nil {
		return nil, err
	}
	defer func() {
		// Best-effort cleanup: the only errors a Close here can produce are
		// double-close panics, and the real result of the listing is what the
		// caller cares about.
		_ = root.Close()
	}()

	dir, err := root.OpenFile(NormalizePath(relPath), os.O_RDONLY|nonBlockingOpenFlag, 0)
	if err != nil {
		return nil, asPathError(err)
	}
	defer func() {
		_ = dir.Close() // best-effort: the listing is already complete by the time
		// the deferred close runs, so a failed close has nothing to do.
	}()

	entries, err := dir.ReadDir(-1)
	if err != nil {
		return nil, err
	}

	out := make([]FileEntry, 0, len(entries))
	for _, e := range entries {
		info, statErr := e.Info()
		if statErr != nil {
			return nil, statErr
		}
		out = append(out, FileEntry{
			Name:    e.Name(),
			IsDir:   e.IsDir(),
			Size:    info.Size(),
			ModTime: info.ModTime().UTC(),
		})
	}
	d.Touch(workflowExecutionID)
	return out, nil
}

func (d *Driver) ReadFile(workflowExecutionID, relPath string) ([]byte, error) {
	sb, err := d.GetByExecution(workflowExecutionID)
	if err != nil {
		return nil, err
	}
	root, err := openWorkspace(sb)
	if err != nil {
		return nil, err
	}
	defer func() {
		_ = root.Close() // best-effort: the read is already complete by the time
		// the deferred close runs, so a failed close has nothing to do.
	}()

	f, err := root.OpenFile(NormalizePath(relPath), os.O_RDONLY|nonBlockingOpenFlag, 0)
	if err != nil {
		return nil, asPathError(err)
	}
	defer func() {
		_ = f.Close() // best-effort: ditto.
	}()

	// Refuse anything that is not a regular file: opening a device or FIFO the
	// sandbox planted in its workspace would otherwise block or stream forever.
	info, err := f.Stat()
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() {
		return nil, ErrInvalidPath
	}

	if info.Size() > MaxReadFileBytes {
		return nil, ErrFileTooLarge
	}
	// Read one byte past the cap so a file that grew while being read is
	// reported as too large instead of being silently truncated.
	data, err := io.ReadAll(io.LimitReader(f, MaxReadFileBytes+1))
	if err != nil {
		return nil, err
	}
	if int64(len(data)) > MaxReadFileBytes {
		return nil, ErrFileTooLarge
	}
	d.Touch(workflowExecutionID)
	return data, nil
}

// openRawFile opens a regular workspace file for streaming and returns it with
// its size. The caller must close the file. Containment is the same os.Root as
// readFile; the file stays valid after the root handle is closed.
func (d *Driver) OpenRawFile(workflowExecutionID, relPath string) (*os.File, int64, error) {
	sb, err := d.GetByExecution(workflowExecutionID)
	if err != nil {
		return nil, 0, err
	}
	root, err := openWorkspace(sb)
	if err != nil {
		return nil, 0, err
	}
	defer func() {
		// Best-effort cleanup: the returned file is independent of the root
		// handle (see openRawFile's doc), so the caller still owns it after
		// this function exits, and a failed close here has nothing to do.
		_ = root.Close()
	}()

	f, err := root.OpenFile(NormalizePath(relPath), os.O_RDONLY|nonBlockingOpenFlag, 0)
	if err != nil {
		return nil, 0, asPathError(err)
	}
	info, err := f.Stat()
	if err != nil {
		// The caller receives the open error; a failed close here has nothing
		// to add (nothing was read, and the file is abandoned either way).
		_ = f.Close()
		return nil, 0, err
	}
	if !info.Mode().IsRegular() {
		_ = f.Close() // ditto: a non-regular file is abandoned either way.
		return nil, 0, ErrInvalidPath
	}
	if d.cfg.MaxRenderBytes > 0 && info.Size() > d.cfg.MaxRenderBytes {
		_ = f.Close() // ditto: an oversized file is abandoned either way.
		return nil, 0, ErrFileTooLarge
	}
	if err := setBlocking(f); err != nil {
		_ = f.Close() // ditto: the handle is abandoned either way.
		return nil, 0, err
	}
	d.Touch(workflowExecutionID)
	return f, info.Size(), nil
}

func (d *Driver) WriteFile(workflowExecutionID, relPath string, content []byte) error {
	sb, err := d.GetByExecution(workflowExecutionID)
	if err != nil {
		return err
	}
	rel := NormalizePath(relPath)
	if rel == "." {
		return ErrInvalidPath
	}

	root, err := openWorkspace(sb)
	if err != nil {
		return err
	}
	defer func() {
		_ = root.Close() // best-effort: the write is already flushed and closed
		// by the time the deferred close runs, so a failed close has nothing
		// to do.
	}()

	if err := mkdirAllIn(root, path.Dir(rel)); err != nil {
		return asPathError(err)
	}
	// O_NONBLOCK so a FIFO the sandbox planted cannot park this goroutine in open.
	f, err := root.OpenFile(rel, os.O_WRONLY|os.O_CREATE|os.O_TRUNC|nonBlockingOpenFlag, 0o644)
	if err != nil {
		return asPathError(err)
	}
	if info, serr := f.Stat(); serr != nil || !info.Mode().IsRegular() {
		_ = f.Close()
		return ErrInvalidPath
	}
	if _, err := f.Write(content); err != nil {
		_ = f.Close()
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	d.Touch(workflowExecutionID)
	return nil
}

func (d *Driver) DeletePath(workflowExecutionID, relPath string) error {
	sb, err := d.GetByExecution(workflowExecutionID)
	if err != nil {
		return err
	}
	rel := NormalizePath(relPath)
	if rel == "." {
		return ErrInvalidPath
	}

	root, err := openWorkspace(sb)
	if err != nil {
		return err
	}
	defer func() {
		_ = root.Close() // best-effort: the removal is already done by the time
		// the deferred close runs, so a failed close has nothing to do.
	}()

	if err := removeAllIn(root, rel); err != nil {
		return asPathError(err)
	}
	d.Touch(workflowExecutionID)
	return nil
}

// FileEntry describes one directory entry in a sandbox workspace listing.
type FileEntry struct {
	Name    string    `json:"name"`
	IsDir   bool      `json:"isDir"`
	Size    int64     `json:"size"`
	ModTime time.Time `json:"modTime"`
}
