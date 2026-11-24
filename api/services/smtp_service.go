package services

import (
	"bytes"
	"crypto/rand"
	"embed"
	"encoding/hex"
	"errors"
	"fmt"
	"log"
	"mime"
	"mime/multipart"
	"mime/quotedprintable"
	"net/mail"
	"net/smtp"
	"net/textproto"
	"net/url"
	"regexp"
	"strings"
	"time"
	"unicode"

	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/models"
)

//go:embed email_templates/*/*.html email_templates/*/*.txt email_templates/subjects.json
var emailTemplateFS embed.FS

// TemplateName identifies a transactional email. Each name has a matching
// email_templates/<name>.html and <name>.txt.
type TemplateName string

const (
	TemplateVerifyEmail      TemplateName = "verify_email"
	TemplateResetPassword    TemplateName = "reset_password"
	TemplateWelcome          TemplateName = "welcome"
	TemplateInvite           TemplateName = "invite"
	TemplateDeletionSchedule TemplateName = "deletion_scheduled"
	TemplateExportReady      TemplateName = "export_ready"
	TemplateLowBalance       TemplateName = "low_balance"
	// TemplateRunnerPaired is the anti-phishing control of the device flow: the main
	// abuse of pairing is getting a victim to approve the attacker's device, which
	// would then receive the victim's media, so every Owner hears about it (E4b step 6).
	TemplateRunnerPaired TemplateName = "runner_paired"
	// The four notification templates (docs/notifications.md). They are
	// opt-in: a mail is only sent when the recipient's stored switch for the
	// category is on, and the switch itself is the unsubscribe — the templates
	// therefore never render a List-Unsubscribe link.
	TemplateJobSucceeded   TemplateName = "job_succeeded"
	TemplateJobFailed      TemplateName = "job_failed"
	TemplateFileCreated    TemplateName = "file_created"
	TemplateGraphicsEdited TemplateName = "graphics_edited"
	// TemplateReviewActivity tells a link creator about a guest note or decision.
	TemplateReviewActivity TemplateName = "review_activity"
	// TemplateApiKeyExpiring / TemplateWebhookDisabled are security mails to one member.
	TemplateApiKeyExpiring  TemplateName = "api_key_expiring"
	TemplateWebhookDisabled TemplateName = "webhook_disabled"
	// TemplateAvatarRetention warns an avatar's attesting member seven days before
	// the avatar is deleted by its retention date.
	TemplateAvatarRetention TemplateName = "avatar_retention"
	// TemplateSocialPublishing tells a member that a social post was published or failed,
	// or that a connected account needs reconnecting.
	TemplateSocialPublishing TemplateName = "social_publishing"
	// TemplateMarketingConfirm is the double opt-in mail the Inference API sends
	// through /internal/mail/send for a marketing subscription. It is plain and
	// carries no promotional content and no unsubscribe footer: until it is
	// confirmed there is no subscription to leave.
	TemplateMarketingConfirm TemplateName = "marketing-confirm"
	// TemplateMarketplaceNotice tells a recipe author that a community recipe was rejected, removed
	// or could not be previewed. It renders fixed wording chosen by a closed reason category (Reason)
	// and never any admin note, reporter detail or free text.
	TemplateMarketplaceNotice TemplateName = "marketplace_notice"
	// TemplateEmailChangeConfirm goes to a user's CURRENT address when an administrator asks to
	// move the account to NewEmail; its one link opens the confirm-or-cancel page (issue #1207).
	TemplateEmailChangeConfirm TemplateName = "email_change_confirm"
	// TemplateEmailChangeDone tells the OLD address that the sign-in address is now NewEmail.
	TemplateEmailChangeDone TemplateName = "email_change_done"
	// TemplateOperatorDigest is the operator's daily platform digest (failed steps refunded to the
	// platform, runs by status, new feedback reports). It renders structured, sanitized fields only
	// (MailData.Digest), is English only, and the relay sends it to nobody but the address configured
	// in its own OPERATOR_DIGEST_EMAIL.
	TemplateOperatorDigest TemplateName = "operator_digest"
)

