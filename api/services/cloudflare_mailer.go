package services

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/mail"
	"net/url"
	"strings"
	"time"
)

// cloudflareAPIBase is the Cloudflare API origin. MailerConfig.CloudflareAPIBaseURL
// overrides it so tests can point at an httptest server; nothing in production sets it.
const cloudflareAPIBase = "https://api.cloudflare.com"

// cloudflareSendPath is the Email Sending endpoint, one POST per message.
const cloudflareSendPath = "/client/v4/accounts/%s/email/sending/send"

// cloudflareTimeout bounds one send. It is a request timeout, not a retry budget: a
// failure is reported, never retried against another provider.
const cloudflareTimeout = 15 * time.Second

// cloudflareErrorBodyLimit caps how much of an error response is kept. Cloudflare's reason
// ("sender not verified", "domain not onboarded", "token revoked") is a short JSON envelope,
// so a bounded read loses nothing and an unbounded one could.
const cloudflareErrorBodyLimit = 8 << 10

// CloudflareMailer is the Mailer backed by Cloudflare Email Sending. It renders through the
// same renderer as the SMTP transport — same templates, same MailData, and therefore the same
// ValidateRecipient and ValidateLinkHost checks — and then POSTs HTML and Text as JSON fields.
// It deliberately does NOT build a MIME message: the API takes the two bodies separately.
type CloudflareMailer struct {
	renderer
	accountID string
	token     string
	from      string
	replyTo   string
	baseURL   string
	client    *http.Client
}

// cloudflareSendRequest is the Email Sending body. It mirrors RenderedMail (plus the optional reply_to), which is
// why the render path is reused rather than re-implemented.
type cloudflareSendRequest struct {
	To   string `json:"to"`
	From string `json:"from"`
	// ReplyTo is the REST field for the Reply-To header; omitted unless MAIL_REPLY_TO is a valid address.
	ReplyTo string `json:"reply_to,omitempty"`
	Subject string `json:"subject"`
	HTML    string `json:"html"`
	Text    string `json:"text"`
}

// NewCloudflareMailer builds the Cloudflare Email Sending transport.
func NewCloudflareMailer(cfg MailerConfig) *CloudflareMailer {
	base := strings.TrimRight(strings.TrimSpace(cfg.CloudflareAPIBaseURL), "/")
	if base == "" {
		base = cloudflareAPIBase
	}
	return &CloudflareMailer{
		renderer:  newRenderer(cfg),
		accountID: strings.TrimSpace(cfg.CloudflareAccountID),
		token:     strings.TrimSpace(cfg.CloudflareToken),
		from:      bareAddress(cfg.From),
		replyTo:   replyToAddress(cfg.ReplyTo),
		baseURL:   base,
		client:    &http.Client{Timeout: cloudflareTimeout},
	}
}

// bareAddress is the address the API's "from" field takes. SMTP_FROM is documented as a bare
// envelope address, but a display-name form ("ReelBolt <welcome@reelbolt.ai>") is accepted and
// reduced rather than sent verbatim as an invalid sender.
func bareAddress(from string) string {
	from = strings.TrimSpace(from)
	if a, err := mail.ParseAddress(from); err == nil && a.Address != "" {
		return a.Address
	}
	return from
}

// replyToAddress is the Reply-To a transactional mail carries: the configured address when it is
// exactly one valid bare address, else "" (no header). Anything else (a list, a display-name form, a
// string with a line break) is dropped rather than repaired, so a bad MAIL_REPLY_TO can never put an
// unexpected header on a message.
func replyToAddress(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" || strings.ContainsAny(raw, "\r\n,;<> ") {
		return ""
	}
	a, err := mail.ParseAddress(raw)
	if err != nil || a.Address != raw {
		return ""
	}
	return raw
}

// configured reports whether Cloudflare Email Sending has everything a send needs. A missing
// from address is a missing configuration, not a licence to invent a sender: the mailer refuses
// instead (see SendTemplate).
func (m *CloudflareMailer) configured() bool {
	return m.token != "" && m.accountID != "" && m.from != ""
}

// endpoint is the absolute URL of this account's send endpoint.
func (m *CloudflareMailer) endpoint() string {
	return m.baseURL + fmt.Sprintf(cloudflareSendPath, url.PathEscape(m.accountID))
}

// SendTemplate renders and sends a message over Cloudflare Email Sending. Every failure —
// a rendering refusal, a transport error, a non-2xx — is returned to the caller and logged;
// nothing here falls back to another transport, because the provider was chosen once, at
// construction, and a runtime failure is a failure.
func (m *CloudflareMailer) SendTemplate(to string, tmpl TemplateName, data MailData) error {
	r, err := m.Render(to, tmpl, data)
	if err != nil {
		return err
	}
	if !m.configured() {
		return errors.New("cloudflare email sending is not configured: CLOUDFLARE_EMAIL_TOKEN, CLOUDFLARE_EMAIL_ACCOUNT_ID and SMTP_FROM are all required")
	}

	body, err := json.Marshal(cloudflareSendRequest{
		To:      r.To,
		From:    m.from,
		ReplyTo: m.replyTo,
		Subject: r.Subject,
		HTML:    r.HTML,
		Text:    r.Text,
	})
	if err != nil {
		return fmt.Errorf("cloudflare email: encode request: %w", err)
	}

	req, err := http.NewRequestWithContext(context.Background(), http.MethodPost, m.endpoint(), bytes.NewReader(body))
	if err != nil {
		return fmt.Errorf("cloudflare email: build request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+m.token)
	req.Header.Set("Content-Type", "application/json")

	resp, err := m.client.Do(req)
	if err != nil {
		// A transport error has no body to quote; the error itself is the whole story.
		// The recipient appears only as its reference and the error only sanitised (C4).
		log.Printf("cloudflare email: send %s to %s: %s", tmpl, MailLogRef(r.To), SanitizeMailError(err))
		return fmt.Errorf("cloudflare email: send %s to %s: %w", tmpl, MailLogRef(r.To), err)
	}
	defer func() { _ = resp.Body.Close() }()

	respBody, _ := io.ReadAll(io.LimitReader(resp.Body, cloudflareErrorBodyLimit))
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		// Cloudflare states the actual reason in this body (unverified sender, unonboarded
		// domain, revoked token), so it is logged and returned rather than swallowed.
		// Any address the body echoes is redacted (C4).
		err = fmt.Errorf("cloudflare email: send %s to %s: %s: %s", tmpl, MailLogRef(r.To), resp.Status,
			RedactAddresses(strings.TrimSpace(string(respBody))))
		log.Print(err)
		return err
	}
	return nil
}
