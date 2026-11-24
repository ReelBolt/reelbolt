package foldersync

import (
	"errors"
	"io/fs"
	"net"
	"os"

	"github.com/vecchiotom/reelbolt/runner/internal/deviceapi"
)

// The sentences a person reads about a folder. The Folders screen, the tray
// and "folders list" show them verbatim, so each one says what happened and
// what to do, in words a creator uses; the error behind it goes to the log.

const (
	// disconnectedSentence is every link's reason when ReelBolt refused this
	// computer (a 401).
	disconnectedSentence = "This computer isn't connected to ReelBolt any more. Pair it again to keep syncing."
	// unlinkedSentence is a link ReelBolt no longer has.
	unlinkedSentence = "This folder was removed from its project in ReelBolt, so it isn't synced any more."
)

// ProblemSentence turns an error from one sync attempt into the sentence shown
// for the folder. The attempt is repeated on the next change or check either
// way, which most sentences say, because "it will try again" is the answer to
// "do I have to do something".
func ProblemSentence(err error) string {
	var netErr net.Error
	switch {
	case err == nil:
		return ""
	case errors.Is(err, deviceapi.ErrNotConnected):
		return disconnectedSentence
	case errors.Is(err, deviceapi.ErrOwnerLacksAccess):
		return "The person who paired this computer can't edit this project any more."
	case errors.Is(err, deviceapi.ErrLinkNotFound):
		return unlinkedSentence
	case errors.Is(err, deviceapi.ErrStorageFull):
		return deviceapi.StorageSentence(err) + "."
	case errors.Is(err, deviceapi.ErrInvalidPath):
		return "A file name isn't allowed in a project. Rename it on this computer."
	case errors.Is(err, deviceapi.ErrOccupied):
		return "Another file in the project already has the same name as a file in this folder."
	case errors.Is(err, fs.ErrPermission):
		return "A file in this folder couldn't be opened. Check that you can open it, and it will be tried again."
	case errors.Is(err, fs.ErrNotExist):
		return "A file disappeared while it was being copied. It will be checked again."
	case errors.Is(err, os.ErrDeadlineExceeded), errors.As(err, &netErr):
		return "Couldn't reach ReelBolt. It will try again when the connection is back."
	default:
		return "Something went wrong while copying this folder. It will try again on the next change."
	}
}
