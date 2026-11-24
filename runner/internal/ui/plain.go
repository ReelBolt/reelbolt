package ui

import (
	"errors"
	"fmt"
)

// plainError is an error whose text is a whole sentence for a person: it
// starts with a capital and ends with a full stop, because the window shows it
// verbatim. It exists so those sentences are not written through errors.New,
// whose convention (and the linters that enforce it) is the opposite.
type plainError struct {
	msg   string
	cause error
}

func (e *plainError) Error() string { return e.msg }
func (e *plainError) Unwrap() error { return e.cause }

// say is a sentence for the window, as an error.
func say(msg string) error { return &plainError{msg: msg} }

// sayf is say with formatting.
func sayf(format string, args ...any) error { return &plainError{msg: fmt.Sprintf(format, args...)} }

// sayWrap is a sentence for the window that still unwraps to the cause, so a
// caller can errors.Is it while the person reads only the sentence.
func sayWrap(msg string, cause error) error { return &plainError{msg: msg, cause: cause} }

// Sentence is say for a host: an error whose text the window shows verbatim,
// so it should be one short, plain sentence a person can act on.
func Sentence(msg string) error { return say(msg) }

// isPlain reports whether err is already a sentence for a person.
func isPlain(err error) bool {
	var p *plainError
	return errors.As(err, &p)
}
