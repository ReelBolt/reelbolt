package auth

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// settingsHarness installs the settings fakes and returns the signed-in user plus the writes the
// handler actually performed. The map is keyed by the user id the handler passed, so a test can assert
// that only ever one id — the caller's — was touched.
type settingsHarness struct {
	user     *models.ApplicationUser
	writes   map[uuid.UUID]bool
	getErr   error
	writeErr error
	missing  bool
}

func newSettingsHarness(t *testing.T, enabled bool) *settingsHarness {
	t.Helper()

	old := settings
	t.Cleanup(func() { settings = old })

	h := &settingsHarness{
		user: &models.ApplicationUser{
			ID:                   uuid.New(),
			Email:                "ada@example.com",
			DisplayName:          "Ada",
			McpAdminToolsEnabled: enabled,
		},
		writes: map[uuid.UUID]bool{},
	}

	settings = settingsDeps{
		getUser: func(id uuid.UUID) (*models.ApplicationUser, error) {
			if h.getErr != nil {
				return nil, h.getErr
			}
			if h.missing || id != h.user.ID {
				// An id the session could not have produced: there is no such account.
				return nil, nil
			}
			// Return a copy so a test cannot observe the caller's mutation by sharing the pointer.
			copied := *h.user
			return &copied, nil
		},
		setMcpAdminTools: func(id uuid.UUID, value bool) error {
			if h.writeErr != nil {
				return h.writeErr
			}
			h.writes[id] = value
			h.user.McpAdminToolsEnabled = value
			return nil
		},
	}
	return h
}

func (h *settingsHarness) sessionRequest(method, body string) *http.Request {
	var reader *strings.Reader
	if body != "" {
		reader = strings.NewReader(body)
	} else {
		reader = strings.NewReader("")
	}
	req := httptest.NewRequestWithContext(context.Background(), method, "/api/v1/auth/settings", reader)
	if body != "" {
		req.Header.Set("Content-Type", "application/json")
	}
	uc := middleware.UserContext{UserID: h.user.ID.String(), Email: h.user.Email}
	return req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey, uc))
}

func decodeSettings(t *testing.T, body string) SettingsResponse {
	t.Helper()
	var got SettingsResponse
	if err := json.Unmarshal([]byte(body), &got); err != nil {
		t.Fatalf("decode settings response %q: %v", body, err)
	}
	return got
}

func TestGetSettingsReturnsTheStoredValue(t *testing.T) {
	tests := []struct {
		name    string
		enabled bool
	}{
		{name: "off", enabled: false},
		{name: "on", enabled: true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := newSettingsHarness(t, tc.enabled)

			rec := httptest.NewRecorder()
			handleGetSettings(rec, h.sessionRequest(http.MethodGet, ""))

			if rec.Code != http.StatusOK {
				t.Fatalf("status = %d, want 200; body: %s", rec.Code, rec.Body.String())
			}
			if got := decodeSettings(t, rec.Body.String()); got.McpAdminToolsEnabled != tc.enabled {
				t.Fatalf("mcpAdminToolsEnabled = %v, want %v", got.McpAdminToolsEnabled, tc.enabled)
			}
			if len(h.writes) != 0 {
				t.Fatalf("a GET must not write anything, got %v", h.writes)
			}
		})
	}
}

// TestGetSettingsUsesCamelCaseJSON pins the one thing the UI binds to. A snake_case or PascalCase key
// would leave the portal's switch permanently reading its initial value.
func TestGetSettingsUsesCamelCaseJSON(t *testing.T) {
	h := newSettingsHarness(t, true)

	rec := httptest.NewRecorder()
	handleGetSettings(rec, h.sessionRequest(http.MethodGet, ""))

	var raw map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &raw); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(raw) != 5 {
		t.Fatalf("the response must carry exactly five fields (mcpAdminToolsEnabled, mcpMediaEgressEnabled, displayName, locale, pendingEmailChange), got %v", raw)
	}
	if _, ok := raw["mcpMediaEgressEnabled"]; !ok {
		t.Fatalf("the field must be mcpMediaEgressEnabled (camelCase), got %v", raw)
	}
	if _, ok := raw["mcpAdminToolsEnabled"]; !ok {
		t.Fatalf("the field must be mcpAdminToolsEnabled (camelCase), got %v", raw)
	}
}

