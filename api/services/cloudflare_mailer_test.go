package services

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// testAccountID is deliberately not a real Cloudflare account id: nothing in this repository
// carries a deployment's account id, and the token below is a placeholder of the same shape.
const (
	testAccountID = "acct_0123456789abcdef"
	testAPIToken  = "cfut_placeholder_not_a_real_token"
)

func cloudflareTestConfig(baseURL string) MailerConfig {
	return MailerConfig{
		From:                 "welcome@reelbolt.ai",
		AppEnv:               "production",
		PublicBaseURL:        "https://reelbolt.ai",
		CloudflareToken:      testAPIToken,
		CloudflareAccountID:  testAccountID,
		CloudflareAPIBaseURL: baseURL,
	}
}

func TestCloudflareSendPostsRenderedMail(t *testing.T) {
	var method, path, auth, contentType string
	var body cloudflareSendRequest
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		method, path = r.Method, r.URL.Path
		auth, contentType = r.Header.Get("Authorization"), r.Header.Get("Content-Type")
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Errorf("decode request body: %v", err)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte("{\"success\":true,\"result\":null}"))
	}))
	defer srv.Close()

	m := NewCloudflareMailer(cloudflareTestConfig(srv.URL))
	d := testData()
	if err := m.SendTemplate("user@example.com", TemplateVerifyEmail, d); err != nil {
		t.Fatalf("send: %v", err)
	}

	if method != http.MethodPost {
		t.Errorf("method = %s", method)
	}
	if want := "/client/v4/accounts/" + testAccountID + "/email/sending/send"; path != want {
		t.Errorf("path = %q, want %q", path, want)
	}
	if want := "Bearer " + testAPIToken; auth != want {
		t.Errorf("Authorization = %q", auth)
	}
	if contentType != "application/json" {
		t.Errorf("Content-Type = %q", contentType)
	}
	if body.To != "user@example.com" || body.From != "welcome@reelbolt.ai" {
		t.Errorf("to/from = %q/%q", body.To, body.From)
	}
	if body.Subject != "Confirm your ReelBolt email" {
		t.Errorf("subject = %q", body.Subject)
	}
	if !strings.Contains(body.Text, d.Link) || !strings.Contains(body.HTML, "token=abc123") {
		t.Error("rendered bodies were not sent")
	}
	// The API takes html and text as separate JSON fields; no MIME assembly is involved.
	if strings.Contains(body.Text, "MIME-Version") || strings.Contains(body.HTML, "multipart/alternative") {
		t.Error("a MIME message leaked into the JSON body")
	}
}

// Cloudflare states the real reason — unverified sender, unonboarded domain, revoked token — in
// the response body, so it must reach the caller rather than be collapsed into "status 400".
func TestCloudflareSendReturnsTheResponseBody(t *testing.T) {
	const apiBody = `{"success":false,"errors":[{"code":1000,"message":"Sender not verified"}]}`
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		_, _ = w.Write([]byte(apiBody))
	}))
	defer srv.Close()

	m := NewCloudflareMailer(cloudflareTestConfig(srv.URL))
	err := m.SendTemplate("user@example.com", TemplateVerifyEmail, testData())
	if err == nil {
		t.Fatal("a non-2xx response must be returned to the caller")
	}
	if !strings.Contains(err.Error(), apiBody) {
		t.Errorf("error %q does not carry Cloudflare's response body", err)
	}
	if !strings.Contains(err.Error(), "400") {
		t.Errorf("error %q does not carry the status", err)
	}
}

func TestCloudflareSendReturnsATransportError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	url := srv.URL
	srv.Close() // nothing is listening on url any more

	m := NewCloudflareMailer(cloudflareTestConfig(url))
	if err := m.SendTemplate("user@example.com", TemplateVerifyEmail, testData()); err == nil {
		t.Fatal("a transport error must be returned, not swallowed")
	}
}

