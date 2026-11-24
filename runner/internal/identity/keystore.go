// Package identity holds the runner's device key: the Ed25519 private key a
// paired device signs every hello with.
//
// The key never leaves the machine and is never written to the config file. It
// is kept in the operating system's secret store — Keychain, Credential Manager
// or the Secret Service — and only when that store is unavailable does the
// daemon fall back to a 0600 file next to the configuration, saying so loudly.
// A silent fallback would be worse than a refusal: a user who believes their key
// is protected by the OS login keyring should be told when it is not.
//
// Nothing here is a bearer token. The key is a proof of possession, so an
// attacker who reads the file still cannot impersonate the device from another
// machine without also being able to sign — but it IS enough to impersonate it
// from anywhere, which is exactly why the fallback warns.
package identity

import (
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"

	"github.com/zalando/go-keyring"
)

// ErrNoKey means no device key is stored for that device.
var ErrNoKey = errors.New("identity: no device key stored")

// KeyFileName is the fallback file, created 0600 beside runner.json.
const KeyFileName = "device.key"

// ServiceName is the keyring service every entry is filed under.
const ServiceName = "reelbolt-runner"

// ring is the OS secret store. It is an interface so the fallback can be tested
// without a session keyring, a D-Bus daemon or a Windows credential store.
type ring interface {
	Set(service, user, secret string) error
	Get(service, user string) (string, error)
	Delete(service, user string) error
}

// osRing is the real store (github.com/zalando/go-keyring): the macOS Keychain,
// the Windows Credential Manager and the Linux Secret Service.
type osRing struct{}

func (osRing) Set(service, user, secret string) error { return keyring.Set(service, user, secret) }
func (osRing) Get(service, user string) (string, error) {
	return keyring.Get(service, user)
}
func (osRing) Delete(service, user string) error { return keyring.Delete(service, user) }

// Logger is the one method the keystore needs from the daemon's logger.
type Logger interface {
	Printf(format string, args ...any)
}

// Store is the device key store: the OS keyring first, a 0600 file second.
type Store struct {
	dir  string
	ring ring
	log  Logger

	mu sync.Mutex
	// fileInUse records that the keyring has already failed in this process, so
	// the warning is printed once rather than on every operation.
	fileInUse bool
}

// Open builds the store. dir is the configuration directory
// (os.UserConfigDir()/ReelBolt). A nil logger means the warnings go nowhere,
// which is only acceptable in tests.
func Open(dir string, log Logger) *Store {
	return &Store{dir: dir, ring: osRing{}, log: log}
}

// NewWithRing builds a store around an injected secret store, for tests.
func NewWithRing(dir string, r ring, log Logger) *Store {
	return &Store{dir: dir, ring: r, log: log}
}

// KeyPath is where the fallback file lives.
func (s *Store) KeyPath() string { return filepath.Join(s.dir, KeyFileName) }

// Describe reports where the key currently lives, for `doctor` and `status`.
func (s *Store) Describe() string {
	if s.fileInUse {
		return "file (" + s.KeyPath() + ")"
	}
	return "os keyring"
}

// Label is where this computer's sign-in is kept, in words for the desktop
// window. Describe stays the technical answer for "doctor" and "status".
func (s *Store) Label() string {
	if s.fileInUse {
		return "Saved in a private file on this computer"
	}
	return "Saved in your system's password store"
}

// Save stores the device key. The keyring is tried first; if it refuses, the
// 0600 file is written and the fallback is reported once.
func (s *Store) Save(deviceID string, key ed25519.PrivateKey) error {
	if deviceID == "" {
		return errors.New("identity: a device key needs a device id")
	}
	secret, err := encodeKey(key)
	if err != nil {
		return err
	}
	if !s.fallbackActive() {
		if err := s.ring.Set(ServiceName, deviceID, secret); err == nil {
			// The key may have been in the file from an earlier run: leaving a
			// stale copy behind would keep an old key usable after a re-pair.
			if err := s.removeFile(); err != nil {
				return err
			}
			return nil
		} else {
			s.warnFallback(err)
		}
	}
	return s.writeFile(deviceID, secret)
}

// Load returns the device key, from the keyring if it is there and from the
// file otherwise. It returns ErrNoKey when neither holds one.
func (s *Store) Load(deviceID string) (ed25519.PrivateKey, error) {
	if deviceID == "" {
		return nil, errors.New("identity: a device key needs a device id")
	}
	if !s.fallbackActive() {
		secret, err := s.ring.Get(ServiceName, deviceID)
		if err == nil {
			return decodeKey(secret)
		}
		if !errors.Is(err, keyring.ErrNotFound) {
			s.warnFallback(err)
		}
	}
	secret, err := s.readFile(deviceID)
	if err != nil {
		return nil, err
	}
	return decodeKey(secret)
}