func TestPatchSettingsTable(t *testing.T) {
	tests := []struct {
		name       string
		start      bool
		body       string
		wantStored bool
		wantWrites int
		wantStatus int
	}{
		{
			name:       "turn it on",
			start:      false,
			body:       `{"mcpAdminToolsEnabled":true}`,
			wantStored: true,
			wantWrites: 1,
		},
		{
			name:       "turn it off",
			start:      true,
			body:       `{"mcpAdminToolsEnabled":false}`,
			wantStored: false,
			wantWrites: 1,
		},
		{
			name:       "an empty object presents no field and writes nothing",
			start:      true,
			body:       `{}`,
			wantStored: true,
			wantWrites: 0,
		},
		{
			name:       "an absent field does not silently disable the setting",
			start:      true,
			body:       `{"somethingElse":false}`,
			wantStored: true,
			wantWrites: 0,
		},
		{
			name:       "setting it true when it is already true still reports true",
			start:      true,
			body:       `{"mcpAdminToolsEnabled":true}`,
			wantStored: true,
			wantWrites: 1,
		},
		{
			name:       "a non-boolean value is refused",
			start:      false,
			body:       `{"mcpAdminToolsEnabled":"yes"}`,
			wantStatus: http.StatusBadRequest,
			wantWrites: 0,
		},
		{
			name:       "malformed JSON is refused",
			start:      false,
			body:       `not json`,
			wantStatus: http.StatusBadRequest,
			wantWrites: 0,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			h := newSettingsHarness(t, tc.start)
			if tc.wantStatus == 0 {
				tc.wantStatus = http.StatusOK
			}

			rec := httptest.NewRecorder()
			handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, tc.body))

			if rec.Code != tc.wantStatus {
				t.Fatalf("status = %d, want %d; body: %s", rec.Code, tc.wantStatus, rec.Body.String())
			}
			if len(h.writes) != tc.wantWrites {
				t.Fatalf("writes = %v, want %d", h.writes, tc.wantWrites)
			}

			if tc.wantStatus != http.StatusOK {
				// A refused request must not have changed the stored value.
				if h.user.McpAdminToolsEnabled != tc.start {
					t.Fatalf("a refused PATCH changed the setting to %v", h.user.McpAdminToolsEnabled)
				}
				return
			}

			// The response is what is STORED, which is what the UI renders.
			if got := decodeSettings(t, rec.Body.String()); got.McpAdminToolsEnabled != tc.wantStored {
				t.Fatalf("response = %v, want the stored value %v", got.McpAdminToolsEnabled, tc.wantStored)
			}
			if h.user.McpAdminToolsEnabled != tc.wantStored {
				t.Fatalf("stored value = %v, want %v", h.user.McpAdminToolsEnabled, tc.wantStored)
			}
		})
	}
}

// TestPatchSettingsIsSelfOnly is the behavioural half of "self only": the handler takes the user from
// the session, so the write can only ever name the caller. There is no id in the body or the query for
// a caller to substitute — this asserts that not even a body that tries is honoured.
func TestPatchSettingsIsSelfOnly(t *testing.T) {
	h := newSettingsHarness(t, false)
	other := uuid.New()

	body := `{"mcpAdminToolsEnabled":true,"userId":"` + other.String() + `","userID":"` + other.String() + `"}`
	rec := httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, body))

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200; body: %s", rec.Code, rec.Body.String())
	}
	if len(h.writes) != 1 {
		t.Fatalf("want exactly one write, got %v", h.writes)
	}
	for id := range h.writes {
		if id != h.user.ID {
			t.Fatalf("the write named %s, but the caller is %s", id, h.user.ID)
		}
	}
	if _, touched := h.writes[other]; touched {
		t.Fatal("an unknown field in the body must not let a caller write another user's setting")
	}
}

func TestSettingsWithoutASessionIsUnauthorized(t *testing.T) {
	h := newSettingsHarness(t, false)

	tests := []struct {
		name    string
		handler http.HandlerFunc
		method  string
		body    string
	}{
		{name: "GET", handler: handleGetSettings, method: http.MethodGet},
		{name: "PATCH", handler: handlePatchSettings, method: http.MethodPatch, body: `{"mcpAdminToolsEnabled":true}`},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var reader *strings.Reader
			if tc.body != "" {
				reader = strings.NewReader(tc.body)
			} else {
				reader = strings.NewReader("")
			}
			req := httptest.NewRequestWithContext(context.Background(), tc.method, "/api/v1/auth/settings", reader)
			req.Header.Set("Content-Type", "application/json")
			// No middleware.UserContextKey: the handler must refuse rather than invent an identity.

			rec := httptest.NewRecorder()
			tc.handler(rec, req)

			if rec.Code != http.StatusUnauthorized {
				t.Fatalf("status = %d, want 401", rec.Code)
			}
			if len(h.writes) != 0 {
				t.Fatalf("an unauthenticated request must write nothing, got %v", h.writes)
			}
		})
	}
}

