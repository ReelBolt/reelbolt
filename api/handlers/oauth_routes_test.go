package handlers

import (
	"net/http"
	"sort"
	"strings"
	"testing"

	"github.com/gorilla/mux"
)

// TestOAuthAuthorizationServerRoutesAreRegistered is the wiring evidence for
// docs/design/mcp-oauth.md §4: it builds the real router the process serves and walks it, so a route
// that was written but never mounted — or one mounted on the wrong surface — fails here rather than at
// the first browser login.
//
// It also pins the boundary the contract calls out: the new authorization server lives under /oauth
// and /.well-known, and the existing Google/GitHub *sign-in client* surface at
// /api/v1/auth/oauth/{provider} must still be there, untouched.
func TestOAuthAuthorizationServerRoutesAreRegistered(t *testing.T) {
	NewHandler()
	RegisterHandlers()

	registered := map[string]map[string]bool{}
	if err := Router.Walk(func(route *mux.Route, _ *mux.Router, _ []*mux.Route) error {
		path, err := route.GetPathTemplate()
		if err != nil {
			return err
		}
		methods, err := route.GetMethods()
		if err != nil {
			methods = nil
		}
		if registered[path] == nil {
			registered[path] = map[string]bool{}
		}
		for _, m := range methods {
			registered[path][m] = true
		}
		if len(methods) == 0 {
			registered[path][""] = true
		}
		return nil
	}); err != nil {
		t.Fatalf("walking the router: %v", err)
	}

	want := []struct {
		method string
		path   string
	}{
		{http.MethodGet, "/.well-known/oauth-authorization-server"},
		{http.MethodPost, "/oauth/register"},
		{http.MethodGet, "/oauth/authorize"},
		{http.MethodPost, "/oauth/authorize"},
		{http.MethodPost, "/oauth/token"},
		{http.MethodPost, "/oauth/revoke"},
	}

	for _, w := range want {
		if !registered[w.path][w.method] {
			t.Errorf("%s %s is not registered", w.method, w.path)
		}
	}

	// The sign-in client surface is a different thing and must be undisturbed.
	if !registered["/api/v1/auth/oauth/{provider}/start"][http.MethodGet] {
		t.Error("the Google/GitHub sign-in start route must remain registered")
	}
	if !registered["/api/v1/auth/oauth/{provider}/callback"][http.MethodGet] {
		t.Error("the Google/GitHub sign-in callback route must remain registered")
	}

	// Nothing from the authorization server may be registered under /api/v1/auth/oauth — that would
	// collide with a provider named "token", "register" or "authorize".
	for path := range registered {
		if strings.HasPrefix(path, "/api/v1/auth/oauth/") {
			rest := strings.TrimPrefix(path, "/api/v1/auth/oauth/")
			head, _, _ := strings.Cut(rest, "/")
			switch head {
			case "token", "register", "authorize", "revoke":
				t.Errorf("%s shadows the OAuth authorization server under the sign-in surface", path)
			}
		}
	}
}

// TestOAuthRoutesDoNotCollide checks there is exactly one registration per (path, method) pair on the
// authorization server surface. Two mux routes with the same path and method are legal Go but only the
// first is ever reached, which is how a second handler silently never runs.
func TestOAuthRoutesDoNotCollide(t *testing.T) {
	NewHandler()
	RegisterHandlers()

	counts := map[string]int{}
	if err := Router.Walk(func(route *mux.Route, _ *mux.Router, _ []*mux.Route) error {
		path, err := route.GetPathTemplate()
		if err != nil {
			return err
		}
		methods, err := route.GetMethods()
		if err != nil || len(methods) == 0 {
			methods = []string{""}
		}
		for _, m := range methods {
			if strings.HasPrefix(path, "/oauth") || strings.HasPrefix(path, "/.well-known/oauth") {
				counts[m+" "+path]++
			}
		}
		return nil
	}); err != nil {
		t.Fatalf("walking the router: %v", err)
	}

	keys := make([]string, 0, len(counts))
	for key := range counts {
		keys = append(keys, key)
	}
	sort.Strings(keys)

	for _, key := range keys {
		if counts[key] > 1 {
			t.Errorf("%s is registered %d times", key, counts[key])
		}
	}
}
