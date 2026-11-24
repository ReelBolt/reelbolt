package services

import (
	"bytes"
	"errors"
	"fmt"
	"log"
	"net"
	"net/textproto"
	"strings"
	"testing"
)

// C4: the shared vectors, asserted verbatim in the C# and the Go suites.
func TestMailLogRef_SharedVectors(t *testing.T) {
	vectors := []struct{ key, addr, want string }{
		{"test-log-key", "Victim+x@Gmail.com", "rcpt=aa31e47adc42"},
		{"other-key", "Victim+x@Gmail.com", "rcpt=c5ed0310f4ca"},
		// Canonicalisation happens before the HMAC: variants of one inbox share a reference.
		{"test-log-key", " v.i.c.t.i.m@googlemail.com ", "rcpt=aa31e47adc42"},
	}
	for _, v := range vectors {
		if got := mailLogRefWithKey([]byte(v.key), v.addr); got != v.want {
			t.Errorf("ref(%q, %q) = %q, want %q", v.key, v.addr, got, v.want)
		}
	}
}

func TestMailLogRef_UsesTheProcessKey(t *testing.T) {
	setMailLogRefKeyForTest(t, []byte("test-log-key"))
	if got := MailLogRef("victim@gmail.com"); got != "rcpt=aa31e47adc42" {
		t.Fatalf("MailLogRef = %q", got)
	}
}

// Unset MAIL_LOG_REF_KEY: a random 32-byte per-process key and exactly one warning, never unkeyed.
func TestLoadMailLogRefKey(t *testing.T) {
	var warnings []string
	logf := func(format string, args ...any) { warnings = append(warnings, fmt.Sprintf(format, args...)) }

	key := loadMailLogRefKey(func(string) string { return "  configured-secret " }, logf)
	if string(key) != "configured-secret" || len(warnings) != 0 {
		t.Fatalf("configured: key %q warnings %v", key, warnings)
	}

	a := loadMailLogRefKey(func(string) string { return "" }, logf)
	b := loadMailLogRefKey(func(string) string { return "   " }, logf)
	if len(a) != 32 || len(b) != 32 || bytes.Equal(a, b) {
		t.Fatalf("unset key must be 32 random bytes per process: %x %x", a, b)
	}
	if len(warnings) != 2 || !strings.Contains(warnings[0], "MAIL_LOG_REF_KEY") {
		t.Fatalf("one warning per load: %v", warnings)
	}
	if mailLogRefWithKey(a, "victim@gmail.com") == "rcpt=aa31e47adc42" {
		t.Fatal("the random key produced the test-key reference")
	}
}

func TestSanitizeMailError(t *testing.T) {
	smtpErr := &textproto.Error{Code: 550, Msg: "5.1.1 <victim@example.com>: user unknown"}
	tests := []struct {
		name     string
		err      error
		contains []string
	}{
		{"smtp permanent", smtpErr, []string{"class=smtp_permanent", "status=550", "enhanced=5.1.1"}},
		{"wrapped smtp", fmt.Errorf("send to victim@example.com: %w", smtpErr), []string{"status=550", "enhanced=5.1.1"}},
		{"smtp transient without enhanced code", &textproto.Error{Code: 451, Msg: "try again, victim@example.com"}, []string{"class=smtp_transient", "status=451"}},
		{"net", &net.OpError{Op: "dial", Net: "tcp", Err: errors.New("connection refused")}, []string{"class=net", "op=dial"}},
		{"plain text", errors.New("550 5.1.1 <victim@example.com>: user unknown"), []string{"550 5.1.1", "[redacted]", "user unknown"}},
		{"plain without address", errors.New("smtp down"), []string{"smtp down"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := SanitizeMailError(tc.err)
			if strings.Contains(got, "@") || strings.Contains(got, "victim") {
				t.Fatalf("leaks the address: %q", got)
			}
			for _, want := range tc.contains {
				if !strings.Contains(got, want) {
					t.Fatalf("%q lacks %q", got, want)
				}
			}
		})
	}
	if SanitizeMailError(nil) != "" {
		t.Fatal("nil error")
	}
}

// captureStdLog redirects the standard logger for one test.
func captureStdLog(t *testing.T) *bytes.Buffer {
	t.Helper()
	var buf bytes.Buffer
	old, flags := log.Writer(), log.Flags()
	log.SetOutput(&buf)
	t.Cleanup(func() { log.SetOutput(old); log.SetFlags(flags) })
	return &buf
}

// F-4 / console mailer: outside development only the reference is logged — no address, no link.
func TestConsoleMailer_LogsOnlyTheRefOutsideDevelopment(t *testing.T) {
	setMailLogRefKeyForTest(t, []byte("test-log-key"))
	link := "https://reelbolt.test/app/verify-email?token=SECRETTOKEN"
	for _, env := range []string{"production", "staging", ""} {
		buf := captureStdLog(t)
		m := NewSMTPMailer(MailerConfig{AppEnv: env, PublicBaseURL: "https://reelbolt.test"})
		if err := m.SendTemplate("Victim+x@Gmail.com", TemplateVerifyEmail, MailData{Link: link, ExpiresIn: "24 hours"}); err != nil {
			t.Fatal(err)
		}
		out := buf.String()
		if strings.Contains(out, "@") || strings.Contains(strings.ToLower(out), "victim") || strings.Contains(out, "SECRETTOKEN") {
			t.Fatalf("env %q: console line leaks: %s", env, out)
		}
		if !strings.Contains(out, "rcpt=aa31e47adc42") || !strings.Contains(out, "verify_email") {
			t.Fatalf("env %q: console line lacks the ref or template: %s", env, out)
		}
	}
}

func TestConsoleMailer_PrintsTheLinkOnlyInDevelopmentAndNeverForMarketing(t *testing.T) {
	setMailLogRefKeyForTest(t, []byte("test-log-key"))
	m := NewSMTPMailer(MailerConfig{AppEnv: "development", PublicBaseURL: "https://reelbolt.test"})

	buf := captureStdLog(t)
	link := "https://reelbolt.test/app/verify-email?token=DEVTOKEN"
	if err := m.SendTemplate("ada@example.com", TemplateVerifyEmail, MailData{Link: link, ExpiresIn: "24 hours"}); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(buf.String(), link) || strings.Contains(buf.String(), "ada@") {
		t.Fatalf("development console line: %s", buf.String())
	}

	buf = captureStdLog(t)
	confirm := "https://reelbolt.test/app/marketing/confirm?token=MKTTOKEN"
	if err := m.SendTemplate("ada@example.com", TemplateMarketingConfirm, MailData{Link: confirm}); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(buf.String(), "MKTTOKEN") || strings.Contains(buf.String(), "@") {
		t.Fatalf("a marketing template's link was logged: %s", buf.String())
	}
}

// The welcome wrapper logs a failed send by reference with a sanitised error.
func TestLogMailFailure_NoAddress(t *testing.T) {
	setMailLogRefKeyForTest(t, []byte("test-log-key"))
	buf := captureStdLog(t)
	LogMailFailure("welcome", "Victim+x@Gmail.com", &textproto.Error{Code: 550, Msg: "5.1.1 <victim@gmail.com>: user unknown"})
	out := buf.String()
	if strings.Contains(out, "@") || !strings.Contains(out, "rcpt=aa31e47adc42") || !strings.Contains(out, "550") {
		t.Fatalf("log: %s", out)
	}
}
