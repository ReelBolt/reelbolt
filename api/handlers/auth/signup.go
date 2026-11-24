package auth

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"github.com/vecchiotom/reelbolt/telemetry"
)

// recordFunnelEvent emits one alpha funnel event (alpha-launch T-018). A package var, like the
// replaceable collaborators on accountDeps, so a test can observe the emission without a collector.
var recordFunnelEvent = telemetry.RecordFunnelEvent

// recordSignupProductEvent writes the alpha.signup product_events row (initiative
// product-events-funnel) beside the OTLP counter above, after RegisterUser's transaction has
// committed. Best-effort: a lost row is a lost metric, a broken signup is a broken product. A
// package var, like recordFunnelEvent, so a test can observe the emission.
//
// personalOrgID is the Personal workspace the same transaction created (provisioning stamps it on
// the user as LastActiveOrganizationID), so the signup row names the organization every later step
// of that person's funnel happens in. Nil when it is not known, and the row is then written with
// the user alone, as it was before.
var recordSignupProductEvent = func(userID uuid.UUID, personalOrgID *uuid.UUID) {
	services.RecordProductEventBestEffort(database.DB, userID, personalOrgID, services.ProductEventSignup)
}

// recordLoginProductEvent writes the auth.login product_events row after a successful sign-in;
// detail is the closed method token (password|google|github). Best-effort, a package var so a
// test can observe it. No email or user id in the detail.
var recordLoginProductEvent = func(userID uuid.UUID, detail string) {
	services.RecordProductEventDetailBestEffort(database.DB, userID, nil, services.ProductEventLogin, detail)
}

// clearFirstTouchCookie expires rb_ft whenever the request carried it. Every 202 branch of signup
// calls it (new, existing, race-loser) so the responses stay header-identical.
func clearFirstTouchCookie(w http.ResponseWriter, r *http.Request) {
	if _, err := r.Cookie(services.FirstTouchCookieName); err != nil {
		return
	}
	http.SetCookie(w, &http.Cookie{
		Name:     services.FirstTouchCookieName,
		Value:    "",
		Path:     "/",
		MaxAge:   -1,
		Secure:   config.Cfg.CookieSecure,
		SameSite: http.SameSiteLaxMode,
	})
}

// recordSignupAttribution stores the first touch of a just-created user. Two sources, in order
// (docs/design/new-user-funnel.md section 6):
//
//  1. The consented rb_ft cookie. When the request carries one it is cleared, and what it parses to
//     is the row (capture = 'cookie').
//  2. Only when the cookie gave no row to write (absent, or nothing valid in it) and only when
//     ATTRIBUTION_URL_CAPTURE_ENABLED is on: the campaign parameters the request itself carried
//     (capture = 'url', first touch = now). Off by default, and then nothing is written.
//
// The insert runs on the async path so response timing does not depend on it, and it sets no header
// of its own beyond clearing a cookie the request sent, so the uniform 202 stays uniform. Best-effort
// and a package var so a test can observe or break it.
var recordSignupAttribution = func(w http.ResponseWriter, r *http.Request, userID uuid.UUID, carried services.URLAttribution) {
	var ft *services.FirstTouch
	if c, err := r.Cookie(services.FirstTouchCookieName); err == nil {
		ft = services.ParseFirstTouchCookie(c.Value)
		clearFirstTouchCookie(w, r)
	}
	if ft == nil && config.Cfg.AttributionURLCaptureEnabled {
		ft = carried.FirstTouch(acct.now())
	}
	if ft == nil {
		return
	}
	db := database.DB
	acct.runAsync(func() { services.RecordAttributionBestEffort(db, userID, ft) })
}

// recordSignupPersona stores the persona the landing page suggested for a just-created user
// (user_onboarding_profiles.suggested_persona). A value outside the closed set is dropped, never a
// refusal. Best-effort and a package var, like the two emitters above.
var recordSignupPersona = func(userID uuid.UUID, persona, plan string) {
	services.RecordSignupProfileBestEffort(database.DB, userID, persona, plan)
}

// signupMarketing is the marketing data an account-creating request carried: the persona of the
// landing page and the campaign parameters of the link. Both are raw here; the two recorders above
// sanitise. It exists only for a NEW account: no branch that finds an existing one reads it.
type signupMarketing struct {
	persona string
	plan    string
	carried services.URLAttribution
}

