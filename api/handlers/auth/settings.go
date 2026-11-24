package auth

import (
	"context"
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// The signed-in user's own settings (docs/design/mcp-oauth.md §11).
//
// The operator's requirement was "add the ability to enable admin tools in the Web portal UI under
// user settings". The setting itself is `application_users.mcp_admin_tools_enabled` — the column the
// MCP bridge reads live, per call, and the column the OAuth consent flow reads at consent time. The
// Go API owns user management, so the read/write surface lives here.
//
// SELF ONLY, and that is a design choice rather than an omission: the user id comes from the verified
// session and there is no {userId} in the path. A path parameter would create an administrative
// surface nobody asked for, and one that would need its own authority rule (who may turn another
// account's admin tools on?). With no parameter there is no such question to get wrong.

// SettingsResponse is the body of both endpoints: the stored value of the one setting, camelCase like
// every other Go response.
type SettingsResponse struct {
	McpAdminToolsEnabled bool   `json:"mcpAdminToolsEnabled"`
	DisplayName          string `json:"displayName"`
	// McpMediaEgressEnabled: connected tools (MCP) may allow sending the user's media for a run. Off by default.
	McpMediaEgressEnabled bool `json:"mcpMediaEgressEnabled"`
	// Locale is the saved language code, or null when none is saved (the default applies).
	Locale *string `json:"locale"`
	// PendingEmailChange is the open admin-requested email change, or null (issue #1207).
	PendingEmailChange *PendingEmailChangeResponse `json:"pendingEmailChange"`
}

// SettingsUpdateRequest is the PATCH body. The pointer is load-bearing: a partial update must be able
// to tell "set it to false" from "say nothing about it", and a plain bool cannot — an absent field
// would decode to false and silently disable the setting on any request that mentioned only the
// other field (or none).
type SettingsUpdateRequest struct {
	McpAdminToolsEnabled *bool `json:"mcpAdminToolsEnabled"`
	// McpMediaEgressEnabled is optional like the others (a pointer: absent is "say nothing", false is "turn it off").
	McpMediaEgressEnabled *bool `json:"mcpMediaEgressEnabled"`
	// DisplayName is optional like the other field. When present it is validated by
	// validateDisplayName and the session is re-issued so the new name shows without a re-login.
	DisplayName *string `json:"displayName"`
	// Locale is optional too. A supported code (case/region tolerant, "it-IT" -> "it") is saved;
	// "" or null clears it. Anything else is a 400. JSON null and an absent field both decode
	// to nil, so clearing is done with the empty string; localeSet records that the key
	// was present.
	Locale    *string `json:"locale"`
	localeSet bool
}

// UnmarshalJSON notes whether the locale key was present, so an explicit null can clear it.
func (r *SettingsUpdateRequest) UnmarshalJSON(b []byte) error {
	type plain SettingsUpdateRequest
	var p plain
	if err := json.Unmarshal(b, &p); err != nil {
		return err
	}
	var keys map[string]json.RawMessage
	if err := json.Unmarshal(b, &keys); err != nil {
		return err
	}
	*r = SettingsUpdateRequest(p)
	_, r.localeSet = keys["locale"]
	return nil
}

const displayNameMaxRunes = 100

// validateDisplayName trims and checks a display name: 1..100 runes, and no control, format
// (zero-width, bidi override/isolate) or other non-printable characters, so a name cannot hide text
// or reorder the surrounding UI. Plain spaces are allowed; tabs and newlines are not.
func validateDisplayName(raw string) (string, bool) {
	name := strings.TrimSpace(raw)
	n := utf8.RuneCountInString(name)
	if n < 1 || n > displayNameMaxRunes || !utf8.ValidString(name) {
		return "", false
	}
	for _, r := range name {
		if r == ' ' {
			continue
		}
		if unicode.IsControl(r) || unicode.Is(unicode.Cf, r) || unicode.Is(unicode.Zl, r) ||
			unicode.Is(unicode.Zp, r) || !unicode.IsPrint(r) {
			return "", false
		}
	}
	return name, true
}

// settingsDeps are the collaborators, replaceable in tests.
type settingsDeps struct {
	getUser           func(uuid.UUID) (*models.ApplicationUser, error)
	setMcpAdminTools  func(uuid.UUID, bool) error
	setMcpMediaEgress func(uuid.UUID, bool) error
	setDisplayName    func(uuid.UUID, string) error
	setLocale         func(uuid.UUID, *string) error
	resolveMember     func(context.Context, *models.ApplicationUser) (*services.ActiveMembership, error)
	// pendingEmailChange reads the open email change; nil means none is ever reported.
	pendingEmailChange func(uuid.UUID, time.Time) (*services.PendingEmailChange, error)
}

var settings = settingsDeps{
	getUser:           services.GetUserByID,
	setMcpAdminTools:  services.SetMcpAdminToolsEnabled,
	setMcpMediaEgress: services.SetMcpMediaEgressEnabled,
	setDisplayName:    services.SetDisplayName,
	setLocale:         services.SetLocale,
	resolveMember:     services.ResolveActiveMembership,

	pendingEmailChange: services.GetPendingEmailChange,
}

// handleGetSettings answers GET /api/v1/auth/settings with the caller's own stored value.
func handleGetSettings(w http.ResponseWriter, r *http.Request) {
	userID, ok := settingsUserID(w, r)
	if !ok {
		return
	}

	user, err := settings.getUser(userID)
	if err != nil {
		log.Printf("settings: load user %s: %v", userID, err)
		http.Error(w, `{"error":"could not read settings"}`, http.StatusInternalServerError)
		return
	}
	if user == nil {
		// The session is valid but its account row is gone, so there is nothing to read. 404 rather
		// than 500: this is an absent resource, not a broken server.
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}

	writeSettings(w, user)
}

// handlePatchSettings answers PATCH /api/v1/auth/settings. It writes only the field that was actually
// presented and answers with the value read back FROM THE DATABASE, so the UI is never told a value
// the server did not store.
//
// A displayName change re-issues the session cookies (reelbolt_token and reelbolt_user, claims
// unchanged) so the new name shows without a re-login; an mcpAdminToolsEnabled-only change sets no
// cookies. Neither change invalidates tokens or touches `tokens_invalidated_at`.
//
// For the MCP setting this is deliberate: it does NOT re-issue the session.
// That is the contract's point (§11): `assistant.admin` is granted at consent time and is an audit
// record of what the user agreed to, while the bridge reads this column live on every call. Turning
// the setting off therefore takes effect on the very next MCP call without logging anybody out, and
// turning it on does not retroactively widen a token that was issued without that scope.
func handlePatchSettings(w http.ResponseWriter, r *http.Request) {
	userID, ok := settingsUserID(w, r)
	if !ok {
		return
	}

	var req SettingsUpdateRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}

	var newName string
	if req.DisplayName != nil {
		var valid bool
		if newName, valid = validateDisplayName(*req.DisplayName); !valid {
			http.Error(w, `{"error":"display name must be 1 to 100 characters with no control or invisible characters"}`, http.StatusBadRequest)
			return
		}
	}

	var newLocale *string
	if req.localeSet && req.Locale != nil && strings.TrimSpace(*req.Locale) != "" {
		loc, ok := services.NormalizeLocale(*req.Locale)
		if !ok {
			http.Error(w, `{"error":"locale is not supported"}`, http.StatusBadRequest)
			return
		}
		newLocale = &loc
	}

	user, err := settings.getUser(userID)
	if err != nil {
		log.Printf("settings: load user %s: %v", userID, err)
		http.Error(w, `{"error":"could not read settings"}`, http.StatusInternalServerError)
		return
	}
	if user == nil {
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}

	if req.McpAdminToolsEnabled == nil && req.McpMediaEgressEnabled == nil && req.DisplayName == nil && !req.localeSet {
		// Nothing presented: a partial update that names no field is a no-op, and the honest answer is
		// the value already stored rather than an error the UI would have to interpret.
		writeSettings(w, user)
		return
	}

	if req.McpAdminToolsEnabled != nil {
		if err := settings.setMcpAdminTools(userID, *req.McpAdminToolsEnabled); err != nil {
			log.Printf("settings: set mcp_admin_tools_enabled for %s: %v", userID, err)
			http.Error(w, `{"error":"could not save settings"}`, http.StatusInternalServerError)
			return
		}
	}
	if req.McpMediaEgressEnabled != nil {
		if settings.setMcpMediaEgress == nil {
			http.Error(w, `{"error":"could not save settings"}`, http.StatusInternalServerError)
			return
		}
		if err := settings.setMcpMediaEgress(userID, *req.McpMediaEgressEnabled); err != nil {
			log.Printf("settings: set mcp_media_egress_enabled for %s: %v", userID, err)
			http.Error(w, `{"error":"could not save settings"}`, http.StatusInternalServerError)
			return
		}
	}
	if req.DisplayName != nil {
		if err := settings.setDisplayName(userID, newName); err != nil {
			log.Printf("settings: set display_name for %s: %v", userID, err)
			http.Error(w, `{"error":"could not save settings"}`, http.StatusInternalServerError)
			return
		}
	}

	if req.localeSet {
		if err := settings.setLocale(userID, newLocale); err != nil {
			log.Printf("settings: set locale for %s: %v", userID, err)
			http.Error(w, `{"error":"could not save settings"}`, http.StatusInternalServerError)
			return
		}
	}

	// Read it back rather than echoing the request: this answers "what is stored", it cannot report a
	// write that silently matched no row, and it is what the UI renders.
	stored, err := settings.getUser(userID)
	if err != nil {
		log.Printf("settings: re-read user %s: %v", userID, err)
		http.Error(w, `{"error":"could not read settings"}`, http.StatusInternalServerError)
		return
	}
	if stored == nil {
		http.Error(w, `{"error":"user not found"}`, http.StatusNotFound)
		return
	}

	if req.DisplayName != nil || req.localeSet {
		// Refresh the readable reelbolt_user cookie (and the JWT cookie, as change-password does)
		// so the new name shows without a re-login. Claims are unchanged.
		m, err := settings.resolveMember(r.Context(), stored)
		if err != nil {
			log.Printf("settings: resolve membership for %s: %v", userID, err)
			http.Error(w, `{"error":"could not refresh session"}`, http.StatusInternalServerError)
			return
		}
		if _, err := issueSession(w, stored, m); err != nil {
			http.Error(w, `{"error":"failed to generate token"}`, http.StatusInternalServerError)
			return
		}
	}

	writeSettings(w, stored)
}