// AllTemplates lists every template the mailer ships.
var AllTemplates = []TemplateName{
	TemplateVerifyEmail, TemplateResetPassword, TemplateWelcome, TemplateInvite,
	TemplateDeletionSchedule, TemplateExportReady, TemplateLowBalance, TemplateRunnerPaired,
	TemplateJobSucceeded, TemplateJobFailed, TemplateFileCreated, TemplateGraphicsEdited,
	TemplateReviewActivity, TemplateApiKeyExpiring, TemplateWebhookDisabled, TemplateAvatarRetention, TemplateSocialPublishing, TemplateMarketingConfirm, TemplateMarketplaceNotice,
	TemplateEmailChangeConfirm, TemplateEmailChangeDone, TemplateOperatorDigest,
}

// MailData is the single data bag every template renders from. Which fields a
// template needs is declared in templateSpecs; a missing required field is an
// error rather than an email with a blank link.
type MailData struct {
	Name         string // recipient display name
	Link         string // absolute action URL
	ExpiresIn    string // human duration, e.g. "24 hours"
	TempPassword string // welcome (admin-created users) only
	OrgName      string
	InviterName  string
	Role         string
	DeletionDate string
	// ExpiryDate is an API key expiry instant, formatted for the mail.
	ExpiryDate string
	Balance    string
	// Reason is the closed reason category of a marketplace_notice (content, unsafe, copyright,
	// low_quality, preview, safety, other); the template maps it to fixed wording.
	Reason string
	// NewEmail is the requested (email_change_confirm) or new (email_change_done) sign-in address.
	NewEmail string
	// DeviceName is the runner a pairing notification is about.
	DeviceName string
	// ProjectName is the workspace project a notification mail is about.
	ProjectName string
	// FileName is the added file (file_created) or the edited graphic's subject
	// line (graphics_edited).
	FileName string
	// ErrorDetail carries a failed run's error, already truncated by the sender.
	ErrorDetail string
	// ActorName is the member whose action the mail describes, "" when the
	// action was system-triggered (the templates then read "A file was added"
	// instead of "So-and-so added a file").
	ActorName string
	// Unsubscribe, when set, is rendered in the footer and sent as a
	// List-Unsubscribe header. Set it only on non-transactional mail.
	Unsubscribe string

	// FooterNote is the alpha version line in the footer. Every template
	// renders it and the mailer fills it from ProductFooterNote, so it has one
	// source and cannot drift; a template must still render cleanly when it is
	// empty (see mail_footer_test.go).
	FooterNote string

	// MarketingOptIn marks a verify_email mail for a signup that ticked the
	// marketing box: verifying the address also confirms that subscription, and
	// the mail says so (S-32).
	MarketingOptIn bool

	// Digest is the structured body of an operator_digest mail; nil for every other template.
	Digest *OperatorDigest

	// Subject is filled in by the mailer; templates may reference it in <title>.
	Subject string

	// Locale selects the template set and subject language. It is allowlisted by the renderer
	// (an unknown value falls back to the default locale); callers pass the recipient's saved
	// locale, else the request locale.
	Locale string
}

type templateSpec struct {
	required []string
}

var templateSpecs = map[TemplateName]templateSpec{
	TemplateVerifyEmail:        {[]string{"ExpiresIn"}},
	TemplateResetPassword:      {[]string{"ExpiresIn"}},
	TemplateWelcome:            {nil},
	TemplateInvite:             {[]string{"OrgName", "InviterName", "Role", "ExpiresIn"}},
	TemplateDeletionSchedule:   {[]string{"DeletionDate"}},
	TemplateExportReady:        {[]string{"ExpiresIn"}},
	TemplateLowBalance:         {[]string{"OrgName", "Balance"}},
	TemplateRunnerPaired:       {[]string{"OrgName", "DeviceName"}},
	TemplateJobSucceeded:       {[]string{"ProjectName"}},
	TemplateJobFailed:          {[]string{"ProjectName"}},
	TemplateFileCreated:        {[]string{"ProjectName"}},
	TemplateGraphicsEdited:     {[]string{"ProjectName"}},
	TemplateReviewActivity:     {[]string{"ProjectName"}},
	TemplateApiKeyExpiring:     {[]string{"OrgName", "FileName", "ExpiryDate"}},
	TemplateWebhookDisabled:    {[]string{"OrgName", "FileName"}},
	TemplateAvatarRetention:    {[]string{"FileName"}},
	TemplateSocialPublishing:   {[]string{"FileName"}},
	TemplateMarketingConfirm:   {[]string{"Link"}},
	TemplateMarketplaceNotice:  {nil},
	TemplateEmailChangeConfirm: {[]string{"Link", "NewEmail", "ExpiresIn"}},
	TemplateEmailChangeDone:    {[]string{"NewEmail"}},
	TemplateOperatorDigest:     {nil},
}

