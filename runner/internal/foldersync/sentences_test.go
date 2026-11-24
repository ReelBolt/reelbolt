package foldersync

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
)

func TestProblemSentencesArePlain(t *testing.T) {
	cases := []struct {
		err  error
		want string
	}{
		{fmt.Errorf("upload: %w", deviceapi.ErrNotConnected), disconnectedSentence},
		{deviceapi.ErrOwnerLacksAccess, "The person who paired this computer can't edit this project any more."},
		{deviceapi.ErrLinkNotFound, unlinkedSentence},
		{&fs.PathError{Op: "open", Path: "/home/ann/Footage/a.mov", Err: fs.ErrPermission}, "A file in this folder couldn't be opened. Check that you can open it, and it will be tried again."},
		{&net.OpError{Op: "dial", Net: "tcp", Err: errors.New("connection refused")}, "Couldn't reach ReelBolt. It will try again when the connection is back."},
		{context.DeadlineExceeded, "Couldn't reach ReelBolt. It will try again when the connection is back."},
		{errors.New("unexpected end of JSON input"), "Something went wrong while copying this folder. It will try again on the next change."},
	}
	for _, tc := range cases {
		got := ProblemSentence(tc.err)
		if got != tc.want {
			t.Errorf("ProblemSentence(%v) = %q, want %q", tc.err, got, tc.want)
		}
		if strings.Contains(got, "/home/") || strings.Contains(got, "—") {
			t.Errorf("%q quotes a path or uses an em-dash", got)
		}
	}
	if ProblemSentence(nil) != "" {
		t.Error("no error has a sentence")
	}
}