func (req SignupRequest) marketing() signupMarketing {
	return signupMarketing{persona: string(req.Persona), plan: string(req.Plan), carried: req.Attribution.carried()}
}

// acceptedBody is the one body every enumeration-safe endpoint answers with,
// whether or not the address belongs to an account.
const acceptedMessage = "If this address can be used, an email is on its way."

// accountDeps are the collaborators of the self-serve account handlers
// (signup, verification, password reset), replaceable in tests.
type accountDeps struct {
	store services.AccountStore
	// orgs is the organizations store, used by the team-invite flows (C8).
	orgs              services.OrgStore
	sendMail          func(to string, tmpl services.TemplateName, data services.MailData) error
	resolveMembership func(ctx context.Context, user *models.ApplicationUser) (*services.ActiveMembership, error)
	// runAsync detaches mail delivery from the response so SMTP latency does
	// not reveal whether the address exists. Tests run it inline.
	runAsync func(func())
	now      func() time.Time
	// verifyChallenge checks the Turnstile token; passes when no secret is set.
	verifyChallenge func(ctx context.Context, token, ip string) (bool, error)
	// checkMX is consulted only when ABUSE_MX_CHECK is on.
	checkMX func(ctx context.Context, email string) error
}

var acct = accountDeps{
	store: services.GormAccountStore{},
	orgs:  services.GormOrgStore{},
	sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
		return services.DefaultMailer().SendTemplate(to, tmpl, data)
	},
	resolveMembership: services.ResolveActiveMembership,
	runAsync:          func(f func()) { go f() },
	now:               time.Now,
	verifyChallenge: func(ctx context.Context, token, ip string) (bool, error) {
		return services.VerifyTurnstile(ctx, config.Cfg.TurnstileSecret, token, ip)
	},
	checkMX: services.CheckMX,
}

// challengePassed runs the optional Turnstile check, writing the 400 itself on
// failure. The same answer for "missing", "wrong" and "Cloudflare unreachable"
// reveals nothing about accounts.
func (d accountDeps) challengePassed(w http.ResponseWriter, r *http.Request, token string) bool {
	ip := ""
	if p := clientIP(r); p != nil {
		ip = *p
	}
	ok, err := d.verifyChallenge(r.Context(), token, ip)
	if err != nil {
		log.Printf("turnstile verification: %v", err)
	}
	if !ok {
		writeJSONError(w, http.StatusBadRequest, "challenge verification failed")
	}
	return ok
}

// emailTokenAllowed enforces the per-account cap on emailed tokens (3/hour),
// counted from user_tokens so it holds across replicas. A refused send is
// silent to the caller: the response stays the uniform 202.
func (d accountDeps) emailTokenAllowed(userID uuid.UUID) bool {
	if middleware.NewEmailTokenLimiter(d.store).Allow(userID, d.now().UTC()) {
		return true
	}
	log.Printf("email token limit reached for %s; mail not sent", userID)
	return false
}

func writeAccepted(w http.ResponseWriter) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusAccepted)
	_ = json.NewEncoder(w).Encode(AcceptedResponse{Message: acceptedMessage})
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeJSONError(w http.ResponseWriter, status int, msg string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]string{"error": msg})
}

func normalizeEmail(e string) string { return strings.ToLower(strings.TrimSpace(e)) }

// clientIP prefers the address nginx forwards; it is stored for abuse review
// only and never used for authorization.
func clientIP(r *http.Request) *string {
	ip := strings.TrimSpace(r.Header.Get("X-Real-IP"))
	if ip == "" {
		if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
			ip = strings.TrimSpace(strings.Split(xff, ",")[0])
		}
	}
	if ip == "" {
		ip, _, _ = net.SplitHostPort(r.RemoteAddr)
	}
	if net.ParseIP(ip) == nil {
		return nil
	}
	return &ip
}

func (d accountDeps) link(path, token string) string {
	return config.Cfg.LinkBaseURL + path + "?token=" + url.QueryEscape(token)
}

// mailVerification issues a fresh VerifyEmail token (superseding earlier ones)
// and sends the link. Errors are logged, never surfaced: the response must not
// depend on delivery.
func (d accountDeps) mailVerification(user *models.ApplicationUser, reqLocale string) {
	if !d.emailTokenAllowed(user.ID) {
		return
	}
	d.issueVerification(user, reqLocale)
}