// Mailer sends templated transactional email.
type Mailer interface {
	SendTemplate(to string, tmpl TemplateName, data MailData) error
}

// RenderedMail is a fully rendered, not yet transported message.
type RenderedMail struct {
	To      string
	Subject string
	HTML    string
	Text    string
}

// MailerConfig is the subset of configuration the mailer needs; injected so
// tests do not touch package-level config.
type MailerConfig struct {
	Host, Port, Username, Password string
	From                           string // bare envelope address
	FromName                       string
	// ReplyTo is the optional bare address a reply goes to (MAIL_REPLY_TO). Empty, or not a
	// single valid address, sends no Reply-To; see replyToAddress.
	ReplyTo string
	AppEnv  string
	// LinkHosts are the origins a templated mail's link may name: PublicBaseURL
	// (the origin every built-in link is constructed from) plus the extra
	// storage origin (F-11). An empty list allows only PublicBaseURL's host;
	// with that unset too, no non-empty link passes.
	LinkHosts []string
	// PublicBaseURL is the origin the deployment's own links are built from.
	PublicBaseURL string

	// CloudflareToken and CloudflareAccountID enable Cloudflare Email Sending. From is
	// shared with SMTP on purpose: one identity sends this deployment's mail, so it has one
	// source address (SMTP_FROM). CloudflareAPIBaseURL overrides the API origin and exists
	// only so a test can point the mailer at an httptest server; empty means the real API.
	CloudflareToken      string
	CloudflareAccountID  string
	CloudflareAPIBaseURL string
}

// renderer holds the parsed template set and the configuration a render depends on. Every
// transport renders through it, so the templates, the required-field checks, ValidateRecipient
// and ValidateLinkHost cannot diverge between providers — a path that skipped them would be a
// header-injection and phishing hole, not a shortcut.
type renderer struct {
	cfg  MailerConfig
	sets map[string]*localeSet
}

// newRenderer parses the embedded templates of every registered locale; a parse failure is a
// programming error and panics at startup.
func newRenderer(cfg MailerConfig) renderer {
	return renderer{cfg: cfg, sets: loadTemplateSets()}
}

// sendFunc matches net/smtp.SendMail so tests can capture the message.
type sendFunc func(addr string, a smtp.Auth, from string, to []string, msg []byte) error

// SMTPMailer is the Mailer backed by net/smtp. When no provider at all is configured it
// is also the console logger, which renders the mail and logs that it did not send it.
type SMTPMailer struct {
	renderer
	send sendFunc
}

// NewSMTPMailer builds the SMTP transport over the embedded templates.
func NewSMTPMailer(cfg MailerConfig) *SMTPMailer {
	return &SMTPMailer{renderer: newRenderer(cfg), send: smtp.SendMail}
}

func (m *SMTPMailer) configured() bool { return m.cfg.Host != "" && m.cfg.From != "" }

// ValidateRecipient accepts only a bare, single RFC 5322 address. Anything with
// a display name, control characters or a second address is rejected, which
// closes header injection through the To line and the envelope.
func ValidateRecipient(to string) (string, error) {
	to = strings.TrimSpace(to)
	if to == "" || strings.ContainsAny(to, "\r\n\x00,;<>") {
		return "", errors.New("invalid recipient address")
	}
	addr, err := mail.ParseAddress(to)
	if err != nil || addr.Address != to || addr.Name != "" {
		return "", errors.New("invalid recipient address")
	}
	// service.invalid is the reserved domain of API-key service accounts: never a real mailbox,
	// never a signup address, never a mail recipient.
	if strings.HasSuffix(strings.ToLower(addr.Address), models.ServiceAccountEmailDomain) {
		return "", errors.New("invalid recipient address")
	}
	return addr.Address, nil
}

