package services

import (
	"bytes"
	"fmt"
	"log"
	"strings"
	"testing"

	"github.com/vecchiotom/reelbolt/config"
)

// swapProcessMailConfig installs cfg as the process configuration and clears the cached mailer,
// returning a func that puts the cache back. Only the mailer reads config.Cfg in these tests.
func swapProcessMailConfig(cfg config.AppConfig) func() {
	original := config.Cfg
	config.Cfg = cfg
	defaultMailerMu.Lock()
	defaultMailer = nil
	defaultMailerMu.Unlock()
	return func() {
		config.Cfg = original
		defaultMailerMu.Lock()
		defaultMailer = nil
		defaultMailerMu.Unlock()
	}
}

// actualProvider classifies the mailer DefaultMailer returned, without asking the selection
// function it is meant to be checking.
func actualProvider(m Mailer) MailerProvider {
	switch v := m.(type) {
	case *CloudflareMailer:
		return ProviderCloudflare
	case *SMTPMailer:
		if v.configured() {
			return ProviderSMTP
		}
		return ProviderConsole
	default:
		return MailerProvider(fmt.Sprintf("%T", m))
	}
}

// captureLogs runs f with the standard logger redirected, returning what it wrote.
func captureLogs(f func()) string {
	var buf bytes.Buffer
	previous := log.Writer()
	log.SetOutput(&buf)
	defer log.SetOutput(previous)
	f()
	return buf.String()
}

// The precedence itself: Cloudflare when its token, account id and from are all set, otherwise
// SMTP when host and from are set, otherwise the console logger.
func TestChooseMailerProviderPrecedence(t *testing.T) {
	cases := []struct {
		name string
		cfg  MailerConfig
		want MailerProvider
	}{
		{
			"Cloudflare when its token, account id and from are all set",
			MailerConfig{CloudflareToken: "t", CloudflareAccountID: "a", From: "welcome@reelbolt.ai"},
			ProviderCloudflare,
		},
		{
			"Cloudflare wins over a fully configured SMTP",
			MailerConfig{CloudflareToken: "t", CloudflareAccountID: "a", From: "welcome@reelbolt.ai", Host: "smtp.test", Port: "587", Username: "u", Password: "p"},
			ProviderCloudflare,
		},
		{
			"SMTP when Cloudflare has no token",
			MailerConfig{CloudflareAccountID: "a", From: "welcome@reelbolt.ai", Host: "smtp.test"},
			ProviderSMTP,
		},
		{
			"SMTP when Cloudflare has no account id",
			MailerConfig{CloudflareToken: "t", From: "welcome@reelbolt.ai", Host: "smtp.test"},
			ProviderSMTP,
		},
		{
			// The from address is shared by both providers — one identity, one source address —
			// so with none set neither can send, whatever else is configured.
			"console when no from address is set for either provider",
			MailerConfig{CloudflareToken: "t", CloudflareAccountID: "a", Host: "smtp.test"},
			ProviderConsole,
		},
		{
			"console when nothing is configured",
			MailerConfig{},
			ProviderConsole,
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, reason := ChooseMailerProvider(c.cfg)
			if got != c.want {
				t.Fatalf("provider = %s, want %s (%s)", got, c.want, reason)
			}
			if strings.TrimSpace(reason) == "" {
				t.Error("a provider choice must come with a reason")
			}
		})
	}
}

// The reason names what the skipped providers were missing; that is what turns "no mail is
// arriving" into a five-minute fix.
func TestChooseMailerProviderNamesWhatIsMissing(t *testing.T) {
	_, reason := ChooseMailerProvider(MailerConfig{Host: "smtp.test", From: "welcome@reelbolt.ai"})
	for _, want := range []string{"CLOUDFLARE_EMAIL_TOKEN", "CLOUDFLARE_EMAIL_ACCOUNT_ID"} {
		if !strings.Contains(reason, want) {
			t.Errorf("reason %q does not name %s", reason, want)
		}
	}

	_, reason = ChooseMailerProvider(MailerConfig{})
	for _, want := range []string{"CLOUDFLARE_EMAIL_TOKEN", "CLOUDFLARE_EMAIL_ACCOUNT_ID", "SMTP_FROM", "SMTP_HOST"} {
		if !strings.Contains(reason, want) {
			t.Errorf("reason %q does not name %s", reason, want)
		}
	}
}

