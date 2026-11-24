package services

import (
	"log"
	"strings"
	"sync"

	"github.com/vecchiotom/reelbolt/config"
)

// MailerProvider names the transport the process will send mail through. It is chosen once,
// from configuration, at construction — never re-considered at send time.
type MailerProvider string

const (
	// ProviderCloudflare is Cloudflare Email Sending (POST
	// /client/v4/accounts/{account}/email/sending/send).
	ProviderCloudflare MailerProvider = "cloudflare"
	// ProviderSMTP is net/smtp with the SMTP_* settings.
	ProviderSMTP MailerProvider = "smtp"
	// ProviderConsole sends nothing at all: it renders the mail and logs that it was not
	// sent. It is the honest last resort for a self-hosted install with no mail server, and
	// it is why selecting it has to be said out loud — welcome, verification,
	// password-reset and invitation mail simply never arrives.
	ProviderConsole MailerProvider = "console"
)

// ChooseMailerProvider applies the one precedence rule the deployment depends on:
// Cloudflare Email Sending when its token, account id and from are all set, otherwise SMTP
// when its host and from are set, otherwise the console logger. It returns the chosen
// provider and the reason, naming what was missing from each provider that was skipped.
func ChooseMailerProvider(cfg MailerConfig) (MailerProvider, string) {
	cfReady := cfg.CloudflareToken != "" && cfg.CloudflareAccountID != "" && cfg.From != ""
	smtpReady := cfg.Host != "" && cfg.From != ""

	switch {
	case cfReady:
		return ProviderCloudflare, "Cloudflare Email Sending is configured (CLOUDFLARE_EMAIL_TOKEN, CLOUDFLARE_EMAIL_ACCOUNT_ID and SMTP_FROM are all set)"
	case smtpReady:
		return ProviderSMTP, "SMTP_HOST and SMTP_FROM are set; Cloudflare Email Sending is skipped (" + missingNames(
			nameIf(cfg.CloudflareToken == "", "CLOUDFLARE_EMAIL_TOKEN"),
			nameIf(cfg.CloudflareAccountID == "", "CLOUDFLARE_EMAIL_ACCOUNT_ID"),
		) + ")"
	default:
		return ProviderConsole, "no email provider is configured. Cloudflare Email Sending needs " + missingNames(
			nameIf(cfg.CloudflareToken == "", "CLOUDFLARE_EMAIL_TOKEN"),
			nameIf(cfg.CloudflareAccountID == "", "CLOUDFLARE_EMAIL_ACCOUNT_ID"),
			nameIf(cfg.From == "", "SMTP_FROM"),
		) + "; SMTP needs " + missingNames(
			nameIf(cfg.Host == "", "SMTP_HOST"),
			nameIf(cfg.From == "", "SMTP_FROM"),
		)
	}
}

// nameIf returns name when missing is true, and "" otherwise, so an assembled sentence lists
// only what is actually absent.
func nameIf(missing bool, name string) string {
	if missing {
		return name
	}
	return ""
}

// missingNames joins the non-empty names as "A, B and C" ("" when there are none).
func missingNames(names ...string) string {
	var set []string
	for _, n := range names {
		if n != "" {
			set = append(set, n)
		}
	}
	switch len(set) {
	case 0:
		return "nothing"
	case 1:
		return set[0]
	default:
		return strings.Join(set[:len(set)-1], ", ") + " and " + set[len(set)-1]
	}
}

// NewMailer builds the mailer the configuration selects. The choice happens here and only
// here: a send that fails at runtime is reported, not routed to another provider.
func NewMailer(cfg MailerConfig) Mailer {
	provider, _ := ChooseMailerProvider(cfg)
	return newMailer(provider, cfg)
}

func newMailer(provider MailerProvider, cfg MailerConfig) Mailer {
	switch provider {
	case ProviderCloudflare:
		return NewCloudflareMailer(cfg)
	case ProviderSMTP:
		return NewSMTPMailer(cfg)
	default:
		// The unconfigured SMTPMailer is the console logger: it renders, logs and returns nil.
		return NewSMTPMailer(cfg)
	}
}

var (
	defaultMailer   Mailer
	defaultMailerMu sync.Mutex
)

// DefaultMailer returns the process-wide mailer built from config.Cfg, choosing the provider
// on first use and logging the choice.
func DefaultMailer() Mailer {
	defaultMailerMu.Lock()
	defer defaultMailerMu.Unlock()
	if defaultMailer == nil {
		cfg := processMailerConfig()
		provider, reason := ChooseMailerProvider(cfg)
		defaultMailer = newMailer(provider, cfg)
		logMailerChoice(provider, reason, cfg)
	}
	return defaultMailer
}

// LogEmailProvider builds the process-wide mailer now and logs the provider it selected. main
// calls it at startup on purpose: the point of the log is to appear before the first user
// wonders why their password-reset mail never arrived, not after.
func LogEmailProvider() { _ = DefaultMailer() }

// MailerConfigured reports whether a real transport is configured, i.e. whether DefaultMailer
// will actually send rather than log. Callers that mean "can this deployment send mail at all"
// must use this and not a per-provider check: a Cloudflare-only deployment would otherwise
// report every invitation as unsent.
func MailerConfigured() bool {
	provider, _ := ChooseMailerProvider(processMailerConfig())
	return provider != ProviderConsole
}

// processMailerConfig is the configuration the running process describes.
func processMailerConfig() MailerConfig {
	return MailerConfig{
		Host: config.Cfg.SMTPHost, Port: config.Cfg.SMTPPort,
		Username: config.Cfg.SMTPUsername, Password: config.Cfg.SMTPPassword,
		From: config.Cfg.SMTPFrom, FromName: config.Cfg.MailFromName,
		ReplyTo:       config.Cfg.MailReplyTo,
		AppEnv:        config.Cfg.AppEnv,
		PublicBaseURL: config.Cfg.LinkBaseURL,
		LinkHosts:     config.Cfg.MailLinkHosts,

		CloudflareToken:     config.Cfg.CloudflareEmailToken,
		CloudflareAccountID: config.Cfg.CloudflareEmailAccountID,
	}
}

// logMailerChoice is the startup line that says which provider will carry the deployment's
// mail, from which address, and why the others were skipped. A mailer that drops silently to
// console logging is the same class of bug as a quietly-defaulted payment environment: the
// deployment looks healthy while no mail arrives.
func logMailerChoice(provider MailerProvider, reason string, cfg MailerConfig) {
	from := cfg.From
	if from == "" {
		from = "(unset)"
	}
	if provider == ProviderConsole {
		log.Printf("[mail] WARNING: provider=%s — %s. Outbound mail is being LOGGED, NOT SENT: welcome, verification, password-reset and invitation emails will never arrive. from=%s", provider, reason, from)
		return
	}
	log.Printf("[mail] provider=%s from=%s — %s", provider, from, reason)
}