// ValidateLinkHost constrains where a templated mail's link may point, the same way
// ValidateRecipient constrains the envelope: an absolute http(s) URL whose origin is
// the configured public base URL or one of the configured extra link hosts. The
// host is the constraint — path, query and fragment are the templates' business.
// An unset base URL and empty host list refuse every non-empty link, so a
// misconfigured deployment fails closed like the endpoint's own token check
// (F-11): a bare scheme check must not let a caller pick the destination.
func ValidateLinkHost(link, baseURL string, extraOrigins []string) error {
	if link == "" {
		return nil
	}
	if !strings.HasPrefix(link, "https://") && !strings.HasPrefix(link, "http://") {
		return errors.New("email link must be an absolute http(s) URL")
	}
	// S-07: whitespace would put free text into the mail around the link, and userinfo makes
	// "https://reelbolt.ai@evil.example/" read as the trusted host; neither has a legitimate use.
	if strings.IndexFunc(link, unicode.IsSpace) >= 0 {
		return errors.New("email link must not contain whitespace")
	}
	u, err := url.Parse(link)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return errors.New("email link must be an absolute http(s) URL")
	}
	if u.User != nil {
		return errors.New("email link must not carry userinfo")
	}
	host := strings.ToLower(u.Host)
	origin := u.Scheme + "://" + host
	if b, err := url.Parse(baseURL); err == nil && (b.Scheme == "http" || b.Scheme == "https") && b.Host != "" {
		if strings.ToLower(b.Host) == host && b.Scheme == u.Scheme {
			return nil
		}
	}
	for _, extra := range extraOrigins {
		if strings.TrimRight(extra, "/") == origin {
			return nil
		}
	}
	return fmt.Errorf("email link host %q is not an allowed origin", u.Host)
}

// marketingConfirmLinkPattern is the one shape a marketing double opt-in link may have: an origin,
// the confirm page and a single token of URL-safe characters — no userinfo, other path, other query
// key, percent-encoding or fragment.
var marketingConfirmLinkPattern = regexp.MustCompile(`^(https?://[^/?#@\s]+)/app/marketing/confirm\?token=[A-Za-z0-9._-]+$`)

// ValidateMarketingConfirmLink accepts exactly
// {PublicBaseURL or an allowed extra origin}/app/marketing/confirm?token=<[A-Za-z0-9._-]+> (S-07).
// The marketing confirmation is the one template mailed to addresses with no account, so its link is
// pinned to the page it confirms on, not merely to an allowed host.
func ValidateMarketingConfirmLink(link, baseURL string, extraOrigins []string) error {
	m := marketingConfirmLinkPattern.FindStringSubmatch(link)
	if m == nil {
		return errors.New("marketing confirmation link must be <origin>/app/marketing/confirm?token=<token>")
	}
	if err := ValidateLinkHost(link, baseURL, extraOrigins); err != nil {
		return err
	}
	origin := strings.ToLower(m[1])
	if b, err := url.Parse(baseURL); err == nil && (b.Scheme == "http" || b.Scheme == "https") && b.Host != "" &&
		b.Scheme+"://"+strings.ToLower(b.Host) == origin {
		return nil
	}
	for _, extra := range extraOrigins {
		if strings.ToLower(strings.TrimRight(extra, "/")) == origin {
			return nil
		}
	}
	return fmt.Errorf("marketing confirmation link origin %q is not an allowed origin", m[1])
}

// sanitizeHeaderValue strips every control character so a value can never
// terminate its header line, whatever it was built from.
func sanitizeHeaderValue(v string) string {
	return strings.Map(func(r rune) rune {
		if r < 0x20 || r == 0x7f {
			return ' '
		}
		return r
	}, v)
}

