package deviceapi

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// The golden vector. cloud-core's DeviceRequestAuthenticator tests verify the
// SAME bytes, so a change on either side that breaks the other fails a test
// rather than every desktop app in the field. Do not change these values
// without changing the server's copy in the same commit.
const (
	goldenDeviceID  = "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
	goldenTimestamp = "2026-10-11T09:41:00Z"
	goldenNonce     = "oKGio6SlpqeoqaqrrK2urw" // base64url of bytes 0xa0..0xaf
	goldenPublicKey = "A6EHv/POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg="
	goldenBody      = `{"projectId":"11111111-2222-3333-4444-555555555555","displayPath":"Footage"}`
	goldenSigned    = "reelbolt-device-request-v1|3f2504e0-4f89-41d3-9a0c-0305e82c3301|2026-10-11T09:41:00Z|oKGio6SlpqeoqaqrrK2urw|POST|/api/v1/device/folder-links|7b0a0217a798ac5c6175fcea2de5771c0a76fd8e7df6ac53a433f48231a91617"
	goldenSignature = "LiQ5PXJgLULENBdR40iKxBPOM07ENFcrnWbM-HgfGe2cPjnXquDt0gPpv3ifg63oVWfhvoDO0uvFH0rd_NCpAg"

	goldenGetPath      = "/api/v1/device/projects?x=1&y=a%20b"
	goldenGetSigned    = "reelbolt-device-request-v1|3f2504e0-4f89-41d3-9a0c-0305e82c3301|2026-10-11T09:41:00Z|oKGio6SlpqeoqaqrrK2urw|GET|/api/v1/device/projects?x=1&y=a%20b|e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
	goldenGetSignature = "OfqCuD6lU5uzryQ20J-g2Yzegbcq8VLKOHqpWSGjOpzNnBwAFUsipjDQPACaBew8A6atCyHXpcMpRETmp5YHDg"
)

// goldenKey is the Ed25519 key whose seed is the bytes 0x00..0x1f.
func goldenKey() ed25519.PrivateKey {
	seed := make([]byte, ed25519.SeedSize)
	for i := range seed {
		seed[i] = byte(i)
	}
	return ed25519.NewKeyFromSeed(seed)
}

// nonceReader yields 0xa0, 0xa1, … so the nonce is the golden one.
type nonceReader struct{ next byte }

func (r *nonceReader) Read(p []byte) (int, error) {
	if r.next == 0 {
		r.next = 0xa0
	}
	for i := range p {
		p[i] = r.next
		r.next++
	}
	return len(p), nil
}

func goldenSigner() *Signer {
	fixed, _ := time.Parse(time.RFC3339, goldenTimestamp)
	return &Signer{DeviceID: goldenDeviceID, Key: goldenKey(), Now: func() time.Time { return fixed }, Rand: &nonceReader{}}
}

func TestGoldenVector(t *testing.T) {
	key := goldenKey()
	if got := base64.StdEncoding.EncodeToString(key.Public().(ed25519.PublicKey)); got != goldenPublicKey {
		t.Fatalf("public key = %s, want %s", got, goldenPublicKey)
	}
	if got := SigningString(goldenDeviceID, goldenTimestamp, goldenNonce, "post", "/api/v1/device/folder-links", []byte(goldenBody)); got != goldenSigned {
		t.Fatalf("signed string =\n%s\nwant\n%s", got, goldenSigned)
	}
	h, err := goldenSigner().Headers(http.MethodPost, "/api/v1/device/folder-links", []byte(goldenBody))
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]string{
		HeaderDevice: goldenDeviceID, HeaderTimestamp: goldenTimestamp,
		HeaderNonce: goldenNonce, HeaderSignature: goldenSignature,
	}
	for k, v := range want {
		if h[k] != v {
			t.Errorf("%s = %q, want %q", k, h[k], v)
		}
	}

	// The empty-body GET, with a query.
	if got := SigningString(goldenDeviceID, goldenTimestamp, goldenNonce, "GET", goldenGetPath, nil); got != goldenGetSigned {
		t.Fatalf("GET signed string =\n%s\nwant\n%s", got, goldenGetSigned)
	}
	h, err = goldenSigner().Headers(http.MethodGet, goldenGetPath, nil)
	if err != nil {
		t.Fatal(err)
	}
	if h[HeaderSignature] != goldenGetSignature {
		t.Fatalf("GET signature = %s, want %s", h[HeaderSignature], goldenGetSignature)
	}
	pub := key.Public().(ed25519.PublicKey)
	if !Verify(pub, goldenDeviceID, goldenTimestamp, goldenNonce, "GET", goldenGetPath, nil, goldenGetSignature) {
		t.Fatal("the golden GET signature does not verify")
	}
	if Verify(pub, goldenDeviceID, goldenTimestamp, goldenNonce, "GET", "/api/v1/device/projects", nil, goldenGetSignature) {
		t.Fatal("a signature verified over a different path")
	}
}

