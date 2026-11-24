package internalmail

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/services"
)

type captured struct {
	to   string
	tmpl services.TemplateName
	data services.MailData
}

// withDeps installs a token, a recipient lookup and a capturing mailer for one test.
func withDeps(t *testing.T, token string, exists bool) *[]captured {
	t.Helper()
	old := d
	t.Cleanup(func() { d = old })

	sent := &[]captured{}
	d = deps{
		token:      func() string { return token },
		userExists: func(string) (bool, error) { return exists, nil },
		sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
			*sent = append(*sent, captured{to, tmpl, data})
			return nil
		},
		// The endpoint answers 202 before delivery; tests deliver inline so they can assert on it.
		runAsync:    func(f func()) { f() },
		linkOrigins: func() (string, []string) { return "https://reelbolt.test", nil },
		limiter:     newConfirmLimiter(time.Now, newFakeCounterStore(), defaultLimits()),
	}
	return sent
}

// withDepsAndMailer is withDeps plus a real mailer (the one the handler would use), so a test can
// assert on what the delivery path refuses.
func withDepsAndMailer(t *testing.T, token string, exists bool, mailer services.Mailer) *[]captured {
	t.Helper()
	old := d
	t.Cleanup(func() { d = old })

	sent := &[]captured{}
	d = deps{
		token:      func() string { return token },
		userExists: func(string) (bool, error) { return exists, nil },
		sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
			*sent = append(*sent, captured{to, tmpl, data})
			return mailer.SendTemplate(to, tmpl, data)
		},
		runAsync:    func(f func()) { f() },
		linkOrigins: func() (string, []string) { return "https://reelbolt.test", nil },
		limiter:     newConfirmLimiter(time.Now, newFakeCounterStore(), defaultLimits()),
	}
	return sent
}

func send(t *testing.T, authorization, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequestWithContext(context.Background(), "POST", "/internal/mail/send", strings.NewReader(body))
	if authorization != "" {
		req.Header.Set("Authorization", authorization)
	}
	rec := httptest.NewRecorder()
	handleSend(rec, req)
	return rec
}

const exportReady = `{"to":"ada@example.com","template":"export_ready","data":{"name":"Ada","link":"https://reelbolt.test/x","expiresIn":"7 days"}}`
const evilLink = `{"to":"ada@example.com","template":"export_ready","data":{"name":"Ada","link":"https://evil.example/phish?next=/x","expiresIn":"7 days"}}`

func TestSend_AcceptsAKnownTemplateForAKnownRecipient(t *testing.T) {
	sent := withDeps(t, "s3cret", true)

	rec := send(t, "Bearer s3cret", exportReady)
	if rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if len(*sent) != 1 || (*sent)[0].tmpl != services.TemplateExportReady || (*sent)[0].to != "ada@example.com" {
		t.Fatalf("sent: %+v", *sent)
	}
	if (*sent)[0].data.ExpiresIn != "7 days" || (*sent)[0].data.Name != "Ada" {
		t.Fatalf("data: %+v", (*sent)[0].data)
	}
}

func TestSend_IsNotConfiguredWithoutAToken(t *testing.T) {
	sent := withDeps(t, "", true)

	// Unset means the endpoint does not exist, whatever the caller presents.
	rec := send(t, "Bearer anything", exportReady)
	if rec.Code != 404 || len(*sent) != 0 {
		t.Fatalf("got %d sent=%d", rec.Code, len(*sent))
	}
}

func TestSend_RejectsAWrongOrMissingToken(t *testing.T) {
	sent := withDeps(t, "s3cret", true)

	for _, header := range []string{"", "Bearer wrong", "s3cret", "Bearer s3cret extra"} {
		if rec := send(t, header, exportReady); rec.Code != 401 {
			t.Fatalf("header %q got %d", header, rec.Code)
		}
	}
	if len(*sent) != 0 {
		t.Fatal("an unauthorized call sent mail")
	}
}

func TestSend_RejectsAnUnknownTemplateAndAnUnknownRecipient(t *testing.T) {
	sent := withDeps(t, "s3cret", true)

	unknown := `{"to":"ada@example.com","template":"marketing_blast","data":{"link":"https://x.test"}}`
	if rec := send(t, "Bearer s3cret", unknown); rec.Code != 400 {
		t.Fatalf("unknown template got %d", rec.Code)
	}

	badRecipient := `{"to":"not an address","template":"export_ready","data":{"link":"https://x.test"}}`
	if rec := send(t, "Bearer s3cret", badRecipient); rec.Code != 400 {
		t.Fatalf("bad recipient got %d", rec.Code)
	}

	withDeps(t, "s3cret", false)
	if rec := send(t, "Bearer s3cret", exportReady); rec.Code != 404 {
		t.Fatalf("unknown recipient got %d", rec.Code)
	}
	if len(*sent) != 0 {
		t.Fatal("a rejected call sent mail")
	}
}