// issueVerification is mailVerification without the per-user limiter. Only a server-initiated
// mail that happens once per already rate-limited event may call it directly (the verify link to
// the new address after an email-change confirm); user-driven paths go through mailVerification.
// The token is bound to user.Email.
func (d accountDeps) issueVerification(user *models.ApplicationUser, reqLocale string) {
	tok, raw, err := services.NewToken(user.ID, user.Email, models.UserTokenVerifyEmail, services.VerifyEmailTTL, d.now().UTC())
	if err == nil {
		err = d.store.IssueToken(tok)
	}
	if err != nil {
		log.Printf("issue verification token for %s: %v", user.ID, err)
		return
	}
	// S-32: a resent or duplicate-signup verify mail confirms a pending opt-in just like the first
	// one, so it says so too. A failed lookup sends the plain mail rather than none.
	pending, err := d.store.MarketingOptInPending(user.ID, user.Email)
	if err != nil {
		log.Printf("marketing opt-in lookup for %s: %v", user.ID, err)
		pending = false
	}
	d.sendVerificationMail(user, raw, pending, reqLocale)
}

// sendVerificationMail mails the verify link. marketingOptIn is set for the mail of a signup that
// ticked the marketing box, which verifying also confirms, so the mail says so (S-32).
func (d accountDeps) sendVerificationMail(user *models.ApplicationUser, raw string, marketingOptIn bool, reqLocale string) {
	if err := d.sendMail(user.Email, services.TemplateVerifyEmail, services.MailData{
		Name: user.DisplayName, Link: d.link("/app/verify-email", raw), ExpiresIn: "24 hours",
		MarketingOptIn: marketingOptIn, Locale: mailLocale(user, reqLocale),
	}); err != nil {
		log.Printf("send verification mail to %s: %s", user.ID, services.SanitizeMailError(err))
	}
}

// mailReset issues a ResetPassword token and sends the link.
func (d accountDeps) mailReset(user *models.ApplicationUser, reqLocale string) {
	if !d.emailTokenAllowed(user.ID) {
		return
	}
	tok, raw, err := services.NewToken(user.ID, user.Email, models.UserTokenResetPassword, services.ResetPasswordTTL, d.now().UTC())
	if err == nil {
		err = d.store.IssueToken(tok)
	}
	if err != nil {
		log.Printf("issue reset token for %s: %v", user.ID, err)
		return
	}
	if err := d.sendMail(user.Email, services.TemplateResetPassword, services.MailData{
		Name: user.DisplayName, Link: d.link("/app/reset-password", raw), ExpiresIn: "1 hour", Locale: mailLocale(user, reqLocale),
	}); err != nil {
		log.Printf("send reset mail to %s: %s", user.ID, services.SanitizeMailError(err))
	}
}