// Render validates data and renders both bodies. It is pure (no I/O) and is the only way
// any transport turns a template into a message.
func (r renderer) Render(to string, tmpl TemplateName, data MailData) (RenderedMail, error) {
	recipient, err := ValidateRecipient(to)
	if err != nil {
		return RenderedMail{}, err
	}
	spec, ok := templateSpecs[tmpl]
	if !ok {
		return RenderedMail{}, fmt.Errorf("unknown email template %q", tmpl)
	}
	fields := map[string]string{
		"Link": data.Link, "ExpiresIn": data.ExpiresIn, "OrgName": data.OrgName,
		"InviterName": data.InviterName, "Role": data.Role,
		"DeletionDate": data.DeletionDate, "Balance": data.Balance,
		"DeviceName": data.DeviceName, "ProjectName": data.ProjectName,
		"FileName": data.FileName, "ErrorDetail": data.ErrorDetail,
		"ActorName": data.ActorName, "ExpiryDate": data.ExpiryDate, "NewEmail": data.NewEmail,
	}
	for _, f := range spec.required {
		if strings.TrimSpace(fields[f]) == "" {
			return RenderedMail{}, fmt.Errorf("email template %q requires %s", tmpl, f)
		}
	}
	if err := ValidateLinkHost(data.Link, r.cfg.PublicBaseURL, r.cfg.LinkHosts); err != nil {
		return RenderedMail{}, err
	}
	if tmpl == TemplateMarketingConfirm {
		if err := ValidateMarketingConfirmLink(data.Link, r.cfg.PublicBaseURL, r.cfg.LinkHosts); err != nil {
			return RenderedMail{}, err
		}
	}
	if tmpl == TemplateOperatorDigest {
		if data.Digest == nil {
			return RenderedMail{}, fmt.Errorf("email template %q requires Digest", tmpl)
		}
		data.Digest = data.Digest.Sanitized()
		data.Locale = DefaultLocale // English only
	}
	// The locale is allowlisted before it selects anything; anything else is the default.
	loc, ok := NormalizeLocale(data.Locale)
	if !ok {
		loc = DefaultLocale
	}
	data.ExpiresIn = localizeDuration(data.ExpiresIn, loc)
	data.Role = localizeRole(data.Role, loc)
	if strings.TrimSpace(data.Name) == "" {
		data.Name = defaultNameFor(loc)
	}
	// Every outbound mail carries the product footer, so no caller and no template can send a
	// mail without the alpha version. The English line is the one constant.
	data.FooterNote = footerNoteFor(loc)
	subj, err := renderSubject(loc, tmpl, data)
	if err != nil {
		return RenderedMail{}, err
	}
	data.Subject = sanitizeHeaderValue(strings.TrimSpace(subj))
	htmlTmpl, textTmpl := r.pick(loc, tmpl)

	var htmlBuf, textBuf bytes.Buffer
	if err := htmlTmpl.ExecuteTemplate(&htmlBuf, string(tmpl)+".html", data); err != nil {
		return RenderedMail{}, fmt.Errorf("render %s html: %w", tmpl, err)
	}
	if err := textTmpl.ExecuteTemplate(&textBuf, string(tmpl)+".txt", data); err != nil {
		return RenderedMail{}, fmt.Errorf("render %s text: %w", tmpl, err)
	}
	return RenderedMail{To: recipient, Subject: data.Subject, HTML: htmlBuf.String(), Text: textBuf.String()}, nil
}

// isMarketingTemplate reports whether tmpl belongs to the marketing path, whose links are never logged.
func isMarketingTemplate(tmpl TemplateName) bool {
	return tmpl == TemplateMarketingConfirm
}

