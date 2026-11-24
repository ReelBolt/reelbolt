package services

import (
	"io"
	"mime"
	"mime/multipart"
	"mime/quotedprintable"
	"net/mail"
	"net/smtp"
	"strings"
	"testing"
)

func testData() MailData {
	return MailData{
		Name:         "Ada",
		Link:         "https://reelbolt.ai/app/verify?token=abc123",
		ExpiresIn:    "24 hours",
		TempPassword: "s3cretTemp",
		OrgName:      "Acme Studio",
		InviterName:  "Grace",
		Role:         "Member",
		DeletionDate: "2026-11-04",
		ExpiryDate:   "2026-12-01 10:00 UTC",
		Balance:      "42",
		DeviceName:   "Ada's laptop",
		ProjectName:  "Q3 Launch",
		FileName:     "hero-shot.mp4",
		ErrorDetail:  "Render failed: out of memory",
		ActorName:    "Grace",
		NewEmail:     "ada.new@example.com",
	}
}

func newTestMailer(captured *[]byte, to *[]string) *SMTPMailer {
	m := NewSMTPMailer(MailerConfig{Host: "smtp.test", Port: "587", From: "noreply@reelbolt.ai", FromName: "ReelBolt", AppEnv: "production", PublicBaseURL: "https://reelbolt.ai"})
	m.send = func(addr string, a smtp.Auth, from string, rcpt []string, msg []byte) error {
		*captured = msg
		*to = rcpt
		return nil
	}
	return m
}

func TestRenderEveryTemplate(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	for _, name := range AllTemplates {
		t.Run(string(name), func(t *testing.T) {
			data := testData()
			if name == TemplateOperatorDigest {
				data.Digest = &OperatorDigest{WindowHours: 24, GeneratedAt: "2026-11-16 06:00 UTC", FeedbackCount: 0}
			}
			if name == TemplateMarketingConfirm {
				// S-07: this template's link may only be the confirm page.
				data.Link = "https://reelbolt.ai/app/marketing/confirm?token=abc123"
			}
			r, err := m.Render("user@example.com", name, data)
			if err != nil {
				t.Fatalf("render: %v", err)
			}
			if r.Subject == "" || strings.ContainsAny(r.Subject, "\r\n") {
				t.Errorf("bad subject %q", r.Subject)
			}
			if strings.TrimSpace(r.HTML) == "" || strings.TrimSpace(r.Text) == "" {
				t.Fatal("empty body")
			}
			if strings.Contains(r.HTML, "<no value>") || strings.Contains(r.Text, "<no value>") {
				t.Error("unresolved template value")
			}
			if name != TemplateWelcome && name != TemplateEmailChangeDone && name != TemplateOperatorDigest { // a notice, nothing to act on
				if !strings.Contains(r.Text, data.Link) {
					t.Error("text body lacks link")
				}
				if !strings.Contains(r.HTML, "token=abc123") {
					t.Error("html body lacks link")
				}
			}
		})
	}
}

func TestWelcomeTempPasswordOptional(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{})
	r, err := m.Render("user@example.com", TemplateWelcome, MailData{Name: "Ada", TempPassword: "pw-123"})
	if err != nil || !strings.Contains(r.Text, "pw-123") || !strings.Contains(r.HTML, "pw-123") {
		t.Fatalf("temp password missing: %v", err)
	}
	r, err = m.Render("user@example.com", TemplateWelcome, MailData{Name: "Ada"})
	if err != nil || strings.Contains(r.Text, "temporary password") {
		t.Fatalf("unexpected temp password block: %v", err)
	}
}

func TestHTMLEscapesUserData(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	d := testData()
	d.Name = `<script>alert(1)</script>`
	d.OrgName = `"><img src=x onerror=alert(1)>`
	d.InviterName = `<b>Mallory</b>`
	r, err := m.Render("user@example.com", TemplateInvite, d)
	if err != nil {
		t.Fatal(err)
	}
	for _, bad := range []string{"<script>", "<img src=x", "<b>Mallory"} {
		if strings.Contains(r.HTML, bad) {
			t.Errorf("html contains unescaped %q", bad)
		}
	}
	// Subject is a header, never HTML.
	if strings.ContainsAny(r.Subject, "\r\n") {
		t.Error("subject has line break")
	}
}