// handleSignup creates an unverified account with a Personal workspace.
// It answers 404 unless SIGNUP_ENABLED, and an identical 202 whether or not
// the email already exists (an existing address gets a mail instead).
func handleSignup(w http.ResponseWriter, r *http.Request) {
	if !config.Cfg.SignupEnabled {
		http.NotFound(w, r)
		return
	}
	var req SignupRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16<<10)).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "invalid request body")
		return
	}

	email := normalizeEmail(req.Email)
	if _, err := services.ValidateRecipient(email); err != nil {
		writeJSONError(w, http.StatusBadRequest, "a valid email address is required")
		return
	}
	if err := services.ValidateNewPassword(req.Password); err != nil {
		writeJSONError(w, http.StatusBadRequest, err.Error())
		return
	}
	if !req.AcceptTerms {
		writeJSONError(w, http.StatusBadRequest, "you must accept the terms and privacy policy")
		return
	}
	// F-7: the acceptance is pinned to the version THIS deployment serves, read
	// from legal_documents, and the submission has to name that same version.
	// Anything else — a stale page, a forged string, a version that was never
	// served — is refused and writes no account. The check runs before the
	// address is looked up, so a refusal still reveals nothing about accounts.
	current, ok := servedLegalVersions(w)
	if !ok {
		return
	}
	if !matchesServedVersions(req.TermsVersion, req.PrivacyVersion, current) {
		writeLegalVersionMismatch(w, current)
		return
	}
	// The marketing opt-in records the exact wording the form showed, so an
	// opt-in naming any version but the current one is refused, again before
	// the address is looked up. An unticked box ignores the field.
	if req.MarketingOptIn && strings.TrimSpace(req.MarketingWordingVersion) != services.MarketingWordingVersion {
		writeJSONError(w, http.StatusBadRequest, "invalid_marketing_wording")
		return
	}
	displayName := strings.TrimSpace(req.DisplayName)
	if displayName == "" {
		displayName = fallbackDisplayName(strings.SplitN(email, "@", 2)[0])
	} else if displayName, ok = validateDisplayName(displayName); !ok {
		writeJSONError(w, http.StatusBadRequest, "invalid display name")
		return
	}

	if !acct.challengePassed(w, r, req.TurnstileToken) {
		return
	}
	// Per-IP cap (3/hour, 10/day) from application_users.signup_ip. Answered
	// 429 because it says nothing about any email address.
	if ipp := clientIP(r); ipp != nil {
		if !middleware.NewSignupIPLimiter(acct.store).Allow(*ipp, acct.now().UTC()) {
			w.Header().Set("Retry-After", "3600")
			writeJSONError(w, http.StatusTooManyRequests, "too many sign-ups from this network; try again later")
			return
		}
	}
	// A valid team invite proves the address, so it takes its own path (C8).
	if strings.TrimSpace(req.InviteToken) != "" {
		signupViaInvite(w, r, req, email, displayName, current)
		return
	}
	if config.Cfg.AbuseBlockDisposable && services.IsDisposableEmail(email) {
		writeJSONError(w, http.StatusBadRequest, "please use a permanent email address")
		return
	}
	if config.Cfg.AbuseMXCheck {
		if err := acct.checkMX(r.Context(), email); err != nil {
			writeJSONError(w, http.StatusBadRequest, "that email domain cannot receive mail")
			return
		}
	}

	// Hash before the existence check so both branches cost one bcrypt.
	hash, err := services.HashPassword(req.Password)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "failed to create account")
		return
	}

	existing, err := acct.store.FindUserByEmail(email)
	if err != nil {
		log.Printf("signup lookup: %v", err)
		writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
		return
	}
	if existing != nil {
		// Never reveal the account; mail its owner instead.
		reqLoc := services.LocaleFromRequest(r, req.Locale)
		acct.runAsync(func() {
			if existing.EmailVerifiedAt == nil {
				acct.mailVerification(existing, reqLoc)
			} else {
				acct.mailReset(existing, reqLoc)
			}
		})
		clearFirstTouchCookie(w, r)
		writeAccepted(w)
		return
	}

	now := acct.now().UTC()
	ip := clientIP(r)
	user := &models.ApplicationUser{
		ID: uuid.New(), Email: email, DisplayName: displayName, CreatedAt: now,
		PasswordHash: hash, SignupIP: ip, AuthProvider: "Password",
		Locale: requestLocalePtr(r, req.Locale),
	}
	acceptances := legalAcceptances(user.ID, now, ip, current)
	tok, raw, err := services.NewToken(user.ID, user.Email, models.UserTokenVerifyEmail, services.VerifyEmailTTL, now)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "failed to create account")
		return
	}
	optIn := marketingOptIn(r, req, now, ip)
	if err := acct.store.RegisterUser(user, acceptances, tok, optIn); err != nil {
		// A concurrent signup for the same address loses the unique index
		// race; answer like every other case.
		log.Printf("signup register: %v", err)
		if again, ferr := acct.store.FindUserByEmail(email); ferr == nil && again != nil {
			clearFirstTouchCookie(w, r)
			writeAccepted(w)
			return
		}
		writeJSONError(w, http.StatusInternalServerError, "failed to create account")
		return
	}
	// alpha-launch T-018: the account row exists, and this is the only place that knows a signup
	// really happened — the 202 above is uniform whether or not the address already had an account,
	// so the browser cannot tell and must not be made able to. Reporting only; the 202 is unchanged.
	recordFunnelEvent(r.Context(), telemetry.FunnelEventSignup)
	recordSignupProductEvent(user.ID, user.LastActiveOrganizationID)
	// The persona and the campaign parameters of the signup link, for this new account only. The
	// existing-address and race-loser branches above never reach here, so they write neither. The
	// persona insert is detached like the attribution insert: the answer below must not wait on it.
	mk := req.marketing()
	_, personaOK := services.NormalizePersona(mk.persona)
	_, planOK := services.NormalizePlanKey(mk.plan)
	if personaOK || planOK {
		acct.runAsync(func() { recordSignupPersona(user.ID, mk.persona, mk.plan) })
	}
	recordSignupAttribution(w, r, user.ID, mk.carried)
	reqLoc := services.LocaleFromRequest(r, req.Locale)
	acct.runAsync(func() { acct.sendVerificationMail(user, raw, optIn != nil, reqLoc) })
	writeAccepted(w)
}

