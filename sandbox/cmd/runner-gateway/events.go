package main

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"time"

	"github.com/google/uuid"
	amqp "github.com/rabbitmq/amqp091-go"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// This file consumes the revocation event E4 publishes.
//
// The exchange name is the MassTransit fanout E4's Go publisher declares
// (api/services/rabbitmq_service.go, "ReelBolt.Shared.IntegrationEvents:RunnerDeviceRevoked"),
// and the body is the envelope the existing Go consumer already unwraps. A bare
// "RunnerDeviceRevoked" exchange name here would silently receive nothing: the
// fast path would never fire, and only the 60-second revoked_at re-check would
// drop a revoked device's socket.
//
// The event is a FAST PATH, never the authority. E4 publishes it off the request
// path (a broker outage must not delay a revocation), so this consumer is
// best-effort and idempotent: a lost message costs at most 60 seconds, and a
// duplicate is harmless because the sweep re-reads revoked_at and the job
// release is fenced by the epoch. A gateway that cannot reach the broker at all
// still revokes correctly through that re-check alone.

// exchangeRunnerDeviceRevoked is the fanout E4 publishes revocations on.
const exchangeRunnerDeviceRevoked = "ReelBolt.Shared.IntegrationEvents:RunnerDeviceRevoked"

// massTransitEnvelope is the shape MassTransit publishes: the payload rides
// inside "message", with the .NET type in "messageType".
type massTransitEnvelope struct {
	MessageID   string          `json:"messageId"`
	MessageType []string        `json:"messageType"`
	Message     json.RawMessage `json:"message"`
}

// RunnerDeviceRevokedEvent is the revocation announcement.
type RunnerDeviceRevokedEvent struct {
	DeviceID        string    `json:"deviceId"`
	OrganizationID  string    `json:"organizationId"`
	RevokedByUserID string    `json:"revokedByUserId,omitempty"`
	RevokedAt       time.Time `json:"revokedAt"`
}

// decodeRevocation unwraps a MassTransit envelope into the revocation event.
//
// It accepts the payload both wrapped and bare: a wrapped body is what
// MassTransit and the Go publisher in api/services/rabbitmq_service.go send, and
// a bare object is what a non-MassTransit producer would send. Being liberal
// here costs nothing — the event only names a device id that must already be
// revoked in the database for anything to happen.
func decodeRevocation(body []byte) (RunnerDeviceRevokedEvent, error) {
	var event RunnerDeviceRevokedEvent
	var envelope massTransitEnvelope
	if err := json.Unmarshal(body, &envelope); err == nil && len(envelope.Message) > 0 {
		if err := json.Unmarshal(envelope.Message, &event); err != nil {
			return RunnerDeviceRevokedEvent{}, err
		}
	} else if err := json.Unmarshal(body, &event); err != nil {
		return RunnerDeviceRevokedEvent{}, err
	}
	if _, err := uuid.Parse(event.DeviceID); err != nil {
		return RunnerDeviceRevokedEvent{}, errors.New("revocation event has no usable deviceId")
	}
	return event, nil
}

// runRevocationConsumer keeps a consumer on the revocation fanout until the
// context is cancelled, reconnecting with backoff.
func (g *gateway) runRevocationConsumer(ctx context.Context) {
	url := g.cfg.RabbitMQURL
	if url == "" {
		log.Printf("RABBITMQ_URL is not set: the RunnerDeviceRevoked fast path is off and revocation " +
			"relies on the 60 s revoked_at re-check alone")
		return
	}
	backoff := time.Second
	for ctx.Err() == nil {
		err := g.consumeRevocations(ctx, url)
		if ctx.Err() != nil {
			return
		}
		g.metrics.revocationEventReconnects.Inc()
		log.Printf("revocation consumer: %v, retrying in %s", err, backoff)
		select {
		case <-ctx.Done():
			return
		case <-time.After(backoff):
		}
		if backoff < 30*time.Second {
			backoff *= 2
		}
	}
}

