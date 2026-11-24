package container

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/google/uuid"
)

func ValidateWorkflowExecutionID(workflowExecutionID string) error {
	if _, err := uuid.Parse(workflowExecutionID); err != nil {
		return ErrBadExecution
	}
	return nil
}

func (d *Driver) Create(workflowExecutionID string) (*Sandbox, bool, error) {
	if err := ValidateWorkflowExecutionID(workflowExecutionID); err != nil {
		return nil, false, err
	}

	// One Create per execution at a time: a second caller waits and then finds the first one's
	// sandbox, instead of racing it and removing the "duplicate" the first is still building on.
	defer d.lockExec(workflowExecutionID)()

	// An existing sandbox is handed back through acquire, which waits out an
	// in-progress stop before restarting a stopped container (see acquire).
	d.mu.RLock()
	_, exists := d.executionIndex[workflowExecutionID]
	d.mu.RUnlock()
	if exists {
		sb, _, err := d.acquire(workflowExecutionID, false)
		if err == nil {
			return sb, false, nil
		}
		if !errors.Is(err, ErrNotFound) {
			return nil, false, err
		}
		// Removed between the check and the acquire: create a fresh one.
	}

	if taken, err := d.takeOverExisting(workflowExecutionID); err != nil {
		return nil, false, err
	} else if taken {
		sb, _, err := d.acquire(workflowExecutionID, false)
		if err != nil {
			return nil, false, err
		}
		return sb, false, nil
	}

	d.mu.Lock()
	// Check if we're at max capacity
	if len(d.sandboxes) >= d.maxSandboxes {
		d.mu.Unlock()
		return nil, false, ErrTooManySandboxes
	}

	// Generate ID and workspace path while still holding the lock
	id := uuid.NewString()
	workspace := filepath.Join(d.cfg.SandboxRoot, id)
	if d.creating == nil {
		d.creating = map[string]bool{}
	}
	d.creating[id] = true
	defer func() {
		d.mu.Lock()
		delete(d.creating, id)
		d.mu.Unlock()
	}()

	// Release the lock before slow operations (container creation, directory setup)
	d.mu.Unlock()

	// 0770, not 0777: the sandbox container writes here as uid 1000 (node) and
	// this service reads it back, but nothing else on the host has any business
	// touching an in-flight workspace. The group bit is what the container needs;
	// world-writable was never required.
	if err := os.MkdirAll(workspace, 0o770); err != nil {
		return nil, false, err
	}
	if err := os.Chown(workspace, sandboxUID, sandboxGID); err != nil {
		log.Printf("warning: could not chown workspace %s to %d:%d: %v", workspace, sandboxUID, sandboxGID, err)
	}

	containerName := d.prefix() + id
	initCommand := `mkdir -p ` + SandboxTempDir + `; if [ ! -f /workspace/package.json ]; then \
		cp -r /opt/remotion-template/. /workspace/; \
	fi; sleep infinity`
	policy := d.containerPolicyArgs()
	args := []string{"run", "-d", "--name", containerName,
		// Lets a restarted executor re-adopt this container (AdoptExisting) instead of
		// purging it: a deploy must not silently end the TTL a finished run was promised.
		"--label", ExecutionLabel + "=" + workflowExecutionID,
		// ...but only while it still runs under the CURRENT isolation settings.
		"--label", ConfigLabel + "=" + d.configFingerprint(policy)}
	if d.cfg.Slot != "" {
		args = append(args, "--label", SlotLabel+"="+d.cfg.Slot)
	}
	args = append(args, policy...)
	args = append(args,
		"-v", workspace+":/workspace",
		"-w", "/workspace",
		"--entrypoint", "sh",
		d.cfg.SandboxImage,
		"-c", initCommand,
	)
	if out, err := d.run(context.Background(), args...); err != nil {
		log.Printf("failed to start sandbox container, output: %s, err: %v", string(out), err)
		// The daemon can leave a Created container behind; it would refuse this execution.
		_, _ = d.runBounded(handoffStopTimeout, "rm", "-f", containerName)
		_ = os.RemoveAll(workspace)
		d.removeOwner(id)
		return nil, false, fmt.Errorf("failed to start sandbox container: %w\noutput: %s", err, string(out))
	} else {
		log.Printf("sandbox container %s created for workflow %s", containerName, workflowExecutionID)
	}
	// The creator is the first holder, recorded always, so no later pass has to guess from a
	// label that a retired slot's name can be reused under.
	if d.cfg.Slot != "" {
		if err := d.writeOwner(id, d.cfg.Slot); err != nil {
			_, _ = d.runBounded(handoffStopTimeout, "rm", "-f", containerName)
			_ = os.RemoveAll(workspace)
			return nil, false, fmt.Errorf("could not record sandbox ownership: %w", err)
		}
	}

	// Wait for the container's init script to finish copying the Remotion template
	// (cp -a runs asynchronously inside the detached container).
	log.Printf("waiting for template init in sandbox %s (workflow %s)", containerName, workflowExecutionID)
	pkgJsonPath := filepath.Join(workspace, "package.json")
	initDeadline := time.Now().Add(60 * time.Second)
	for time.Now().Before(initDeadline) {
		if fileExists(pkgJsonPath) {
			break
		}
		time.Sleep(250 * time.Millisecond)
	}
	if !fileExists(pkgJsonPath) {
		log.Printf("sandbox init timed out for container %s, cleaning up", containerName)
		_, _ = d.run(context.Background(), "rm", "-f", containerName)
		_ = os.RemoveAll(workspace)
		d.removeOwner(id)
		return nil, false, fmt.Errorf("sandbox init timed out: package.json not found after 60s")
	}
	log.Printf("sandbox %s ready (workflow %s)", containerName, workflowExecutionID)

	now := time.Now().UTC()
	sb := &Sandbox{
		ID:                  id,
		WorkflowExecutionID: workflowExecutionID,
		ContainerName:       containerName,
		WorkspacePath:       workspace,
		CreatedAt:           now,
		LastActivity:        now,
	}

	// Re-acquire lock to register the sandbox
	d.mu.Lock()

	// Double-check that we're still below capacity and that the execution ID wasn't claimed
	// while we were creating the container
	if len(d.sandboxes) >= d.maxSandboxes {
		d.mu.Unlock()
		log.Printf("sandbox capacity exceeded while creating container for workflow %s, cleaning up", workflowExecutionID)
		_, _ = d.run(context.Background(), "rm", "-f", containerName)
		_ = os.RemoveAll(workspace)
		d.removeOwner(id)
		return nil, false, ErrTooManySandboxes
	}

	if existingID, ok := d.executionIndex[workflowExecutionID]; ok {
		// Another goroutine created the sandbox for this execution while we were building
		if existing, found := d.sandboxes[existingID]; found {
			existing.LastActivity = time.Now().UTC()
			copy := *existing
			d.mu.Unlock()
			log.Printf("sandbox already exists for workflow %s, cleaning up duplicate container %s", workflowExecutionID, containerName)
			_, _ = d.run(context.Background(), "rm", "-f", containerName)
			_ = os.RemoveAll(workspace)
			d.removeOwner(id)
			return &copy, false, nil
		}
	}

	d.sandboxes[id] = sb
	d.executionIndex[workflowExecutionID] = id
	d.mu.Unlock()
	return sb, true, nil
}

