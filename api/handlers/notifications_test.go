package handlers

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// fakeNotifStore is a NotificationStore the handler tests drive.
type fakeNotifStore struct {
	prefs  map[uuid.UUID]services.NotificationPreferences
	saved  map[uuid.UUID]services.NotificationPreferences
	recent []services.NotificationRecord
}

func newFakeNotifStore() *fakeNotifStore {
	return &fakeNotifStore{
		prefs:  map[uuid.UUID]services.NotificationPreferences{},
		saved:  map[uuid.UUID]services.NotificationPreferences{},
		recent: []services.NotificationRecord{},
	}
}

func (f *fakeNotifStore) GetPreferences(_ context.Context, userID uuid.UUID) (services.NotificationPreferences, error) {
	if p, ok := f.prefs[userID]; ok {
		return p, nil
	}
	return services.DefaultNotificationPreferences(), nil
}

func (f *fakeNotifStore) SavePreferences(_ context.Context, userID uuid.UUID, prefs services.NotificationPreferences) error {
	f.saved[userID] = prefs
	f.prefs[userID] = prefs
	return nil
}

func (f *fakeNotifStore) RecordDelivery(context.Context, services.NotificationRecord) error {
	return nil
}

func (f *fakeNotifStore) ListRecent(_ context.Context, _ uuid.UUID, _ int) ([]services.NotificationRecord, error) {
	return f.recent, nil
}

func (f *fakeNotifStore) LookupUser(context.Context, uuid.UUID) (string, string, bool, error) {
	return "", "", false, nil
}
func (f *fakeNotifStore) OrgName(context.Context, uuid.UUID) (string, error) { return "", nil }
func (f *fakeNotifStore) OrgMembers(context.Context, uuid.UUID) ([]uuid.UUID, error) {
	return nil, nil
}
func (f *fakeNotifStore) OrgOwner(context.Context, uuid.UUID) (uuid.UUID, error) {
	return uuid.Nil, nil
}
func (f *fakeNotifStore) WorkflowExecution(context.Context, uuid.UUID) (*models.WorkflowExecution, error) {
	return nil, nil
}
func (f *fakeNotifStore) ProjectName(context.Context, uuid.UUID) (string, error) {
	return "", nil
}

func notifRouter() *mux.Router {
	r := mux.NewRouter()
	RegisterNotificationsRoutes(r)
	return r
}

func notifRequest(t *testing.T, router *mux.Router, method, path string, userID uuid.UUID, body string) *httptest.ResponseRecorder {
	t.Helper()
	var req *http.Request
	if body != "" {
		req = httptest.NewRequestWithContext(context.Background(), method, path, strings.NewReader(body))
	} else {
		req = httptest.NewRequestWithContext(context.Background(), method, path, nil)
	}
	req = req.WithContext(context.WithValue(req.Context(), middleware.UserContextKey,
		middleware.UserContext{UserID: userID.String()}))
	w := httptest.NewRecorder()
	router.ServeHTTP(w, req)
	return w
}

func TestPreferencesGetDefaultsWhenNoRow(t *testing.T) {
	router := notifRouter()
	notificationStore = newFakeNotifStore()
	userID := uuid.New()

	w := notifRequest(t, router, http.MethodGet, "/api/v1/notifications/preferences", userID, "")
	if w.Code != http.StatusOK {
		t.Fatalf("status = %d", w.Code)
	}
	var got services.NotificationPreferences
	if err := json.NewDecoder(w.Body).Decode(&got); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if !got.JobSucceeded || !got.JobFailed || !got.FileCreated || !got.GraphicsEdited || !got.ReviewActivity {
		t.Errorf("defaults should be all on: %+v", got)
	}
}

func TestPreferencesRoundTrip(t *testing.T) {
	router := notifRouter()
	store := newFakeNotifStore()
	notificationStore = store
	userID := uuid.New()

	w := notifRequest(t, router, http.MethodPut, "/api/v1/notifications/preferences", userID,
		`{"fileCreated": false}`)
	if w.Code != http.StatusOK {
		t.Fatalf("put status = %d", w.Code)
	}
	saved, ok := store.saved[userID]
	if !ok {
		t.Fatal("save not recorded")
	}
	if saved.FileCreated {
		t.Error("fileCreated should now be off")
	}
	if !saved.JobSucceeded || !saved.GraphicsEdited {
		t.Errorf("unspecified switches should keep their default (on): %+v", saved)
	}
}

func TestPreferencesPutRejectsBadBody(t *testing.T) {
	router := notifRouter()
	notificationStore = newFakeNotifStore()
	userID := uuid.New()

	w := notifRequest(t, router, http.MethodPut, "/api/v1/notifications/preferences", userID, `not json`)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("want 400 for a bad body, got %d", w.Code)
	}
}

func TestNotificationsList(t *testing.T) {
	router := notifRouter()
	store := newFakeNotifStore()
	notificationStore = store
	store.recent = []services.NotificationRecord{{
		ID: uuid.New(), Category: "job_failed", Title: "boom", Delivered: false,
	}}
	userID := uuid.New()

	w := notifRequest(t, router, http.MethodGet, "/api/v1/notifications", userID, "")
	if w.Code != http.StatusOK {
		t.Fatalf("status = %d", w.Code)
	}
	var resp struct {
		Notifications []services.NotificationRecord `json:"notifications"`
	}
	if err := json.NewDecoder(w.Body).Decode(&resp); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if len(resp.Notifications) != 1 || resp.Notifications[0].Category != "job_failed" {
		t.Errorf("unexpected list: %+v", resp.Notifications)
	}
}

func TestPreferencesReviewActivityRoundTrip(t *testing.T) {
	router := notifRouter()
	store := newFakeNotifStore()
	notificationStore = store
	userID := uuid.New()

	// Omitted field: stays on.
	w := notifRequest(t, router, http.MethodPut, "/api/v1/notifications/preferences", userID, `{"fileCreated": false}`)
	if w.Code != http.StatusOK || !store.saved[userID].ReviewActivity {
		t.Fatalf("omitted reviewActivity must stay on: %d %+v", w.Code, store.saved[userID])
	}
	// Explicit off, other switches untouched.
	w = notifRequest(t, router, http.MethodPut, "/api/v1/notifications/preferences", userID, `{"reviewActivity": false}`)
	if w.Code != http.StatusOK || store.saved[userID].ReviewActivity || store.saved[userID].FileCreated {
		t.Fatalf("reviewActivity off not saved: %+v", store.saved[userID])
	}
	if !strings.Contains(w.Body.String(), `"reviewActivity":false`) {
		t.Errorf("response lacks reviewActivity: %s", w.Body.String())
	}
	// Later PUT omitting it keeps it off.
	notifRequest(t, router, http.MethodPut, "/api/v1/notifications/preferences", userID, `{"jobFailed": false}`)
	if store.saved[userID].ReviewActivity {
		t.Error("omitted field must not turn it back on")
	}
	// GET includes it.
	w = notifRequest(t, router, http.MethodGet, "/api/v1/notifications/preferences", userID, "")
	if !strings.Contains(w.Body.String(), `"reviewActivity":false`) {
		t.Errorf("GET lacks reviewActivity: %s", w.Body.String())
	}
}