func TestSettingsForAVanishedAccountIsNotFound(t *testing.T) {
	h := newSettingsHarness(t, true)
	h.missing = true

	get := httptest.NewRecorder()
	handleGetSettings(get, h.sessionRequest(http.MethodGet, ""))
	if get.Code != http.StatusNotFound {
		t.Fatalf("GET status = %d, want 404", get.Code)
	}

	patch := httptest.NewRecorder()
	handlePatchSettings(patch, h.sessionRequest(http.MethodPatch, `{"mcpAdminToolsEnabled":true}`))
	if patch.Code != http.StatusNotFound {
		t.Fatalf("PATCH status = %d, want 404", patch.Code)
	}
	if len(h.writes) != 0 {
		t.Fatalf("nothing may be written for an account that does not exist, got %v", h.writes)
	}
}

// TestPatchSettingsDoesNotTouchTheSession is the contract's point (§11): the setting is read live by
// the bridge, so changing it must not re-issue or invalidate the caller's session. A Set-Cookie here
// would be a re-issued session; there must be none.
func TestPatchSettingsDoesNotTouchTheSession(t *testing.T) {
	h := newSettingsHarness(t, false)

	rec := httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, `{"mcpAdminToolsEnabled":true}`))

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if cookies := rec.Result().Cookies(); len(cookies) != 0 {
		t.Fatalf("PATCH must not re-issue the session, got cookies %v", cookies)
	}
}

func TestSettingsWriteFailureIsReported(t *testing.T) {
	h := newSettingsHarness(t, false)
	h.writeErr = context.DeadlineExceeded

	rec := httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, `{"mcpAdminToolsEnabled":true}`))

	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("status = %d, want 500", rec.Code)
	}
	if h.user.McpAdminToolsEnabled {
		t.Fatal("the setting must not appear changed when the write failed")
	}
}

func withDisplayNameFakes(t *testing.T, h *settingsHarness) {
	t.Helper()
	oldSess := sess
	t.Cleanup(func() { sess = oldSess })
	sess = sessionDeps{}
	settings.setDisplayName = func(id uuid.UUID, name string) error {
		h.writes[id] = true
		h.user.DisplayName = name
		return nil
	}
	settings.resolveMember = func(context.Context, *models.ApplicationUser) (*services.ActiveMembership, error) {
		return &services.ActiveMembership{OrgID: uuid.New(), OrgName: "Acme", OrgKind: "personal", Role: "owner"}, nil
	}
}

func TestPatchDisplayNameRefreshesSessionAndTrims(t *testing.T) {
	h := newSettingsHarness(t, false)
	withDisplayNameFakes(t, h)

	rec := httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, `{"displayName":"  Ada L.  "}`))

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	if got := decodeSettings(t, rec.Body.String()); got.DisplayName != "Ada L." {
		t.Fatalf("displayName = %q", got.DisplayName)
	}
	var userCookie string
	for _, c := range rec.Result().Cookies() {
		if c.Name == "reelbolt_user" {
			userCookie = c.Value
		}
	}
	if !strings.Contains(userCookie, "Ada+L.") && !strings.Contains(userCookie, "Ada%20L.") && !strings.Contains(userCookie, "Ada L.") {
		t.Fatalf("reelbolt_user cookie lacks new name: %q", userCookie)
	}
}

func TestPatchDisplayNameValidation(t *testing.T) {
	for name, body := range map[string]string{
		"empty":      `{"displayName":""}`,
		"blank":      `{"displayName":"   "}`,
		"too long":   `{"displayName":"` + strings.Repeat("a", 101) + `"}`,
		"newline":    "{\"displayName\":\"a\\nb\"}",
		"zero width": "{\"displayName\":\"a\\u200bb\"}",
		"bidi":       "{\"displayName\":\"a\\u202eb\"}",
	} {
		t.Run(name, func(t *testing.T) {
			h := newSettingsHarness(t, false)
			withDisplayNameFakes(t, h)
			rec := httptest.NewRecorder()
			handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, body))
			if rec.Code != http.StatusBadRequest {
				t.Fatalf("status = %d, want 400", rec.Code)
			}
			if h.user.DisplayName != "Ada" || len(h.writes) != 0 {
				t.Fatalf("a rejected name must not be stored")
			}
		})
	}
}

func TestPatchDisplayNameIsSelfOnlyAndAuthenticated(t *testing.T) {
	h := newSettingsHarness(t, false)
	withDisplayNameFakes(t, h)
	other := uuid.New()

	rec := httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, `{"displayName":"Eve","userId":"`+other.String()+`"}`))
	if rec.Code != http.StatusOK || len(h.writes) != 1 || !h.writes[h.user.ID] {
		t.Fatalf("only the caller may be written, writes=%v code=%d", h.writes, rec.Code)
	}

	rec = httptest.NewRecorder()
	req := httptest.NewRequestWithContext(context.Background(), http.MethodPatch, "/api/v1/auth/settings", strings.NewReader(`{"displayName":"X"}`))
	handlePatchSettings(rec, req)
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("unauthenticated status = %d", rec.Code)
	}
}

