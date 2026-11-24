package deviceapi

import (
	"encoding/json"
	"fmt"
	"time"
)

// Me is GET /api/v1/device/me.
type Me struct {
	DeviceID         string `json:"deviceId"`
	DeviceName       string `json:"deviceName"`
	OrganizationID   string `json:"organizationId"`
	OrganizationName string `json:"organizationName"`
	PairedByEmail    string `json:"pairedByEmail"`
}

// Project is one entry of GET /api/v1/device/projects.
type Project struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

// Pause reasons the server reports.
const (
	PausedByGuard = "guard"
	PausedByUser  = "user"
)

// Link is one of this device's folder links, as the server sees it.
type Link struct {
	ID               string     `json:"id"`
	ProjectID        string     `json:"projectId"`
	ProjectName      string     `json:"projectName"`
	TargetFolder     string     `json:"targetFolder"`
	DisplayPath      string     `json:"displayPath"`
	Paused           bool       `json:"paused"`
	PausedReason     string     `json:"pausedReason,omitempty"`
	FileCount        int        `json:"fileCount"`
	LastReconciledAt *time.Time `json:"lastReconciledAt,omitempty"`
	LastError        string     `json:"lastError,omitempty"`
}

// CreateLinkRequest is POST /api/v1/device/folder-links. DisplayPath is what
// the dashboard shows; it is the only form in which the local path leaves the
// machine.
type CreateLinkRequest struct {
	ProjectID    string `json:"projectId"`
	TargetFolder string `json:"targetFolder,omitempty"`
	DisplayPath  string `json:"displayPath"`
}

// ManifestFile is one file of a manifest.
type ManifestFile struct {
	Path   string `json:"path"`
	Size   int64  `json:"size"`
	SHA256 string `json:"sha256"`
}

// ManifestBatch is one POST …/manifest. Batches are 0-based; the final one is
// the highest number.
type ManifestBatch struct {
	ManifestID         string         `json:"manifestId"`
	Batch              int            `json:"batch"`
	Final              bool           `json:"final"`
	ConfirmLargeDelete bool           `json:"confirmLargeDelete,omitempty"`
	Files              []ManifestFile `json:"files"`
}

// SkippedPath is a path the server will not take, and why ("occupied",
// "invalid_path", "duplicate_path").
type SkippedPath struct {
	Path   string `json:"path"`
	Reason string `json:"reason"`
}

// Storage is the workspace's storage use. LimitBytes nil means unlimited.
type Storage struct {
	UsedBytes  int64  `json:"usedBytes"`
	LimitBytes *int64 `json:"limitBytes"`
}

// ManifestResult is the final batch's answer. Non-final batches answer
// Accepted only.
type ManifestResult struct {
	Accepted     int           `json:"accepted,omitempty"`
	Upload       []string      `json:"upload"`
	Delete       []string      `json:"delete"`
	Skipped      []SkippedPath `json:"skipped"`
	Paused       bool          `json:"paused"`
	PausedReason string        `json:"pausedReason,omitempty"`
	LastError    string        `json:"lastError,omitempty"`
	Storage      *Storage      `json:"storage,omitempty"`
}

// UploadRequest is POST …/uploads.
type UploadRequest struct {
	Path        string `json:"path"`
	Size        int64  `json:"size"`
	SHA256      string `json:"sha256"`
	ContentType string `json:"contentType,omitempty"`
}

// Upload modes.
const (
	ModeSinglePut = "singlePut"
	ModeMultipart = "multipart"
)

// Ticket is an open upload. Raw is the object exactly as the server sent it:
// it is echoed back whole on parts/complete/abort, so a field this client does
// not know about still reaches the server's MAC check unchanged.
type Ticket struct {
	Raw json.RawMessage `json:"-"`

	Mode          string            `json:"-"`
	FileID        string            `json:"fileId"`
	SizeBytes     int64             `json:"sizeBytes"`
	ExpiresAtUnix int64             `json:"expiresAtUnix"`
	URL           string            `json:"url,omitempty"`
	UploadID      string            `json:"uploadId,omitempty"`
	PartSizeBytes int64             `json:"partSizeBytes"`
	PartCount     int               `json:"partCount"`
	Headers       map[string]string `json:"headers,omitempty"`
}

// UnmarshalJSON keeps the raw bytes and accepts mode as a string or a number.
func (t *Ticket) UnmarshalJSON(b []byte) error {
	type plain Ticket
	var p plain
	if err := json.Unmarshal(b, &p); err != nil {
		return err
	}
	var m struct {
		Mode json.RawMessage `json:"mode"`
	}
	if err := json.Unmarshal(b, &m); err != nil {
		return err
	}
	*t = Ticket(p)
	t.Raw = append(json.RawMessage(nil), b...)
	switch string(m.Mode) {
	case `"singlePut"`, `"SinglePut"`, `0`:
		t.Mode = ModeSinglePut
	case `"multipart"`, `"Multipart"`, `1`:
		t.Mode = ModeMultipart
	default:
		return fmt.Errorf("deviceapi: unknown upload mode %s", string(m.Mode))
	}
	return nil
}

// MarshalJSON echoes the ticket exactly as received.
func (t Ticket) MarshalJSON() ([]byte, error) {
	if len(t.Raw) == 0 {
		return nil, fmt.Errorf("deviceapi: a ticket can only be echoed as the server sent it")
	}
	return t.Raw, nil
}

// PartURL is one presigned part URL. URL is a credential: never log it.
type PartURL struct {
	PartNumber    int    `json:"partNumber"`
	URL           string `json:"url"`
	ExpiresAtUnix int64  `json:"expiresAtUnix"`
}

// CompletedPart is one part's number and the ETag storage returned for it.
type CompletedPart struct {
	PartNumber int    `json:"partNumber"`
	ETag       string `json:"etag"`
}

// Completed is POST …/uploads/complete's answer.
type Completed struct {
	FileID string `json:"fileId"`
	Path   string `json:"path"`
}

// MaxPartsPerPresign is the server's limit on part numbers per call.
const MaxPartsPerPresign = 100

// MaxManifestFiles is the server's limit on files per manifest batch.
const MaxManifestFiles = 2000

// MaxDeletePaths is the server's limit on paths per delete call.
const MaxDeletePaths = 1000

// MaxBodyBytes is the server's request-body limit.
const MaxBodyBytes = 4 << 20