func (d *Driver) List() []*Sandbox {
	d.mu.RLock()
	defer d.mu.RUnlock()

	out := make([]*Sandbox, 0, len(d.sandboxes))
	for _, sb := range d.sandboxes {
		copy := *sb
		out = append(out, &copy)
	}
	return out
}

func (d *Driver) GetByExecution(workflowExecutionID string) (*Sandbox, error) {
	sb, _, err := d.acquire(workflowExecutionID, false)
	return sb, err
}

// acquire returns a copy of the execution's sandbox with its container
// running, and a release func the caller must call when done.
//
// It is the single place a stopped container is restarted, and it never starts
// one while a `docker stop` is still completing: the janitor (or a /complete
// call) marks the sandbox with a transition channel for the duration of the
// stop, and every caller that needs the container running waits on it. Without
// that, a restart racing the stop produced a container that came up and was
// then killed by the stop still in progress, and every later `docker exec`
// answered "container is not running".
//
// With forExec the sandbox is also counted as having an exec in flight, from
// before the container is started until release, so the janitor cannot stop
// it in between. LastActivity is touched on entry for the same reason.
func (d *Driver) acquire(workflowExecutionID string, forExec bool) (*Sandbox, func(), error) {
	if err := ValidateWorkflowExecutionID(workflowExecutionID); err != nil {
		return nil, nil, err
	}
	for {
		d.forgetIfTakenOver(workflowExecutionID)
		d.mu.Lock()
		sb, ok := d.lookupLocked(workflowExecutionID)
		if !ok || sb.deleting {
			d.mu.Unlock()
			return nil, nil, ErrNotFound
		}
		if ch := sb.transition; ch != nil {
			d.mu.Unlock()
			<-ch
			continue
		}
		sb.LastActivity = time.Now().UTC()
		release := func() {}
		if forExec {
			sb.inFlight++
			release = func() { d.endExec(sb) }
		}
		if !sb.IsStopped {
			copy := *sb
			d.mu.Unlock()
			return &copy, release, nil
		}
		sb.transition = make(chan struct{})
		d.mu.Unlock()

		log.Printf("restarting stopped sandbox container %s for workflow %s", sb.ContainerName, workflowExecutionID)
		if err := d.startContainer(sb); err != nil {
			release()
			return nil, nil, err
		}
		d.mu.RLock()
		copy := *sb
		d.mu.RUnlock()
		return &copy, release, nil
	}
}

