package admin

import (
	"context"
	"database/sql/driver"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/testutil/fakesql"
)

const svcID = "00000000-0000-0000-0000-0000000000aa"

// asServiceAccount answers the "is this user a service account" count with 1, which is what the
// database says about an API key's user.
func asServiceAccount(q string, _ []driver.Value) ([]string, [][]driver.Value) {
	if strings.Contains(q, "count(*)") && strings.Contains(q, "application_users") {
		return []string{"count"}, [][]driver.Value{{int64(1)}}
	}
	return nil, nil
}

func idReq(method, body string) *http.Request {
	req := httptest.NewRequestWithContext(context.Background(), method, "/users/"+svcID, strings.NewReader(body))
	return mux.SetURLVars(req, map[string]string{"id": svcID})
}

func TestServiceAccount_AdminUpdateAndDeleteAre404(t *testing.T) {
	db := fakesql.Install(t, asServiceAccount)
	rec := httptest.NewRecorder()
	handleUpdateUser(rec, idReq(http.MethodPut, `{"isAdmin":true}`))
	if rec.Code != http.StatusNotFound {
		t.Fatalf("update: got %d %s", rec.Code, rec.Body)
	}
	// A password reset must answer 404 before ResetPassword can write to the hidden user.
	rec = httptest.NewRecorder()
	handleUpdateUser(rec, idReq(http.MethodPut, `{"resetPassword":true}`))
	if rec.Code != http.StatusNotFound {
		t.Fatalf("reset: got %d %s", rec.Code, rec.Body)
	}
	for _, s := range db.Statements() {
		if s.Exec && strings.Contains(s.Query, "application_users") {
			t.Fatalf("a write reached a service account: %s", s.Query)
		}
	}
	rec = httptest.NewRecorder()
	handleDeleteUser(rec, idReq(http.MethodDelete, ""))
	if rec.Code != http.StatusNotFound {
		t.Fatalf("delete: got %d %s", rec.Code, rec.Body)
	}
}

func TestServiceAccount_AdminUpdateRefusesReservedDomain(t *testing.T) {
	db := fakesql.Install(t, nil)
	rec := httptest.NewRecorder()
	handleUpdateUser(rec, idReq(http.MethodPut, `{"email":"apikey+other@service.invalid"}`))
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("got %d %s", rec.Code, rec.Body)
	}
	if len(db.Statements()) != 0 {
		t.Fatalf("the database was touched: %+v", db.Statements())
	}
}

func TestServiceAccount_AdminGetIs404AndListFilters(t *testing.T) {
	db := fakesql.Install(t, nil) // the filtered lookup finds no row
	rec := httptest.NewRecorder()
	handleGetUser(rec, idReq(http.MethodGet, ""))
	if rec.Code != http.StatusNotFound {
		t.Fatalf("get: got %d", rec.Code)
	}
	rec = httptest.NewRecorder()
	handleListUsers(rec, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/users", nil))
	if rec.Code != http.StatusOK || !db.Contains("from \"application_users\"", "is_service_account") {
		t.Fatalf("list: got %d, queries %+v", rec.Code, db.Statements())
	}
}

func TestServiceAccount_AdminCreateRefusesReservedDomain(t *testing.T) {
	db := fakesql.Install(t, nil)
	rec := httptest.NewRecorder()
	handleCreateUser(rec, httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/users",
		strings.NewReader(`{"email":"apikey+x@service.invalid","displayName":"k","isAdmin":true}`)))
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("got %d", rec.Code)
	}
	if len(db.Statements()) != 0 {
		t.Fatal("the database was touched")
	}
}
