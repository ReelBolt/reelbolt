package deviceapi

import (
	"errors"
	"fmt"
	"net/http"
)

// Sentinel errors a caller tests with errors.Is. An *Error matches the one
// that fits its status and code.
var (
	// ErrNotConnected is any 401: the server no longer accepts this device
	// (revoked, unknown, clock far off, or a bad signature — the server never
	// says which). Folder sync stops and asks the person to pair again.
	ErrNotConnected = errors.New("this computer is no longer connected to ReelBolt — pair it again")
	// ErrOwnerLacksAccess is 403 device_owner_lacks_access: the person who
	// paired this computer left the workspace or can no longer edit the project.
	ErrOwnerLacksAccess = errors.New("the person who paired this computer can no longer edit this project")
	// ErrLinkNotFound is 404 link_not_found: the link was removed in ReelBolt.
	ErrLinkNotFound = errors.New("this folder was unlinked in ReelBolt")
	// ErrNotFound is any other 404 (a project, a route this server lacks).
	ErrNotFound = errors.New("not found")
	// ErrUnchanged is 409 unchanged: the project already has this content.
	ErrUnchanged = errors.New("unchanged")
	// ErrLinkPaused is 409 link_paused.
	ErrLinkPaused = errors.New("paused from the ReelBolt dashboard — resume it there")
	// ErrOccupied is 409 occupied: a file from another source holds the path.
	ErrOccupied = errors.New("another file in the project already has this name")
	// ErrManifestIncomplete is 409 manifest_incomplete: start a new manifest.
	ErrManifestIncomplete = errors.New("manifest incomplete")
	// ErrStorageFull is 413 STORAGE_QUOTA_EXCEEDED.
	ErrStorageFull = errors.New("storage is full")
	// ErrInvalidPath is 400 invalid_path / INVALID_PATH.
	ErrInvalidPath = errors.New("the name is not allowed in a project")
	// ErrTargetTaken is 409 target_folder_taken.
	ErrTargetTaken = errors.New("that folder name is already used in the project")
	// ErrInvalidTarget is 400 invalid_target_folder.
	ErrInvalidTarget = errors.New("that folder name is not allowed")
)

// Error is a non-2xx answer. Message is the server's plain sentence, which is
// written for people and carries no secret.
type Error struct {
	Method     string
	Route      string // the path WITHOUT a query, for logs
	Status     int
	Code       string
	Message    string
	UsedBytes  *int64
	LimitBytes *int64
	// Unlimited is set when the server sent limitBytes: null.
	Unlimited bool
}

func (e *Error) Error() string {
	s := fmt.Sprintf("%s %s: HTTP %d", e.Method, e.Route, e.Status)
	if e.Code != "" {
		s += " " + e.Code
	}
	if e.Message != "" {
		s += ": " + e.Message
	}
	return s
}

// Is maps the answer onto the sentinels.
func (e *Error) Is(target error) bool {
	switch target {
	case ErrNotConnected:
		return e.Status == http.StatusUnauthorized
	case ErrOwnerLacksAccess:
		return e.Status == http.StatusForbidden && e.Code == "device_owner_lacks_access"
	case ErrLinkNotFound:
		return e.Status == http.StatusNotFound && e.Code == "link_not_found"
	case ErrNotFound:
		return e.Status == http.StatusNotFound
	case ErrUnchanged:
		return e.Status == http.StatusConflict && e.Code == "unchanged"
	case ErrLinkPaused:
		return e.Status == http.StatusConflict && e.Code == "link_paused"
	case ErrOccupied:
		return e.Status == http.StatusConflict && e.Code == "occupied"
	case ErrManifestIncomplete:
		return e.Status == http.StatusConflict && e.Code == "manifest_incomplete"
	case ErrStorageFull:
		return e.Status == http.StatusRequestEntityTooLarge && e.Code == "STORAGE_QUOTA_EXCEEDED"
	case ErrInvalidPath:
		return e.Status == http.StatusBadRequest && (e.Code == "invalid_path" || e.Code == "INVALID_PATH")
	case ErrTargetTaken:
		return e.Status == http.StatusConflict && e.Code == "target_folder_taken"
	case ErrInvalidTarget:
		return e.Status == http.StatusBadRequest && e.Code == "invalid_target_folder"
	}
	return false
}

// Temporary reports whether retrying the same request later may succeed: a
// server error, a rate limit, or a gateway in front of the server.
func (e *Error) Temporary() bool {
	return e.Status >= 500 || e.Status == http.StatusTooManyRequests || e.Status == http.StatusRequestTimeout
}

// StorageSentence is "Storage is full (X of Y used)" for a 413, in the words
// the design gives.
func StorageSentence(err error) string {
	var e *Error
	if !errors.As(err, &e) || e.UsedBytes == nil {
		return "Storage is full"
	}
	if e.LimitBytes == nil {
		return fmt.Sprintf("Storage is full (%s used)", HumanBytes(*e.UsedBytes))
	}
	return fmt.Sprintf("Storage is full (%s of %s used)", HumanBytes(*e.UsedBytes), HumanBytes(*e.LimitBytes))
}

// HumanBytes renders a byte count the way the dashboard does ("4.2 GB").
func HumanBytes(n int64) string {
	const unit = 1000
	if n < unit {
		return fmt.Sprintf("%d B", n)
	}
	units := []string{"KB", "MB", "GB", "TB", "PB"}
	v := float64(n)
	i := -1
	for v >= unit && i < len(units)-1 {
		v /= unit
		i++
	}
	return fmt.Sprintf("%.1f %s", v, units[i])
}