// SendTemplate renders and sends a message. With SMTP unconfigured it logs and
// returns nil, so a self-hosted install without a mail server keeps working.
func (m *SMTPMailer) SendTemplate(to string, tmpl TemplateName, data MailData) error {
	r, err := m.Render(to, tmpl, data)
	if err != nil {
		return err
	}
	if !m.configured() {
		// The console logger. Choosing it is logged loudly at startup (logMailerChoice);
		// this line says which mail passed through unsent, by reference only (C4). The link
		// carries a live token, so it is printed only with APP_ENV=development, and never for
		// a marketing template (its token confirms a subscription for someone else's address).
		if m.cfg.AppEnv == "development" && data.Link != "" && !isMarketingTemplate(tmpl) {
			log.Printf("[mail: no provider configured] %s email to %s NOT SENT; link (development only): %s", tmpl, MailLogRef(r.To), data.Link)
		} else {
			log.Printf("[mail: no provider configured] %s email to %s NOT SENT", tmpl, MailLogRef(r.To))
		}
		return nil
	}
	msg, err := m.buildMessage(r, data.Unsubscribe)
	if err != nil {
		return err
	}
	addr := fmt.Sprintf("%s:%s", m.cfg.Host, m.cfg.Port)
	var auth smtp.Auth
	if m.cfg.Username != "" {
		auth = smtp.PlainAuth("", m.cfg.Username, m.cfg.Password, m.cfg.Host)
	}
	return m.send(addr, auth, m.cfg.From, []string{r.To}, msg)
}

func (m *SMTPMailer) buildMessage(r RenderedMail, unsubscribe string) ([]byte, error) {
	from := mail.Address{Name: sanitizeHeaderValue(m.cfg.FromName), Address: sanitizeHeaderValue(m.cfg.From)}
	var idBytes [12]byte
	if _, err := rand.Read(idBytes[:]); err != nil {
		return nil, err
	}
	domain := "reelbolt.local"
	if at := strings.LastIndex(from.Address, "@"); at >= 0 {
		domain = from.Address[at+1:]
	}

	var buf bytes.Buffer
	mw := multipart.NewWriter(&buf)
	var out bytes.Buffer
	hdr := func(k, v string) { fmt.Fprintf(&out, "%s: %s\r\n", k, v) }
	hdr("From", from.String())
	hdr("To", r.To)
	if rt := replyToAddress(m.cfg.ReplyTo); rt != "" {
		hdr("Reply-To", rt)
	}
	hdr("Subject", mime.QEncoding.Encode("utf-8", r.Subject))
	hdr("Date", time.Now().UTC().Format(time.RFC1123Z))
	hdr("Message-ID", fmt.Sprintf("<%s@%s>", hex.EncodeToString(idBytes[:]), domain))
	hdr("MIME-Version", "1.0")
	if u := sanitizeHeaderValue(unsubscribe); u != "" {
		hdr("List-Unsubscribe", "<"+u+">")
	}
	hdr("Content-Type", fmt.Sprintf("multipart/alternative; boundary=%q", mw.Boundary()))
	out.WriteString("\r\n")

	for _, part := range []struct{ ct, body string }{
		{"text/plain; charset=UTF-8", r.Text},
		{"text/html; charset=UTF-8", r.HTML},
	} {
		w, err := mw.CreatePart(textproto.MIMEHeader{
			"Content-Type":              {part.ct},
			"Content-Transfer-Encoding": {"quoted-printable"},
		})
		if err != nil {
			return nil, err
		}
		qp := quotedprintable.NewWriter(w)
		if _, err := qp.Write([]byte(part.body)); err != nil {
			return nil, err
		}
		if err := qp.Close(); err != nil {
			return nil, err
		}
	}
	if err := mw.Close(); err != nil {
		return nil, err
	}
	out.Write(buf.Bytes())
	return out.Bytes(), nil
}

// SendWelcomeEmail is the admin user-creation mail (temporary password). Kept
// as a wrapper so existing callers are unchanged; it never fails the caller.
func SendWelcomeEmail(toEmail, displayName, otp string) {
	if !MailerConfigured() {
		log.Printf("[mail: no provider configured] Welcome email skipped for %s; temporary password was returned in the admin API response", MailLogRef(toEmail))
		return
	}
	err := DefaultMailer().SendTemplate(toEmail, TemplateWelcome, MailData{
		Name:         displayName,
		TempPassword: otp,
		Locale:       UserLocaleByEmail(toEmail),
		Link:         config.Cfg.LinkBaseURL + "/app/login",
	})
	if err != nil {
		LogMailFailure(string(TemplateWelcome), toEmail, err)
	}
}
