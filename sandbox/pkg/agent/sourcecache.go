package agent

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"strings"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// The downloaded-media cache: how a runner avoids fetching the same source clip
// twice.
//
// A stage op names a presigned GET, and the engine usually sends no digest with
// it, so a content-addressed cache would almost never hit. What IS stable is the
// object: a presigned URL's scheme, host and path name the object, and only its
// query string (the signature, which differs on every op) changes. So the cache
// is keyed by that identity and remembers the ETag the object store answered
// with; the next stage of the same object asks the store "If-None-Match" that
// ETag, and a 304 means the bytes on this disk are still the object's bytes.
//
// Two properties make that safe rather than merely fast:
//
//   - THE STORE STILL DECIDES. Nothing is served from the cache without a
//     request carrying the op's own presigned signature, and S3-compatible stores
//     check the signature before the precondition, so a 304 is only ever the
//     answer to a request that was allowed to read the object. A runner cannot
//     use the cache to read an object the engine did not grant it this time.
//     (The engine presigns GETs with no signed headers besides Host, so the
//     extra If-None-Match header is accepted unsigned by S3, R2 and Garage.)
//   - THE BYTES ARE CHECKED ON EVERY HIT. Each entry records the SHA-256 the
//     bytes had when they came from the store. A hit is COPIED into the session
//     (never linked: a session may overwrite its own files in place, and a
//     linked file would be the cache's) and the copy is hashed, as every staged
//     file already is; a copy whose digest is not the entry's is discarded, the
//     entry with it, and the object is fetched again. An entry is never used at
//     all when the engine supplied a digest the entry does not have.
//
// With no cache configured, FetchToFSCached is FetchToFS exactly.

// SourceCache is where downloaded source media is kept between jobs. The
// implementation is sandbox/pkg/agent/srccache; a nil SourceCache is no cache.
type SourceCache interface {
	// Acquire returns the entry for an object key pinned against eviction, or
	// nil when there is none. The caller must Release it.
	Acquire(key string) SourceEntry
	// Create starts writing a new entry for key. The caller must Commit or
	// Abort it. An error means "do not cache this one" and never fails a stage.
	Create(key string) (SourceWriter, error)
	// RecordHit counts a stage served from the cache, and the bytes it did not
	// download. RecordMiss counts one that went to the network.
	RecordHit(savedBytes int64)
	RecordMiss()
}

// SourceEntry is one cached object, pinned.
type SourceEntry interface {
	ETag() string
	Size() int64
	SHA256() string
	// Open opens the cached bytes for reading.
	Open() (*os.File, error)
	// Release unpins it. Invalidate drops it from the cache (it is removed once
	// nothing holds it) and also releases it.
	Release()
	Invalidate()
}

// SourceWriter receives a new entry's bytes as they are downloaded.
type SourceWriter interface {
	io.Writer
	// Commit makes the entry visible under its key, replacing any older one.
	// It may decline to keep it (too large for the cache); that is not an error
	// the stage sees.
	Commit(etag string, size int64, sha256 string) error
	// Abort discards what was written.
	Abort()
}

// SourceCacheKey is the cache identity of a presigned URL: scheme, host and
// path, lower-cased where the URL is case-insensitive, with the query (the
// signature) and any fragment or user info dropped.
func SourceCacheKey(u *url.URL) string {
	path := u.EscapedPath()
	if path == "" {
		path = "/"
	}
	return strings.ToLower(u.Scheme) + "://" + strings.ToLower(u.Host) + path
}

// SourceCacheKeyOf is SourceCacheKey for a raw URL; "" when it does not parse.
func SourceCacheKeyOf(rawURL string) string {
	u, err := url.Parse(rawURL)
	if err != nil {
		return ""
	}
	return SourceCacheKey(u)
}

// FetchToFSCached is FetchToFS with a source cache in front of it. With a nil
// cache it IS FetchToFS. With one, the size cap, the digest check and the error
// codes are the same; the difference is that an object this runner already
// holds is revalidated with If-None-Match instead of downloaded again.
func FetchToFSCached(ctx context.Context, client *http.Client, cache SourceCache, fs *RootFS, rawURL, virtual string, maxBytes int64, wantSHA string) (Transfer, *protocol.OpError) {
	if cache == nil {
		return FetchToFS(ctx, client, fs, rawURL, virtual, maxBytes, wantSHA)
	}
	u, err := CheckPresignedURL(rawURL)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "the stage url is not usable: "+redactErr(err), false)
	}
	if _, err := VirtualPath(virtual); err != nil {
		return Transfer{}, OpError(protocol.ErrCodeInvalidPath, err.Error(), false)
	}
	if maxBytes <= 0 {
		maxBytes = MaxInlineBytes
	}
	key := SourceCacheKey(u)

	entry := cache.Acquire(key)
	if entry != nil && (entry.ETag() == "" || (wantSHA != "" && !strings.EqualFold(entry.SHA256(), wantSHA))) {
		// The engine wants bytes this entry does not hold: it is not consulted,
		// and the fresh download below replaces it.
		entry.Release()
		entry = nil
	}
	if entry != nil {
		transfer, opErr, done := revalidate(ctx, client, cache, key, entry, fs, rawURL, virtual, maxBytes, wantSHA)
		if done {
			return transfer, opErr
		}
		// The cached bytes did not survive the check: fetch the object again,
		// unconditionally, which also replaces the entry.
	}
	return fetchAndFill(ctx, client, cache, key, fs, rawURL, virtual, maxBytes, wantSHA)
}

