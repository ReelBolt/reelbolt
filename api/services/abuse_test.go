package services

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestIsDisposableEmail(t *testing.T) {
	for email, want := range map[string]bool{
		"a@mailinator.com":          true,
		"A@MAILINATOR.COM":          true,
		"a@sub.mailinator.com":      true,
		"a@mailinator.com.":         true,
		"a@gmail.com":               false,
		"a@example.com":             false,
		"a@xmailinator-example.org": false,
		"no-at-sign":                false,
		"a@com":                     false,
		"a@guerrillamail.com":       true,
		"someone@company.example":   false,
	} {
		if got := IsDisposableEmail(email); got != want {
			t.Errorf("IsDisposableEmail(%q) = %v, want %v", email, got, want)
		}
	}
}

func TestDisposableList_LoadedAndHeaderSkipped(t *testing.T) {
	set := disposableDomains()
	if len(set) < 1000 {
		t.Fatalf("embedded blocklist looks truncated: %d entries", len(set))
	}
	for d := range set {
		if d == "" || d[0] == '#' {
			t.Fatalf("comment or blank leaked into the set: %q", d)
		}
	}
}

func TestCheckMX(t *testing.T) {
	oldMX, oldHost := lookupMX, lookupHost
	defer func() { lookupMX, lookupHost = oldMX, oldHost }()
	notFound := &net.DNSError{Err: "no such host", IsNotFound: true}
	temp := &net.DNSError{Err: "timeout", IsTimeout: true}

	cases := []struct {
		name string
		mx   func() ([]*net.MX, error)
		host func() ([]string, error)
		want error
	}{
		{"has MX", func() ([]*net.MX, error) { return []*net.MX{{Host: "mx."}}, nil }, nil, nil},
		{"no MX but A record", func() ([]*net.MX, error) { return nil, notFound }, func() ([]string, error) { return []string{"192.0.2.1"}, nil }, nil},
		{"nothing at all", func() ([]*net.MX, error) { return nil, notFound }, func() ([]string, error) { return nil, notFound }, ErrNoMailServer},
		{"resolver outage is not evidence", func() ([]*net.MX, error) { return nil, temp }, nil, nil},
		{"A lookup outage is not evidence", func() ([]*net.MX, error) { return nil, notFound }, func() ([]string, error) { return nil, temp }, nil},
	}
	for _, c := range cases {
		lookupMX = func(context.Context, string) ([]*net.MX, error) { return c.mx() }
		lookupHost = func(context.Context, string) ([]string, error) {
			if c.host == nil {
				t.Fatalf("%s: unexpected host lookup", c.name)
			}
			return c.host()
		}
		if got := CheckMX(context.Background(), "a@example.org"); !errors.Is(got, c.want) {
			t.Errorf("%s: got %v want %v", c.name, got, c.want)
		}
	}
	if err := CheckMX(context.Background(), "no-domain"); !errors.Is(err, ErrNoMailServer) {
		t.Fatalf("no domain: %v", err)
	}
}

func TestVerifyTurnstile(t *testing.T) {
	var gotSecret, gotResponse string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_ = r.ParseForm()
		gotSecret, gotResponse = r.PostForm.Get("secret"), r.PostForm.Get("response")
		_ = json.NewEncoder(w).Encode(map[string]bool{"success": gotResponse == "good"})
	}))
	defer srv.Close()
	old := TurnstileVerifyURL
	TurnstileVerifyURL = srv.URL
	defer func() { TurnstileVerifyURL = old }()

	if ok, err := VerifyTurnstile(context.Background(), "", "", ""); !ok || err != nil {
		t.Fatalf("unset secret must skip the challenge: %v %v", ok, err)
	}
	if ok, _ := VerifyTurnstile(context.Background(), "s3cret", "", "1.2.3.4"); ok {
		t.Fatal("empty token must fail when configured")
	}
	if ok, err := VerifyTurnstile(context.Background(), "s3cret", "good", "1.2.3.4"); !ok || err != nil || gotSecret != "s3cret" {
		t.Fatalf("good: %v %v %q", ok, err, gotSecret)
	}
	if ok, _ := VerifyTurnstile(context.Background(), "s3cret", "bad", ""); ok {
		t.Fatal("bad token must fail")
	}
	srv.Close()
	if ok, err := VerifyTurnstile(context.Background(), "s3cret", "good", ""); ok || err == nil {
		t.Fatal("unreachable verifier must fail closed with an error")
	}
}
