package identity

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"log"
	"os"
	"runtime"
	"strings"
	"testing"

	"github.com/zalando/go-keyring"
)

// fakeRing is a secret store that can be made to fail the way a headless Linux
// box without a D-Bus session does.
type fakeRing struct {
	values  map[string]string
	setErr  error
	getErr  error
	delErr  error
	deletes []string
}

func newFakeRing() *fakeRing { return &fakeRing{values: map[string]string{}} }

func (f *fakeRing) Set(service, user, secret string) error {
	if f.setErr != nil {
		return f.setErr
	}
	f.values[service+"/"+user] = secret
	return nil
}

func (f *fakeRing) Get(service, user string) (string, error) {
	if f.getErr != nil {
		return "", f.getErr
	}
	v, ok := f.values[service+"/"+user]
	if !ok {
		return "", keyring.ErrNotFound
	}
	return v, nil
}

func (f *fakeRing) Delete(service, user string) error {
	if f.delErr != nil {
		return f.delErr
	}
	f.deletes = append(f.deletes, user)
	delete(f.values, service+"/"+user)
	return nil
}

func testKey(t *testing.T) ed25519.PrivateKey {
	t.Helper()
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return priv
}

func TestSaveAndLoadThroughTheKeyring(t *testing.T) {
	ring := newFakeRing()
	s := NewWithRing(t.TempDir(), ring, log.New(&bytes.Buffer{}, "", 0))
	key := testKey(t)

	if err := s.Save("device-1", key); err != nil {
		t.Fatalf("Save: %v", err)
	}
	got, err := s.Load("device-1")
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if !got.Equal(key) {
		t.Fatal("the loaded key differs from the stored one")
	}
	if _, err := os.Stat(s.KeyPath()); !errors.Is(err, os.ErrNotExist) {
		t.Error("a working keyring must not leave a key file behind")
	}
	if s.Describe() != "os keyring" {
		t.Errorf("Describe = %q", s.Describe())
	}
	if s.Label() != "Saved in your system's password store" {
		t.Errorf("Label = %q", s.Label())
	}
}

func TestKeyringFailureFallsBackToAPrivateFileAndWarns(t *testing.T) {
	var logs bytes.Buffer
	ring := newFakeRing()
	ring.setErr = errors.New("no D-Bus session bus")
	dir := t.TempDir()
	s := NewWithRing(dir, ring, log.New(&logs, "", 0))
	key := testKey(t)

	if err := s.Save("device-1", key); err != nil {
		t.Fatalf("Save: %v", err)
	}
	info, err := os.Stat(s.KeyPath())
	if err != nil {
		t.Fatalf("the fallback file was not written: %v", err)
	}
	if perm := info.Mode().Perm(); runtime.GOOS != "windows" && perm != 0o600 { // POSIX modes do not apply on Windows
		t.Errorf("key file mode = %o, want 600", perm)
	}
	if !strings.Contains(logs.String(), "keyring is unavailable") {
		t.Errorf("the fallback was not reported; log was %q", logs.String())
	}
	if strings.Contains(logs.String(), "PRIVATE") || strings.Contains(logs.String(), "privateKey") {
		t.Errorf("the log leaked key material: %q", logs.String())
	}
	if s.Describe() != "file ("+s.KeyPath()+")" {
		t.Errorf("Describe = %q", s.Describe())
	}
	if s.Label() != "Saved in a private file on this computer" {
		t.Errorf("Label = %q", s.Label())
	}

	// A later Load must find it, even though the keyring claims not to have it.
	got, err := s.Load("device-1")
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if !got.Equal(key) {
		t.Fatal("the loaded key differs from the stored one")
	}
}

func TestLoadPrefersTheKeyringOverTheFile(t *testing.T) {
	ring := newFakeRing()
	dir := t.TempDir()
	s := NewWithRing(dir, ring, log.New(&bytes.Buffer{}, "", 0))
	ringKey := testKey(t)
	fileKey := testKey(t)

	// Write the file first (through a store whose keyring fails), then the
	// keyring, and confirm the keyring wins.
	failing := NewWithRing(dir, &fakeRing{setErr: errors.New("unavailable")}, nil)
	if err := failing.Save("device-1", fileKey); err != nil {
		t.Fatal(err)
	}
	if err := s.Save("device-1", ringKey); err != nil {
		t.Fatal(err)
	}
	got, err := s.Load("device-1")
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if !got.Equal(ringKey) {
		t.Error("Load preferred the file over the keyring")
	}
	// And a successful keyring write must clear the stale file: it holds a key
	// for a device that has moved on.
	if _, err := os.Stat(s.KeyPath()); !errors.Is(err, os.ErrNotExist) {
		t.Error("a stale key file survived a successful keyring write")
	}
}

func TestLoadWithoutAKey(t *testing.T) {
	s := NewWithRing(t.TempDir(), newFakeRing(), log.New(&bytes.Buffer{}, "", 0))
	if _, err := s.Load("device-1"); !errors.Is(err, ErrNoKey) {
		t.Fatalf("Load = %v, want ErrNoKey", err)
	}
}

func TestFileHoldingAnotherDeviceIsRefused(t *testing.T) {
	dir := t.TempDir()
	s := NewWithRing(dir, &fakeRing{setErr: errors.New("unavailable")}, nil)
	key := testKey(t)
	if err := s.Save("device-1", key); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Load("device-2"); err == nil {
		t.Fatal("a key file for another device must not be used")
	}
}

func TestDeleteRemovesBothCopies(t *testing.T) {
	ring := newFakeRing()
	dir := t.TempDir()
	s := NewWithRing(dir, ring, log.New(&bytes.Buffer{}, "", 0))
	key := testKey(t)
	if err := s.Save("device-1", key); err != nil {
		t.Fatal(err)
	}
	// A copy in the file as well, as a machine that lost its keyring once has.
	failing := NewWithRing(dir, &fakeRing{setErr: errors.New("unavailable")}, nil)
	if err := failing.Save("device-1", key); err != nil {
		t.Fatal(err)
	}
	if err := s.Delete("device-1"); err != nil {
		t.Fatalf("Delete: %v", err)
	}
	if _, err := s.Load("device-1"); !errors.Is(err, ErrNoKey) {
		t.Fatalf("Load after Delete = %v, want ErrNoKey", err)
	}
	if len(ring.deletes) != 1 {
		t.Errorf("keyring delete calls = %d, want 1", len(ring.deletes))
	}
}

func TestSaveRejectsANonEd25519Key(t *testing.T) {
	s := NewWithRing(t.TempDir(), newFakeRing(), nil)
	if err := s.Save("device-1", []byte("too short")); err == nil {
		t.Fatal("Save accepted a key that is not an Ed25519 private key")
	}
}

func TestSaveNeedsADeviceID(t *testing.T) {
	s := NewWithRing(t.TempDir(), newFakeRing(), nil)
	if err := s.Save("", testKey(t)); err == nil {
		t.Fatal("Save accepted an empty device id")
	}
}

func TestStoredSecretIsTheSeedNotTheExpandedKey(t *testing.T) {
	ring := newFakeRing()
	s := NewWithRing(t.TempDir(), ring, nil)
	key := testKey(t)
	if err := s.Save("device-1", key); err != nil {
		t.Fatal(err)
	}
	secret := ring.values[ServiceName+"/device-1"]
	if len(secret) == 0 {
		t.Fatal("nothing was stored")
	}
	// 32 bytes base64 is 44 characters; the 64-byte expanded key would be 88.
	if len(secret) > 64 {
		t.Errorf("the stored secret looks like the expanded key (%d chars)", len(secret))
	}
}