func TestCloudflareSendIsBoundedByARequestTimeout(t *testing.T) {
	release := make(chan struct{})
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		<-release
	}))
	defer func() { close(release); srv.Close() }()

	m := NewCloudflareMailer(cloudflareTestConfig(srv.URL))
	m.client.Timeout = 50 * time.Millisecond
	if err := m.SendTemplate("user@example.com", TemplateVerifyEmail, testData()); err == nil {
		t.Fatal("a send that never completes must time out and report it")
	}
}

// The recipient and link validators are security controls, so the Cloudflare path must be
// unable to reach the API without passing them.
func TestCloudflareSendStillAppliesTheRecipientAndLinkValidators(t *testing.T) {
	var calls int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&calls, 1)
		_, _ = w.Write([]byte("{\"success\":true}"))
	}))
	defer srv.Close()
	m := NewCloudflareMailer(cloudflareTestConfig(srv.URL))

	if err := m.SendTemplate("a@b.com\r\nBcc: attacker@example.com", TemplateWelcome, MailData{}); err == nil {
		t.Error("accepted a recipient with a header injection")
	}
	if err := m.SendTemplate("Name <user@example.com>", TemplateWelcome, MailData{}); err == nil {
		t.Error("accepted a recipient with a display name")
	}

	d := testData()
	d.Link = "https://evil.example/phish"
	if err := m.SendTemplate("user@example.com", TemplateVerifyEmail, d); err == nil {
		t.Error("accepted a link to a host we do not own")
	}

	// With no origin configured the link must be refused outright, exactly as on the SMTP path
	// (F-11: an empty allowlist must not degrade to "any host").
	bare := cloudflareTestConfig(srv.URL)
	bare.PublicBaseURL = ""
	d.Link = "https://reelbolt.ai/app/verify?token=abc123"
	if err := NewCloudflareMailer(bare).SendTemplate("user@example.com", TemplateVerifyEmail, d); err == nil {
		t.Error("an unconfigured origin accepted a link")
	}

	if got := atomic.LoadInt32(&calls); got != 0 {
		t.Fatalf("%d requests reached the API for mail the validators refused", got)
	}
}

// A missing from address is missing configuration, not a licence to invent a sender.
func TestCloudflareMailerRefusesToSendFromAGuessedAddress(t *testing.T) {
	for _, tc := range []struct {
		name  string
		mutil func(*MailerConfig)
		token string
	}{
		{"no from address", func(c *MailerConfig) { c.From = "" }, "SMTP_FROM"},
		{"no token", func(c *MailerConfig) { c.CloudflareToken = "" }, "CLOUDFLARE_EMAIL_TOKEN"},
		{"no account id", func(c *MailerConfig) { c.CloudflareAccountID = "" }, "CLOUDFLARE_EMAIL_ACCOUNT_ID"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var calls int32
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				atomic.AddInt32(&calls, 1)
				_, _ = w.Write([]byte("{\"success\":true}"))
			}))
			defer srv.Close()

			cfg := cloudflareTestConfig(srv.URL)
			tc.mutil(&cfg)
			m := NewCloudflareMailer(cfg)
			if m.configured() {
				t.Fatalf("mailer reports itself configured without %s", tc.token)
			}
			err := m.SendTemplate("user@example.com", TemplateVerifyEmail, testData())
			if err == nil {
				t.Fatal("an unconfigured Cloudflare mailer must return an error")
			}
			if !strings.Contains(err.Error(), tc.token) {
				t.Errorf("error %q does not name the missing setting %s", err, tc.token)
			}
			if got := atomic.LoadInt32(&calls); got != 0 {
				t.Fatalf("%d requests were sent by an unconfigured mailer", got)
			}
		})
	}
}

// SMTP_FROM is documented as a bare address, but a display-name form must not be sent verbatim
// as an invalid sender.
func TestCloudflareMailerSendsABareFromAddress(t *testing.T) {
	var body cloudflareSendRequest
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewDecoder(r.Body).Decode(&body)
		_, _ = w.Write([]byte("{\"success\":true}"))
	}))
	defer srv.Close()

	cfg := cloudflareTestConfig(srv.URL)
	cfg.From = "ReelBolt <welcome@reelbolt.ai>"
	if err := NewCloudflareMailer(cfg).SendTemplate("user@example.com", TemplateWelcome, MailData{Name: "Ada"}); err != nil {
		t.Fatalf("send: %v", err)
	}
	if body.From != "welcome@reelbolt.ai" {
		t.Errorf("from = %q, want the bare address", body.From)
	}
}