// revalidate asks the store whether the cached entry is still the object. It
// reports done=false when the stage must fall back to a plain download (the
// copy did not match the entry's digest); anything else is the stage's answer.
// It always releases the entry.
func revalidate(ctx context.Context, client *http.Client, cache SourceCache, key string, entry SourceEntry, fs *RootFS, rawURL, virtual string, maxBytes int64, wantSHA string) (Transfer, *protocol.OpError, bool) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		entry.Release()
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "could not build the download: "+redactErr(err), false), true
	}
	req.Header.Set("If-None-Match", entry.ETag())
	resp, err := doGuarded(ctx, client, req)
	if err != nil {
		entry.Release()
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "the download failed: "+redactErr(err), true), true
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusNotModified {
		// The object changed, or this store ignores the precondition: the
		// response is a normal download and is handled as one, filling the
		// cache afresh.
		entry.Release()
		transfer, opErr := fillFromResponse(cache, key, resp, fs, virtual, maxBytes, wantSHA)
		return transfer, opErr, true
	}
	// A 304 has no body worth keeping: release the connection before the
	// local copy, which can be long. The deferred Close is then a no-op.
	_ = resp.Body.Close()

	if entry.Size() > maxBytes {
		entry.Release()
		return Transfer{}, OpError(protocol.ErrCodeFileTooLarge,
			fmt.Sprintf("the object is %d bytes and this op allows %d", entry.Size(), maxBytes), false), true
	}
	f, err := entry.Open()
	if err != nil {
		// The entry vanished from disk underneath the index (another process
		// cleared the cache): it is dropped and the object fetched again.
		entry.Invalidate()
		return Transfer{}, nil, false
	}
	written, err := fs.WriteStream(virtual, f, maxBytes)
	_ = f.Close()
	if err != nil {
		entry.Release()
		return Transfer{}, fsError(err, "stage"), true
	}
	digest, err := digestOfFile(fs, virtual)
	if err != nil {
		entry.Release()
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "could not hash the downloaded file: "+err.Error(), false), true
	}
	if written != entry.Size() || !strings.EqualFold(digest, entry.SHA256()) {
		_, _ = fs.Delete(virtual)
		entry.Invalidate()
		return Transfer{}, nil, false
	}
	entry.Release()
	cache.RecordHit(written)
	return Transfer{Bytes: written, SHA256: digest}, nil, true
}

// fetchAndFill is an unconditional download that also fills the cache.
func fetchAndFill(ctx context.Context, client *http.Client, cache SourceCache, key string, fs *RootFS, rawURL, virtual string, maxBytes int64, wantSHA string) (Transfer, *protocol.OpError) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "could not build the download: "+redactErr(err), false)
	}
	resp, err := doGuarded(ctx, client, req)
	if err != nil {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "the download failed: "+redactErr(err), true)
	}
	defer func() { _ = resp.Body.Close() }()
	return fillFromResponse(cache, key, resp, fs, virtual, maxBytes, wantSHA)
}

// fillFromResponse consumes a GET response the way FetchToFS does, teeing a
// cacheable body into a new entry. It closes the body.
func fillFromResponse(cache SourceCache, key string, resp *http.Response, fs *RootFS, virtual string, maxBytes int64, wantSHA string) (Transfer, *protocol.OpError) {
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode/100 != 2 {
		return Transfer{}, OpError(protocol.ErrCodeStageFailed,
			fmt.Sprintf("the object store answered %d", resp.StatusCode), resp.StatusCode >= 500)
	}
	if resp.ContentLength > maxBytes {
		return Transfer{}, OpError(protocol.ErrCodeFileTooLarge,
			fmt.Sprintf("the object is %d bytes and this op allows %d", resp.ContentLength, maxBytes), false)
	}
	cache.RecordMiss()

	// Only a whole object with a validator can be revalidated later.
	var w SourceWriter
	etag := resp.Header.Get("ETag")
	if resp.StatusCode == http.StatusOK && etag != "" && key != "" {
		if created, err := cache.Create(key); err == nil {
			w = created
		}
	}
	var body io.Reader = resp.Body
	var tee *softWriter
	if w != nil {
		tee = &softWriter{w: w}
		body = io.TeeReader(resp.Body, tee)
	}
	written, err := fs.WriteStream(virtual, body, maxBytes)
	if err != nil {
		if w != nil {
			w.Abort()
		}
		return Transfer{}, fsError(err, "stage")
	}
	digest, err := digestOfFile(fs, virtual)
	if err != nil {
		if w != nil {
			w.Abort()
		}
		return Transfer{}, OpError(protocol.ErrCodeStageFailed, "could not hash the downloaded file: "+err.Error(), false)
	}
	if wantSHA != "" && !strings.EqualFold(wantSHA, digest) {
		_, _ = fs.Delete(virtual)
		if w != nil {
			w.Abort()
		}
		return Transfer{}, OpError(protocol.ErrCodeStageFailed,
			"the downloaded object does not match the sha256 the engine supplied", false)
	}
	if w != nil {
		if tee.failed {
			w.Abort()
		} else {
			// A cache that cannot keep the entry is not the stage's problem.
			_ = w.Commit(etag, written, digest)
		}
	}
	return Transfer{Bytes: written, SHA256: digest}, nil
}

// softWriter feeds the cache from the download without ever failing it: the
// first write error marks the entry as broken and the rest of the body still
// reaches the session.
type softWriter struct {
	w      io.Writer
	failed bool
}

func (s *softWriter) Write(p []byte) (int, error) {
	if !s.failed {
		if _, err := s.w.Write(p); err != nil {
			s.failed = true
		}
	}
	return len(p), nil
}