func TestSubjectHeaderInjectionNeutralised(t *testing.T) {
	var msg []byte
	var rcpt []string
	m := newTestMailer(&msg, &rcpt)
	d := testData()
	d.OrgName = "Evil\r\nBcc: attacker@example.com"
	if err := m.SendTemplate("user@example.com", TemplateInvite, d); err != nil {
		t.Fatal(err)
	}
	parsed, err := mail.ReadMessage(strings.NewReader(string(msg)))
	if err != nil {
		t.Fatal(err)
	}
	if parsed.Header.Get("Bcc") != "" {
		t.Fatal("injected Bcc header present")
	}
}

func TestRecipientValidation(t *testing.T) {
	bad := []string{
		"", "not-an-email", "a@b.com\r\nBcc: x@y.com", "a@b.com\nBcc: x@y.com",
		"a@b.com, c@d.com", "Name <a@b.com>", "a@b.com;c@d.com", "<a@b.com>", "a@b.com\x00",
	}
	for _, b := range bad {
		if _, err := ValidateRecipient(b); err == nil {
			t.Errorf("accepted %q", b)
		}
	}
	if got, err := ValidateRecipient("  user@example.com "); err != nil || got != "user@example.com" {
		t.Errorf("rejected valid address: %q %v", got, err)
	}
	m := NewSMTPMailer(MailerConfig{})
	if _, err := m.Render("a@b.com\r\nBcc: x@y.com", TemplateWelcome, MailData{}); err == nil {
		t.Error("Render accepted injected recipient")
	}
}

func TestRequiredFieldsAndLinks(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	if _, err := m.Render("u@example.com", TemplateVerifyEmail, MailData{Name: "A"}); err == nil {
		t.Error("expected error for missing ExpiresIn")
	}
	d := testData()
	d.Link = "javascript:alert(1)"
	if _, err := m.Render("u@example.com", TemplateVerifyEmail, d); err == nil {
		t.Error("expected error for non-http link")
	}
	if _, err := m.Render("u@example.com", TemplateName("nope"), testData()); err == nil {
		t.Error("expected error for unknown template")
	}
}

func TestSendBuildsMultipartMessage(t *testing.T) {
	var msg []byte
	var rcpt []string
	m := newTestMailer(&msg, &rcpt)
	d := testData()
	d.Name = "Zoë"
	d.Unsubscribe = "https://reelbolt.ai/unsub?t=1"
	if err := m.SendTemplate("user@example.com", TemplateVerifyEmail, d); err != nil {
		t.Fatal(err)
	}
	if len(rcpt) != 1 || rcpt[0] != "user@example.com" {
		t.Fatalf("envelope recipients = %v", rcpt)
	}
	parsed, err := mail.ReadMessage(strings.NewReader(string(msg)))
	if err != nil {
		t.Fatal(err)
	}
	if from := parsed.Header.Get("From"); !strings.Contains(from, "noreply@reelbolt.ai") || !strings.Contains(from, "ReelBolt") {
		t.Errorf("From = %q", from)
	}
	if parsed.Header.Get("List-Unsubscribe") != "<https://reelbolt.ai/unsub?t=1>" {
		t.Errorf("List-Unsubscribe = %q", parsed.Header.Get("List-Unsubscribe"))
	}
	if subj, _ := new(mime.WordDecoder).DecodeHeader(parsed.Header.Get("Subject")); subj != "Confirm your ReelBolt email" {
		t.Errorf("Subject = %q", subj)
	}
	mt, params, err := mime.ParseMediaType(parsed.Header.Get("Content-Type"))
	if err != nil || mt != "multipart/alternative" {
		t.Fatalf("content type %q %v", mt, err)
	}
	mr := multipart.NewReader(parsed.Body, params["boundary"])
	var types []string
	for {
		p, err := mr.NextPart()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatal(err)
		}
		body, _ := io.ReadAll(quotedprintable.NewReader(p))
		types = append(types, p.Header.Get("Content-Type"))
		if !strings.Contains(string(body), "Zoë") {
			t.Errorf("part %s lacks recipient name", p.Header.Get("Content-Type"))
		}
	}
	if len(types) != 2 {
		t.Fatalf("parts = %v", types)
	}
}