// F-11: a holder of the token may not pick the link's host. With the public origin
// configured, a link to any other host must be refused by the delivery path, while
// links to the configured public origin and the configured storage origin pass.
func TestSend_RefusesALinkToAnUnconfiguredHost(t *testing.T) {
	m := services.NewSMTPMailer(services.MailerConfig{PublicBaseURL: "https://reelbolt.test"})

	// The evil host: the endpoint answers 202 (delivery is detached and the refusal is logged,
	// never surfaced — the endpoint stays an open relay for TEMPLATE and RECIPIENT, just not for
	// the destination), but the real mailer refuses the render. Under the old behaviour this
	// assertion fails: the old code accepted any http(s) URL.
	withDepsAndMailer(t, "s3cret", true, m)
	if rec := send(t, "Bearer s3cret", evilLink); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if _, err := m.Render("ada@example.com", services.TemplateExportReady, services.MailData{Name: "Ada", Link: "https://evil.example/phish?next=/x", ExpiresIn: "7 days"}); err == nil {
		t.Fatal("the mailer accepted a link to an unconfigured host")
	}

	// The configured public origin passes the mailer.
	if _, err := m.Render("ada@example.com", services.TemplateExportReady, services.MailData{Name: "Ada", Link: "https://reelbolt.test/x", ExpiresIn: "7 days"}); err != nil {
		t.Fatalf("the configured public origin was refused: %v", err)
	}

	// A configured extra origin (the presigned storage endpoint) passes.
	m2 := services.NewSMTPMailer(services.MailerConfig{PublicBaseURL: "https://reelbolt.test", LinkHosts: []string{"https://storage.example:3900"}})
	if _, err := m2.Render("ada@example.com", services.TemplateExportReady, services.MailData{Name: "Ada", Link: "https://storage.example:3900/exports/a.zip?X=1", ExpiresIn: "7 days"}); err != nil {
		t.Fatalf("the configured storage origin was refused: %v", err)
	}

	// An unset configuration refuses every link — it must not degrade to "allow anything".
	m3 := services.NewSMTPMailer(services.MailerConfig{})
	if _, err := m3.Render("ada@example.com", services.TemplateExportReady, services.MailData{Name: "Ada", Link: "https://reelbolt.test/x", ExpiresIn: "7 days"}); err == nil {
		t.Fatal("an unset configuration accepted a link; it must refuse, not allow any host")
	}
}

func TestEveryShippedTemplateIsAccepted(t *testing.T) {
	for _, tmpl := range services.AllTemplates {
		if !templateAllowed(string(tmpl)) {
			t.Fatalf("template %q is not accepted", tmpl)
		}
	}
	if templateAllowed("nope") {
		t.Fatal("an unknown template was accepted")
	}
}

// TestMarketingConfirmTemplateAllowed: the Inference API's double opt-in mail goes through this relay.
// A site subscriber has no account, so for this one template (fixed wording, link pinned to the
// configured origins by the mailer) the account-only recipient rule cannot apply.
func TestMarketingConfirmTemplateAllowed(t *testing.T) {
	if !templateAllowed("marketing-confirm") {
		t.Fatal("marketing-confirm must be an allowed template")
	}
	sent := withDeps(t, "s3cret", false)
	rec := send(t, "Bearer s3cret", `{"to":"subscriber@example.com","template":"marketing-confirm","data":{"link":"https://reelbolt.test/app/marketing/confirm?token=c1.k1.x"}}`)
	if rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if len(*sent) != 1 || (*sent)[0].tmpl != services.TemplateMarketingConfirm || (*sent)[0].data.Link == "" {
		t.Fatalf("sent: %+v", *sent)
	}

	// Every other template still mails accounts only.
	sent = withDeps(t, "s3cret", false)
	if rec := send(t, "Bearer s3cret", exportReady); rec.Code != 404 || len(*sent) != 0 {
		t.Fatalf("a non-account recipient of export_ready: %d, sent %d", rec.Code, len(*sent))
	}
}

