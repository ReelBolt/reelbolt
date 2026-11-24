package internalmail

import (
	"bytes"
	"errors"
	"log"
	"net/textproto"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/services"
)

// captureLog redirects the standard logger into a buffer for one test.
func captureLog(t *testing.T) *bytes.Buffer {
	t.Helper()
	var buf bytes.Buffer
	oldOut, oldFlags := log.Writer(), log.Flags()
	log.SetOutput(&buf)
	t.Cleanup(func() {
		log.SetOutput(oldOut)
		log.SetFlags(oldFlags)
	})
	return &buf
}

// wantRef is the expected log reference: the keyed C4 reference (its vectors are pinned in services).
func wantRef(addr string) string {
	return services.MailLogRef(addr)
}

// The request uses mixed case and padding so a log of either the raw or the normalised form is caught.
const logBody = `{"to":"  Ada.Lovelace@Example.com ","template":"export_ready","data":{"name":"Ada","link":"https://reelbolt.test/x","expiresIn":"7 days"}}`

func assertNoAddress(t *testing.T, out string) {
	t.Helper()
	lower := strings.ToLower(out)
	for _, leak := range []string{"ada.lovelace", "example.com", "@"} {
		if strings.Contains(lower, leak) {
			t.Fatalf("log line leaks the recipient (%q): %s", leak, out)
		}
	}
	if !strings.Contains(out, wantRef("ada.lovelace@example.com")) {
		t.Fatalf("log line lacks reference %s: %s", wantRef("ada.lovelace@example.com"), out)
	}
}

func TestLog_RecipientLookupFailureDoesNotPrintTheAddress(t *testing.T) {
	withDeps(t, "s3cret", true)
	d.userExists = func(string) (bool, error) { return false, errors.New("db down") }
	buf := captureLog(t)

	rec := send(t, "Bearer s3cret", logBody)
	if rec.Code != 503 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	assertNoAddress(t, buf.String())
}

func TestLog_SendFailureDoesNotPrintTheAddress(t *testing.T) {
	withDeps(t, "s3cret", true)
	d.sendMail = func(string, services.TemplateName, services.MailData) error { return errors.New("smtp down") }
	buf := captureLog(t)

	rec := send(t, "Bearer s3cret", logBody)
	if rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if !strings.Contains(buf.String(), "smtp down") {
		t.Fatalf("send failure not logged: %s", buf.String())
	}
	assertNoAddress(t, buf.String())
}

// F-4: an SMTP rejection echoing the recipient is logged as status, enhanced code and class only.
func TestLog_SMTPErrorEchoingTheAddressIsSanitised(t *testing.T) {
	for _, err := range []error{
		&textproto.Error{Code: 550, Msg: "5.1.1 <victim@example.com>: user unknown"},
		errors.New("550 5.1.1 <victim@example.com>: user unknown"),
	} {
		withDeps(t, "s3cret", true)
		d.sendMail = func(string, services.TemplateName, services.MailData) error { return err }
		buf := captureLog(t)

		if rec := send(t, "Bearer s3cret", logBody); rec.Code != 202 {
			t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
		}
		out := buf.String()
		if strings.Contains(out, "victim") || !strings.Contains(out, "550") || !strings.Contains(out, "5.1.1") {
			t.Fatalf("SMTP error not sanitised: %s", out)
		}
		assertNoAddress(t, out)
	}
}

// F-4: the reference is keyed: it is not the unkeyed sha256 of the mailbox the round-2 code logged.
func TestLog_RecipientRefIsKeyed(t *testing.T) {
	unkeyed := "rcpt=" + sha256Hex(CanonicalMailbox("ada.lovelace@example.com"))[:12]
	if recipientRef("ada.lovelace@example.com") == unkeyed {
		t.Fatal("recipientRef is an unkeyed hash")
	}
	if recipientRef("ada.lovelace@example.com") != services.MailLogRef("Ada.Lovelace+x@Example.com") {
		t.Fatal("recipientRef does not use the shared C4 reference")
	}
}
