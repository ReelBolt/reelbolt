package services

import (
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
)

// RFC 7636 Appendix B's published vector, used verbatim so the S256 implementation is checked against
// the specification rather than against itself.
const (
	rfc7636Verifier  = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
	rfc7636Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
)

func TestVerifyPKCES256RFC7636Vector(t *testing.T) {
	if !VerifyPKCES256(rfc7636Challenge, rfc7636Verifier) {
		t.Fatalf("the RFC 7636 Appendix B vector must verify")
	}
}

func TestVerifyPKCES256Table(t *testing.T) {
	tests := []struct {
		name      string
		challenge string
		verifier  string
		want      bool
	}{
		{"rfc vector", rfc7636Challenge, rfc7636Verifier, true},
		{"wrong verifier", rfc7636Challenge, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false},
		{"empty verifier", rfc7636Challenge, "", false},
		{"empty challenge", "", rfc7636Verifier, false},
		// "plain" is not offered at all: a challenge equal to the verifier must never verify.
		{"plain method is refused", rfc7636Verifier, rfc7636Verifier, false},
		{"challenge too short", "abc", rfc7636Verifier, false},
		{"verifier too short", rfc7636Challenge, "abc", false},
		{"challenge not base64url", "!!!!" + rfc7636Challenge[4:], rfc7636Verifier, false},
		{"verifier not base64url", rfc7636Challenge, "!!!!" + rfc7636Verifier[4:], false},
		{"both absent", "", "", false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := VerifyPKCES256(tc.challenge, tc.verifier); got != tc.want {
				t.Fatalf("VerifyPKCES256(%q, %q) = %v, want %v", tc.challenge, tc.verifier, got, tc.want)
			}
		})
	}
}

func TestGrantOAuthScopesTable(t *testing.T) {
	tests := []struct {
		name      string
		requested []string
		admin     bool
		want      []string
	}{
		{name: "no request still gets tools", requested: nil, admin: false, want: []string{OAuthScopeAssistantTools}},
		{name: "tools requested", requested: []string{"assistant.tools"}, want: []string{OAuthScopeAssistantTools}},
		{name: "admin requested and enabled", requested: []string{"assistant.tools assistant.admin"}, admin: true, want: []string{OAuthScopeAssistantTools, OAuthScopeAssistantAdmin}},
		{name: "admin requested but disabled is dropped", requested: []string{"assistant.tools assistant.admin"}, admin: false, want: []string{OAuthScopeAssistantTools}},
		{name: "admin not requested is not granted even when enabled", requested: []string{"assistant.tools"}, admin: true, want: []string{OAuthScopeAssistantTools}},
		{name: "unknown scopes are dropped", requested: []string{"openid email assistant.tools"}, admin: true, want: []string{OAuthScopeAssistantTools}},
		{name: "duplicates collapse", requested: []string{"assistant.tools assistant.tools assistant.admin"}, admin: true, want: []string{OAuthScopeAssistantTools, OAuthScopeAssistantAdmin}},
		{name: "runs.watch alone", requested: []string{"runs.watch"}, want: []string{OAuthScopeRunsWatch}},
		{name: "runs.watch alone ignores the admin opt-in", requested: []string{"runs.watch"}, admin: true, want: []string{OAuthScopeRunsWatch}},
		{name: "runs.watch with unknown scopes", requested: []string{"openid runs.watch"}, want: []string{OAuthScopeRunsWatch}},
		{name: "runs.watch with tools is dropped", requested: []string{"runs.watch assistant.tools"}, want: []string{OAuthScopeAssistantTools}},
		{name: "runs.watch with admin, opt-in on", requested: []string{"runs.watch assistant.admin"}, admin: true, want: []string{OAuthScopeAssistantTools, OAuthScopeAssistantAdmin}},
		{name: "runs.watch with admin, opt-in off", requested: []string{"runs.watch assistant.admin"}, admin: false, want: []string{OAuthScopeAssistantTools}},
		{name: "a lookalike runs scope is not runs.watch", requested: []string{"runs.watch.all"}, want: []string{OAuthScopeAssistantTools}},
		{name: "a lookalike scope is not admin", requested: []string{"assistant.admin.all"}, admin: true, want: []string{OAuthScopeAssistantTools}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := GrantOAuthScopes(tc.requested, tc.admin)
			if len(got) != len(tc.want) {
				t.Fatalf("GrantOAuthScopes(%v, %v) = %v, want %v", tc.requested, tc.admin, got, tc.want)
			}
			for i := range got {
				if got[i] != tc.want[i] {
					t.Fatalf("GrantOAuthScopes(%v, %v) = %v, want %v", tc.requested, tc.admin, got, tc.want)
				}
			}
		})
	}
}

