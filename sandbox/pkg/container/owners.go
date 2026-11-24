package container

import (
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

// OwnersDirName is the executor-owned directory directly under SANDBOX_ROOT that records which
// slot took a sandbox over from the slot that created it. It sits outside every workspace (a
// sandbox mounts only <root>/<id>), so sandboxed code can neither read nor write it. Entries are
// <root>/.owners/<sandbox id> holding the owning slot. With SANDBOX_SLOT empty none of this runs.
const OwnersDirName = ".owners"

const maxOwnerEntryBytes = 128

var (
	ownerIDRE   = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9-]{0,63}$`)
	ownerSlotRE = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`)
)

func (d *Driver) ownersDir() string { return filepath.Join(d.cfg.SandboxRoot, OwnersDirName) }

// ownerPath returns the entry path for a sandbox id, or false when the id could escape the
// directory or collide with a temp file.
func (d *Driver) ownerPath(id string) (string, bool) {
	if !ownerIDRE.MatchString(id) {
		return "", false
	}
	return filepath.Join(d.ownersDir(), id), true
}

// readOwner returns the slot recorded for a sandbox, or "" when there is no usable entry (absent,
// not a regular file, oversized, or malformed). It does file I/O: never call it holding d.mu.
func (d *Driver) readOwner(id string) string {
	p, ok := d.ownerPath(id)
	if !ok {
		return ""
	}
	f, err := os.OpenFile(p, os.O_RDONLY|ownerOpenFlags, 0)
	if err != nil {
		return ""
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil || !info.Mode().IsRegular() || info.Size() > maxOwnerEntryBytes {
		return ""
	}
	b, err := io.ReadAll(io.LimitReader(f, maxOwnerEntryBytes+1))
	if err != nil || len(b) > maxOwnerEntryBytes {
		return ""
	}
	slot := strings.TrimSpace(string(b))
	if !ownerSlotRE.MatchString(slot) {
		return ""
	}
	return slot
}

// writeOwner atomically records slot as the owner of a sandbox (temp file + rename, O_EXCL,
// O_NOFOLLOW, mode 0600). It does file I/O: never call it holding d.mu.
func (d *Driver) writeOwner(id, slot string) error {
	p, ok := d.ownerPath(id)
	if !ok || !ownerSlotRE.MatchString(slot) {
		return fmt.Errorf("invalid owner entry %q/%q", id, slot)
	}
	dir := d.ownersDir()
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	if info, err := os.Lstat(dir); err != nil || !info.IsDir() {
		return fmt.Errorf("%s is not a directory", dir)
	}
	tmp := fmt.Sprintf("%s.tmp-%d-%s", p, os.Getpid(), newExecMarker())
	f, err := os.OpenFile(tmp, os.O_WRONLY|os.O_CREATE|os.O_EXCL|ownerOpenFlags, 0o600)
	if err != nil {
		return err
	}
	_, werr := f.WriteString(slot + "\n")
	cerr := f.Close()
	if werr != nil || cerr != nil {
		_ = os.Remove(tmp)
		if werr != nil {
			return werr
		}
		return cerr
	}
	if err := os.Rename(tmp, p); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}

func (d *Driver) removeOwner(id string) {
	if d.cfg.Slot == "" {
		return
	}
	if p, ok := d.ownerPath(id); ok {
		_ = os.Remove(p)
	}
}

// holderOf is the slot that holds a sandbox: the owner entry, else the creating slot's label. No
// label means a legacy container, which nobody holds ("").
func (d *Driver) holderOf(id, label string) string {
	if o := d.readOwner(id); o != "" {
		return o
	}
	return label
}

// ownedElsewhere reports whether the owner entry names another slot. The entry is honoured
// whatever the peer list says and whenever the peer started. File I/O: not under d.mu.
func (d *Driver) ownedElsewhere(id string) bool {
	if d.cfg.Slot == "" {
		return false
	}
	o := d.readOwner(id)
	return o != "" && o != d.cfg.Slot
}

// forgetIfTakenOver drops one execution's sandbox from the index when a live peer owns it,
// without touching the container. Callers invoke it before taking d.mu for a lookup.
func (d *Driver) forgetIfTakenOver(execID string) {
	if d.cfg.Slot == "" {
		return
	}
	d.mu.RLock()
	id, ok := d.executionIndex[execID]
	d.mu.RUnlock()
	if !ok || !d.ownedElsewhere(id) {
		return
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.executionIndex[execID] != id {
		return
	}
	log.Printf("sandbox %s was taken over by another executor; dropping it from the index", id)
	delete(d.sandboxes, id)
	delete(d.executionIndex, execID)
}

// dropTakenOver is the janitor's version of forgetIfTakenOver for every indexed sandbox that is
// idle (nothing in flight, not deleting, no transition).
func (d *Driver) dropTakenOver() {
	if d.cfg.Slot == "" {
		return
	}
	d.mu.RLock()
	ids := make([]string, 0, len(d.sandboxes))
	for id, sb := range d.sandboxes {
		if sb.inFlight == 0 && !sb.deleting && sb.transition == nil {
			ids = append(ids, id)
		}
	}
	d.mu.RUnlock()
	for _, id := range ids {
		if !d.ownedElsewhere(id) {
			// Still ours: make sure the entry exists (an adoption whose write failed retries here).
			if d.readOwner(id) == "" {
				if err := d.writeOwner(id, d.cfg.Slot); err != nil {
					log.Printf("warning: could not record ownership of sandbox %s: %v", id, err)
				}
			}
			continue
		}
		d.mu.Lock()
		if sb, ok := d.sandboxes[id]; ok && sb.inFlight == 0 && !sb.deleting && sb.transition == nil {
			log.Printf("sandbox %s was taken over by another executor; dropping it from the index", id)
			delete(d.sandboxes, id)
			if d.executionIndex[sb.WorkflowExecutionID] == id {
				delete(d.executionIndex, sb.WorkflowExecutionID)
			}
		}
		d.mu.Unlock()
	}
}

// staleTempAge is how old a leftover temp file must be before pruneOwners may delete it; a
// younger one may be a peer's write about to be renamed.
const staleTempAge = time.Hour

// pruneOwners removes well-formed entries whose container no longer exists (containers is every id
// the pass saw), that nothing indexes and whose workspace is gone, plus temp files older than an
// hour. It never touches any other name, so it cannot race a peer's rename.
func (d *Driver) pruneOwners(containers map[string]bool) {
	if d.cfg.Slot == "" {
		return
	}
	entries, err := os.ReadDir(d.ownersDir())
	if err != nil {
		return
	}
	for _, e := range entries {
		name := e.Name()
		p, ok := d.ownerPath(name)
		if !ok {
			if strings.Contains(name, ".tmp-") {
				if info, ierr := e.Info(); ierr == nil && info.Mode().IsRegular() && time.Since(info.ModTime()) > staleTempAge {
					_ = os.Remove(filepath.Join(d.ownersDir(), name))
				}
			}
			continue
		}
		if containers[name] || d.indexes(name) || dirExists(filepath.Join(d.cfg.SandboxRoot, name)) {
			continue
		}
		_ = os.Remove(p)
	}
}
