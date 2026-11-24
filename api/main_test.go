package main

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"net"
	"net/http"
	"strings"
	"testing"
	"time"
)

// API-GO-08: the server bounds a slow or idle client, and has no write deadline.
func TestNewServer_SetsTheClientTimeouts(t *testing.T) {
	srv := newServer(http.NotFoundHandler())
	if srv.ReadHeaderTimeout <= 0 || srv.ReadTimeout <= 0 || srv.IdleTimeout <= 0 {
		t.Fatalf("timeouts not set: header %s, read %s, idle %s", srv.ReadHeaderTimeout, srv.ReadTimeout, srv.IdleTimeout)
	}
	if srv.WriteTimeout != 0 {
		t.Fatalf("WriteTimeout = %s: it would cut every SSE stream", srv.WriteTimeout)
	}
}

// serveLoopback serves srv on a loopback port for the life of the test.
func serveLoopback(t *testing.T, srv *http.Server) string {
	t.Helper()
	ln, err := (&net.ListenConfig{}).Listen(context.Background(), "tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() { _ = srv.Serve(ln) }()
	t.Cleanup(func() { _ = srv.Close() })
	return ln.Addr().String()
}

// A client that connects and never finishes its request headers is dropped at the header
// deadline rather than held for ever.
func TestNewServer_DropsAClientThatNeverSendsItsHeaders(t *testing.T) {
	srv := newServer(http.NotFoundHandler())
	srv.ReadHeaderTimeout = 100 * time.Millisecond // the production value, shortened
	addr := serveLoopback(t, srv)

	conn, err := (&net.Dialer{}).DialContext(context.Background(), "tcp", addr)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = conn.Close() }()
	if _, err := conn.Write([]byte("GET / HTTP/1.1\r\nHost: x\r\n")); err != nil { // no final CRLF
		t.Fatal(err)
	}
	_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	buf := make([]byte, 512)
	for {
		if _, err := conn.Read(buf); err != nil {
			var ne net.Error
			if errors.As(err, &ne) && ne.Timeout() {
				t.Fatal("the server kept a header-less connection open")
			}
			return // closed by the server
		}
	}
}

// An SSE stream keeps flowing long past every timeout the server sets: the read deadlines end
// once the request is read, and there is no write deadline.
func TestNewServer_AStreamOutlivesTheTimeouts(t *testing.T) {
	const events = 8
	srv := newServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		for i := 0; i < events; i++ {
			fmt.Fprintf(w, "data: %d\n\n", i)
			w.(http.Flusher).Flush()
			time.Sleep(60 * time.Millisecond)
		}
	}))
	// The production timeouts, shortened so the stream (about 480 ms) outlasts all of them.
	srv.ReadHeaderTimeout, srv.ReadTimeout, srv.IdleTimeout = 50*time.Millisecond, 100*time.Millisecond, 100*time.Millisecond
	addr := serveLoopback(t, srv)

	req, err := http.NewRequestWithContext(context.Background(), http.MethodGet, "http://"+addr+"/events", nil)
	if err != nil {
		t.Fatal(err)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = resp.Body.Close() }()
	got := 0
	sc := bufio.NewScanner(resp.Body)
	for sc.Scan() {
		if strings.HasPrefix(sc.Text(), "data: ") {
			got++
		}
	}
	if got != events {
		t.Fatalf("the stream was cut: %d of %d events (%v)", got, events, sc.Err())
	}
}
