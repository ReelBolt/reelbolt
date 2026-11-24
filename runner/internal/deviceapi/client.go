package deviceapi

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// Client calls the device routes as one device.
type Client struct {
	// BaseURL is the origin the device was paired at (config.APIBaseURL,
	// already checked against the allowlist).
	BaseURL string
	Signer  Signer
	// HTTP performs API requests. Nil means a client with a 60 s timeout.
	HTTP *http.Client
	// Storage performs presigned PUTs to object storage. Nil means a client
	// with no overall timeout (a part can be large); a stalled connection is
	// still cut by the transport's idle timeouts.
	Storage *http.Client

	// pauseMu guards pauseWhy, the last pause reason each link reported.
	pauseMu  sync.Mutex
	pauseWhy map[string]string
}

// New builds a client for one device.
func New(baseURL, deviceID string, key ed25519.PrivateKey) *Client {
	return &Client{BaseURL: strings.TrimRight(baseURL, "/"), Signer: Signer{DeviceID: deviceID, Key: key}}
}

func (c *Client) api() *http.Client {
	if c.HTTP != nil {
		return c.HTTP
	}
	return &http.Client{Timeout: 60 * time.Second}
}

func (c *Client) storage() *http.Client {
	if c.Storage != nil {
		return c.Storage
	}
	return &http.Client{Transport: &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		ResponseHeaderTimeout: 2 * time.Minute,
		IdleConnTimeout:       90 * time.Second,
		TLSHandshakeTimeout:   30 * time.Second,
		MaxIdleConnsPerHost:   4,
	}}
}

// Me is GET /api/v1/device/me.
func (c *Client) Me(ctx context.Context) (Me, error) {
	var out Me
	err := c.do(ctx, http.MethodGet, "/api/v1/device/me", nil, &out)
	return out, err
}

// ForgetMe is DELETE /api/v1/device/me: this computer removes itself from its
// workspace, which is what "Unpair" does on the server. The device is revoked
// exactly as the dashboard's removal revokes it - it leaves the workspace's
// device list, every request it signs afterwards is refused, and the gateway
// drops its connection within a minute - while its folder links and the files
// they synced stay where they are.
//
// nil means the server removed it. Two errors are worth telling apart, both by
// errors.Is:
//
//   - ErrNotConnected (401): the server no longer accepts this device. After a
//     first attempt whose answer was lost that means it is ALREADY removed; it
//     can also mean the clock is far off, so a caller that wants certainty
//     says "if it still appears in the dashboard, remove it there".
//   - ErrNotFound (404): this server predates the route. The device was NOT
//     removed and must be removed from the dashboard.
//
// Anything else (a network error, a 5xx) means nothing is known to have
// changed; the local unpair can go ahead regardless, since the key is the
// device's and the server's record is only a list entry.
func (c *Client) ForgetMe(ctx context.Context) error {
	return c.do(ctx, http.MethodDelete, "/api/v1/device/me", nil, nil)
}

// Projects is GET /api/v1/device/projects.
func (c *Client) Projects(ctx context.Context) ([]Project, error) {
	var out []Project
	err := c.do(ctx, http.MethodGet, "/api/v1/device/projects", nil, &out)
	return out, err
}

// Links is GET /api/v1/device/folder-links.
func (c *Client) Links(ctx context.Context) ([]Link, error) {
	var out []Link
	err := c.do(ctx, http.MethodGet, "/api/v1/device/folder-links", nil, &out)
	return out, err
}

// CreateLink is POST /api/v1/device/folder-links.
func (c *Client) CreateLink(ctx context.Context, req CreateLinkRequest) (Link, error) {
	var out Link
	err := c.do(ctx, http.MethodPost, "/api/v1/device/folder-links", req, &out)
	return out, err
}

// DeleteLink is DELETE /api/v1/device/folder-links/{id}. Files stay in the project.
func (c *Client) DeleteLink(ctx context.Context, linkID string) error {
	return c.do(ctx, http.MethodDelete, linkPath(linkID, ""), nil, nil)
}

// Manifest posts one batch. A non-final batch's result carries only Accepted.
func (c *Client) Manifest(ctx context.Context, linkID string, batch ManifestBatch) (ManifestResult, error) {
	if batch.Files == nil {
		batch.Files = []ManifestFile{}
	}
	var out ManifestResult
	err := c.do(ctx, http.MethodPost, linkPath(linkID, "/manifest"), batch, &out)
	if err == nil && out.Paused {
		c.recordPause(linkID, out.PausedReason)
	}
	return out, err
}