func TestDefaultMailerPicksProviderByConfiguration(t *testing.T) {
	cases := []struct {
		name string
		cfg  config.AppConfig
		want MailerProvider
	}{
		{
			"Cloudflare when its token, account id and from are set",
			config.AppConfig{CloudflareEmailToken: "t", CloudflareEmailAccountID: "a", SMTPFrom: "welcome@reelbolt.ai"},
			ProviderCloudflare,
		},
		{
			"Cloudflare even when SMTP is fully configured",
			config.AppConfig{CloudflareEmailToken: "t", CloudflareEmailAccountID: "a", SMTPFrom: "welcome@reelbolt.ai", SMTPHost: "smtp.test", SMTPPort: "587", SMTPUsername: "u", SMTPPassword: "p"},
			ProviderCloudflare,
		},
		{
			"SMTP when only SMTP is configured",
			config.AppConfig{SMTPHost: "smtp.test", SMTPPort: "587", SMTPFrom: "noreply@reelbolt.ai"},
			ProviderSMTP,
		},
		{
			"console when neither provider is configured",
			config.AppConfig{},
			ProviderConsole,
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			defer swapProcessMailConfig(c.cfg)()

			var m Mailer
			logs := captureLogs(func() { m = DefaultMailer() })

			if got := actualProvider(m); got != c.want {
				t.Fatalf("DefaultMailer returned %s, want %s", got, c.want)
			}
			if again := DefaultMailer(); again != m {
				t.Error("DefaultMailer must be process-wide and stable")
			}

			// The selection is stated out loud, with the from address, and — when the console
			// logger won — as a warning that nothing will be sent.
			if !strings.Contains(logs, "provider="+string(c.want)) {
				t.Errorf("log %q does not state provider=%s", logs, c.want)
			}
			if c.want == ProviderConsole {
				if !strings.Contains(logs, "WARNING") || !strings.Contains(logs, "NOT SENT") {
					t.Errorf("log %q does not warn that nothing is sent", logs)
				}
			} else if !strings.Contains(logs, c.cfg.SMTPFrom) {
				t.Errorf("log %q does not name the from address %s", logs, c.cfg.SMTPFrom)
			}
			// The provider that was skipped is named.
			switch c.want {
			case ProviderSMTP:
				if !strings.Contains(logs, "CLOUDFLARE_EMAIL_TOKEN") {
					t.Errorf("log %q does not say why Cloudflare was skipped", logs)
				}
			case ProviderCloudflare:
				if !strings.Contains(logs, "Cloudflare Email Sending is configured") {
					t.Errorf("log %q does not say why Cloudflare was chosen", logs)
				}
			}
		})
	}
}

// A Cloudflare-only deployment can send mail, so the "can this deployment send at all" answer
// must not be an SMTP-only check — that is what reported delivered invitations as unsent.
func TestMailerConfiguredIsProviderAgnostic(t *testing.T) {
	defer swapProcessMailConfig(config.AppConfig{
		SMTPFrom:                 "welcome@reelbolt.ai",
		CloudflareEmailToken:     "t",
		CloudflareEmailAccountID: "a",
	})()

	if !MailerConfigured() {
		t.Fatal("a Cloudflare-only deployment reports itself unable to send mail")
	}

	defer swapProcessMailConfig(config.AppConfig{})()
	if MailerConfigured() {
		t.Fatal("an unconfigured deployment reports itself able to send mail")
	}
}

// NewMailer picks the transport the configuration selects, and nothing else does.
func TestNewMailerReturnsTheSelectedTransport(t *testing.T) {
	if m := NewMailer(MailerConfig{CloudflareToken: "t", CloudflareAccountID: "a", From: "welcome@reelbolt.ai"}); actualProvider(m) != ProviderCloudflare {
		t.Fatalf("got %T", m)
	}
	if m := NewMailer(MailerConfig{Host: "smtp.test", From: "noreply@reelbolt.ai"}); actualProvider(m) != ProviderSMTP {
		t.Fatalf("got %T", m)
	}
	if m := NewMailer(MailerConfig{}); actualProvider(m) != ProviderConsole {
		t.Fatalf("got %T", m)
	}
}

// LogEmailProvider is what main.go calls at startup; it must log even though nothing sends.
func TestLogEmailProviderLogsTheSelectedProvider(t *testing.T) {
	defer swapProcessMailConfig(config.AppConfig{
		SMTPFrom:                 "welcome@reelbolt.ai",
		CloudflareEmailToken:     "t",
		CloudflareEmailAccountID: "a",
	})()

	logs := captureLogs(LogEmailProvider)
	if !strings.Contains(logs, "provider=cloudflare") || !strings.Contains(logs, "welcome@reelbolt.ai") {
		t.Fatalf("startup log %q does not name the provider and the from address", logs)
	}
}
