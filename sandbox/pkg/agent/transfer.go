package agent

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"os"
	"strings"
	"syscall"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Transfer is the outcome of a presigned GET or PUT: the bytes moved and the
// SHA-256 of them, lower-case hex, as the protocol's sha256 fields require.
type Transfer struct {
	Bytes  int64  `json:"bytes"`
	SHA256 string `json:"sha256"`
}

// CheckPresignedURL refuses anything that is not an http(s) URL. A presigned
// URL grants one object and one method; a file: or data: URL in a stage or
// publish op is not one, and a runner that honoured it would read the runner's
// own disk for whoever wrote the op.
func CheckPresignedURL(raw string) (*url.URL, error) {
	if raw == "" {
		return nil, errors.New("no url")
	}
	u, err := url.Parse(raw)
	if err != nil {
		return nil, fmt.Errorf("unparseable url: %w", err)
	}
	switch strings.ToLower(u.Scheme) {
	case "http", "https":
	default:
		return nil, fmt.Errorf("scheme %q is not http or https", u.Scheme)
	}
	if u.Host == "" {
		return nil, errors.New("url has no host")
	}
	return u, nil
}

// FetchToFS GETs a presigned URL into a session directory.
//
// The size cap is enforced while streaming, and the digest the engine supplied
// is verified before the file is kept: an object that arrived truncated or
// substituted is removed rather than handed to a renderer. The digest the
// runner computes is returned either way, because the engine treats its own
// value as the authority and this one as a hint.
func FetchToFS(ctx context.Context, client *http.Client, fs *RootFS, rawURL, virtual string, maxBytes int64, wantSHA string) (Transfer, *protocol.OpError) {
	if _, err := CheckPresignedURL(rawURL); err != nil {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "the stage url is not usable: "+redactErr(err), false)
	}
	if _, err := VirtualPath(virtual); err != nil {
		return Transfer{}, OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	}
	if maxBytes <= 0 {
		maxBytes = MaxInlineBytes
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "could not build the download: "+redactErr(err), false)
	}
	resp, err := doGuarded(ctx, client, req)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "the download failed: "+redactErr(err), true)
	}
	defer func() {
		// Best-effort: the body was already fully streamed to the session
		// filesystem (or the request failed), so a close failure has nothing
		// to do.
		_ = resp.Body.Close()
	}()
	if resp.StatusCode/100 != 2 {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed,
			fmt.Sprintf("the object store answered %d", resp.StatusCode), resp.StatusCode >= 500)
	}
	if resp.ContentLength > maxBytes {
		return Transfer{}, OpError(protocol.ErrCodeFileTooLarge,
			fmt.Sprintf("the object is %d bytes and this op allows %d", resp.ContentLength, maxBytes), false)
	}
	written, err := fs.WriteStream(virtual, resp.Body, maxBytes)
	if err != nil {
		return Transfer{}, fsError(err, "stage")
	}
	digest, err := digestOfFile(fs, virtual)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "could not hash the downloaded file: "+err.Error(), false)
	}
	if wantSHA != "" && !strings.EqualFold(wantSHA, digest) {
		_, _ = fs.Delete(virtual)
		return Transfer{}, OpError(protocol.ErrCodeStageFailed,
			"the downloaded object does not match the sha256 the engine supplied", false)
	}
	return Transfer{Bytes: written, SHA256: digest}, nil
}

// PublishFromFS PUTs a session file to a presigned URL and returns its digest.
func PublishFromFS(ctx context.Context, client *http.Client, fs *RootFS, rawURL, virtual string, maxBytes int64, contentType string) (Transfer, *protocol.OpError) {
	if _, err := CheckPresignedURL(rawURL); err != nil {
		return Transfer{}, OpError(protocol.ErrCodePublishFailed, "the publish url is not usable: "+redactErr(err), false)
	}
	f, size, err := fs.OpenStream(virtual)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return Transfer{}, OpError(protocol.ErrCodePublishFailed, "there is nothing at "+virtual, false)
		}
		if errors.Is(err, ErrUnsafePath) {
			return Transfer{}, OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
		}
		return Transfer{}, OpError(protocol.ErrCodePublishFailed, err.Error(), false)
	}
	defer func() {
		// Best-effort: the body was already uploaded (or the request failed),
		// so a close failure has nothing to add.
		_ = f.Close()
	}()
	if maxBytes > 0 && size > maxBytes {
		return Transfer{}, OpError(protocol.ErrCodeOutputTooLarge,
			fmt.Sprintf("%s is %d bytes and this op allows %d", virtual, size, maxBytes), false)
	}

	// The digest is computed while the bytes are already moving: hashing first
	// would read a multi-gigabyte render twice.
	hasher := sha256.New()
	body := io.TeeReader(f, hasher)
	req, err := http.NewRequestWithContext(ctx, http.MethodPut, rawURL, body)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodePublishFailed, "could not build the upload: "+redactErr(err), false)
	}
	req.ContentLength = size
	if contentType == "" {
		contentType = "application/octet-stream"
	}
	req.Header.Set("Content-Type", contentType)
	resp, err := client.Do(req)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodePublishFailed, "the upload failed: "+redactErr(err), true)
	}
	defer func() {
		// Best-effort: the body was drained (to discard, up to 1 MiB) before
		// this returns, so a close failure has nothing to do.
		_ = resp.Body.Close()
	}()
	_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode/100 != 2 {
		return Transfer{}, OpError(protocol.ErrCodePublishFailed,
			fmt.Sprintf("the object store answered %d", resp.StatusCode), resp.StatusCode >= 500)
	}
	return Transfer{Bytes: size, SHA256: hex.EncodeToString(hasher.Sum(nil))}, nil
}

