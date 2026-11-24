package auth

import (
	"encoding/json"
	"time"

	"github.com/vecchiotom/reelbolt/services"
)

type TokenRequest struct {
	Email    string `json:"email"`
	Password string `json:"password"`
}

type TokenResponse struct {
	AccessToken string `json:"accessToken"`
	TokenType   string `json:"tokenType"`
	ExpiresIn   int    `json:"expiresIn"`
	Email       string `json:"email"`
	DisplayName string `json:"displayName"`
	// Locale is the saved language code, omitted when unset.
	Locale             string `json:"locale,omitempty"`
	IsAdmin            bool   `json:"isAdmin"` // alias of isPlatformAdmin, one release
	IsPlatformAdmin    bool   `json:"isPlatformAdmin"`
	MustChangePassword bool   `json:"mustChangePassword"`
	EmailVerified      bool   `json:"emailVerified"`
	Org                string `json:"org"`
	OrgName            string `json:"orgName"`
	OrgKind            string `json:"orgKind"`
	OrgRole            string `json:"orgRole"`
	// TermsVersion is the accepted Terms version and is omitted when there is
	// none, exactly like the claim and the readable cookie (C10).
	TermsVersion string `json:"termsVersion,omitempty"`
	// PrivacyVersion is the accepted Privacy Policy version, omitted when there
	// is none, like TermsVersion.
	PrivacyVersion string `json:"privacyVersion,omitempty"`
	// DeletionCancelled is true when this sign-in cancelled a pending account deletion (C9b: signing
	// in during the grace period cancels it), so the client can say so. Omitted otherwise.
	DeletionCancelled bool `json:"deletionCancelled,omitempty"`
}

type SignupRequest struct {
	Email       string `json:"email"`
	Password    string `json:"password"`
	DisplayName string `json:"displayName"`
	// Locale is the optional language chosen on the signup form (allowlisted; else the
	// rb_locale cookie / Accept-Language decide).
	Locale         string `json:"locale,omitempty"`
	AcceptTerms    bool   `json:"acceptTerms"`
	TermsVersion   string `json:"termsVersion"`
	PrivacyVersion string `json:"privacyVersion"`
	// TurnstileToken is the Cloudflare Turnstile response, required only when
	// the server has TURNSTILE_SECRET set.
	TurnstileToken string `json:"turnstileToken,omitempty"`
	// InviteToken is a team invite token. When valid for this email the account
	// is created already verified and joins the organization (C8).
	InviteToken string `json:"inviteToken,omitempty"`
	// MarketingOptIn is the optional, unticked-by-default marketing checkbox,
	// separate from AcceptTerms. MarketingWordingVersion names the registry
	// version of the text the form showed; it must be the current one when
	// MarketingOptIn is true (400 invalid_marketing_wording otherwise).
	MarketingOptIn          bool   `json:"marketingOptIn,omitempty"`
	MarketingWordingVersion string `json:"marketingWordingVersion,omitempty"`
	// Persona is the audience token the landing page put in the signup link (`?for=creator`), one
	// of the closed set in services.NormalizePersona. Optional. It is stored as a suggestion on a
	// NEW account only; anything else is dropped (docs/design/new-user-funnel.md section 6).
	Persona marketingString `json:"persona,omitempty"`
	// Plan is the plan key the visitor picked before signing up (services.NormalizePlanKey).
	// Optional, lenient like Persona, stored on a NEW account only; anything else is dropped.
	Plan marketingString `json:"plan,omitempty"`
	// Attribution is the campaign parameters of the signup link. Optional, sanitised like the rb_ft
	// cookie, and recorded on a NEW account only, only when there is no cookie row to write and
	// only when ATTRIBUTION_URL_CAPTURE_ENABLED is on.
	Attribution SignupAttribution `json:"attribution"`
}

// marketingString is a string field of marketing data in a request body. Signup must never fail on
// marketing data, so a value of the wrong JSON type (a number, an object, null) decodes to "" and
// is dropped later like any other invalid value, instead of failing the whole body with a 400.
type marketingString string

func (s *marketingString) UnmarshalJSON(b []byte) error {
	var v string
	if json.Unmarshal(b, &v) == nil {
		*s = marketingString(v)
	}
	return nil
}