// CreateUpload opens an upload. ErrUnchanged means there is nothing to send.
func (c *Client) CreateUpload(ctx context.Context, linkID string, req UploadRequest) (Ticket, error) {
	var out Ticket
	err := c.do(ctx, http.MethodPost, linkPath(linkID, "/uploads"), req, &out)
	return out, err
}

// PresignParts returns presigned URLs for up to MaxPartsPerPresign parts.
func (c *Client) PresignParts(ctx context.Context, linkID string, t Ticket, partNumbers []int) ([]PartURL, error) {
	if len(partNumbers) > MaxPartsPerPresign {
		return nil, fmt.Errorf("deviceapi: at most %d parts per call", MaxPartsPerPresign)
	}
	body := struct {
		Ticket      Ticket `json:"ticket"`
		PartNumbers []int  `json:"partNumbers"`
	}{t, partNumbers}
	var out []PartURL
	err := c.do(ctx, http.MethodPost, linkPath(linkID, "/uploads/parts"), body, &out)
	return out, err
}

// CompleteUpload finishes an upload. parts is empty for a single PUT.
func (c *Client) CompleteUpload(ctx context.Context, linkID string, t Ticket, parts []CompletedPart) (Completed, error) {
	if parts == nil {
		parts = []CompletedPart{}
	}
	body := struct {
		Ticket Ticket          `json:"ticket"`
		Parts  []CompletedPart `json:"parts"`
	}{t, parts}
	var out Completed
	err := c.do(ctx, http.MethodPost, linkPath(linkID, "/uploads/complete"), body, &out)
	return out, err
}

// AbortUpload abandons an upload. It is allowed while a link is paused.
func (c *Client) AbortUpload(ctx context.Context, linkID string, t Ticket) error {
	body := struct {
		Ticket Ticket `json:"ticket"`
	}{t}
	return c.do(ctx, http.MethodPost, linkPath(linkID, "/uploads/abort"), body, nil)
}

// DeletePaths deletes up to MaxDeletePaths of this link's files.
func (c *Client) DeletePaths(ctx context.Context, linkID string, paths []string) (int, error) {
	if len(paths) > MaxDeletePaths {
		return 0, fmt.Errorf("deviceapi: at most %d paths per call", MaxDeletePaths)
	}
	var out struct {
		Deleted int `json:"deleted"`
	}
	err := c.do(ctx, http.MethodPost, linkPath(linkID, "/delete"), struct {
		Paths []string `json:"paths"`
	}{paths}, &out)
	return out.Deleted, err
}

func linkPath(linkID, suffix string) string {
	return "/api/v1/device/folder-links/" + url.PathEscape(linkID) + suffix
}

// do sends one signed JSON request.
func (c *Client) do(ctx context.Context, method, path string, in, out any) error {
	if strings.TrimSpace(c.BaseURL) == "" {
		return errors.New("deviceapi: no API origin configured")
	}
	var body []byte
	if in != nil {
		var err error
		body, err = json.Marshal(in)
		if err != nil {
			return fmt.Errorf("deviceapi: encode %s %s: %w", method, path, err)
		}
		if len(body) > MaxBodyBytes {
			return fmt.Errorf("deviceapi: %s %s: request body is %d bytes, above the server's %d", method, path, len(body), MaxBodyBytes)
		}
	}
	req, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(c.BaseURL, "/")+path, bytes.NewReader(body))
	if err != nil {
		return fmt.Errorf("deviceapi: build %s %s: %w", method, path, err)
	}
	if in != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	req.Header.Set("Accept", "application/json")
	if err := c.Signer.Sign(req, body); err != nil {
		return err
	}
	resp, err := c.api().Do(req)
	if err != nil {
		// url.Error repeats the URL, which has no query here and no secret.
		return fmt.Errorf("deviceapi: %s %s: %w", method, path, err)
	}
	defer func() { _ = resp.Body.Close() }()
	raw, err := io.ReadAll(io.LimitReader(resp.Body, 16<<20))
	if err != nil {
		return fmt.Errorf("deviceapi: read %s %s: %w", method, path, err)
	}
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		return decodeError(method, path, resp.StatusCode, raw)
	}
	if out == nil || len(bytes.TrimSpace(raw)) == 0 {
		return nil
	}
	if err := json.Unmarshal(raw, out); err != nil {
		return fmt.Errorf("deviceapi: parse %s %s: %w", method, path, err)
	}
	return nil
}

