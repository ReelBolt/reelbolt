package agent

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestDialGuardTable(t *testing.T) {
	tests := []struct {
		addr    string
		blocked bool
	}{
		{"169.254.169.254:80", true},
		{"169.254.1.1:443", true},
		{"[fe80::1]:80", true},
		{"0.0.0.0:80", true},
		{"[::]:80", true},
		{"224.0.0.1:80", true},
		{"[ff02::1]:80", true},
		{"255.255.255.255:80", true},
		{"[::ffff:169.254.169.254]:80", true},
		{"127.0.0.1:9000", false},
		{"[::1]:9000", false},
		{"10.0.0.5:9000", false},
		{"172.18.0.2:9000", false},
		{"192.168.1.10:9000", false},
		{"93.184.216.34:443", false},
	}
	for _, tc := range tests {
		err := DialGuard("tcp", tc.addr, nil)
		if tc.blocked && !errors.Is(err, ErrBlockedAddress) {
			t.Errorf("%s: want blocked, got %v", tc.addr, err)
		}
		if !tc.blocked && err != nil {
			t.Errorf("%s: want allowed, got %v", tc.addr, err)
		}
	}
}

func TestGuardedTransportReachesLoopback(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { _, _ = w.Write([]byte("ok")) }))
	defer srv.Close()
	resp, err := DefaultHTTPClient().Do(mustReq(t, srv.URL))
	if err != nil {
		t.Fatalf("loopback storage must work: %v", err)
	}
	_ = resp.Body.Close()
}

func TestGuardedTransportRefusesMetadata(t *testing.T) {
	resp, err := DefaultHTTPClient().Do(mustReq(t, "http://169.254.169.254/latest/meta-data"))
	if err == nil {
		_ = resp.Body.Close()
		t.Fatal("metadata address must be refused")
	}
}

func mustReq(t *testing.T, u string) *http.Request {
	t.Helper()
	req, err := http.NewRequestWithContext(context.Background(), http.MethodGet, u, nil)
	if err != nil {
		t.Fatal(err)
	}
	return req
}