// SignupAttribution is SignupRequest.Attribution: the utm_* values and the landing path the signup
// page read from its own URL. Every field is optional and untrusted.
type SignupAttribution struct {
	Source      marketingString `json:"source,omitempty"`
	Medium      marketingString `json:"medium,omitempty"`
	Campaign    marketingString `json:"campaign,omitempty"`
	Content     marketingString `json:"content,omitempty"`
	Term        marketingString `json:"term,omitempty"`
	LandingPath marketingString `json:"landingPath,omitempty"`
}

// UnmarshalJSON accepts anything: an `attribution` that is not an object at all is ignored, for the
// reason marketingString gives.
func (a *SignupAttribution) UnmarshalJSON(b []byte) error {
	type fields SignupAttribution
	var f fields
	if json.Unmarshal(b, &f) == nil {
		*a = SignupAttribution(f)
	}
	return nil
}

// carried is the raw values in the shape the attribution service sanitises.
func (a SignupAttribution) carried() services.URLAttribution {
	return services.URLAttribution{
		Source: string(a.Source), Medium: string(a.Medium), Campaign: string(a.Campaign),
		Content: string(a.Content), Term: string(a.Term), LandingPath: string(a.LandingPath),
	}
}

// InvitationPreviewResponse is GET /api/v1/auth/invitations/{token}.
type InvitationPreviewResponse struct {
	OrganizationName string    `json:"organizationName"`
	InviterName      string    `json:"inviterName"`
	Role             string    `json:"role"`
	Email            string    `json:"email"`
	ExpiresAt        time.Time `json:"expiresAt"`
	// AccountExists is true when the invited address already has an account, so
	// the page should offer sign-in rather than signup.
	AccountExists bool `json:"accountExists"`
}

// AcceptTermsRequest is POST /api/v1/auth/accept-terms: the re-acceptance an
// already-signed-in user posts from /app/accept-terms. The field names and
// validation match the signup form's, since both write the same rows.
type AcceptTermsRequest struct {
	AcceptTerms    bool   `json:"acceptTerms"`
	TermsVersion   string `json:"termsVersion"`
	PrivacyVersion string `json:"privacyVersion"`
	// PrivacyAcknowledgement is the privacy-only Continue (S-23): the user already
	// accepted the served Terms and acknowledges a new Privacy Policy. It records
	// the Privacy row only, never a Terms acceptance, and is refused (409) unless
	// the user's latest accepted Terms is the served one. PrivacyVersion must name
	// the served Privacy version; AcceptTerms and TermsVersion are ignored.
	PrivacyAcknowledgement bool `json:"privacyAcknowledgement,omitempty"`
}

type VerifyEmailRequest struct {
	Token string `json:"token"`
}

type ResendVerificationRequest struct {
	Email          string `json:"email"`
	TurnstileToken string `json:"turnstileToken,omitempty"`
}

type ResetPasswordRequest struct {
	Token       string `json:"token"`
	NewPassword string `json:"newPassword"`
}

// AcceptedResponse is the uniform body of every enumeration-safe 202.
type AcceptedResponse struct {
	Message string `json:"message"`
}

type SwitchOrgRequest struct {
	OrganizationID string `json:"organizationId"`
}

// DeleteAccountRequest is POST /api/v1/auth/account/delete (C9b). The password is re-entered on
// purpose: scheduling a deletion is destructive and a stolen session must not be enough. An account
// that signs in with a provider has no password to re-enter, so an empty password is answered with a
// 409 DeletionConfirmationRequired and the round trip that takes its place (C11 F-8).
type DeleteAccountRequest struct {
	Password string `json:"password"`
}

// DeletionConfirmationRequired is the 409 body of POST /api/v1/auth/account/delete when the caller
// has no password to confirm with. Nothing has been deleted at this point: the caller completes the
// provider round trip at confirmUrl — which re-authenticates the identity the account was created
// from, the same control a password proves — and the deletion is scheduled when that round trip
// finishes. error is "provider_confirmation_required" with provider and confirmUrl, or
// "confirmation_unavailable" with only a message, when the account's provider is not configured on
// this deployment and a password has to be set first.
type DeletionConfirmationRequired struct {
	Error      string `json:"error"`
	Provider   string `json:"provider,omitempty"`
	ConfirmURL string `json:"confirmUrl,omitempty"`
	Message    string `json:"message"`
}

type ChangePasswordRequest struct {
	CurrentPassword string `json:"currentPassword"`
	NewPassword     string `json:"newPassword"`
}