func TestGetSettingsReturnsDisplayName(t *testing.T) {
	h := newSettingsHarness(t, false)
	rec := httptest.NewRecorder()
	handleGetSettings(rec, h.sessionRequest(http.MethodGet, ""))
	if got := decodeSettings(t, rec.Body.String()); got.DisplayName != "Ada" {
		t.Fatalf("displayName = %q", got.DisplayName)
	}
}

func TestValidateDisplayNameBoundaries(t *testing.T) {
	ok := map[string]string{
		"ascii 100":   strings.Repeat("a", 100),
		"e-acute 100": strings.Repeat("é", 100),
		"emoji 100":   strings.Repeat("😀", 100),
		"multibyte":   "José Müller 山田",
	}
	for name, in := range ok {
		if got, valid := validateDisplayName(in); !valid || got != in {
			t.Errorf("%s: want accepted unchanged, got %q %v", name, got, valid)
		}
	}
	bad := map[string]string{
		"ascii 101":    strings.Repeat("a", 101),
		"é 101":        strings.Repeat("é", 101),
		"emoji 101":    strings.Repeat("😀", 101),
		"nbsp":         "a b",
		"tab":          "a\tb",
		"rtl mark":     "a\u200fb",
		"invalid utf8": "a\xffb",
	}
	for name, in := range bad {
		if _, valid := validateDisplayName(in); valid {
			t.Errorf("%s: want rejected", name)
		}
	}
}

func TestSessionCarriesDisplayName(t *testing.T) {
	h := newSettingsHarness(t, false)
	withDisplayNameFakes(t, h)
	m := &services.ActiveMembership{OrgID: uuid.New(), OrgName: "Acme", OrgKind: "personal", Role: "owner"}
	setup(t, true)

	rec := httptest.NewRecorder()
	writeSession(rec, h.user, m)
	var resp map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil || resp["displayName"] != "Ada" {
		t.Fatalf("login response displayName = %v (%v)", resp["displayName"], err)
	}
	found := false
	for _, c := range rec.Result().Cookies() {
		if c.Name == "reelbolt_user" {
			found = true
			raw, _ := url.QueryUnescape(c.Value)
			var ui map[string]any
			if err := json.Unmarshal([]byte(raw), &ui); err != nil || ui["displayName"] != "Ada" {
				t.Fatalf("cookie displayName = %v (%v)", ui["displayName"], err)
			}
		}
	}
	if !found {
		t.Fatal("no reelbolt_user cookie")
	}
}

// The per-user "connected tools may allow sending my media" switch: off by default, written only by
// its own field, and a PATCH that names another field leaves it alone.
func TestPatchSettingsMcpMediaEgress(t *testing.T) {
	h := newSettingsHarness(t, false)
	egressWrites := map[uuid.UUID]bool{}
	settings.setMcpMediaEgress = func(id uuid.UUID, value bool) error {
		egressWrites[id] = value
		h.user.McpMediaEgressEnabled = value
		return nil
	}

	rec := httptest.NewRecorder()
	handleGetSettings(rec, h.sessionRequest(http.MethodGet, ""))
	if got := decodeSettings(t, rec.Body.String()); got.McpMediaEgressEnabled {
		t.Fatalf("the setting must be off by default, got %+v", got)
	}
	if !strings.Contains(rec.Body.String(), `"mcpMediaEgressEnabled":false`) {
		t.Fatalf("the response must carry mcpMediaEgressEnabled in camelCase: %s", rec.Body.String())
	}

	rec = httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, `{"mcpMediaEgressEnabled":true}`))
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	if got := decodeSettings(t, rec.Body.String()); !got.McpMediaEgressEnabled {
		t.Fatalf("the response must show the stored value, got %+v", got)
	}
	if v, ok := egressWrites[h.user.ID]; !ok || !v {
		t.Fatalf("the setting was not written for the session user: %v", egressWrites)
	}
	if len(h.writes) != 0 {
		t.Fatalf("the admin-tools setting must not be touched: %v", h.writes)
	}

	// Another field alone says nothing about this one.
	rec = httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, `{"mcpAdminToolsEnabled":true}`))
	if got := decodeSettings(t, rec.Body.String()); !got.McpMediaEgressEnabled {
		t.Fatalf("a PATCH of another field switched the setting off: %+v", got)
	}

	rec = httptest.NewRecorder()
	handlePatchSettings(rec, h.sessionRequest(http.MethodPatch, `{"mcpMediaEgressEnabled":false}`))
	if got := decodeSettings(t, rec.Body.String()); got.McpMediaEgressEnabled {
		t.Fatalf("the setting must be switched off again, got %+v", got)
	}
}