// F-11: with the default config (no base URL configured), a link must be refused —
// "allow anything" is the bug this fix closes.
func TestRenderRefusesAnyLinkWhenNoBaseIsConfigured(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{})
	d := testData()
	for _, link := range []string{"https://reelbolt.ai/app", "http://localhost/app", "https://any.host/x"} {
		d.Link = link
		if _, err := m.Render("u@example.com", TemplateVerifyEmail, d); err == nil {
			t.Fatalf("an unconfigured mailer accepted %q; it must refuse", link)
		}
	}
}

func TestUnconfiguredSMTPLogsAndSucceeds(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{AppEnv: "production", PublicBaseURL: "https://reelbolt.ai"})
	m.send = func(string, smtp.Auth, string, []string, []byte) error {
		t.Fatal("send called without SMTP configured")
		return nil
	}
	if err := m.SendTemplate("user@example.com", TemplateVerifyEmail, testData()); err != nil {
		t.Fatalf("unconfigured SMTP must not error: %v", err)
	}
	// Invalid input still errors even when unconfigured.
	if err := m.SendTemplate("bad", TemplateVerifyEmail, testData()); err == nil {
		t.Fatal("expected validation error")
	}
}

// F-11: the link's host is constrained to the configured origins; an unset
// configuration must refuse every link rather than degrade to "any host".
func TestValidateLinkHost(t *testing.T) {
	cases := []struct {
		link, base string
		extra      []string
		ok         bool
	}{
		{"https://reelbolt.ai/app/verify?token=abc", "https://reelbolt.ai", nil, true},
		// Hosts are matched case-insensitively (RFC 3986), so a differently-cased
		// configured origin still names the same host.
		{"https://ReelBolt.Ai/app", "https://reelbolt.ai", nil, true},
		{"https://evil.example/phish", "https://reelbolt.ai", nil, false},
		{"https://evil.example.phish/app", "https://reelbolt.ai", nil, false}, // suffix, not the host
		{"https://reelbolt.ai.evil.example/x", "https://reelbolt.ai", nil, false},
		{"javascript:alert(1)", "https://reelbolt.ai", nil, false},
		{"ftp://files.example/x", "https://reelbolt.ai", nil, false},
		{"//reelbolt.ai/app", "https://reelbolt.ai", nil, false},
		{"https://storage.example:3900/exports/a.zip?X=1", "https://reelbolt.ai", []string{"https://storage.example:3900"}, true},
		{"http://storage.example:3900/exports/a.zip", "https://reelbolt.ai", []string{"https://storage.example:3900"}, false}, // scheme differs
		{"https://storage.example:4900/x", "https://reelbolt.ai", []string{"https://storage.example:3900"}, false},            // port differs
		{"https://storage.example/x", "https://reelbolt.ai", []string{"https://storage.example:3900"}, false},                 // missing port
		{"https://reelbolt.ai/app", "https://reelbolt.ai", []string{"https://storage.example:3900"}, true},
		{"https://storage.example:3900/x", "", nil, false}, // unset base and empty list: fails closed
		{"https://reelbolt.ai/app", "", nil, false},        // even the "default" host is refused with no config
		{"", "https://reelbolt.ai", nil, true},             // empty link is optional, never a phishing vector
	}
	for i, c := range cases {
		err := ValidateLinkHost(c.link, c.base, c.extra)
		if c.ok && err != nil {
			t.Errorf("case %d: %q refused: %v", i, c.link, err)
		}
		if !c.ok && err == nil {
			t.Errorf("case %d: %q accepted", i, c.link)
		}
	}
}

