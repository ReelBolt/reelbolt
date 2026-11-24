package hetzner

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"syscall"
)

const redacted = "[redacted]"

// Token is a Hetzner API token. Every way of printing it yields "[redacted]";
// the value is reachable only through reveal, which the transport alone calls.
type Token struct{ v string }

// NewToken wraps a token value.
func NewToken(v string) Token { return Token{v: strings.TrimSpace(v)} }

func (Token) String() string               { return redacted }
func (Token) GoString() string             { return redacted }
func (Token) MarshalJSON() ([]byte, error) { return []byte(`"` + redacted + `"`), nil }
func (Token) MarshalText() ([]byte, error) { return []byte(redacted), nil }
func (Token) Format(f fmt.State, _ rune)   { _, _ = f.Write([]byte(redacted)) }

// Empty reports whether no token is set.
func (t Token) Empty() bool { return t.v == "" }

func (t Token) reveal() string { return t.v }

// TokenFileRel is the token file's path under the home directory.
const TokenFileRel = ".config/reelbolt/hcloud.token"

// LoadToken reads HCLOUD_TOKEN from env, else the file HCLOUD_TOKEN_FILE names, else the token file
// under homeDir. A file must be a regular file, mode 0600 or stricter, owned by the current user. When
// HCLOUD_TOKEN_FILE is set it is the only file consulted: an unreadable one is an error, never a
// reason to look elsewhere. Error messages name the path, never the content.
func LoadToken(env func(string) string, homeDir string) (Token, error) {
	if env != nil {
		if v := strings.TrimSpace(env("HCLOUD_TOKEN")); v != "" {
			return NewToken(v), nil
		}
	}
	if env != nil {
		if path := strings.TrimSpace(env("HCLOUD_TOKEN_FILE")); path != "" {
			return readTokenFile(path)
		}
	}
	if homeDir == "" {
		return Token{}, errors.New("hetzner: no HCLOUD_TOKEN and no home directory for the token file")
	}
	return readTokenFile(filepath.Join(homeDir, TokenFileRel))
}

func readTokenFile(path string) (Token, error) {
	fi, err := os.Lstat(path)
	if err != nil {
		return Token{}, fmt.Errorf("hetzner: no HCLOUD_TOKEN and token file %s is not readable", path)
	}
	if !fi.Mode().IsRegular() {
		return Token{}, fmt.Errorf("hetzner: token file %s is not a regular file", path)
	}
	if fi.Mode().Perm()&0o077 != 0 {
		return Token{}, fmt.Errorf("hetzner: token file %s must be mode 0600 or stricter", path)
	}
	if st, ok := fi.Sys().(*syscall.Stat_t); ok && int(st.Uid) != os.Geteuid() {
		return Token{}, fmt.Errorf("hetzner: token file %s is not owned by the current user", path)
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return Token{}, fmt.Errorf("hetzner: token file %s is not readable", path)
	}
	t := NewToken(string(b))
	if t.Empty() {
		return Token{}, fmt.Errorf("hetzner: token file %s is empty", path)
	}
	return t, nil
}