// marketingOptIn is the opt-in a signup carries, or nil when the box was not
// ticked. The wording version was already checked by handleSignup.
func marketingOptIn(r *http.Request, req SignupRequest, now time.Time, ip *string) *services.MarketingOptIn {
	if !req.MarketingOptIn {
		return nil
	}
	return &services.MarketingOptIn{
		WordingVersion: strings.TrimSpace(req.MarketingWordingVersion),
		IP:             ip,
		UserAgent:      r.UserAgent(),
		At:             now,
	}
}

// handleVerifyEmail consumes a verification token and, on success, logs the
// user in exactly like /auth/token does. Verifying the address also confirms a
// signup marketing opt-in (inside ConsumeVerifyEmail's transaction).
func handleVerifyEmail(w http.ResponseWriter, r *http.Request) {
	var req VerifyEmailRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil || strings.TrimSpace(req.Token) == "" {
		writeJSONError(w, http.StatusBadRequest, "invalid or expired token")
		return
	}
	user, err := acct.store.ConsumeVerifyEmail(services.HashToken(strings.TrimSpace(req.Token)), acct.now().UTC())
	if err != nil {
		if !errors.Is(err, services.ErrInvalidToken) {
			log.Printf("verify email: %v", err)
		}
		writeJSONError(w, http.StatusBadRequest, "invalid or expired token")
		return
	}

	m, err := acct.resolveMembership(r.Context(), user)
	if err != nil {
		log.Printf("resolve active organization for %s: %v", user.ID, err)
		writeJSONError(w, http.StatusForbidden, "no workspace available for this account")
		return
	}

	acct.runAsync(func() {
		if err := acct.sendMail(user.Email, services.TemplateWelcome, services.MailData{
			Name: user.DisplayName, Link: config.Cfg.LinkBaseURL + "/app/login", Locale: mailLocale(user, ""),
		}); err != nil {
			log.Printf("send welcome mail to %s: %s", user.ID, services.SanitizeMailError(err))
		}
	})
	writeSession(w, user, m)
}

// handleResendVerification re-sends the link to an unverified account; always 202.
func handleResendVerification(w http.ResponseWriter, r *http.Request) {
	if !config.Cfg.SignupEnabled {
		http.NotFound(w, r)
		return
	}
	var req ResendVerificationRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "invalid request body")
		return
	}
	if !acct.challengePassed(w, r, req.TurnstileToken) {
		return
	}
	email := normalizeEmail(req.Email)
	if user, err := acct.store.FindUserByEmail(email); err != nil {
		log.Printf("resend verification lookup: %v", err)
	} else if user != nil && !models.IsServiceAccount(user) && user.EmailVerifiedAt == nil {
		reqLoc := services.LocaleFromRequest(r)
		acct.runAsync(func() { acct.mailVerification(user, reqLoc) })
	}
	writeAccepted(w)
}

// requestLocalePtr is the locale captured at account creation: the explicit body field, the
// rb_locale cookie or Accept-Language. It is an allowlisted code, or nil when the request carried
// no language signal (the account then has no saved preference and falls back at read time).
func requestLocalePtr(r *http.Request, explicit ...string) *string {
	loc, ok := services.LocaleSignalFromRequest(r, explicit...)
	if !ok {
		return nil
	}
	return &loc
}

// mailLocale is the language of a mail to user: the saved locale, else the request locale.
func mailLocale(user *models.ApplicationUser, reqLocale string) string {
	if loc := services.UserLocale(user.Locale); loc != "" {
		return loc
	}
	if loc, ok := services.NormalizeLocale(reqLocale); ok {
		return loc
	}
	return services.DefaultLocale
}