func TestRenderRefusesAHostOutsideTheConfiguredOrigins(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	d := testData()
	d.Link = "https://evil.example/phish"
	if _, err := m.Render("u@example.com", TemplateVerifyEmail, d); err == nil {
		t.Fatal("accepted a link to an unconfigured host")
	}
	// The configured extra origin passes.
	m = NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai", LinkHosts: []string{"https://storage.example:3900"}})
	d.Link = "https://storage.example:3900/exports/a.zip?X=1"
	if _, err := m.Render("u@example.com", TemplateExportReady, d); err != nil {
		t.Fatalf("refused the configured storage origin: %v", err)
	}
	// With nothing configured, a link must not pass at all — "allow anything" is the bug.
	m = NewSMTPMailer(MailerConfig{})
	d.Link = "https://reelbolt.ai/app"
	if _, err := m.Render("u@example.com", TemplateVerifyEmail, d); err == nil {
		t.Fatal("an unset configuration must refuse the link, not allow any host")
	}
	// …but the mail still goes out without a link (the archive remains available via the endpoint).
	if _, err := m.Render("u@example.com", TemplateExportReady, MailData{Name: "Ada", ExpiresIn: "7 days"}); err != nil {
		t.Fatalf("no-link mail must still render: %v", err)
	}
}

// S-07: an origin match alone is not enough. A link carrying userinfo reads as the trusted host to a
// skimming human ("https://reelbolt.ai@evil.example/" or "https://user:pw@reelbolt.ai/"), and a link with
// whitespace in it smuggles free text into the mail. Both are refused for every template.
func TestValidateLinkHost_RefusesUserinfoAndWhitespace(t *testing.T) {
	base := "https://reelbolt.ai"
	for _, link := range []string{
		"https://reelbolt.ai@evil.example/x",
		"https://user:pw@reelbolt.ai/x",
		"https://user@reelbolt.ai/x",
		"https://reelbolt.ai/x y",
		"https://reelbolt.ai/x\ty",
		"https://reelbolt.ai/x?a=b c",
		"https://reelbolt.ai/x WIN",
		"https://reelbolt.ai/x\n",
	} {
		if err := ValidateLinkHost(link, base, nil); err == nil {
			t.Errorf("accepted %q", link)
		}
	}
	if err := ValidateLinkHost("https://reelbolt.ai/x?a=b%20c", base, nil); err != nil {
		t.Fatalf("an escaped space in a query is not whitespace in the link: %v", err)
	}
}

// S-07: the marketing confirmation link has exactly one allowed shape.
func TestValidateMarketingConfirmLink(t *testing.T) {
	base := "https://reelbolt.ai"
	extra := []string{"https://www.reelbolt.ai"}
	for _, ok := range []string{
		"https://reelbolt.ai/app/marketing/confirm?token=c1.k1.abc_-",
		"https://www.reelbolt.ai/app/marketing/confirm?token=X",
		"https://REELBOLT.ai/app/marketing/confirm?token=c1",
	} {
		if err := ValidateMarketingConfirmLink(ok, base, extra); err != nil {
			t.Errorf("refused %q: %v", ok, err)
		}
	}
	for _, bad := range []string{
		"",
		"https://evil.example/app/marketing/confirm?token=c1",
		"https://reelbolt.ai@evil.example/app/marketing/confirm?token=c1",
		"https://u@reelbolt.ai/app/marketing/confirm?token=c1",
		"https://reelbolt.ai/app/marketing/confirm/?token=c1",
		"https://reelbolt.ai/app/marketing/confirm?token=c1&x=1",
		"https://reelbolt.ai/app/marketing/confirm?x=1",
		"https://reelbolt.ai/app/marketing/confirm?token=c1#frag",
		"https://reelbolt.ai/app/marketing/confirm?token=c1#",
		"https://reelbolt.ai/app/marketing/confirm?token=c%31",
		"https://reelbolt.ai/app/marketing/confirm?token=c1+WIN",
		"https://reelbolt.ai/app/marketing/confirm?token=c1;x=1",
		"https://reelbolt.ai/app/%6Darketing/confirm?token=c1",
		"https://reelbolt.ai:8443/app/marketing/confirm?token=c1",
		"http://reelbolt.ai/app/marketing/confirm?token=c1",
	} {
		if err := ValidateMarketingConfirmLink(bad, base, extra); err == nil {
			t.Errorf("accepted %q", bad)
		}
	}
	// No configured origin: nothing passes.
	if err := ValidateMarketingConfirmLink("https://reelbolt.ai/app/marketing/confirm?token=c1", "", nil); err == nil {
		t.Fatal("an unconfigured deployment accepted a confirm link")
	}
}