func digestOfFile(fs *RootFS, virtual string) (string, error) {
	f, _, err := fs.OpenStream(virtual)
	if err != nil {
		return "", err
	}
	defer func() {
		// Best-effort: the hash above is complete by the time the deferred
		// close runs, so a close failure has nothing to add.
		_ = f.Close()
	}()
	return SHA256File(f)
}

// fsError maps a session-filesystem failure onto the protocol's error codes.
func fsError(err error, verb string) *protocol.OpError {
	switch {
	case errors.Is(err, ErrTooLarge):
		if verb == "stage" {
			return OpError(protocol.ErrCodeFileTooLarge, err.Error(), false)
		}
		return OpError(protocol.ErrCodeOutputTooLarge, err.Error(), false)
	case errors.Is(err, ErrUnsafePath):
		return OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	case errors.Is(err, ErrNoSpace):
		return OpError(protocol.ErrCodeDiskFull, err.Error(), false)
	default:
		return OpError(protocol.ErrCodeStageFailed, err.Error(), false)
	}
}

// StallTimeout is how long a download may receive no bytes before it is abandoned.
var StallTimeout = 60 * time.Second

type stallReader struct {
	rc     io.ReadCloser
	timer  *time.Timer
	cancel context.CancelFunc
}

// doGuarded runs a GET whose body is abandoned after StallTimeout without bytes. The caller closes the body,
// which also releases the guard.
func doGuarded(ctx context.Context, client *http.Client, req *http.Request) (*http.Response, error) {
	ctx, cancel := context.WithCancel(ctx)
	resp, err := client.Do(req.WithContext(ctx))
	if err != nil {
		cancel()
		return nil, err
	}
	resp.Body = &stallReader{rc: resp.Body, timer: time.AfterFunc(StallTimeout, cancel), cancel: cancel}
	return resp, nil
}

// redactErr is err's text with any presigned URL's query removed: a url.Error carries the full URL.
func redactErr(err error) string {
	var ue *url.Error
	if errors.As(err, &ue) {
		return ue.Op + " " + RedactForLog(ue.URL) + ": " + ue.Err.Error()
	}
	return err.Error()
}

func (s *stallReader) Read(p []byte) (int, error) {
	n, err := s.rc.Read(p)
	if n > 0 {
		s.timer.Reset(StallTimeout)
	}
	return n, err
}

func (s *stallReader) Close() error {
	s.timer.Stop()
	err := s.rc.Close()
	s.cancel()
	return err
}

// ErrBlockedAddress is returned by DialGuard for an address a presigned transfer must never reach.
var ErrBlockedAddress = errors.New("agent: transfer address is not permitted")

// DialGuard is a net.Dialer.Control func. It checks the IP actually being dialled (so DNS rebinding
// cannot slip past a hostname check) and refuses link-local (including the 169.254.169.254 cloud
// metadata address), unspecified, multicast and broadcast addresses. Loopback and private ranges
// stay allowed: self-hosted Garage/MinIO on a LAN or docker network is a supported deployment.
func DialGuard(_, address string, _ syscall.RawConn) error {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return fmt.Errorf("%w: %w", ErrBlockedAddress, err)
	}
	ip, err := netip.ParseAddr(host)
	if err != nil {
		return fmt.Errorf("%w: %w", ErrBlockedAddress, err)
	}
	ip = ip.Unmap()
	if ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || ip.IsUnspecified() || ip.IsMulticast() ||
		ip == netip.AddrFrom4([4]byte{255, 255, 255, 255}) {
		return fmt.Errorf("%w: %s", ErrBlockedAddress, ip)
	}
	return nil
}
