package services

import (
	"bytes"
	"log"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func captureLog(t *testing.T) *bytes.Buffer {
	t.Helper()
	var buf bytes.Buffer
	prevOut, prevFlags := log.Writer(), log.Flags()
	log.SetOutput(&buf)
	log.SetFlags(0)
	t.Cleanup(func() {
		log.SetOutput(prevOut)
		log.SetFlags(prevFlags)
	})
	return &buf
}

func TestLogAdminBootstrap_GeneratedPasswordNamesTheFileNotThePassword(t *testing.T) {
	buf := captureLog(t)
	logAdminBootstrap("admin@example.test", "/data/bootstrap-admin-password", true, true)

	out := buf.String()
	if !strings.Contains(out, "/data/bootstrap-admin-password") {
		t.Fatalf("the log must name the file holding the generated password; got:\n%s", out)
	}
	if !strings.Contains(out, "admin@example.test") {
		t.Fatalf("the admin email should be logged; got:\n%s", out)
	}
}

func TestWriteBootstrapPassword_Mode0600AndContent(t *testing.T) {
	path := filepath.Join(t.TempDir(), "sub", "bootstrap-admin-password")
	// A pre-existing, world-readable file must end up 0600 and overwritten.
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("stale"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := writeBootstrapPassword(path, "generated-secret-value"); err != nil {
		t.Fatalf("writeBootstrapPassword: %v", err)
	}
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if mode := info.Mode().Perm(); mode != 0o600 {
		t.Fatalf("mode = %o, want 600", mode)
	}
	b, _ := os.ReadFile(path)
	if string(b) != "generated-secret-value\n" {
		t.Fatalf("content = %q", b)
	}
}

func TestWriteBootstrapPassword_FailsOnUnwritablePath(t *testing.T) {
	dir := t.TempDir()
	blocker := filepath.Join(dir, "file")
	if err := os.WriteFile(blocker, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := writeBootstrapPassword(filepath.Join(blocker, "pw"), "x"); err == nil {
		t.Fatal("expected an error when the parent is not a directory")
	}
}

func TestBootstrapPasswordFile_DefaultAndOverride(t *testing.T) {
	t.Setenv("ADMIN_PASSWORD_FILE", "")
	if got := bootstrapPasswordFile(); got != defaultBootstrapPasswordFile {
		t.Fatalf("default = %q", got)
	}
	t.Setenv("ADMIN_PASSWORD_FILE", "/elsewhere/pw")
	if got := bootstrapPasswordFile(); got != "/elsewhere/pw" {
		t.Fatalf("override = %q", got)
	}
}

func TestLogAdminBootstrap_OperatorSuppliedPasswordIsNeverLogged(t *testing.T) {
	buf := captureLog(t)
	logAdminBootstrap("admin@example.test", "", false, true)

	out := buf.String()
	if !strings.Contains(out, "ADMIN_PASSWORD") {
		t.Fatalf("the log should say where the password came from; got:\n%s", out)
	}
}

func TestWriteBootstrapPassword_RefusesSymlink(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "target")
	if err := os.WriteFile(target, []byte("keep"), 0o644); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(dir, "pw")
	if err := os.Symlink(target, link); err != nil {
		t.Fatal(err)
	}
	if err := writeBootstrapPassword(link, "generated-secret-value"); err == nil {
		t.Fatal("expected a symlink at the password path to be refused")
	}
	if b, _ := os.ReadFile(target); string(b) != "keep" {
		t.Fatalf("the symlink target was written through: %q", b)
	}
}

func TestWriteBootstrapPassword_RefusesDirectory(t *testing.T) {
	if err := writeBootstrapPassword(t.TempDir(), "x"); err == nil {
		t.Fatal("expected a directory at the password path to be refused")
	}
}