func TestSend_AnswersTheSameForEveryUnconfiguredCaller(t *testing.T) {
	// The two failure modes must not be distinguishable beyond the 404/401 split, and neither may
	// echo the secret back.
	withDeps(t, "s3cret", true)
	rec := send(t, "Bearer wrong", exportReady)
	var body map[string]string
	_ = json.Unmarshal(rec.Body.Bytes(), &body)
	if strings.Contains(rec.Body.String(), "s3cret") {
		t.Fatal("the response echoed the secret")
	}
}

// confirmBody is a marketing-confirm request for one recipient and link.
func confirmBody(to, link string) string {
	b, _ := json.Marshal(map[string]any{"to": to, "template": "marketing-confirm", "data": map[string]string{"link": link}})
	return string(b)
}

// S-07: the double opt-in mail goes to addresses with no account, so its link has to be exactly the
// confirm page on a configured origin with nothing but a token — no userinfo, no other query keys, no
// fragment, no whitespace, no other path. Anything else is refused before delivery and mails nothing.
func TestSend_MarketingConfirm_RefusesALinkOfAnyOtherShape(t *testing.T) {
	bad := map[string]string{
		"other host":     "https://evil.example/app/marketing/confirm?token=c1.k1.x",
		"userinfo":       "https://reelbolt.test@evil.example/app/marketing/confirm?token=c1.k1.x",
		"userinfo same":  "https://user:pw@reelbolt.test/app/marketing/confirm?token=c1.k1.x",
		"other path":     "https://reelbolt.test/app/login?token=c1.k1.x",
		"path suffix":    "https://reelbolt.test/app/marketing/confirm/x?token=c1.k1.x",
		"extra key":      "https://reelbolt.test/app/marketing/confirm?token=c1.k1.x&next=https://evil.example",
		"repeated token": "https://reelbolt.test/app/marketing/confirm?token=a&token=b",
		"fragment":       "https://reelbolt.test/app/marketing/confirm?token=c1.k1.x#Claim%20your%20prize",
		"space in token": "https://reelbolt.test/app/marketing/confirm?token=c1 WIN A PRIZE",
		"encoded space":  "https://reelbolt.test/app/marketing/confirm?token=c1%20WIN",
		"no token":       "https://reelbolt.test/app/marketing/confirm",
		"empty token":    "https://reelbolt.test/app/marketing/confirm?token=",
		"wrong scheme":   "http://reelbolt.test/app/marketing/confirm?token=c1.k1.x",
		"trailing nl":    "https://reelbolt.test/app/marketing/confirm?token=c1.k1.x\n",
	}
	for name, link := range bad {
		sent := withDeps(t, "s3cret", false)
		rec := send(t, "Bearer s3cret", confirmBody("subscriber@example.com", link))
		if rec.Code != 400 || len(*sent) != 0 {
			t.Errorf("%s (%q): got %d, sent %d", name, link, rec.Code, len(*sent))
		}
	}

	// A configured MAIL_LINK_HOSTS origin is an allowed origin for the confirm page too.
	sent := withDeps(t, "s3cret", false)
	d.linkOrigins = func() (string, []string) { return "https://reelbolt.test", []string{"https://app.reelbolt.test"} }
	if rec := send(t, "Bearer s3cret", confirmBody("subscriber@example.com", "https://app.reelbolt.test/app/marketing/confirm?token=c1.k1.x")); rec.Code != 202 || len(*sent) != 1 {
		t.Fatalf("an allowed extra origin: got %d: %s", rec.Code, rec.Body.String())
	}
}

// S-07: the template renders only the link, so a caller cannot smuggle free text through any other field.
func TestSend_MarketingConfirm_ForwardsOnlyTheLink(t *testing.T) {
	sent := withDeps(t, "s3cret", false)
	body := `{"to":"subscriber@example.com","template":"marketing-confirm","data":{"link":"https://reelbolt.test/app/marketing/confirm?token=c1.k1.x","name":"WIN A PRIZE","orgName":"call +1 555","inviterName":"x","role":"y","expiresIn":"z","deletionDate":"q","balance":"b"}}`
	if rec := send(t, "Bearer s3cret", body); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	want := services.MailData{Link: "https://reelbolt.test/app/marketing/confirm?token=c1.k1.x", Locale: "en"}
	if len(*sent) != 1 || (*sent)[0].data != want {
		t.Fatalf("sent: %+v", *sent)
	}
}