// F-4: a rejection whose body echoes the recipient is logged (and returned) without the address.
func TestCloudflareSendLogsNoAddress(t *testing.T) {
	const apiBody = `{"success":false,"errors":[{"code":2001,"message":"recipient victim@example.com rejected"}]}`
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
		_, _ = w.Write([]byte(apiBody))
	}))
	defer srv.Close()
	buf := captureStdLog(t)

	err := NewCloudflareMailer(cloudflareTestConfig(srv.URL)).SendTemplate("victim@example.com", TemplateVerifyEmail, testData())
	if err == nil {
		t.Fatal("a non-2xx response must be returned to the caller")
	}
	for _, s := range []string{buf.String(), err.Error()} {
		if strings.Contains(s, "@") || strings.Contains(s, "victim") {
			t.Fatalf("leaks the address: %s", s)
		}
	}
	if !strings.Contains(buf.String(), "rcpt=") || !strings.Contains(buf.String(), "2001") {
		t.Fatalf("log lacks the reference or the reason: %s", buf.String())
	}

	srv.Close() // a transport error, too
	buf.Reset()
	if err := NewCloudflareMailer(cloudflareTestConfig(srv.URL)).SendTemplate("victim@example.com", TemplateVerifyEmail, testData()); err == nil ||
		strings.Contains(err.Error(), "@") || strings.Contains(buf.String(), "@") {
		t.Fatalf("transport error leaks: err %v log %s", err, buf.String())
	}
}

// launch-soft-open T-003: a reply to a transactional mail must reach a monitored mailbox. The REST
// field is reply_to; it is sent only for a valid MAIL_REPLY_TO and omitted otherwise.
func TestCloudflareSendCarriesTheConfiguredReplyTo(t *testing.T) {
	var raw map[string]any
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewDecoder(r.Body).Decode(&raw)
		_, _ = w.Write([]byte("{\"success\":true}"))
	}))
	defer srv.Close()

	cfg := cloudflareTestConfig(srv.URL)
	cfg.ReplyTo = "support@reelbolt.ai"
	if err := NewCloudflareMailer(cfg).SendTemplate("user@example.com", TemplateWelcome, MailData{Name: "Ada"}); err != nil {
		t.Fatalf("send: %v", err)
	}
	if got := raw["reply_to"]; got != "support@reelbolt.ai" {
		t.Errorf("reply_to = %v, want support@reelbolt.ai", got)
	}
	if got := raw["from"]; got != "welcome@reelbolt.ai" {
		t.Errorf("from = %v, the Reply-To must not change the sender", got)
	}
}

func TestCloudflareSendOmitsReplyToWhenUnsetOrInvalid(t *testing.T) {
	for _, replyTo := range []string{"", "   ", "not an address", "a@b.c, d@e.f", "Support <support@reelbolt.ai>", "x@y.z\r\nBcc: v@w.x"} {
		var raw map[string]any
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			_ = json.NewDecoder(r.Body).Decode(&raw)
			_, _ = w.Write([]byte("{\"success\":true}"))
		}))
		cfg := cloudflareTestConfig(srv.URL)
		cfg.ReplyTo = replyTo
		err := NewCloudflareMailer(cfg).SendTemplate("user@example.com", TemplateWelcome, MailData{Name: "Ada"})
		srv.Close()
		if err != nil {
			t.Fatalf("ReplyTo %q: send: %v", replyTo, err)
		}
		if _, present := raw["reply_to"]; present {
			t.Errorf("ReplyTo %q: reply_to was sent (%v); an unset or invalid value must send no header", replyTo, raw["reply_to"])
		}
	}
}