func TestNoncesAreFreshAndLongEnough(t *testing.T) {
	s := &Signer{DeviceID: goldenDeviceID, Key: goldenKey()}
	seen := map[string]bool{}
	for i := 0; i < 50; i++ {
		h, err := s.Headers("GET", "/x", nil)
		if err != nil {
			t.Fatal(err)
		}
		raw, err := base64.RawURLEncoding.DecodeString(h[HeaderNonce])
		if err != nil || len(raw) < 16 {
			t.Fatalf("nonce %q is not 16 bytes of base64url", h[HeaderNonce])
		}
		if seen[h[HeaderNonce]] {
			t.Fatal("a nonce repeated")
		}
		seen[h[HeaderNonce]] = true
		if _, err := time.Parse(time.RFC3339, h[HeaderTimestamp]); err != nil || !strings.HasSuffix(h[HeaderTimestamp], "Z") {
			t.Fatalf("timestamp %q is not RFC 3339 UTC", h[HeaderTimestamp])
		}
	}
}

// verifyingServer checks every request's signature the way the server does,
// over the path and query it actually received and the body it actually read.
func verifyingServer(t *testing.T, handler func(w http.ResponseWriter, r *http.Request, body []byte)) (*httptest.Server, *Client) {
	t.Helper()
	key := goldenKey()
	pub := key.Public().(ed25519.PublicKey)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		if !Verify(pub, r.Header.Get(HeaderDevice), r.Header.Get(HeaderTimestamp), r.Header.Get(HeaderNonce),
			r.Method, r.URL.RequestURI(), body, r.Header.Get(HeaderSignature)) {
			w.WriteHeader(http.StatusUnauthorized)
			_, _ = w.Write([]byte(`{"error":"device_unauthorized"}`))
			return
		}
		if r.Header.Get("Authorization") != "" {
			t.Errorf("a device request carried an Authorization header")
		}
		handler(w, r, body)
	}))
	t.Cleanup(srv.Close)
	return srv, New(srv.URL, goldenDeviceID, key)
}

