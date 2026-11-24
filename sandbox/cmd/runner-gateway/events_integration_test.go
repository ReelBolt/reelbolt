package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"testing"
	"time"

	"github.com/google/uuid"
	amqp "github.com/rabbitmq/amqp091-go"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// TestRevocationConsumerDropsARevokedDevice exercises the fast path against a
// REAL broker. It runs only when RUNNER_GATEWAY_TEST_RABBITMQ_URL is set:
//
//	docker run --rm -d --name reelbolt-d5b-rmq -e RABBITMQ_DEFAULT_USER=guest \
//	  -e RABBITMQ_DEFAULT_PASS=guest -p 55672:5672 rabbitmq:3-management-alpine
//	RUNNER_GATEWAY_TEST_RABBITMQ_URL='amqp://guest:guest@host.docker.internal:55672/' \
//	  go test ./cmd/runner-gateway/ -run TestRevocationConsumerDropsARevokedDevice -v
//
// The point of running it against a broker rather than a stub is the CROSS-WP
// contract: it publishes the envelope exactly as E4's Go publisher does
// (api/services/rabbitmq_service.go — the fanout
// "ReelBolt.Shared.IntegrationEvents:RunnerDeviceRevoked" carrying the
// MassTransit envelope) and asserts that this gateway reacts. A name mismatch
// would leave the test hanging on a delivery that never arrives.
func TestRevocationConsumerDropsARevokedDevice(t *testing.T) {
	brokerURL := os.Getenv("RUNNER_GATEWAY_TEST_RABBITMQ_URL")
	if brokerURL == "" {
		t.Skip("set RUNNER_GATEWAY_TEST_RABBITMQ_URL to run the revocation consumer test")
	}
	qg := newQueueGateway(t, func(cfg *appConfig) { cfg.RabbitMQURL = brokerURL })
	r := qg.newQueueRunner(t, testOrgA)
	view := qg.createJob(t, map[string]any{
		"orgId": testOrgA, "executionId": testExecA,
		"jobType": protocol.JobVideoCompile, "target": targetOrgRunner,
	})
	ws, _ := qg.handshake(t, r)
	send(t, ws, mustEnvelope(t, protocol.TypeReady, protocol.ReadyPayload{FreeSlots: 1}))
	assign := readPayload[protocol.AssignPayload](t, readEnvelope(t, ws))
	send(t, ws, jobEnvelope(t, protocol.TypeAccept, view.JobID, protocol.AcceptPayload{
		LeaseEpoch: assign.LeaseEpoch, JobToken: assign.JobToken,
	}))
	waitForStatus(t, qg, view.JobID, jobRunning)

	// The gateway's consumer is started with the service; a consumer that cannot
	// reach the broker retries with backoff, so the publish below is repeated
	// until the socket drops or the test gives up.
	deadline := time.Now().Add(20 * time.Second)
	closed := make(chan protocol.CloseCode, 1)
	go func() {
		_, code := readUntilClose(t, ws)
		closed <- code
	}()
	for time.Now().Before(deadline) {
		publishRevocation(t, brokerURL, RunnerDeviceRevokedEvent{
			DeviceID: r.deviceID, OrganizationID: testOrgA, RevokedAt: time.Now().UTC(),
		})
		select {
		case code := <-closed:
			if code != protocol.CloseUnauthenticated {
				t.Fatalf("close code = %d, want 4401", code)
			}
			if status := qg.jobFor(t, view.JobID).Status; status != jobExpired {
				t.Errorf("status = %q, want Expired after the revocation event", status)
			}
			return
		case <-time.After(time.Second):
		}
	}
	t.Fatal("the gateway never dropped the socket after a RunnerDeviceRevoked event")
}

// publishRevocation reproduces E4's publisher: the same fanout exchange, the same
// MassTransit envelope shape (messageId, messageType urn, message).
func publishRevocation(t *testing.T, url string, event RunnerDeviceRevokedEvent) {
	t.Helper()
	conn, err := amqp.DialConfig(url, amqp.Config{Dial: amqp.DefaultDial(5 * time.Second)})
	if err != nil {
		t.Fatalf("dial the broker: %v", err)
	}
	defer func() { _ = conn.Close() }()
	ch, err := conn.Channel()
	if err != nil {
		t.Fatalf("channel: %v", err)
	}
	defer func() { _ = ch.Close() }()
	if err := ch.ExchangeDeclare(exchangeRunnerDeviceRevoked, "fanout", true, false, false, false, nil); err != nil {
		t.Fatalf("exchange declare: %v", err)
	}
	body, err := json.Marshal(struct {
		MessageID   string   `json:"messageId"`
		MessageType []string `json:"messageType"`
		Message     any      `json:"message"`
	}{
		MessageID:   uuid.NewString(),
		MessageType: []string{"urn:message:" + exchangeRunnerDeviceRevoked},
		Message:     event,
	})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := ch.PublishWithContext(ctx, exchangeRunnerDeviceRevoked, "", false, false, amqp.Publishing{
		ContentType: "application/json",
		Body:        body,
	}); err != nil {
		t.Fatalf("publish: %v", err)
	}
}

// TestExchangeNameMatchesThePublisher pins the cross-WP contract without a
// broker: if E4 ever renames its exchange, this is the test that says so.
func TestExchangeNameMatchesThePublisher(t *testing.T) {
	const want = "ReelBolt.Shared.IntegrationEvents:RunnerDeviceRevoked"
	if exchangeRunnerDeviceRevoked != want {
		t.Fatalf("exchangeRunnerDeviceRevoked = %q, want %q", exchangeRunnerDeviceRevoked, want)
	}
	var event RunnerDeviceRevokedEvent
	raw := []byte(fmt.Sprintf(`{"deviceId":%q,"organizationId":%q,"revokedAt":"2026-10-08T09:00:00Z"}`,
		uuid.NewString(), testOrgA))
	if err := json.Unmarshal(raw, &event); err != nil {
		t.Fatalf("the E4 event shape does not decode: %v", err)
	}
}