func TestHashOAuthSecretIsDeterministicAndOneWay(t *testing.T) {
	const secret = "a-secret-value"

	first := HashOAuthSecret(secret)
	if first != HashOAuthSecret(secret) {
		t.Fatal("the hash must be deterministic: codes and refresh tokens are looked up by it")
	}
	if len(first) != 64 {
		t.Fatalf("hash length = %d, want 64 hex characters (the column is varchar(64))", len(first))
	}
	if first == secret {
		t.Fatal("the stored value must not be the secret")
	}
	if HashOAuthSecret("another-value") == first {
		t.Fatal("different inputs must hash differently")
	}
}

func TestVerifyOAuthSecretTable(t *testing.T) {
	stored := HashOAuthSecret("correct-secret")

	tests := []struct {
		name      string
		stored    string
		presented string
		want      bool
	}{
		{"correct", stored, "correct-secret", true},
		{"wrong", stored, "wrong-secret", false},
		{"empty presented", stored, "", false},
		{"empty stored", "", "correct-secret", false},
		{"both empty", "", "", false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := VerifyOAuthSecret(tc.stored, tc.presented); got != tc.want {
				t.Fatalf("VerifyOAuthSecret = %v, want %v", got, tc.want)
			}
		})
	}
}

func TestNewOAuthSecretIsLongAndUnique(t *testing.T) {
	seen := make(map[string]bool)
	for i := 0; i < 64; i++ {
		s, err := NewOAuthSecret()
		if err != nil {
			t.Fatalf("NewOAuthSecret: %v", err)
		}
		// 32 random bytes, base64url, unpadded: 43 characters. The length also satisfies RFC 7636's
		// minimum verifier length, which is why the same generator can serve both.
		if len(s) != 43 {
			t.Fatalf("secret length = %d, want 43 (%q)", len(s), s)
		}
		if seen[s] {
			t.Fatalf("NewOAuthSecret repeated a value: %q", s)
		}
		seen[s] = true
	}
}

func TestValidPKCEChallengeLengthBounds(t *testing.T) {
	tests := []struct {
		name      string
		challenge string
		want      bool
	}{
		{"43 characters is the minimum", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true},
		{"128 characters is the maximum", strings.Repeat("a", 128), true},
		{"42 characters is too short", strings.Repeat("a", 42), false},
		{"129 characters is too long", strings.Repeat("a", 129), false},
		{"underscore and hyphen are base64url", "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := ValidPKCEChallenge(tc.challenge); got != tc.want {
				t.Fatalf("ValidPKCEChallenge(len=%d) = %v, want %v", len(tc.challenge), got, tc.want)
			}
		})
	}
}

func TestGenerateOAuthAccessTokenRejectsEmptyScope(t *testing.T) {
	user := &models.ApplicationUser{ID: uuid.New()}
	for _, scope := range []string{"", "   "} {
		if tok, _, err := GenerateOAuthAccessToken(user, nil, scope, "", "", time.Now()); err == nil || tok != "" {
			t.Fatalf("scope %q: want error and no token, got %q, %v", scope, tok, err)
		}
	}
}

func TestSupportedOAuthScopesListsRunsWatchLast(t *testing.T) {
	got := SupportedOAuthScopes()
	want := []string{OAuthScopeAssistantTools, OAuthScopeAssistantAdmin, OAuthScopeRunsWatch}
	if len(got) != len(want) {
		t.Fatalf("SupportedOAuthScopes() = %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("SupportedOAuthScopes() = %v, want %v", got, want)
		}
	}
}
