package config

import (
	"fmt"
	"log"
	"net/url"
	"os"
	"strconv"
	"strings"
	"time"
)

var Version = "0.4.0-alpha"

type AppConfig struct {
	DatabaseURL  string
	InferenceURL string

	// DBPool caps the database/sql pool GORM runs on (DB_MAX_OPEN_CONNS, DB_MAX_IDLE_CONNS,
	// DB_CONN_MAX_LIFETIME). database/sql's own default is an unbounded pool, which on a shared
	// Postgres with max_connections=100 lets one busy Go API starve every other service.
	DBPool DBPool

	JWTSigningKey string
	JWTIssuer     string
	JWTAudience   string

	SMTPHost     string
	SMTPPort     string
	SMTPUsername string
	SMTPPassword string
	SMTPFrom     string

	// CloudflareEmailToken and CloudflareEmailAccountID turn on Cloudflare Email Sending
	// (CLOUDFLARE_EMAIL_TOKEN, CLOUDFLARE_EMAIL_ACCOUNT_ID), the deployment's outbound mail
	// provider. The token is a Cloudflare **user API token** — it is a SECRET, belongs in the
	// box's .env and never in the repository, and it is NOT an R2 key (the account is the same
	// one R2 uses, but the credential is a different thing). With either value unset the
	// provider is not offered at all; services.ChooseMailerProvider owns the precedence.
	CloudflareEmailToken     string
	CloudflareEmailAccountID string

	// MailFromName is the display name on outgoing mail ("ReelBolt <noreply@…>").
	MailFromName string
	// MailReplyTo (MAIL_REPLY_TO) is the optional address a person's reply to any transactional mail
	// goes to. Empty sends no Reply-To, so a reply goes to the From address. Set it to the monitored
	// support mailbox when the From address (welcome@, noreply@) is not one.
	MailReplyTo string
	// PublicBaseURL is the externally reachable origin (no trailing slash) used
	// to build links in emails, e.g. https://reelbolt.ai.
	PublicBaseURL string
	// AppEnv is "development" or "production". Only outside production does the
	// mailer's no-SMTP fallback log a mail's action link (it is a bearer secret).
	AppEnv string

	AdminEmail    string
	AdminPassword string

	RabbitMQHost     string
	RabbitMQPort     string
	RabbitMQUsername string
	RabbitMQPassword string

	// CookieSecure sets the Secure flag on the reelbolt_token/reelbolt_user
	// cookies. Leave false for http://localhost development — browsers
	// silently drop Secure cookies over plain HTTP, which would break login
	// entirely. Set COOKIE_SECURE=true once nginx is actually serving TLS
	// (see docs/tls.md).
	CookieSecure bool

	// SignupEnabled turns on self-serve signup (SIGNUP_ENABLED, default false so
	// self-hosted installs stay invite-only; decision D12).
	SignupEnabled bool

	// AttributionURLCaptureEnabled records campaign parameters carried in the account-creating
	// request itself (ATTRIBUTION_URL_CAPTURE_ENABLED, default false): the `attribution` object of
	// POST /api/v1/auth/signup (campaign labels and landing path), or the utm_* and lp query of the
	// OAuth start endpoint, which then ride the OAuth state. They are written to
	// user_attribution with capture = 'url', and only when the consented rb_ft cookie gave no row
	// (the cookie always wins, capture = 'cookie'). Nothing is read from or stored on the device,
	// but it is recorded on the account without the cookie banner, on a legitimate-interest basis,
	// so the controller turns it on only once the Privacy Policy text that describes it is live
	// (docs/design/new-user-funnel.md section 7). The Inference API reads the same variable as
	// Attribution:UrlCaptureEnabled to tell the admin funnel page whether per-link account counts
	// are complete; set it for both services or neither. The suggested persona is not behind this
	// flag: it is a product setting, not attribution.
	AttributionURLCaptureEnabled bool

	// AbuseBlockDisposable rejects signups from disposable-email domains
	// (ABUSE_BLOCK_DISPOSABLE, default true; embedded blocklist snapshot).
	AbuseBlockDisposable bool
	// AbuseMXCheck additionally requires the signup domain to have a mail
	// server (ABUSE_MX_CHECK, default false: it adds a DNS lookup per signup).
	AbuseMXCheck bool
	// TurnstileSecret enables Cloudflare Turnstile on signup/forgot-password/
	// resend-verification when set (TURNSTILE_SECRET); unset skips the challenge.
	TurnstileSecret string
	// TrialRequiresCard would route the trial grant through a $0 card checkout
	// (TRIAL_REQUIRES_CARD, default false). It is read and logged only: the
	// checkout needs the Paddle implementation (B9b), so the flag has no effect yet.
	TrialRequiresCard bool

	// OAuth (C10) client credentials. A provider is offered only when both its
	// id and secret are set; the endpoints and scopes are fixed in
	// handlers/auth/oauth.go, so no env var can point them at another host.
	OAuthGoogleClientID     string
	OAuthGoogleClientSecret string
	OAuthGitHubClientID     string
	OAuthGitHubClientSecret string

	// TenancyMode is "SelfHosted" (default: users also join the Default org) or
	// "Cloud" (users get only their Personal org). See docs/design/tenancy.md.
	TenancyMode string

	// RunnerGatewayURL is the wss:// endpoint a paired desktop runner dials
	// (RUNNER_GATEWAY_URL; the D protocol's `wss://runner.<domain>/v1/connect`). It is
	// handed to the device in the pairing poll response, and the runner validates it
	// against a compiled-in allowlist (E5a) before trusting it. Empty means no gateway
	// is deployed: pairing still works, and no device can connect.
	RunnerGatewayURL string

	// OAuthIssuer is the public origin this API's OAuth 2.1 authorization server
	// advertises as its issuer (OAUTH_ISSUER). It defaults to PublicBaseURL, because
	// nginx serves the Go API's /oauth/* and /.well-known/* paths on the deployment's
	// own origin; it exists as its own key so a deployment that splits the API onto a
	// hostname of its own can say so without touching the links mail is built from
	// (docs/design/mcp-oauth.md §8: the origin comes from config, never a hardcoded
	// host). No trailing slash.
	OAuthIssuer string

	// InternalAPIToken is the shared secret the Inference API presents on /internal/mail/send
	// (INTERNAL_API_TOKEN). Unset means "no internal mail": the endpoint answers 404 and every
	// .NET-originated mail is skipped with a log line. All outbound mail is sent from here, because
	// this service is the only one that holds SMTP credentials and the template set (C0).
	InternalAPIToken string

	// MailLinkHosts are the extra origins, besides PublicBaseURL, whose host a templated mail link
	// may name (MAIL_LINK_HOSTS, comma-separated, http(s) origins only). It covers the presigned
	// storage endpoint the .NET side mints export_ready links against (MinIO:PublicEndpoint /
	// MinIO:Endpoint on that side). Unset allows only the public site's origin: an empty list must
	// not degrade to "any host".
	MailLinkHosts []string
	// LinkBaseURL is the origin the Go side's own mail links are built from (LINK_BASE_URL,
	// e.g. the dashboard's public address when it is not the marketing site). It defaults to
	// PublicBaseURL; setting it keeps the link host the mailer validates against identical to
	// the origin the links name, without widening the allowlist.
	LinkBaseURL string
}