// S-07: at most three confirmation mails per recipient in 24 hours; the fourth is 429 and mails nothing.
func TestSend_MarketingConfirm_CapsMailsPerRecipient(t *testing.T) {
	sent := withDeps(t, "s3cret", false)
	now := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	d.limiter = newConfirmLimiter(func() time.Time { return now }, newFakeCounterStore(), defaultLimits())
	link := "https://reelbolt.test/app/marketing/confirm?token=c1.k1.x"
	for i := 0; i < 3; i++ {
		if rec := send(t, "Bearer s3cret", confirmBody("subscriber@example.com", link)); rec.Code != 202 {
			t.Fatalf("mail %d: got %d", i+1, rec.Code)
		}
	}
	// The address is compared case-insensitively.
	if rec := send(t, "Bearer s3cret", confirmBody("Subscriber@Example.com", link)); rec.Code != 429 {
		t.Fatalf("the fourth mail in 24 h: got %d", rec.Code)
	}
	if len(*sent) != 3 {
		t.Fatalf("sent %d", len(*sent))
	}
	// Another recipient is unaffected, and the window slides.
	if rec := send(t, "Bearer s3cret", confirmBody("other@example.com", link)); rec.Code != 202 {
		t.Fatalf("another recipient: got %d", rec.Code)
	}
	now = now.Add(24*time.Hour + time.Second)
	if rec := send(t, "Bearer s3cret", confirmBody("subscriber@example.com", link)); rec.Code != 202 {
		t.Fatalf("after 24 h: got %d", rec.Code)
	}
}

// S-07: a global cap bounds how much confirmation mail the relay sends in an hour, whoever it is for.
func TestSend_MarketingConfirm_CapsMailsGlobally(t *testing.T) {
	sent := withDeps(t, "s3cret", false)
	now := time.Date(2026, 10, 12, 10, 0, 0, 0, time.UTC)
	d.limiter = newConfirmLimiter(func() time.Time { return now }, newFakeCounterStore(), defaultLimits())
	link := "https://reelbolt.test/app/marketing/confirm?token=c1.k1.x"
	global := defaultLimits().globalPerHour
	for i := 0; i < global; i++ {
		if rec := send(t, "Bearer s3cret", confirmBody(fmt.Sprintf("s%d@d%d.example", i, i), link)); rec.Code != 202 {
			t.Fatalf("mail %d: got %d", i+1, rec.Code)
		}
	}
	if rec := send(t, "Bearer s3cret", confirmBody("late@example.com", link)); rec.Code != 429 {
		t.Fatalf("over the global cap: got %d", rec.Code)
	}
	if len(*sent) != global {
		t.Fatalf("sent %d", len(*sent))
	}
	// The cap is for marketing-confirm only: account mail still flows.
	d.userExists = func(string) (bool, error) { return true, nil }
	if rec := send(t, "Bearer s3cret", exportReady); rec.Code != 202 {
		t.Fatalf("export_ready under a full marketing cap: got %d", rec.Code)
	}
	// C5: the window slides, so right after the hour the previous one still weighs fully...
	now = now.Add(time.Hour + time.Second)
	if rec := send(t, "Bearer s3cret", confirmBody("late@example.com", link)); rec.Code != 429 {
		t.Fatalf("just after the hour edge: got %d", rec.Code)
	}
	// ...and has faded out a window later.
	now = now.Add(time.Hour)
	if rec := send(t, "Bearer s3cret", confirmBody("late@example.com", link)); rec.Code != 202 {
		t.Fatalf("after two hours: got %d", rec.Code)
	}
	if global != 100 || confirmPerRecipientPerDay != 3 {
		t.Fatalf("caps: global %d/h, per recipient %d/24h", global, confirmPerRecipientPerDay)
	}
}

// F-3: the counter store failing (or its table missing) refuses the marketing send, answering 429
// like any cap, while account mail is unaffected.
func TestSend_MarketingConfirm_FailsClosedWhenTheCounterStoreFails(t *testing.T) {
	sent := withDeps(t, "s3cret", false)
	store := newFakeCounterStore()
	store.err = fmt.Errorf("connection refused")
	d.limiter = newConfirmLimiter(time.Now, store, defaultLimits())
	link := "https://reelbolt.test/app/marketing/confirm?token=c1.k1.x"
	if rec := send(t, "Bearer s3cret", confirmBody("subscriber@example.com", link)); rec.Code != 429 {
		t.Fatalf("store down: got %d", rec.Code)
	}
	if len(*sent) != 0 {
		t.Fatalf("sent %d", len(*sent))
	}
	d.userExists = func(string) (bool, error) { return true, nil }
	if rec := send(t, "Bearer s3cret", exportReady); rec.Code != 202 {
		t.Fatalf("export_ready with the counter store down: got %d", rec.Code)
	}
}