// Delete removes the device key from both stores. A missing key is not an error:
// unpairing twice is not a failure.
func (s *Store) Delete(deviceID string) error {
	if deviceID == "" {
		return nil
	}
	var firstErr error
	if err := s.ring.Delete(ServiceName, deviceID); err != nil && !errors.Is(err, keyring.ErrNotFound) {
		// A keyring that never worked will also refuse a delete; that is not
		// worth reporting when the file is the only real store.
		if !s.fallbackActive() {
			firstErr = err
		}
	}
	if err := s.removeFile(); err != nil && firstErr == nil {
		firstErr = err
	}
	return firstErr
}

func (s *Store) fallbackActive() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.fileInUse
}

func (s *Store) warnFallback(cause error) {
	s.mu.Lock()
	first := !s.fileInUse
	s.fileInUse = true
	s.mu.Unlock()
	if first && s.log != nil {
		s.log.Printf("warning: the operating system keyring is unavailable (%v); "+
			"the device key is stored in %s with 0600 permissions instead", cause, s.KeyPath())
	}
}

// fileContents is the fallback file's shape. The device id is stored with the
// key so a stale file from a previous pairing is detected rather than used.
type fileContents struct {
	DeviceID   string `json:"deviceId"`
	PrivateKey string `json:"privateKey"`
}

func (s *Store) writeFile(deviceID, secret string) error {
	if err := os.MkdirAll(s.dir, 0o700); err != nil {
		return fmt.Errorf("identity: create %s: %w", s.dir, err)
	}
	raw, err := json.Marshal(fileContents{DeviceID: deviceID, PrivateKey: secret})
	if err != nil {
		return fmt.Errorf("identity: marshal key file: %w", err)
	}
	path := s.KeyPath()
	tmp, err := os.CreateTemp(s.dir, KeyFileName+".tmp*")
	if err != nil {
		return fmt.Errorf("identity: temporary key file: %w", err)
	}
	tmpName := tmp.Name()
	defer func() { _ = os.Remove(tmpName) }()
	if err := tmp.Chmod(0o600); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("identity: chmod key file: %w", err)
	}
	if _, err := tmp.Write(raw); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("identity: write key file: %w", err)
	}
	if err := tmp.Close(); err != nil {
		return fmt.Errorf("identity: close key file: %w", err)
	}
	if err := os.Rename(tmpName, path); err != nil {
		return fmt.Errorf("identity: replace %s: %w", path, err)
	}
	return nil
}

func (s *Store) readFile(deviceID string) (string, error) {
	raw, err := os.ReadFile(s.KeyPath())
	if errors.Is(err, os.ErrNotExist) {
		return "", ErrNoKey
	}
	if err != nil {
		return "", fmt.Errorf("identity: read %s: %w", s.KeyPath(), err)
	}
	// Tighten a file somebody loosened; a group-readable device key is a
	// finding, and refusing to load it would strand the user instead.
	if info, statErr := os.Stat(s.KeyPath()); statErr == nil && info.Mode().Perm()&0o077 != 0 {
		if s.log != nil {
			s.log.Printf("warning: %s is readable by other users (mode %o); tightening to 0600",
				s.KeyPath(), info.Mode().Perm())
		}
		_ = os.Chmod(s.KeyPath(), 0o600)
	}
	var fc fileContents
	if err := json.Unmarshal(raw, &fc); err != nil {
		return "", fmt.Errorf("identity: parse %s: %w", s.KeyPath(), err)
	}
	if fc.DeviceID != deviceID {
		return "", fmt.Errorf("identity: %s holds the key of a different device", s.KeyPath())
	}
	return fc.PrivateKey, nil
}

func (s *Store) removeFile() error {
	err := os.Remove(s.KeyPath())
	if err == nil || errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return fmt.Errorf("identity: remove %s: %w", s.KeyPath(), err)
}

// encodeKey stores the 32-byte seed, not the 64-byte expanded key: the seed is
// the canonical secret and ed25519.NewKeyFromSeed reconstructs the same key.
func encodeKey(key ed25519.PrivateKey) (string, error) {
	if len(key) != ed25519.PrivateKeySize {
		return "", fmt.Errorf("identity: expected a %d-byte Ed25519 private key, got %d",
			ed25519.PrivateKeySize, len(key))
	}
	return base64.StdEncoding.EncodeToString(key.Seed()), nil
}

func decodeKey(secret string) (ed25519.PrivateKey, error) {
	seed, err := base64.StdEncoding.DecodeString(secret)
	if err != nil {
		return nil, fmt.Errorf("identity: stored key is not base64: %w", err)
	}
	if len(seed) != ed25519.SeedSize {
		return nil, fmt.Errorf("identity: stored key is %d bytes, expected %d", len(seed), ed25519.SeedSize)
	}
	return ed25519.NewKeyFromSeed(seed), nil
}