const (
	TenancySelfHosted = "SelfHosted"
	TenancyCloud      = "Cloud"
)

var Cfg AppConfig

// CloudTenancy reports whether the deployment runs in Cloud tenancy mode.
func CloudTenancy() bool { return Cfg.TenancyMode == TenancyCloud }

func Load() {
	Cfg = AppConfig{
		DatabaseURL:  getEnv("DATABASE_URL", "postgres://postgres:postgres@localhost:5432/reelforge?sslmode=disable"),
		InferenceURL: getEnv("INFERENCE_URL", "http://localhost:5200"),

		JWTSigningKey: getEnv("JWT_SIGNING_KEY", ""),
		JWTIssuer:     getEnv("JWT_ISSUER", "reelbolt-api"),
		JWTAudience:   getEnv("JWT_AUDIENCE", "reelbolt-inference"),

		SMTPHost:     getEnv("SMTP_HOST", ""),
		SMTPPort:     getEnv("SMTP_PORT", "587"),
		SMTPUsername: getEnv("SMTP_USERNAME", ""),
		SMTPPassword: getEnv("SMTP_PASSWORD", ""),
		SMTPFrom:     getEnv("SMTP_FROM", ""),

		CloudflareEmailToken:     getEnv("CLOUDFLARE_EMAIL_TOKEN", ""),
		CloudflareEmailAccountID: getEnv("CLOUDFLARE_EMAIL_ACCOUNT_ID", ""),

		MailFromName:  getEnv("MAIL_FROM_NAME", "ReelBolt"),
		MailReplyTo:   getEnv("MAIL_REPLY_TO", ""),
		PublicBaseURL: strings.TrimRight(getEnv("PUBLIC_BASE_URL", "http://localhost"), "/"),
		AppEnv:        strings.ToLower(getEnv("APP_ENV", "production")),

		AdminEmail:    getEnv("ADMIN_EMAIL", "admin@reelbolt.local"),
		AdminPassword: getEnv("ADMIN_PASSWORD", ""),

		RabbitMQHost:     getEnv("RABBITMQ_HOST", "localhost"),
		RabbitMQPort:     getEnv("RABBITMQ_PORT", "5672"),
		RabbitMQUsername: getEnv("RABBITMQ_USER", "guest"),
		RabbitMQPassword: getEnv("RABBITMQ_PASSWORD", "guest"),

		CookieSecure: getBoolEnv("COOKIE_SECURE", false),

		SignupEnabled: getBoolEnv("SIGNUP_ENABLED", false),

		AttributionURLCaptureEnabled: getBoolEnv("ATTRIBUTION_URL_CAPTURE_ENABLED", false),

		AbuseBlockDisposable: getBoolEnv("ABUSE_BLOCK_DISPOSABLE", true),
		AbuseMXCheck:         getBoolEnv("ABUSE_MX_CHECK", false),
		TurnstileSecret:      getEnv("TURNSTILE_SECRET", ""),
		TrialRequiresCard:    getBoolEnv("TRIAL_REQUIRES_CARD", false),

		OAuthGoogleClientID:     getEnv("OAUTH_GOOGLE_CLIENT_ID", ""),
		OAuthGoogleClientSecret: getEnv("OAUTH_GOOGLE_CLIENT_SECRET", ""),
		OAuthGitHubClientID:     getEnv("OAUTH_GITHUB_CLIENT_ID", ""),
		OAuthGitHubClientSecret: getEnv("OAUTH_GITHUB_CLIENT_SECRET", ""),

		TenancyMode: getEnv("TENANCY_MODE", TenancySelfHosted),

		RunnerGatewayURL: getEnv("RUNNER_GATEWAY_URL", ""),

		InternalAPIToken: getEnv("INTERNAL_API_TOKEN", ""),
	}

	pool, err := parseDBPool(os.Getenv)
	if err != nil {
		log.Fatalf("%v", err)
	}
	Cfg.DBPool = pool

	// Mail link host allowlist (F-11): origins, not hosts, so a configured storage endpoint keeps
	// its port; a bare hostname is not a usable URL and is refused rather than coerced.
	for _, origin := range strings.Split(getEnv("MAIL_LINK_HOSTS", ""), ",") {
		if u, err := url.Parse(origin); err == nil && (u.Scheme == "http" || u.Scheme == "https") && u.Host != "" {
			Cfg.MailLinkHosts = append(Cfg.MailLinkHosts, strings.TrimRight(strings.ToLower(u.String()), "/"))
		}
	}
	// The origin the Go side's own mail links are built from; the mailer validates link
	// hosts against it, so the two must name the same origin (F-11).
	if b := getEnv("LINK_BASE_URL", ""); b != "" {
		Cfg.LinkBaseURL = strings.TrimRight(b, "/")
	} else {
		Cfg.LinkBaseURL = Cfg.PublicBaseURL
	}

	// The OAuth issuer/endpoint origin. Defaults to the API's public origin; a deployment
	// that serves the API on its own hostname sets OAUTH_ISSUER (docs/design/mcp-oauth.md §8).
	Cfg.OAuthIssuer = strings.TrimRight(getEnv("OAUTH_ISSUER", Cfg.PublicBaseURL), "/")

	if Cfg.RunnerGatewayURL == "" {
		log.Printf("RUNNER_GATEWAY_URL is not set: a runner can be approved, but pairing cannot finish (the runner is told it has no gateway) and no runner can connect")
	}

	switch strings.ToLower(Cfg.TenancyMode) {
	case "selfhosted":
		Cfg.TenancyMode = TenancySelfHosted
	case "cloud":
		Cfg.TenancyMode = TenancyCloud
	default:
		log.Fatalf("TENANCY_MODE must be SelfHosted or Cloud (got %q)", Cfg.TenancyMode)
	}

	if Cfg.TrialRequiresCard {
		log.Printf("TRIAL_REQUIRES_CARD is set but has no effect until the Paddle integration (B9b) ships")
	}

	if len(Cfg.JWTSigningKey) < 32 {
		log.Fatalf("JWT_SIGNING_KEY must be set to a random value of at least 32 characters (got %d); generate one with: openssl rand -hex 32", len(Cfg.JWTSigningKey))
	}
}

