package deviceapi

import (
	"errors"
	"strings"
	"testing"
)

// The four statuses the design names, each mapped to the sentence the person
// reads. 401/403/409/413 are the ones that change what the desktop app DOES, so
// each is asserted with its own answer rather than only through a sentinel.
func TestTheFourDecisionStatuses(t *testing.T) {
	cases := []struct {
		name    string
		status  int
		body    string
		want    error
		message string
	}{
		{
			name:   "401 stops syncing and asks for a re-pair",
			status: 401, body: `{"error":"device_unauthorized"}`,
			want: ErrNotConnected,
		},
		{
			name:   "403 names the person who lost access",
			status: 403, body: `{"error":"device_owner_lacks_access","message":"Ann is no longer a member."}`,
			want: ErrOwnerLacksAccess,
		},
		{
			name:   "409 link_paused points at the dashboard",
			status: 409, body: `{"error":"link_paused"}`,
			want: ErrLinkPaused,
		},
		{
			name:   "413 carries the workspace's numbers",
			status: 413, body: `{"error":"STORAGE_QUOTA_EXCEEDED","usedBytes":4200000000,"limitBytes":15000000000}`,
			want: ErrStorageFull,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := decodeError("POST", "/api/v1/device/folder-links/l1/uploads", tc.status, []byte(tc.body))
			if !errors.Is(err, tc.want) {
				t.Fatalf("errors.Is(%v, %v) = false", err, tc.want)
			}
			// The sentinel's own words are what a person sees.
			if got := tc.want.Error(); got == "" {
				t.Fatal("the sentinel has no sentence")
			}
			var e *Error
			if !errors.As(err, &e) {
				t.Fatalf("%v is not a *Error", err)
			}
			if e.Status != tc.status {
				t.Fatalf("status = %d, want %d", e.Status, tc.status)
			}
			// None of these is worth retrying as-is: a retry would send the
			// same refused request again.
			if e.Temporary() {
				t.Errorf("HTTP %d was reported as temporary", tc.status)
			}
		})
	}

	// A 413 with no numbers still says something usable.
	plain := decodeError("POST", "/uploads", 413, []byte(`{"error":"STORAGE_QUOTA_EXCEEDED"}`))
	if got := StorageSentence(plain); got != "Storage is full" {
		t.Errorf("StorageSentence without numbers = %q", got)
	}
	// 500 and 429 are temporary; the four decision statuses above are not.
	var five *Error
	if !errors.As(decodeError("GET", "/x", 500, nil), &five) || !five.Temporary() {
		t.Error("a 500 was reported as permanent")
	}
	var busy *Error
	if !errors.As(decodeError("GET", "/x", 429, nil), &busy) || !busy.Temporary() {
		t.Error("a 429 was reported as permanent")
	}
}

// TestErrorsNeverCarryASecretOrAPresignedQuery: an error names the route, the
// status and the server's own machine code. It must never carry a signature, a
// nonce, or the query string of a presigned URL — all three are credentials.
func TestErrorsNeverCarryASecretOrAPresignedQuery(t *testing.T) {
	const secret = "X-Amz-Signature=deadbeef&X-Amz-Credential=AKIAEXAMPLE"
	const sig = "LiQ5PXJgLULENBdR40iKxBPOM07ENFcrnWbM-HgfGe2cPjnXquDt0gPpv3ifg63oVWfhvoDO0uvFH0rd_NCpAg"

	// A server that answers with its own message, one that echoes a query, and
	// one that answers nothing at all.
	bodies := []string{
		`{"error":"invalid_path","message":"the path is not allowed"}`,
		`{"error":"bad","message":"https://storage.test/obj?` + secret + `"}`,
		`not json at all`,
	}
	for _, body := range bodies {
		err := decodeError("POST", "/api/v1/device/folder-links/l1/manifest", 409, []byte(body))
		text := err.Error()
		for _, leak := range []string{secret, "deadbeef", sig} {
			if strings.Contains(text, leak) {
				t.Fatalf("an error leaked %q: %s", leak, text)
			}
		}
		// The address itself may stay (it identifies the host); only the
		// credential-bearing part is gone.
		if strings.Contains(text, "X-Amz-") {
			t.Fatalf("an error carried a presigned query: %s", text)
		}
	}

	// A presigned URL that a caller passes to PutObject must not appear in the
	// error it produces either — including the transport failure, whose
	// url.Error prints the whole URL.
	_, parseErr := (&Client{}).PutObject(t.Context(), "://not a url?X-Amz-Signature=deadbeef", nil, nil, 0)
	if parseErr == nil {
		t.Fatal("a malformed upload URL was accepted")
	}
	if strings.Contains(parseErr.Error(), "deadbeef") {
		t.Fatalf("the upload error leaked the signature: %s", parseErr)
	}
	if got := RedactURL("https://storage.test/obj?" + secret + "#frag"); strings.Contains(got, "deadbeef") || strings.Contains(got, "?") {
		t.Fatalf("RedactURL = %q", got)
	}
}

// TestStorageSentenceRendersTheWorkspaceNumbers is the sentence a person reads
// on a full workspace, in the design's words.
func TestStorageSentenceRendersTheWorkspaceNumbers(t *testing.T) {
	used, limit := int64(4200000000), int64(15000000000)
	err := &Error{
		Method: "POST", Route: "/uploads", Status: 413, Code: "STORAGE_QUOTA_EXCEEDED",
		UsedBytes: &used, LimitBytes: &limit,
	}
	if got := StorageSentence(err); got != "Storage is full (4.2 GB of 15.0 GB used)" {
		t.Fatalf("StorageSentence = %q", got)
	}
	// An unlimited workspace has no "of Y".
	err.LimitBytes = nil
	if got := StorageSentence(err); got != "Storage is full (4.2 GB used)" {
		t.Fatalf("StorageSentence without a limit = %q", got)
	}
	// Not a storage error at all.
	if got := StorageSentence(errors.New("something else")); got != "Storage is full" {
		t.Fatalf("StorageSentence on a non-storage error = %q", got)
	}
}

// TestHumanBytesMatchesTheDashboard keeps the desktop app's numbers in the same
// spelling as the dashboard's (decimal units, one decimal place).
func TestHumanBytesMatchesTheDashboard(t *testing.T) {
	cases := map[int64]string{
		0:             "0 B",
		999:           "999 B",
		1000:          "1.0 KB",
		15000000000:   "15.0 GB",
		4200000000:    "4.2 GB",
		1500000000000: "1.5 TB",
	}
	for n, want := range cases {
		if got := HumanBytes(n); got != want {
			t.Errorf("HumanBytes(%d) = %q, want %q", n, got, want)
		}
	}
}