// lookupLocked resolves an execution id to its sandbox. The caller holds d.mu. Callers first run
// forgetIfTakenOver, which does the owner-entry file I/O outside the lock.
func (d *Driver) lookupLocked(workflowExecutionID string) (*Sandbox, bool) {
	id, ok := d.executionIndex[workflowExecutionID]
	if !ok {
		return nil, false
	}
	sb, ok := d.sandboxes[id]
	return sb, ok
}

// endExec records the end of an exec and counts it as activity, so the
// inactivity window restarts from when the command finished.
func (d *Driver) endExec(sb *Sandbox) {
	d.mu.Lock()
	if sb.inFlight > 0 {
		sb.inFlight--
	}
	sb.LastActivity = time.Now().UTC()
	d.mu.Unlock()
}

// startContainer runs `docker start` for a sandbox whose transition channel the
// caller has already set, then clears IsStopped on success and releases every
// waiter. A failed start leaves IsStopped set so the next caller retries.
func (d *Driver) startContainer(sb *Sandbox) error {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	out, err := d.run(ctx, "start", sb.ContainerName)

	d.mu.Lock()
	if err == nil {
		sb.IsStopped = false
	}
	ch := sb.transition
	sb.transition = nil
	d.mu.Unlock()
	if ch != nil {
		close(ch)
	}
	if err != nil {
		log.Printf("failed to start container %s: %v: %s", sb.ContainerName, err, strings.TrimSpace(string(out)))
		return fmt.Errorf("%w and could not be restarted: %w", ErrSandboxNotRunning, err)
	}
	return nil
}

// stopContainer runs `docker stop` for a sandbox the caller has already marked
// IsStopped with a transition channel, and releases the waiters once it returns.
func (d *Driver) stopContainer(sb *Sandbox) error {
	_, err := d.run(context.Background(), "stop", sb.ContainerName)

	d.mu.Lock()
	ch := sb.transition
	sb.transition = nil
	d.mu.Unlock()
	if ch != nil {
		close(ch)
	}
	return err
}

// revive is the defence in depth behind acquire: a `docker exec` answered "is
// not running", so whatever state the driver believed, the container is down.
// It marks the sandbox stopped and starts it again (or waits for whoever is
// already doing that). The command that failed did not run, so the caller may
// retry it once.
func (d *Driver) revive(workflowExecutionID string) error {
	d.forgetIfTakenOver(workflowExecutionID)
	d.mu.Lock()
	sb, ok := d.lookupLocked(workflowExecutionID)
	if !ok || sb.deleting {
		d.mu.Unlock()
		return ErrNotFound
	}
	if ch := sb.transition; ch != nil {
		d.mu.Unlock()
		<-ch
		return nil
	}
	sb.IsStopped = true
	sb.transition = make(chan struct{})
	d.mu.Unlock()
	log.Printf("sandbox container %s is not running; restarting it", sb.ContainerName)
	return d.startContainer(sb)
}

func (d *Driver) Touch(workflowExecutionID string) {
	d.forgetIfTakenOver(workflowExecutionID)
	d.mu.Lock()
	if sb, found := d.lookupLocked(workflowExecutionID); found {
		sb.LastActivity = time.Now().UTC()
	}
	d.mu.Unlock()
}

func (d *Driver) deleteInternal(id, workflowExecutionID, containerName, workspace string) error {
	log.Printf("deleting sandbox container %s and workspace %s", containerName, workspace)
	if out, err := d.run(context.Background(), "rm", "-f", containerName); err != nil {
		log.Printf("error deleting container %s: %v, output: %s", containerName, err, string(out))
	}
	if err := os.RemoveAll(workspace); err != nil {
		log.Printf("error removing workspace %s: %v", workspace, err)
		// Still registered: let the next sweep or call see it again.
		d.mu.Lock()
		if sb, ok := d.sandboxes[id]; ok {
			sb.deleting = false
		}
		d.mu.Unlock()
		return err
	}

	d.removeOwner(id)
	d.mu.Lock()
	delete(d.sandboxes, id)
	// Only delete the execution index entry if it still points to this sandbox ID.
	// This prevents a race condition where another goroutine created a new container
	// for the same execution ID after this one was created but before cleanup completed.
	if d.executionIndex[workflowExecutionID] == id {
		delete(d.executionIndex, workflowExecutionID)
	}
	d.mu.Unlock()
	return nil
}