// DBPool is the Go API's connection pool budget, applied by database.ApplyPoolLimits.
type DBPool struct {
	MaxOpenConns    int
	MaxIdleConns    int
	ConnMaxLifetime time.Duration
}

// Pool defaults. They are part of the deployment-wide connection budget (every service's pool
// plus a reserve must fit the server's max_connections), so a change here is a change to that sum.
const (
	DefaultDBMaxOpenConns    = 8
	DefaultDBMaxIdleConns    = 4
	DefaultDBConnMaxLifetime = 30 * time.Minute
)

// parseDBPool reads the pool limits through getenv. An empty value means the default; a value
// that is set but unparsable or not positive is an error naming the variable, because a typo in
// a pool limit must stop startup rather than silently fall back to a number nobody chose. An idle
// limit above the open limit is clamped to the open limit (database/sql would do the same
// silently) and logged, never refused.
func parseDBPool(getenv func(string) string) (DBPool, error) {
	pool := DBPool{ConnMaxLifetime: DefaultDBConnMaxLifetime}
	var err error
	if pool.MaxOpenConns, err = positiveIntEnv(getenv, "DB_MAX_OPEN_CONNS", DefaultDBMaxOpenConns); err != nil {
		return DBPool{}, err
	}
	if pool.MaxIdleConns, err = positiveIntEnv(getenv, "DB_MAX_IDLE_CONNS", DefaultDBMaxIdleConns); err != nil {
		return DBPool{}, err
	}
	if v := strings.TrimSpace(getenv("DB_CONN_MAX_LIFETIME")); v != "" {
		d, perr := time.ParseDuration(v)
		if perr != nil || d <= 0 {
			return DBPool{}, fmt.Errorf("DB_CONN_MAX_LIFETIME must be a positive Go duration such as 30m (got %q)", v)
		}
		pool.ConnMaxLifetime = d
	}
	if pool.MaxIdleConns > pool.MaxOpenConns {
		log.Printf("DB_MAX_IDLE_CONNS (%d) is above DB_MAX_OPEN_CONNS (%d): clamping idle connections to %d",
			pool.MaxIdleConns, pool.MaxOpenConns, pool.MaxOpenConns)
		pool.MaxIdleConns = pool.MaxOpenConns
	}
	return pool, nil
}

func positiveIntEnv(getenv func(string) string, key string, fallback int) (int, error) {
	v := strings.TrimSpace(getenv(key))
	if v == "" {
		return fallback, nil
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return 0, fmt.Errorf("%s must be a positive integer (got %q)", key, v)
	}
	return n, nil
}

func getEnv(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func getBoolEnv(key string, fallback bool) bool {
	if v := os.Getenv(key); v != "" {
		if parsed, err := strconv.ParseBool(v); err == nil {
			return parsed
		}
	}
	return fallback
}