// settingsUserID resolves the caller from the session. There is no other source: the handler never
// reads a body or a query parameter for identity, which is what makes the route self-only.
func settingsUserID(w http.ResponseWriter, r *http.Request) (uuid.UUID, bool) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return uuid.Nil, false
	}

	userID, err := uuid.Parse(uc.UserID)
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusUnauthorized)
		return uuid.Nil, false
	}
	return userID, true
}

func writeSettings(w http.ResponseWriter, u *models.ApplicationUser) {
	resp := SettingsResponse{McpAdminToolsEnabled: u.McpAdminToolsEnabled, McpMediaEgressEnabled: u.McpMediaEgressEnabled, DisplayName: u.DisplayName, Locale: localePtr(u)}
	if settings.pendingEmailChange != nil {
		// A failed lookup shows no notice rather than failing the whole settings read.
		p, err := settings.pendingEmailChange(u.ID, time.Now().UTC())
		if err != nil {
			log.Printf("settings: pending email change for %s: %v", u.ID, err)
		} else if p != nil {
			resp.PendingEmailChange = &PendingEmailChangeResponse{NewEmail: p.NewEmail, ExpiresAt: p.ExpiresAt}
		}
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(resp)
}

// fallbackDisplayName derives a name from an email local part when the user gave none. The local
// part is attacker-controlled (net/mail accepts very long ones with bidi or zero-width characters),
// so rejected runes are dropped and the result is cut to displayNameMaxRunes on a rune boundary.
func fallbackDisplayName(local string) string {
	var b strings.Builder
	n := 0
	for _, r := range local {
		if r == utf8.RuneError || unicode.IsControl(r) || unicode.Is(unicode.Cf, r) || !unicode.IsPrint(r) {
			continue
		}
		if n == displayNameMaxRunes {
			break
		}
		b.WriteRune(r)
		n++
	}
	if name, ok := validateDisplayName(b.String()); ok {
		return name
	}
	return "User"
}

// localePtr is the stored locale when it is still a registered code, else nil.
func localePtr(u *models.ApplicationUser) *string {
	if loc := services.UserLocale(u.Locale); loc != "" {
		return &loc
	}
	return nil
}