// S-07, defence in depth: the mailer itself refuses a marketing-confirm link of any other shape, so a
// path that skipped the relay's check still cannot send it.
func TestRender_MarketingConfirmRequiresTheExactLinkShape(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	if _, err := m.Render("u@example.com", TemplateMarketingConfirm, MailData{Link: "https://reelbolt.ai/app/login?next=x"}); err == nil {
		t.Fatal("rendered a marketing-confirm mail with a non-confirm link")
	}
}

// S-32: a signup that ticked the marketing box is told, in the verification mail, that verifying the
// address also confirms that subscription. Without the opt-in the mail is unchanged.
func TestVerifyEmailTemplate_MentionsTheMarketingOptInOnlyWhenAsked(t *testing.T) {
	const sentence = "Verifying this address also confirms the ReelBolt marketing email subscription you asked for."
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	d := testData()
	plain, err := m.Render("u@example.com", TemplateVerifyEmail, d)
	if err != nil {
		t.Fatal(err)
	}
	d.MarketingOptIn = true
	opted, err := m.Render("u@example.com", TemplateVerifyEmail, d)
	if err != nil {
		t.Fatal(err)
	}
	for _, body := range []string{opted.Text, opted.HTML} {
		if strings.Count(body, sentence) != 1 {
			t.Fatalf("the opt-in sentence is missing:\n%s", body)
		}
	}
	for _, body := range []string{plain.Text, plain.HTML} {
		if strings.Contains(body, "marketing") {
			t.Fatalf("a mail without an opt-in mentions marketing:\n%s", body)
		}
	}
	if opted.Subject != plain.Subject {
		t.Fatalf("subject changed: %q vs %q", opted.Subject, plain.Subject)
	}
}

// launch-soft-open T-003: the SMTP transport carries MAIL_REPLY_TO as a Reply-To header, and only
// for a single valid address.
func TestSMTPBuildMessageCarriesReplyToOnlyWhenValid(t *testing.T) {
	r := RenderedMail{To: "user@example.com", Subject: "Hello", HTML: "<p>x</p>", Text: "x"}
	base := MailerConfig{Host: "smtp.test", Port: "587", From: "welcome@reelbolt.ai", FromName: "ReelBolt"}

	with := base
	with.ReplyTo = "support@reelbolt.ai"
	msg, err := NewSMTPMailer(with).buildMessage(r, "")
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if !strings.Contains(string(msg), "\r\nReply-To: support@reelbolt.ai\r\n") {
		t.Errorf("message has no Reply-To header:\n%s", string(msg))
	}

	for _, bad := range []string{"", "a@b.c\r\nBcc: v@w.x", "Support <support@reelbolt.ai>"} {
		without := base
		without.ReplyTo = bad
		msg, err := NewSMTPMailer(without).buildMessage(r, "")
		if err != nil {
			t.Fatalf("build: %v", err)
		}
		if strings.Contains(string(msg), "Reply-To:") || strings.Contains(string(msg), "Bcc:") {
			t.Errorf("ReplyTo %q produced a header:\n%s", bad, string(msg))
		}
	}
}