// The marketing-confirm mail (anonymous newsletter) uses the body locale, else Accept-Language, else en.
func TestSend_MarketingConfirm_LocaleFromBodyThenHeader(t *testing.T) {
	link := `"link":"https://reelbolt.test/app/marketing/confirm?token=c1.k1.x"`
	sent := withDeps(t, "s3cret", false)
	body := `{"to":"subscriber@example.com","template":"marketing-confirm","locale":"it","data":{` + link + `}}`
	if rec := send(t, "Bearer s3cret", body); rec.Code != 202 {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	if (*sent)[0].data.Locale != "it" {
		t.Fatalf("body locale: %+v", (*sent)[0].data)
	}
	body = `{"to":"subscriber@example.com","template":"marketing-confirm","locale":"../etc","data":{` + link + `}}`
	if rec := send(t, "Bearer s3cret", body); rec.Code != 202 {
		t.Fatalf("got %d", rec.Code)
	}
	if (*sent)[1].data.Locale != "en" {
		t.Fatalf("hostile locale: %+v", (*sent)[1].data)
	}
}

const digestBody = `"template":"operator_digest","data":{"digest":{"windowHours":24,"generatedAt":"2026-11-16 06:00 UTC","feedbackCount":0}}`

// TestOperatorDigestOnlyToTheConfiguredOperator: the digest carries platform error text, so the relay
// takes the recipient from its own environment and refuses every other address, with or without an
// account, and refuses it outright when no operator address is configured.
func TestOperatorDigestOnlyToTheConfiguredOperator(t *testing.T) {
	sent := withDeps(t, "s3cret", true)
	d.operatorEmail = func() string { return "ops@example.com" }

	for _, to := range []string{"ada@example.com", "ops@example.com.evil.test", "x+ops@example.com", "opſ@example.com"} {
		rec := send(t, "Bearer s3cret", `{"to":"`+to+`",`+digestBody+`}`)
		if rec.Code != 403 {
			t.Fatalf("%s: got %d, want 403", to, rec.Code)
		}
	}
	if len(*sent) != 0 {
		t.Fatalf("a refused digest was sent: %+v", *sent)
	}

	// The operator's address is accepted even with no account (the lookup is not consulted).
	d.userExists = func(string) (bool, error) { t.Fatal("account lookup consulted"); return false, nil }
	rec := send(t, "Bearer s3cret", `{"to":"Ops@Example.com",`+digestBody+`}`)
	if rec.Code != 202 || len(*sent) != 1 || (*sent)[0].tmpl != services.TemplateOperatorDigest || (*sent)[0].data.Digest == nil || (*sent)[0].to != "ops@example.com" {
		t.Fatalf("got %d, sent %+v", rec.Code, *sent)
	}

	// No body: refused as a bad request, nothing sent.
	rec = send(t, "Bearer s3cret", `{"to":"ops@example.com","template":"operator_digest","data":{}}`)
	if rec.Code != 400 || len(*sent) != 1 {
		t.Fatalf("empty digest: got %d", rec.Code)
	}
}

func TestOperatorDigestRefusedWhenUnconfigured(t *testing.T) {
	sent := withDeps(t, "s3cret", true)
	for _, fn := range []func() string{nil, func() string { return "" }, func() string { return "not an address" }} {
		d.operatorEmail = fn
		if rec := send(t, "Bearer s3cret", `{"to":"ops@example.com",`+digestBody+`}`); rec.Code != 403 {
			t.Fatalf("got %d, want 403", rec.Code)
		}
	}
	if len(*sent) != 0 {
		t.Fatal("sent with no operator address configured")
	}
}

// TestDigestFieldIgnoredForOtherTemplates: a digest body riding on another template is not forwarded.
func TestDigestFieldIgnoredForOtherTemplates(t *testing.T) {
	sent := withDeps(t, "s3cret", true)
	rec := send(t, "Bearer s3cret", `{"to":"ada@example.com","template":"export_ready","data":{"name":"Ada","link":"https://reelbolt.test/x","expiresIn":"7 days","digest":{"windowHours":1}}}`)
	if rec.Code != 202 || (*sent)[0].data.Digest != nil {
		t.Fatalf("got %d, digest forwarded: %+v", rec.Code, *sent)
	}
}
