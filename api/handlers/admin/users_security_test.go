package admin

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// fakeAccounts replaces the account operations and records which users were evicted from the
// auth caches.
type fakeAccounts struct {
	user    *models.ApplicationUser
	evicted []uuid.UUID
	resets  int
	deletes int
	updates []map[string]interface{}
}

func useFakeAccounts(t *testing.T) *fakeAccounts {
	t.Helper()
	verifiedAt := time.Now()
	f := &fakeAccounts{user: &models.ApplicationUser{ID: uuid.New(), Email: "ada@example.com", DisplayName: "Ada", EmailVerifiedAt: &verifiedAt}}
	prevGet, prevReset, prevUpdate, prevDelete, prevInvalidate := getUserByID, resetPassword, updateUser, deleteUser, invalidateUser
	t.Cleanup(func() {
		getUserByID, resetPassword, updateUser, deleteUser, invalidateUser = prevGet, prevReset, prevUpdate, prevDelete, prevInvalidate
	})
	getUserByID = func(uuid.UUID) (*models.ApplicationUser, error) { return f.user, nil }
	resetPassword = func(uuid.UUID, string, services.AdminActor) (string, error) { f.resets++; return "temporary", nil }
	updateUser = func(_ uuid.UUID, fields map[string]interface{}, _ services.AdminActor) (*services.UpdateUserResult, error) {
		f.updates = append(f.updates, fields)
		_, admin := fields["is_admin"]
		_, email := fields["email"]
		return &services.UpdateUserResult{User: f.user, SessionsEnded: admin || email}, nil
	}
	deleteUser = func(uuid.UUID, services.AdminActor) error { f.deletes++; return nil }
	invalidateUser = func(id uuid.UUID) { f.evicted = append(f.evicted, id) }
	return f
}

func adminRequest(method string, id uuid.UUID, body io.Reader) *http.Request {
	req := httptest.NewRequestWithContext(context.Background(), method, "/users/"+id.String(), body)
	return mux.SetURLVars(req, map[string]string{"id": id.String()})
}

// AUTHZ-GO-5: an admin password reset, an is_admin change and a delete each evict the target's
// cached session and memberships, so they apply at once instead of after the cache TTL.
func TestAdminUserChanges_EvictTheTargetFromTheAuthCaches(t *testing.T) {
	for _, tc := range []struct {
		name   string
		method string
		body   string
		handle http.HandlerFunc
		status int
	}{
		{"reset password", http.MethodPut, `{"resetPassword":true}`, handleUpdateUser, http.StatusOK},
		{"demote", http.MethodPut, `{"isPlatformAdmin":false}`, handleUpdateUser, http.StatusOK},
		{"legacy isAdmin", http.MethodPut, `{"isAdmin":true}`, handleUpdateUser, http.StatusOK},
		{"delete", http.MethodDelete, ``, handleDeleteUser, http.StatusNoContent},
	} {
		f := useFakeAccounts(t)
		rec := httptest.NewRecorder()
		tc.handle(rec, adminRequest(tc.method, f.user.ID, strings.NewReader(tc.body)))
		if rec.Code != tc.status {
			t.Fatalf("%s: status %d (%s)", tc.name, rec.Code, rec.Body.String())
		}
		if len(f.evicted) != 1 || f.evicted[0] != f.user.ID {
			t.Errorf("%s: evicted %v, want exactly the target %s", tc.name, f.evicted, f.user.ID)
		}
	}
}

// A change that touches neither the password nor the platform-admin flag evicts nothing, and a
// change that failed evicts nothing either.
func TestAdminUserChanges_EvictOnlyWhenAuthorityChanged(t *testing.T) {
	f := useFakeAccounts(t)
	rec := httptest.NewRecorder()
	handleUpdateUser(rec, adminRequest(http.MethodPut, f.user.ID, strings.NewReader(`{"displayName":"Ada L."}`)))
	if rec.Code != http.StatusOK || len(f.updates) != 1 {
		t.Fatalf("status %d updates %d", rec.Code, len(f.updates))
	}
	if len(f.evicted) != 0 {
		t.Fatalf("a rename evicted %v", f.evicted)
	}

	f = useFakeAccounts(t)
	deleteUser = func(uuid.UUID, services.AdminActor) error { return io.ErrUnexpectedEOF }
	rec = httptest.NewRecorder()
	handleDeleteUser(rec, adminRequest(http.MethodDelete, f.user.ID, nil))
	if rec.Code != http.StatusInternalServerError || len(f.evicted) != 0 {
		t.Fatalf("failed delete: status %d evicted %v", rec.Code, f.evicted)
	}
}

// countingReader counts what a handler pulled from the request body.
type countingReader struct {
	r io.Reader
	n int
}

func (c *countingReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	c.n += n
	return n, err
}

// API-GO-03: the admin decoders stop reading at their cap instead of buffering whatever is
// sent (nginx allows 100 MB).
func TestAdminUserBodies_AreCapped(t *testing.T) {
	huge := `{"email":"a@example.com","displayName":"` + strings.Repeat("a", 1<<20) + `"}`
	for _, tc := range []struct {
		name   string
		method string
		handle http.HandlerFunc
	}{
		{"create", http.MethodPost, handleCreateUser},
		{"update", http.MethodPut, handleUpdateUser},
	} {
		f := useFakeAccounts(t)
		body := &countingReader{r: strings.NewReader(huge)}
		rec := httptest.NewRecorder()
		tc.handle(rec, adminRequest(tc.method, f.user.ID, body))
		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s: status %d, want 400", tc.name, rec.Code)
		}
		if body.n > maxBodyBytes+1024 {
			t.Errorf("%s: read %d bytes of an oversized body, cap is %d", tc.name, body.n, maxBodyBytes)
		}
		if len(f.updates) != 0 {
			t.Errorf("%s: an oversized body was acted on", tc.name)
		}
	}
}
