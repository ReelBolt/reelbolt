package handlers

import (
	"encoding/json"
	"net/http"
	"strconv"
	"strings"

	"github.com/google/uuid"
	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/services"
)

// RegisterNotificationsRoutes mounts the caller's own notification endpoints on
// the authenticated router: read/update the four switches and list the recent
// delivery log. Every query is scoped to the caller by the JWT's user id — a
// request for another user's switches or log is a 404, not a 403, so the
// endpoint is not an existence oracle for user ids.
func RegisterNotificationsRoutes(router *mux.Router) {
	router.HandleFunc("/api/v1/notifications/preferences", func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet:
			handleNotificationPreferencesGet(w, r)
		case http.MethodPut:
			handleNotificationPreferencesPut(w, r)
		default:
			http.Error(w, `{"error":"method not allowed"}`, http.StatusMethodNotAllowed)
		}
	}).Methods(http.MethodGet, http.MethodPut)

	router.HandleFunc("/api/v1/notifications", handleNotificationsList).Methods(http.MethodGet)
}

// notificationStore is the store the handlers ask; replaceable in tests.
var notificationStore services.NotificationStore = services.GormNotificationStore{}

func callerUserID(r *http.Request) (uuid.UUID, bool) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		return uuid.Nil, false
	}
	id, err := uuid.Parse(uc.UserID)
	if err != nil {
		return uuid.Nil, false
	}
	return id, true
}

func handleNotificationPreferencesGet(w http.ResponseWriter, r *http.Request) {
	userID, _ := callerUserID(r)
	prefs, err := notificationStore.GetPreferences(r.Context(), userID)
	if err != nil {
		http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
		return
	}
	writeJSON(w, http.StatusOK, prefs)
}

type preferencesUpdate struct {
	JobSucceeded     *bool `json:"jobSucceeded"`
	JobFailed        *bool `json:"jobFailed"`
	FileCreated      *bool `json:"fileCreated"`
	GraphicsEdited   *bool `json:"graphicsEdited"`
	ReviewActivity   *bool `json:"reviewActivity"`
	AvatarRetention  *bool `json:"avatarRetention"`
	SocialPublishing *bool `json:"socialPublishing"`
}

func handleNotificationPreferencesPut(w http.ResponseWriter, r *http.Request) {
	userID, _ := callerUserID(r)
	var in preferencesUpdate
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4*1024)).Decode(&in); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}
	current, err := notificationStore.GetPreferences(r.Context(), userID)
	if err != nil {
		http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
		return
	}
	// A field the caller did not send keeps its current (or default) value.
	if in.JobSucceeded != nil {
		current.JobSucceeded = *in.JobSucceeded
	}
	if in.JobFailed != nil {
		current.JobFailed = *in.JobFailed
	}
	if in.FileCreated != nil {
		current.FileCreated = *in.FileCreated
	}
	if in.GraphicsEdited != nil {
		current.GraphicsEdited = *in.GraphicsEdited
	}
	if in.ReviewActivity != nil {
		current.ReviewActivity = *in.ReviewActivity
	}
	if in.AvatarRetention != nil {
		current.AvatarRetention = *in.AvatarRetention
	}
	if in.SocialPublishing != nil {
		current.SocialPublishing = *in.SocialPublishing
	}
	if err := notificationStore.SavePreferences(r.Context(), userID, current); err != nil {
		http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
		return
	}
	writeJSON(w, http.StatusOK, current)
}

type notificationListResponse struct {
	Notifications []services.NotificationRecord `json:"notifications"`
}

func handleNotificationsList(w http.ResponseWriter, r *http.Request) {
	userID, _ := callerUserID(r)
	limit := 30
	if v := strings.TrimSpace(r.URL.Query().Get("limit")); v != "" {
		if n, err := strconv.Atoi(v); err == nil && n > 0 {
			limit = n
		}
	}
	recs, err := notificationStore.ListRecent(r.Context(), userID, limit)
	if err != nil {
		http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
		return
	}
	if recs == nil {
		recs = []services.NotificationRecord{}
	}
	writeJSON(w, http.StatusOK, notificationListResponse{Notifications: recs})
}

// writeJSON is the one response writer the file uses.
func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