func (d *Driver) StopByExecution(workflowExecutionID string) error {
	var sb *Sandbox
	for {
		d.forgetIfTakenOver(workflowExecutionID)
		d.mu.Lock()
		found, ok := d.lookupLocked(workflowExecutionID)
		if !ok {
			d.mu.Unlock()
			return ErrNotFound
		}
		if ch := found.transition; ch != nil {
			// A stop or a start is already running; see where it ends up.
			d.mu.Unlock()
			<-ch
			continue
		}
		if found.IsStopped {
			d.mu.Unlock()
			return nil
		}
		found.IsStopped = true
		found.transition = make(chan struct{})
		sb = found
		d.mu.Unlock()
		break
	}

	log.Printf("stopping sandbox container %s for workflow %s", sb.ContainerName, workflowExecutionID)
	return d.stopContainer(sb)
}

// DeleteByExecution stops and removes the sandbox's container and its
// workspace.
//
// StopByExecution only pauses a container so the TTL sweep can remove it later.
// A runner session that is over wants its workspace gone now: it holds
// model-authored code and the customer's media, and nothing will read it again.
func (d *Driver) DeleteByExecution(workflowExecutionID string) error {
	if err := ValidateWorkflowExecutionID(workflowExecutionID); err != nil {
		return err
	}
	d.forgetIfTakenOver(workflowExecutionID)
	d.mu.Lock()
	sb, found := d.lookupLocked(workflowExecutionID)
	if !found {
		d.mu.Unlock()
		return ErrNotFound
	}
	sb.deleting = true
	id, containerName, workspace := sb.ID, sb.ContainerName, sb.WorkspacePath
	d.mu.Unlock()
	return d.deleteInternal(id, workflowExecutionID, containerName, workspace)
}

func (d *Driver) StartJanitor(ctx context.Context) {
	// run more frequently than the TTL so containers are removed within a
	// minute even when the TTL is small. default tick interval is 30s.
	interval := 30 * time.Second
	if d.cfg.SandboxTTL > 0 && d.cfg.SandboxTTL < interval {
		interval = d.cfg.SandboxTTL / 2
		if interval < 10*time.Second {
			interval = 10 * time.Second
		}
	}
	ticker := time.NewTicker(interval)
	go func() {
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				d.cleanupInactive()
			case <-ctx.Done():
				return
			}
		}
	}()
}

// inactivityStopAfter is how long a sandbox with nothing running in it may sit
// idle before the janitor stops (not deletes) its container. SandboxTTL is the
// separate, longer deadline for removing it.
const inactivityStopAfter = 5 * time.Minute

// cleanupInactive stops sandboxes idle for inactivityStopAfter and removes
// those idle past SandboxTTL. A sandbox with an exec in flight is never
// touched, however stale its LastActivity: LastActivity is only refreshed when
// an exec starts and ends, and a render takes longer than the idle window.
func (d *Driver) cleanupInactive() {
	now := time.Now().UTC()
	toDelete := make([]Sandbox, 0)
	toStop := make([]*Sandbox, 0)

	d.dropTakenOver()
	d.mu.Lock()
	for _, sb := range d.sandboxes {
		if sb.inFlight > 0 || sb.deleting || sb.transition != nil {
			continue
		}
		idle := now.Sub(sb.LastActivity)
		if idle > d.cfg.SandboxTTL {
			sb.deleting = true
			toDelete = append(toDelete, *sb)
		} else if idle > inactivityStopAfter && !sb.IsStopped {
			sb.IsStopped = true
			sb.transition = make(chan struct{})
			toStop = append(toStop, sb)
		}
	}
	d.mu.Unlock()

	for _, sb := range toStop {
		log.Printf("janitor stopping inactive sandbox %s (workflow %s)", sb.ID, sb.WorkflowExecutionID)
		if err := d.stopContainer(sb); err != nil {
			log.Printf("janitor failed to stop sandbox %s: %v", sb.ID, err)
		}
	}

	if d.janitorHook != nil {
		d.janitorHook()
	}
	for _, sb := range toDelete {
		// A takeover can land after the scan above (during the stop loop): re-check the entry right
		// before deleting and forget the sandbox without touching it if another slot holds it now.
		if d.ownedElsewhere(sb.ID) {
			log.Printf("sandbox %s was taken over during the janitor pass; dropping it from the index", sb.ID)
			d.mu.Lock()
			delete(d.sandboxes, sb.ID)
			if d.executionIndex[sb.WorkflowExecutionID] == sb.ID {
				delete(d.executionIndex, sb.WorkflowExecutionID)
			}
			d.mu.Unlock()
			continue
		}
		log.Printf("janitor cleaning up sandbox %s (workflow %s)", sb.ID, sb.WorkflowExecutionID)
		if err := d.deleteInternal(sb.ID, sb.WorkflowExecutionID, sb.ContainerName, sb.WorkspacePath); err != nil {
			log.Printf("janitor failed to cleanup sandbox %s: %v", sb.ID, err)
		}
	}
}
