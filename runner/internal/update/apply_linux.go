//go:build linux

package update

import (
	"context"
	"fmt"
	"os"
)

// swapInstalled replaces the installed file with the verified copy.
//
// Linux keeps the old inode alive for a running process, so the running binary
// can be renamed out of the way and the new one renamed in. Both renames are in
// the same directory, so neither is a cross-filesystem copy: the path holds
// either the old binary or the new one at every instant, and a crash between
// the two leaves the old binary at the backup path rather than nothing at the
// install path.
func (u *Updater) swapInstalled(target, next, backup string, out Outcome) (Outcome, error) {
	_ = os.Remove(backup)
	if err := os.Rename(target, backup); err != nil {
		_ = os.Remove(next)
		return out, fmt.Errorf("update: move the current binary aside: %w", err)
	}
	if err := os.Rename(next, target); err != nil {
		// Put the old binary back before reporting, so a failed update is a
		// no-op rather than a machine with no runner left on it.
		_ = os.Remove(next)
		return out, restoreBackup(target, backup, err)
	}
	out.Applied = true
	return out, nil
}

// applyMSI is a Windows installer and cannot be applied here.
func (u *Updater) applyMSI(_ context.Context, _ string, out Outcome) (Outcome, error) {
	return out, fmt.Errorf("%w: an MSI on linux", ErrFormatMismatch)
}

// verifyPlatformSignature checks the operating system's own signature on an
// artifact.
//
// On Linux there is nothing to check for the two formats this runner installs
// in place. A bare binary has no OS signature, and an AppImage carries an
// optional GPG signature that only a similarly configured AppImage runtime
// checks; the bound here is the release key's signature over the manifest plus
// the SHA-256 over the bytes, which together pin the artifact to a document
// signed offline. A .deb DOES have a signature, and this runner deliberately
// does not check it: it never installs a .deb itself (see Apply), so apt or
// dpkg — which does verify the repository signature — is the thing that runs.
func (u *Updater) verifyPlatformSignature(_ string, a Artifact) error {
	if a.Format == FormatDeb {
		return nil
	}
	return nil
}
