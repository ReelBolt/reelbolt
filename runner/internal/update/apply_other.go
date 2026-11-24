//go:build !linux && !windows

package update

import (
	"context"
	"fmt"
)

// D26 ships a Windows x64 and Linux x64/arm64 runner and no macOS runner, so
// there is no macOS self-update path to write: no notarised .app to swap, no
// codesign/spctl step, no LaunchAgent to restart. This file exists so that a
// build for any other platform fails closed and says why, rather than compiling
// a self-update that silently does nothing.
func platformUnsupported() error {
	return fmt.Errorf("%w: this build has no self-update path (D26 ships windows/amd64, linux/amd64 and linux/arm64)",
		ErrUnsupportedPlatform)
}

// swapInstalled refuses on a platform with no in-place install.
func (u *Updater) swapInstalled(_, _, _ string, out Outcome) (Outcome, error) {
	return out, platformUnsupported()
}

// applyMSI refuses on a platform with no Windows installer.
func (u *Updater) applyMSI(_ context.Context, _ string, out Outcome) (Outcome, error) {
	return out, platformUnsupported()
}

// verifyPlatformSignature refuses on a platform this build does not install to.
func (u *Updater) verifyPlatformSignature(_ string, _ Artifact) error {
	return platformUnsupported()
}
