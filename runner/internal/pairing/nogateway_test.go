package pairing

import (
	"context"
	"errors"
	"net/http/httptest"
	"strings"
	"testing"
)

// An approval that carries a device but no gateway address (the server has no
// RUNNER_GATEWAY_URL) is ErrNoGateway, not the generic "incomplete" error: the
// fault is on the service side and the message must say so.
func TestPollMapsAnApprovalWithoutAGatewayToErrNoGateway(t *testing.T) {
	api := &fakeAPI{answers: []pollAnswer{
		{status: 200, result: map[string]string{"deviceId": "device-uuid", "organizationId": "org-uuid", "gatewayUrl": ""}},
	}}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	p, err := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	if err != nil {
		t.Fatalf("Start: %v", err)
	}
	res, err := c.Poll(context.Background(), p)
	if !errors.Is(err, ErrNoGateway) {
		t.Fatalf("Poll = (%v, %v), want ErrNoGateway", res, err)
	}
	if res != nil {
		t.Errorf("Poll returned a result alongside ErrNoGateway: %+v", res)
	}
	if strings.Contains(err.Error(), "incomplete") {
		t.Errorf("ErrNoGateway reads like a client bug: %q", err)
	}
}

// A missing device id is still the generic incomplete-response error: the new
// branch must not swallow it.
func TestPollStillRejectsAnApprovalWithoutADevice(t *testing.T) {
	api := &fakeAPI{answers: []pollAnswer{
		{status: 200, result: map[string]string{"deviceId": "", "gatewayUrl": "wss://runner.reelbolt.ai/v1/connect"}},
	}}
	srv := httptest.NewServer(api.handler())
	defer srv.Close()
	c, _ := newClient(t, srv.URL)
	p, err := c.Start(context.Background(), Request{PublicKey: testPub(t)})
	if err != nil {
		t.Fatalf("Start: %v", err)
	}
	_, err = c.Poll(context.Background(), p)
	if err == nil || errors.Is(err, ErrNoGateway) || !strings.Contains(err.Error(), "incomplete") {
		t.Fatalf("Poll = %v, want the incomplete-response error", err)
	}
}
