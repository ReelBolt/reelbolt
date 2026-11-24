package daemon

import (
	"context"
	"encoding/json"
	"io"
	"strings"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/agent"
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/governor"
)

// fakeTransport is one socket, driven frame by frame.
type fakeTransport struct {
	frames [][]byte
	read   int
	closed bool
}

func (t *fakeTransport) Read(context.Context) ([]byte, error) {
	if t.read >= len(t.frames) {
		return nil, io.EOF
	}
	f := t.frames[t.read]
	t.read++
	return f, nil
}

func (t *fakeTransport) Write(context.Context, []byte) error { return nil }
func (t *fakeTransport) Subprotocol() string                 { return protocol.Subprotocol }
func (t *fakeTransport) Close(protocol.CloseCode, string) error {
	t.closed = true
	return nil
}

type fakeDialer struct{ transport *fakeTransport }

func (d fakeDialer) Dial(context.Context, string) (agent.Transport, error) {
	return d.transport, nil
}

func drainFrame(t *testing.T, reason string) []byte {
	t.Helper()
	env, err := protocol.New(protocol.TypeDrain, protocol.DrainPayload{Reason: reason, DeadlineSec: 5})
	if err != nil {
		t.Fatal(err)
	}
	raw, err := env.Encode()
	if err != nil {
		t.Fatal(err)
	}
	return raw
}

// The gateway's pause is a drain whose reason says so. It is one-way by
// construction: the worker agent has no way to clear its own draining state, so
// the daemon rebuilds the agent to resume. This test pins both halves.
func TestAGatewayPauseIsRecognisedAndAResumeAsksForAReconnect(t *testing.T) {
	d, _ := governorDaemon(t, pairedConfig(t), mediaOnlyResult(), runningMachine(), &agentFactory{})

	tr := &fakeTransport{frames: [][]byte{
		drainFrame(t, ControlPauseReason),
		drainFrame(t, ControlResumeReason),
	}}
	dialer := &controlDialer{inner: fakeDialer{transport: tr}, onDrain: d.onGatewayControl}
	transport, err := dialer.Dial(context.Background(), "wss://runner.reelbolt.ai/v1/connect")
	if err != nil {
		t.Fatal(err)
	}

	// The pause frame.
	frame, err := transport.Read(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if !d.gov.Status().Held || d.gov.Status().HeldBy != governor.SourceRemote {
		t.Fatalf("the gateway's pause was not applied: %+v", d.gov.Status())
	}
	// A remote hold keeps the connection: the gateway has to be able to reach
	// this runner to resume it.
	if d.gov.Status().Offline() {
		t.Fatal("a remote pause must not take the runner offline")
	}
	if len(frame) == 0 {
		t.Fatal("the frame must still reach the worker agent unchanged")
	}

	// The resume frame.
	if _, err := transport.Read(context.Background()); err != nil {
		t.Fatal(err)
	}
	if d.gov.Status().Held {
		t.Fatalf("the gateway's resume was not applied: %+v", d.gov.Status())
	}
	select {
	case <-d.reconnect:
	case <-time.After(time.Second):
		t.Fatal("a remote resume must ask the daemon to reconnect: the agent cannot un-drain itself")
	}
	if tr.closed {
		t.Error("nothing should have closed the socket")
	}
}

// The safety property, and the reason the reason is read at all: a drain the
// gateway sends for its own purposes — a deploy, a shutdown — must NOT be
// mistaken for a pause, or every gateway restart would silently park the fleet
// until somebody ran a command on each machine.
func TestAnUnrelatedGatewayDrainIsNotAPause(t *testing.T) {
	d, _ := governorDaemon(t, pairedConfig(t), mediaOnlyResult(), runningMachine(), &agentFactory{})
	dialer := &controlDialer{inner: fakeDialer{transport: &fakeTransport{frames: [][]byte{
		drainFrame(t, protocol.CancelShutdown),
		drainFrame(t, "deploy"),
		drainFrame(t, ""),
	}}}, onDrain: d.onGatewayControl}

	transport, err := dialer.Dial(context.Background(), "wss://runner.reelbolt.ai/v1/connect")
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 3; i++ {
		if _, err := transport.Read(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if !d.gov.Allowed() || d.gov.Status().Held {
		t.Fatalf("an ordinary drain paused the runner: %+v", d.gov.Status())
	}
	select {
	case <-d.reconnect:
		t.Fatal("an ordinary drain must not ask for a reconnect")
	default:
	}
}

// Anything that is not a drain is passed straight through, and anything this
// build cannot parse is left to the library — which has its own rules for a
// malformed frame, and must keep them.
func TestTheObserverOnlyActsOnDrains(t *testing.T) {
	d, _ := governorDaemon(t, pairedConfig(t), mediaOnlyResult(), runningMachine(), &agentFactory{})
	other, err := protocol.New(protocol.TypeHeartbeat, protocol.HeartbeatPayload{ServerTime: time.Now().Format(time.RFC3339)})
	if err != nil {
		t.Fatal(err)
	}
	raw, err := other.Encode()
	if err != nil {
		t.Fatal(err)
	}
	frames := [][]byte{raw, []byte("{not a frame"), []byte("{}"), drainFrame(t, ControlPauseReason)}
	tr := &fakeTransport{frames: frames}
	transport := &controlTransport{inner: tr, onDrain: d.onGatewayControl}

	for i, want := range frames {
		got, err := transport.Read(context.Background())
		if err != nil {
			t.Fatalf("frame %d: %v", i, err)
		}
		if string(got) != string(want) {
			t.Fatalf("frame %d was modified: %s", i, got)
		}
	}
	// Only the last one is a pause.
	if !d.gov.Status().Held {
		t.Fatalf("the drain was not seen: %+v", d.gov.Status())
	}
}

// A dialer that fails must fail, not be silently swallowed: the agent's own
// reconnect policy is the thing that handles it.
func TestTheObserverPropagatesADialFailure(t *testing.T) {
	dialer := &controlDialer{inner: failingDialer{}, onDrain: func(string) {}}
	if _, err := dialer.Dial(context.Background(), "wss://x"); err == nil {
		t.Fatal("a failed dial must be reported")
	}
}

type failingDialer struct{}

func (failingDialer) Dial(context.Context, string) (agent.Transport, error) {
	return nil, io.ErrUnexpectedEOF
}

// The pause a gateway sends is reported in full, so a support conversation can
// say who did it.
func TestARemotePauseSaysWhoAndWhy(t *testing.T) {
	d, _ := governorDaemon(t, pairedConfig(t), mediaOnlyResult(), runningMachine(), &agentFactory{})
	d.onGatewayControl(ControlPauseReason)
	st := d.gov.Status()
	if !st.Held || st.HeldBy != governor.SourceRemote || !strings.Contains(st.HeldReason, "dashboard") {
		t.Fatalf("status = %+v", st)
	}
	// And one command releases it, whoever placed it.
	if !d.Resume() {
		t.Fatal("Resume must release a hold the gateway placed")
	}
}

var _ = json.Marshal