// consumeRevocations holds one connection and one consumer until it fails.
func (g *gateway) consumeRevocations(ctx context.Context, url string) error {
	conn, err := amqp.DialConfig(url, amqp.Config{Dial: amqp.DefaultDial(5 * time.Second)})
	if err != nil {
		return err
	}
	defer func() { _ = conn.Close() }()

	ch, err := conn.Channel()
	if err != nil {
		return err
	}
	defer func() { _ = ch.Close() }()

	// Declared durable and non-auto-delete to match the publisher, which declares
	// it before any consumer exists. Declaring it identically is what makes the
	// two sides agree on the exchange.
	if err := ch.ExchangeDeclare(exchangeRunnerDeviceRevoked, "fanout", true, false, false, false, nil); err != nil {
		return err
	}
	// Every gateway replica needs every revocation, so the queue is
	// server-named, exclusive and auto-delete: the fanout copies one event to
	// each replica's own queue, and a replica that dies takes its queue with it.
	queue, err := ch.QueueDeclare("", false, true, true, false, nil)
	if err != nil {
		return err
	}
	if err := ch.QueueBind(queue.Name, "", exchangeRunnerDeviceRevoked, false, nil); err != nil {
		return err
	}
	deliveries, err := ch.Consume(queue.Name, "", false, false, false, false, nil)
	if err != nil {
		return err
	}

	closed := conn.NotifyClose(make(chan *amqp.Error, 1))
	log.Printf("revocation consumer: listening on %s", exchangeRunnerDeviceRevoked)
	for {
		select {
		case <-ctx.Done():
			return nil
		case err := <-closed:
			if err == nil {
				return errors.New("connection closed")
			}
			return err
		case delivery, ok := <-deliveries:
			if !ok {
				return errors.New("delivery channel closed")
			}
			event, err := decodeRevocation(delivery.Body)
			if err != nil {
				// A message this gateway cannot read is dead-lettered rather
				// than requeued: retrying it would spin forever.
				g.metrics.revocationEvents.WithLabelValues("malformed").Inc()
				log.Printf("revocation consumer: ignoring an unreadable message: %v", err)
				_ = delivery.Nack(false, false)
				continue
			}
			g.metrics.revocationEvents.WithLabelValues("received").Inc()
			g.handleRevokedDevice(ctx, event.DeviceID, "event")
			_ = delivery.Ack(false)
		}
	}
}

// handleRevokedDevice drops a revoked device's socket and disposes of its jobs.
// Both the event and the periodic re-check call it, so a device revoked either
// way is treated identically: cancel the jobs, say goodbye, close with 4401.
func (g *gateway) handleRevokedDevice(ctx context.Context, deviceID, source string) {
	c := g.registry.get(deviceID)
	if c == nil {
		return
	}
	if !c.beginRevoke() {
		// Already being revoked: the re-check and the event race by design, and
		// the first one to arrive does the work.
		return
	}
	log.Printf("%s: device revoked (%s), dropping the connection", c.logTag, source)
	g.metrics.revocationEvents.WithLabelValues("dropped").Inc()
	// Closed from another goroutine: coder/websocket's Close waits for the peer's
	// close frame, and one unresponsive runner must not hold a sweep.
	go func(c *conn) {
		byeCtx, byeCancel := context.WithTimeout(context.Background(), g.writeTimeout)
		defer byeCancel()
		// "the gateway sends cancel (reason revoked) for each of its jobs, sends
		// bye, closes with 4401, and handles the jobs as lease expiry."
		g.cancelRunnerWork(byeCtx, c, protocol.CancelRevoked)
		_ = c.sendPayload(byeCtx, protocol.TypeBye, protocol.ByePayload{Reason: protocol.CancelRevoked})
		g.metrics.countClose(int(protocol.CloseUnauthenticated))
		c.closeWith(protocol.CloseUnauthenticated, "device revoked")
	}(c)
}