func decodeError(method, path string, status int, raw []byte) error {
	e := &Error{Method: method, Route: path, Status: status}
	var env struct {
		Error      string          `json:"error"`
		Code       string          `json:"code"`
		Message    string          `json:"message"`
		UsedBytes  *int64          `json:"usedBytes"`
		LimitBytes json.RawMessage `json:"limitBytes"`
	}
	if json.Unmarshal(raw, &env) == nil {
		e.Code = env.Error
		if e.Code == "" {
			e.Code = env.Code
		}
		e.Message = env.Message
		e.UsedBytes = env.UsedBytes
		switch s := strings.TrimSpace(string(env.LimitBytes)); s {
		case "":
		case "null":
			e.Unlimited = true
		default:
			var n int64
			if json.Unmarshal(env.LimitBytes, &n) == nil {
				e.LimitBytes = &n
			}
		}
	}
	if len(e.Message) > 500 {
		e.Message = e.Message[:500]
	}
	e.Message = stripQueries(e.Message)
	return e
}

// stripQueries removes any URL query or fragment from a sentence.
//
// A presigned storage URL IS a credential: its query carries the signature. The
// server's message is written for people, but it is the server's text and this
// client cannot assume it never quotes a URL back — and a message reaches a log
// file and a window. Dropping the query keeps the sentence readable (the
// address stays) and removes the part that would still be valid an hour later.
func stripQueries(s string) string {
	if !strings.ContainsAny(s, "?#") {
		return s
	}
	var b strings.Builder
	b.Grow(len(s))
	dropping := false
	for i := 0; i < len(s); i++ {
		c := s[i]
		if dropping {
			// The query ends at whitespace.
			switch c {
			case ' ', '\t', '\n', '\r':
				dropping = false
				b.WriteByte(c)
			}
			continue
		}
		switch c {
		case '?', '#':
			dropping = true
			b.WriteString("?(removed)")
		default:
			b.WriteByte(c)
		}
	}
	return b.String()
}

// PutObject PUTs size bytes from body to a presigned storage URL and returns
// the ETag storage answered with. headers are the ticket's (they are covered
// by the presigned signature and must be sent exactly). The device's own
// signing headers are never sent to storage.
func (c *Client) PutObject(ctx context.Context, rawURL string, headers map[string]string, body io.Reader, size int64) (string, error) {
	u, err := url.Parse(rawURL)
	if err != nil || (u.Scheme != "https" && u.Scheme != "http") || u.Host == "" {
		return "", fmt.Errorf("deviceapi: the server sent an upload address that is not a URL")
	}
	if size == 0 {
		// An empty body must still be a PUT with Content-Length: 0. A nil body
		// would do that too, but http.NoBody says so explicitly.
		body = http.NoBody
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPut, rawURL, body)
	if err != nil {
		return "", fmt.Errorf("deviceapi: build upload to %s: %v", RedactURL(rawURL), redactErr(err, rawURL))
	}
	req.ContentLength = size
	for k, v := range headers {
		if strings.EqualFold(k, "Content-Length") || strings.EqualFold(k, "Host") {
			continue
		}
		req.Header.Set(k, v)
	}
	resp, err := c.storage().Do(req)
	if err != nil {
		return "", fmt.Errorf("deviceapi: upload to %s: %v", RedactURL(rawURL), redactErr(err, rawURL))
	}
	defer func() { _ = resp.Body.Close() }()
	_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 64<<10))
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		return "", &StorageError{Status: resp.StatusCode, Host: u.Host}
	}
	return resp.Header.Get("ETag"), nil
}

// StorageError is a refused PUT to object storage.
type StorageError struct {
	Status int
	Host   string
}

func (e *StorageError) Error() string {
	return fmt.Sprintf("deviceapi: storage at %s answered HTTP %d", e.Host, e.Status)
}

// RedactURL drops the query (the presigned credential) and any user info.
func RedactURL(raw string) string {
	u, err := url.Parse(raw)
	if err != nil {
		return "(an upload address)"
	}
	u.RawQuery = ""
	u.ForceQuery = false
	u.User = nil
	u.Fragment = ""
	return u.String()
}

// redactErr strips a presigned URL out of an error's text: net/http's
// url.Error prints the whole URL, query included.
func redactErr(err error, rawURL string) string {
	msg := err.Error()
	if rawURL != "" {
		msg = strings.ReplaceAll(msg, rawURL, RedactURL(rawURL))
	}
	var ue *url.Error
	if errors.As(err, &ue) {
		msg = strings.ReplaceAll(msg, ue.URL, RedactURL(ue.URL))
	}
	return msg
}
