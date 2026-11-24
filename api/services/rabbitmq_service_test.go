package services

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"
)

// The relay's one hard promise: the media-derivative event carries the source object's storage key,
// and no client-facing response may carry a storage key. This is the test that fails if somebody
// replaces the projection with a verbatim relay.
func TestProjectMediaDerivativeProgress_NeverRelaysTheSourceStorageKey(t *testing.T) {
	body := json.RawMessage(`{
		"projectId": "11111111-1111-1111-1111-111111111111",
		"sourceProjectFileId": "22222222-2222-2222-2222-222222222222",
		"sourceStorageKey": "projects/secret/userFiles/clip.mp4",
		"cacheKey": "deadbeefdeadbeef",
		"stage": "Proxy",
		"percentComplete": 42,
		"detail": "encoding",
		"occurredAt": "2025-01-01T00:00:00Z"
	}`)

	projected, projectID, ok := projectMediaDerivativeProgress(body)
	if !ok {
		t.Fatalf("expected the projection to succeed")
	}
	if projectID != "11111111-1111-1111-1111-111111111111" {
		t.Fatalf("unexpected project id %q", projectID)
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(projected, &fields); err != nil {
		t.Fatalf("projected payload is not valid JSON: %v", err)
	}

	for _, forbidden := range []string{"sourceStorageKey", "cacheKey", "storageKey", "storageBucket"} {
		if _, present := fields[forbidden]; present {
			t.Fatalf("%q reached the client payload: %s", forbidden, projected)
		}
	}

	// The leak check above is only meaningful if the payload is otherwise intact.
	var got mediaDerivativeProgressPayload
	if err := json.Unmarshal(projected, &got); err != nil {
		t.Fatalf("projected payload does not decode to the expected shape: %v", err)
	}
	if got.Stage != "Proxy" {
		t.Fatalf("stage lost: %q", got.Stage)
	}
	if got.PercentComplete == nil || *got.PercentComplete != 42 {
		t.Fatalf("percent lost: %v", got.PercentComplete)
	}
	if got.SourceProjectFileID != "22222222-2222-2222-2222-222222222222" {
		t.Fatalf("source project file id lost: %q", got.SourceProjectFileID)
	}
	if got.Detail != "encoding" {
		t.Fatalf("detail lost: %q", got.Detail)
	}
	if len(got.OccurredAt) == 0 {
		t.Fatalf("occurredAt lost")
	}
}

// MassTransit's serializer spelling is not pinned anywhere in this repo, and extractExecutionID
// already has to try both spellings. The projection must read either, or it would silently drop
// every event the day the serializer's options change.
func TestProjectMediaDerivativeProgress_ReadsPascalCaseAsWellAsCamelCase(t *testing.T) {
	body := json.RawMessage(`{
		"ProjectId": "11111111-1111-1111-1111-111111111111",
		"SourceStorageKey": "projects/secret/userFiles/clip.mp4",
		"Stage": "Queued",
		"PercentComplete": 7
	}`)

	projected, projectID, ok := projectMediaDerivativeProgress(body)
	if !ok {
		t.Fatalf("expected the PascalCase body to project")
	}
	if projectID != "11111111-1111-1111-1111-111111111111" {
		t.Fatalf("unexpected project id %q", projectID)
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(projected, &fields); err != nil {
		t.Fatalf("projected payload is not valid JSON: %v", err)
	}
	if _, present := fields["sourceStorageKey"]; present {
		t.Fatalf("storage key reached the client payload: %s", projected)
	}

	var got mediaDerivativeProgressPayload
	if err := json.Unmarshal(projected, &got); err != nil {
		t.Fatalf("projected payload does not decode: %v", err)
	}
	if got.Stage != "Queued" || got.PercentComplete == nil || *got.PercentComplete != 7 {
		t.Fatalf("PascalCase fields not read: %+v", got)
	}
}

// A body that cannot be scoped to an owner must be dropped, not broadcast. An unscoped frame would
// go to every subscriber of the stream.
func TestProjectMediaDerivativeProgress_RefusesWhatCannotBeScoped(t *testing.T) {
	for name, body := range map[string]json.RawMessage{
		"no project id": json.RawMessage(`{"sourceStorageKey":"projects/secret","stage":"Proxy"}`),
		"empty project": json.RawMessage(`{"projectId":"","stage":"Proxy"}`),
		"not json":      json.RawMessage(`not json at all`),
		"not an object": json.RawMessage(`[1,2,3]`),
	} {
		if _, _, ok := projectMediaDerivativeProgress(body); ok {
			t.Fatalf("%s: expected the projection to refuse the body", name)
		}
	}
}

// A run-level transition (a MIME-type skip, a stale message) has no cache key, and the UI must show
// the stage alone rather than a bogus percentage.
func TestProjectMediaDerivativeProgress_OmitsAbsentOptionals(t *testing.T) {
	body := json.RawMessage(`{
		"projectId": "11111111-1111-1111-1111-111111111111",
		"sourceStorageKey": "projects/secret/userFiles/clip.mp4",
		"stage": "Skipped"
	}`)

	projected, _, ok := projectMediaDerivativeProgress(body)
	if !ok {
		t.Fatalf("expected the projection to succeed")
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(projected, &fields); err != nil {
		t.Fatalf("projected payload is not valid JSON: %v", err)
	}
	for _, absent := range []string{"percentComplete", "detail", "occurredAt", "sourceProjectFileId"} {
		if _, present := fields[absent]; present {
			t.Fatalf("%q should be omitted when absent, payload: %s", absent, projected)
		}
	}
}

// The relay's second hard promise, and the one a whitelist over field NAMES cannot make. `detail`
// is producer-authored free text, and the engine writes things into it that are legitimate log copy
// and are not browser copy: a wrapped ffprobe/ffmpeg failure surfaces as the exception message, which
// embeds the local scratch path the tool was handed followed by a tail of raw process stderr.
//
// This body is what such a failure actually looks like, and the client must receive the stage and the
// percentage WITHOUT that text. It is the test that fails if somebody relays `detail` verbatim again.
func TestProjectMediaDerivativeProgress_NeverRelaysAPathOrRawToolOutputInDetail(t *testing.T) {
	stderrTail := "ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers\n" +
		"  built with gcc 13.2.1 (Alpine 13.2.1_git20231014)\n" +
		"[mov,mp4,m4a,3gp,3g2,mj2 @ 0x55d0a1f2c300] moov atom not found\n" +
		"/data/reelforge/scratch/derivatives/7f3a9c21/clip.mp4: Invalid data found when processing input"

	body, err := json.Marshal(map[string]any{
		"projectId":           "11111111-1111-1111-1111-111111111111",
		"sourceProjectFileId": "22222222-2222-2222-2222-222222222222",
		"stage":               "Failed",
		"percentComplete":     12,
		"detail": "ffprobe failed for '/data/reelforge/scratch/derivatives/7f3a9c21/clip.mp4' " +
			"(exitCode=1, timedOut=False). stderr: " + stderrTail,
	})
	if err != nil {
		t.Fatalf("building the body failed: %v", err)
	}

	projected, projectID, ok := projectMediaDerivativeProgress(body)
	if !ok {
		t.Fatalf("expected the projection to succeed")
	}
	if projectID != "11111111-1111-1111-1111-111111111111" {
		t.Fatalf("unexpected project id %q", projectID)
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(projected, &fields); err != nil {
		t.Fatalf("projected payload is not valid JSON: %v", err)
	}
	if _, present := fields["detail"]; present {
		t.Fatalf("a path-bearing detail reached the client payload: %s", projected)
	}

	// Dropping the text must not cost the frame its useful, non-text content — otherwise "sanitise"
	// would have quietly turned into "silence every failure".
	var got mediaDerivativeProgressPayload
	if err := json.Unmarshal(projected, &got); err != nil {
		t.Fatalf("projected payload does not decode to the expected shape: %v", err)
	}
	if got.Stage != "Failed" {
		t.Fatalf("stage lost: %q", got.Stage)
	}
	if got.PercentComplete == nil || *got.PercentComplete != 12 {
		t.Fatalf("percent lost: %v", got.PercentComplete)
	}
}

// The rules behind the promise above, one row per rule, and the boundaries of each. Kept as a table
// so tightening or loosening a rule is a one-line edit with a failing row, rather than a rewrite.
func TestSanitizeProgressDetail(t *testing.T) {
	// A long single-line tool counter dump: no separator, no control character, so this row isolates
	// the length rule from the other two.
	longSingleLine := strings.Repeat("frame= 1234 fps=0.0 q=28.0 size= 512kB ", 60)

	cases := map[string]struct {
		in   string
		want string
	}{
		"authored copy survives verbatim": {
			"Cache hit — a live media_derivatives row already owns this cache key.",
			"Cache hit — a live media_derivatives row already owns this cache key.",
		},
		"empty stays empty": {"", ""},

		// Separator rule.
		"absolute posix path":     {"ffprobe failed for '/data/scratch/clip.mp4'", ""},
		"relative path":           {"could not read projects/abc/userFiles/clip.mp4", ""},
		"windows drive path":      {`open C:\Users\runner\AppData\clip.mp4: denied`, ""},
		"unc path":                {`failed reading \\build-host\share\clip.mp4`, ""},
		"file url":                {"cannot open file:///data/scratch/clip.mp4", ""},
		"object storage key":      {"resolved s3://reelforge/outputFiles/clip.mp4", ""},
		"bare separator anywhere": {"odd/odd", ""},

		// Control-character rule: multi-line text is a stderr dump, not authored copy.
		"newline":          {"ffmpeg version 7.1\nbuilt with gcc", ""},
		"carriage return":  {"stage one\rstage two", ""},
		"tab":              {"frame= 1\tfps=0.0", ""},
		"zero width space": {"Produced proxy\u200b variant 480p", ""},
		"null byte":        {"Produced proxy\x00variant 480p", ""},
		"escape":           {"Produced proxy\x1b[0m variant 480p", ""},

		// Length rule.
		"long single-line tool dump":      {longSingleLine, ""},
		"exactly at the rune ceiling":     {strings.Repeat("a", progressDetailMaxRunes), strings.Repeat("a", progressDetailMaxRunes)},
		"one rune past the ceiling":       {strings.Repeat("a", progressDetailMaxRunes+1), ""},
		"ceiling counts runes, not bytes": {strings.Repeat("—", progressDetailMaxRunes), strings.Repeat("—", progressDetailMaxRunes)},

		// The documented, accepted cost of the separator rule — pinned so that "recover the MIME
		// sentence" has to be a deliberate edit here, not a silent re-opening of the path channel.
		"mime sentence is dropped for its slash": {
			"No derivatives are defined for MIME type 'video/quicktime'.",
			"",
		},
	}

	for name, tc := range cases {
		if got := sanitizeProgressDetail(tc.in); got != tc.want {
			t.Fatalf("%s: sanitizeProgressDetail(%q) = %q, want %q", name, tc.in, got, tc.want)
		}
	}
}

// The sanitizer is a relay-side rule, so it must hold for the PascalCase spelling too — the same
// reason the whitelist itself reads both spellings. A shortcut that only sanitised the camelCase
// branch would leave the PascalCase one wide open the day the serializer's options change.
func TestProjectMediaDerivativeProgress_SanitisesPascalCaseDetailToo(t *testing.T) {
	body := json.RawMessage(`{
		"ProjectId": "11111111-1111-1111-1111-111111111111",
		"Stage": "Failed",
		"Detail": "ffprobe failed for '/data/reelforge/scratch/derivatives/7f3a9c21/clip.mp4'"
	}`)

	projected, _, ok := projectMediaDerivativeProgress(body)
	if !ok {
		t.Fatalf("expected the PascalCase body to project")
	}

	if strings.Contains(string(projected), "scratch") {
		t.Fatalf("a scratch path reached the client payload: %s", projected)
	}
	if strings.Contains(string(projected), "detail") {
		t.Fatalf("a path-bearing detail reached the client payload: %s", projected)
	}
}

// A detail the producer wrote for a human must still arrive, or the sanitizer has become a mute
// button rather than a guard.
func TestProjectMediaDerivativeProgress_KeepsAuthoredDetail(t *testing.T) {
	body := json.RawMessage(`{
		"projectId": "11111111-1111-1111-1111-111111111111",
		"stage": "Skipped",
		"detail": "Cache hit — a live media_derivatives row already owns this cache key."
	}`)

	projected, _, ok := projectMediaDerivativeProgress(body)
	if !ok {
		t.Fatalf("expected the projection to succeed")
	}

	var got mediaDerivativeProgressPayload
	if err := json.Unmarshal(projected, &got); err != nil {
		t.Fatalf("projected payload does not decode: %v", err)
	}
	if got.Detail != "Cache hit — a live media_derivatives row already owns this cache key." {
		t.Fatalf("authored detail lost: %q", got.Detail)
	}
}

func TestStopConsumerNeverStartedReturnsAtOnce(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	start := time.Now()
	StopRabbitMQConsumer(ctx)
	if time.Since(start) > 500*time.Millisecond {
		t.Fatal("stop blocked although no consumer was started")
	}
}

func TestWaitNotificationsWaitsForInFlightMailBounded(t *testing.T) {
	release := make(chan struct{})
	notifyWG.Add(1)
	go func() { defer notifyWG.Done(); <-release }()

	short, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	start := time.Now()
	WaitNotifications(short)
	cancel()
	if time.Since(start) < 40*time.Millisecond {
		t.Fatal("returned while a notification was still in flight")
	}

	close(release)
	long, cancel2 := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel2()
	start = time.Now()
	WaitNotifications(long)
	if time.Since(start) > time.Second {
		t.Fatal("did not return once the notification finished")
	}
}
