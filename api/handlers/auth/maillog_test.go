package auth

import (
	"bytes"
	"log"
	"net/textproto"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/services"
)

func captureAuthLog(t *testing.T) *bytes.Buffer {
	t.Helper()
	var buf bytes.Buffer
	old := log.Writer()
	log.SetOutput(&buf)
	t.Cleanup(func() { log.SetOutput(old) })
	return &buf
}

func rejectingSMTP(string, services.TemplateName, services.MailData) error {
	return &textproto.Error{Code: 550, Msg: "5.1.1 <new@example.com>: user unknown"}
}

// F-4: a failed verification mail logs the sanitised SMTP status, never the echoed address.
func TestSignup_VerificationMailFailureLogsNoAddress(t *testing.T) {
	setup(t, true)
	acct.sendMail = rejectingSMTP
	buf := captureAuthLog(t)
	if rec := post(handleSignup, goodSignup); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if out := buf.String(); strings.Contains(out, "@") || !strings.Contains(out, "550") {
		t.Fatalf("log: %s", out)
	}
}

// F-4: the deletion-scheduled mail failure is logged by reference with the error sanitised.
func TestAccountDelete_MailFailureLogsNoAddress(t *testing.T) {
	_, user, _, _ := deletionHarness(t)
	deletion.sendMail = rejectingSMTP
	buf := captureAuthLog(t)
	if rec := deleteAccount(t, user, `{"password":"correct horse battery"}`); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body)
	}
	if out := buf.String(); strings.Contains(out, "@") || !strings.Contains(out, "rcpt=") || !strings.Contains(out, "550") {
		t.Fatalf("log: %s", out)
	}
}

// F-4: the welcome mail of an invited signup logs a failure sanitised.
func TestInviteSignup_WelcomeMailFailureLogsNoAddress(t *testing.T) {
	e := newInviteEnv(t)
	acct.sendMail = rejectingSMTP
	buf := captureAuthLog(t)
	if rec := e.do("POST", "/api/v1/auth/signup", inviteSignupBody("new@example.com", e.token), ""); rec.Code != 200 {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if out := buf.String(); strings.Contains(out, "@") || !strings.Contains(out, "550") {
		t.Fatalf("log: %s", out)
	}
}
