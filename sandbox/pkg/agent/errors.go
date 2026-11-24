package agent

import (
	"errors"
	"fmt"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Terminal errors. A Run that ends with one of these will not be retried,
// because retrying cannot fix it: the identity or the protocol version is
// wrong, or another connection took the socket.
var (
	// ErrStopped is returned once Shutdown has been called.
	ErrStopped = errors.New("agent: shut down")
	// ErrUnsupportedVersion is a 4400: this agent and that gateway do not speak
	// the same protocol.
	ErrUnsupportedVersion = errors.New("agent: unsupported protocol version")
	// ErrUnauthenticated is a 4401: the device is unknown, revoked, or sent a
	// signature that does not verify.
	ErrUnauthenticated = errors.New("agent: unauthenticated")
	// ErrForbidden is a 4403: the identity is real but may not be here.
	ErrForbidden = errors.New("agent: forbidden")
	// ErrSuperseded is a 4409: a newer connection from the same device took the
	// socket. The displaced runner stops instead of fighting for it.
	ErrSuperseded = errors.New("agent: superseded by a newer connection")
	// ErrRefused is a refusal with no code the library recognises.
	ErrRefused = errors.New("agent: refused by the gateway")
)

// terminalError wraps an error that must end the reconnect loop.
type terminalError struct{ err error }

func (e *terminalError) Error() string { return e.err.Error() }
func (e *terminalError) Unwrap() error { return e.err }

// IsTerminal reports whether err came from a refusal that will not change by
// reconnecting.
func IsTerminal(err error) bool {
	var t *terminalError
	return errors.As(err, &t) || errors.Is(err, ErrStopped)
}

// terminal marks an error as ending the loop.
func terminal(err error) error {
	if err == nil {
		return nil
	}
	return &terminalError{err: err}
}

// retryableClose reports whether a close code is worth reconnecting through.
//
// 4400, 4401, 4403 and 4409 all mean "this identity may not be here, or is not
// the current one": retrying is what the nonce cache, the revocation check and
// the supersede rule exist to stop, so the library does not.
func retryableClose(code protocol.CloseCode) bool {
	switch code {
	case protocol.CloseUnsupportedVersion, protocol.CloseUnauthenticated,
		protocol.CloseForbidden, protocol.CloseSuperseded:
		return false
	case protocol.CloseMessageTooLarge:
		// Our own framing bug: retrying would repeat it.
		return false
	case protocol.CloseNormal:
		// The peer said goodbye. Reconnecting immediately would be rude and is
		// never what a normal close means.
		return false
	}
	return true
}

// errorForClose maps a close code to the error a caller sees.
func errorForClose(code protocol.CloseCode, reason string) error {
	var base error
	term := true
	switch code {
	case protocol.CloseUnsupportedVersion:
		base = ErrUnsupportedVersion
	case protocol.CloseUnauthenticated:
		base = ErrUnauthenticated
	case protocol.CloseForbidden:
		base = ErrForbidden
	case protocol.CloseSuperseded:
		base = ErrSuperseded
	case protocol.CloseHeartbeatTimeout:
		base, term = ErrRefused, false
	case protocol.CloseGoingAway:
		base, term = ErrRefused, false
	case protocol.CloseRateLimited:
		base, term = ErrRefused, false
	case protocol.CloseInternal:
		base, term = ErrRefused, false
	case protocol.CloseNormal:
		base, term = ErrRefused, false
	default:
		base, term = ErrRefused, false
	}
	err := fmt.Errorf("%w (close %d: %s)", base, int(code), reason)
	if term || !retryableClose(code) {
		return terminal(err)
	}
	return err
}

// OpError builds the payload error the protocol defines. Sessions return these
// instead of an error so the in-tree codes stay the specification's.
func OpError(code, message string, retryable bool) *protocol.OpError {
	return &protocol.OpError{Code: code, Message: message, Retryable: retryable}
}