func TestTypedCallsAreSignedAndDecoded(t *testing.T) {
	var lastPath, lastMethod string
	var lastBody []byte
	_, c := verifyingServer(t, func(w http.ResponseWriter, r *http.Request, body []byte) {
		lastPath, lastMethod, lastBody = r.URL.Path, r.Method, body
		switch {
		case r.URL.Path == "/api/v1/device/me":
			_, _ = w.Write([]byte(`{"deviceId":"d","deviceName":"Mac","organizationId":"o","organizationName":"Acme","pairedByEmail":"a@b.c"}`))
		case r.URL.Path == "/api/v1/device/projects":
			_, _ = w.Write([]byte(`[{"id":"p1","name":"Launch"}]`))
		case r.URL.Path == "/api/v1/device/folder-links" && r.Method == http.MethodPost:
			w.WriteHeader(http.StatusCreated)
			_, _ = w.Write([]byte(`{"id":"l1","projectId":"p1","projectName":"Launch","targetFolder":"Footage","displayPath":"/Users/a/Footage","paused":false,"fileCount":0}`))
		case r.URL.Path == "/api/v1/device/folder-links/l1/manifest":
			_, _ = w.Write([]byte(`{"upload":["a.txt"],"delete":["b.txt"],"skipped":[{"path":"c","reason":"occupied"}],"paused":false,"storage":{"usedBytes":5,"limitBytes":null}}`))
		case r.URL.Path == "/api/v1/device/folder-links/l1/delete":
			_, _ = w.Write([]byte(`{"deleted":2}`))
		case r.URL.Path == "/api/v1/device/folder-links/l1" && r.Method == http.MethodDelete:
			w.WriteHeader(http.StatusNoContent)
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	})
	ctx := context.Background()

	me, err := c.Me(ctx)
	if err != nil || me.OrganizationName != "Acme" {
		t.Fatalf("Me = %+v, %v", me, err)
	}
	ps, err := c.Projects(ctx)
	if err != nil || len(ps) != 1 || ps[0].Name != "Launch" {
		t.Fatalf("Projects = %+v, %v", ps, err)
	}
	l, err := c.CreateLink(ctx, CreateLinkRequest{ProjectID: "p1", TargetFolder: "Footage", DisplayPath: "/Users/a/Footage"})
	if err != nil || l.ID != "l1" {
		t.Fatalf("CreateLink = %+v, %v", l, err)
	}
	res, err := c.Manifest(ctx, "l1", ManifestBatch{ManifestID: "m", Batch: 0, Final: true})
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(lastBody, []byte(`"files":[]`)) {
		t.Errorf("an empty manifest must send files: [], sent %s", lastBody)
	}
	if bytes.Contains(lastBody, []byte("confirmLargeDelete")) {
		t.Errorf("confirmLargeDelete must be absent unless the person confirmed: %s", lastBody)
	}
	if len(res.Upload) != 1 || len(res.Delete) != 1 || res.Skipped[0].Reason != "occupied" || res.Storage.LimitBytes != nil {
		t.Fatalf("Manifest = %+v", res)
	}
	n, err := c.DeletePaths(ctx, "l1", []string{"b.txt", "c.txt"})
	if err != nil || n != 2 {
		t.Fatalf("DeletePaths = %d, %v", n, err)
	}
	if err := c.DeleteLink(ctx, "l1"); err != nil || lastMethod != http.MethodDelete || lastPath != "/api/v1/device/folder-links/l1" {
		t.Fatalf("DeleteLink: %v (%s %s)", err, lastMethod, lastPath)
	}
}

func TestTicketIsEchoedExactly(t *testing.T) {
	ticketJSON := `{"uploadToken":"tok","mode":"multipart","projectId":"p","fileId":"f","key":"k","fileName":"a.bin","relativePath":"Footage","contentType":"application/octet-stream","sizeBytes":200,"expiresAtUnix":99,"url":null,"uploadId":"u","partSizeBytes":100,"partCount":2,"headers":{},"sourceKind":"local_folder","sourceRevision":"abc","sourceLinkId":"l1","replacesFileId":null,"futureField":7}`
	var echoed json.RawMessage
	_, c := verifyingServer(t, func(w http.ResponseWriter, r *http.Request, body []byte) {
		switch r.URL.Path {
		case "/api/v1/device/folder-links/l1/uploads":
			_, _ = w.Write([]byte(ticketJSON))
		case "/api/v1/device/folder-links/l1/uploads/parts":
			var req struct {
				Ticket      json.RawMessage `json:"ticket"`
				PartNumbers []int           `json:"partNumbers"`
			}
			_ = json.Unmarshal(body, &req)
			echoed = req.Ticket
			_, _ = w.Write([]byte(`[{"partNumber":1,"url":"https://s/1?sig=x","expiresAtUnix":1},{"partNumber":2,"url":"https://s/2?sig=y","expiresAtUnix":1}]`))
		}
	})
	tk, err := c.CreateUpload(context.Background(), "l1", UploadRequest{Path: "a.bin", Size: 200, SHA256: "abc"})
	if err != nil {
		t.Fatal(err)
	}
	if tk.Mode != ModeMultipart || tk.PartCount != 2 || tk.PartSizeBytes != 100 || tk.SizeBytes != 200 {
		t.Fatalf("ticket = %+v", tk)
	}
	parts, err := c.PresignParts(context.Background(), "l1", tk, []int{1, 2})
	if err != nil || len(parts) != 2 {
		t.Fatalf("PresignParts = %+v, %v", parts, err)
	}
	if string(echoed) != ticketJSON {
		t.Fatalf("the ticket was not echoed byte for byte:\n%s", echoed)
	}

	var numeric Ticket
	if err := json.Unmarshal([]byte(`{"mode":0,"url":"https://s/x","sizeBytes":0}`), &numeric); err != nil || numeric.Mode != ModeSinglePut {
		t.Fatalf("numeric mode: %+v, %v", numeric, err)
	}
	var bad Ticket
	if err := json.Unmarshal([]byte(`{"mode":"carrier-pigeon"}`), &bad); err == nil {
		t.Fatal("an unknown mode was accepted")
	}
}

func TestErrorMapping(t *testing.T) {
	cases := []struct {
		status int
		body   string
		want   error
	}{
		{401, `{"error":"device_unauthorized"}`, ErrNotConnected},
		{403, `{"error":"device_owner_lacks_access","message":"Ann left."}`, ErrOwnerLacksAccess},
		{404, `{"error":"link_not_found"}`, ErrLinkNotFound},
		{409, `{"error":"unchanged"}`, ErrUnchanged},
		{409, `{"error":"link_paused"}`, ErrLinkPaused},
		{409, `{"error":"occupied"}`, ErrOccupied},
		{409, `{"error":"manifest_incomplete"}`, ErrManifestIncomplete},
		{413, `{"error":"STORAGE_QUOTA_EXCEEDED","message":"full","usedBytes":15000000000,"limitBytes":15000000000}`, ErrStorageFull},
		{400, `{"error":"INVALID_PATH"}`, ErrInvalidPath},
		{409, `{"error":"target_folder_taken"}`, ErrTargetTaken},
	}
	for _, tc := range cases {
		_, c := verifyingServer(t, func(w http.ResponseWriter, r *http.Request, body []byte) {
			w.WriteHeader(tc.status)
			_, _ = w.Write([]byte(tc.body))
		})
		_, err := c.Me(context.Background())
		if !errors.Is(err, tc.want) {
			t.Errorf("HTTP %d %s: got %v, want %v", tc.status, tc.body, err, tc.want)
		}
		if errors.Is(tc.want, ErrStorageFull) {
			if got := StorageSentence(err); got != "Storage is full (15.0 GB of 15.0 GB used)" {
				t.Errorf("StorageSentence = %q", got)
			}
		}
	}
	// A 401 for a forged signature, produced by the verifying server itself.
	_, c := verifyingServer(t, func(http.ResponseWriter, *http.Request, []byte) {})
	c.Signer.Key = ed25519.NewKeyFromSeed(make([]byte, 32))
	if _, err := c.Me(context.Background()); !errors.Is(err, ErrNotConnected) {
		t.Fatalf("a wrong key: %v", err)
	}
}

func TestPutObjectNeverLeaksTheQueryOrSigns(t *testing.T) {
	var got http.Header
	var gotLen int64 = -1
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r.Header.Clone()
		b, _ := io.ReadAll(r.Body)
		gotLen = int64(len(b))
		if r.URL.Query().Get("fail") == "1" {
			w.WriteHeader(http.StatusForbidden)
			return
		}
		w.Header().Set("ETag", `"etag-1"`)
	}))
	defer srv.Close()
	c := New("https://app.reelbolt.ai", goldenDeviceID, goldenKey())

	etag, err := c.PutObject(context.Background(), srv.URL+"/obj?X-Amz-Signature=secret", map[string]string{"x-amz-checksum-sha256": "abc="}, nil, 0)
	if err != nil || etag != `"etag-1"` {
		t.Fatalf("PutObject = %q, %v", etag, err)
	}
	if gotLen != 0 {
		t.Fatalf("a zero-byte PUT sent %d bytes", gotLen)
	}
	if got.Get(HeaderSignature) != "" || got.Get(HeaderDevice) != "" {
		t.Fatal("the device's signing headers reached storage")
	}
	if got.Get("X-Amz-Checksum-Sha256") != "abc=" {
		t.Fatalf("ticket header not sent: %v", got)
	}

	_, err = c.PutObject(context.Background(), srv.URL+"/obj?fail=1&X-Amz-Signature=secret", nil, strings.NewReader("x"), 1)
	if err == nil || strings.Contains(err.Error(), "secret") {
		t.Fatalf("refusal error = %v", err)
	}
	// A transport failure: url.Error would print the whole URL.
	_, err = c.PutObject(context.Background(), "http://127.0.0.1:1/obj?X-Amz-Signature=secret", nil, strings.NewReader("x"), 1)
	if err == nil || strings.Contains(err.Error(), "secret") {
		t.Fatalf("transport error leaked the query: %v", err)
	}
}
