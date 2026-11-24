package services

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/google/uuid"
	amqp "github.com/rabbitmq/amqp091-go"
	"github.com/vecchiotom/reelbolt/config"
)

// WorkflowEvent is the structure broadcast to SSE clients.
//
// Exactly one of ExecutionID / ProjectID identifies what the event is about, and which one it is
// varies by event type: every workflow event is scoped by its execution, while the media-derivative
// progress events have no execution at all (a derivative is not a workflow step) and are scoped by
// their project. The SSE handler's visibility check reads both, so a new event type must populate
// one of them — an event carrying neither is dropped rather than broadcast unscoped.
type WorkflowEvent struct {
	Type        string          `json:"type"`
	ExecutionID string          `json:"executionId,omitempty"`
	ProjectID   string          `json:"projectId,omitempty"`
	Timestamp   time.Time       `json:"timestamp"`
	Data        json.RawMessage `json:"data"`
}

// SSEHub manages a set of SSE subscriber channels and broadcasts events to them.
type SSEHub struct {
	mu                sync.RWMutex
	clients           map[chan WorkflowEvent]string
	connectionsByUser map[string]int
}

var Hub = &SSEHub{
	clients:           make(map[chan WorkflowEvent]string),
	connectionsByUser: make(map[string]int),
}

const maxConnectionsPerUser = 3

// HubScopeKey is the connection-limit scope of a user's workflow event stream.
// It includes the organization so the per-user cap applies per (org, user): an
// org switch is a new token and a new stream, never a slot shared with the old.
func HubScopeKey(orgID uuid.UUID, userID string) string {
	return "org:" + orgID.String() + ":" + userID
}

// ExecutionHubScopeKey is HubScopeKey for single-execution streams.
func ExecutionHubScopeKey(orgID uuid.UUID, userID string) string {
	return "execution:" + HubScopeKey(orgID, userID)
}

// Subscribe returns a channel that will receive workflow events. Call Unsubscribe
// when the SSE connection closes.
func (h *SSEHub) Subscribe(scopeKey string) (chan WorkflowEvent, bool) {
	if scopeKey == "" {
		scopeKey = "anonymous"
	}

	ch := make(chan WorkflowEvent, 32)
	h.mu.Lock()
	if h.connectionsByUser[scopeKey] >= maxConnectionsPerUser {
		h.mu.Unlock()
		close(ch)
		return nil, false
	}
	h.clients[ch] = scopeKey
	h.connectionsByUser[scopeKey]++
	h.mu.Unlock()
	return ch, true
}

// Unsubscribe removes the channel and closes it.
func (h *SSEHub) Unsubscribe(ch chan WorkflowEvent) {
	h.mu.Lock()
	scopeKey, exists := h.clients[ch]
	if exists {
		delete(h.clients, ch)
		if count := h.connectionsByUser[scopeKey]; count <= 1 {
			delete(h.connectionsByUser, scopeKey)
		} else {
			h.connectionsByUser[scopeKey] = count - 1
		}
	}
	h.mu.Unlock()
	if exists {
		close(ch)
	}
}

// CloseAll ends every open stream by closing its channel: the SSE handlers return when their
// channel closes, so browsers reconnect (to the other instance) at once. Later Unsubscribe calls
// find nothing to do, so no channel is closed twice.
func (h *SSEHub) CloseAll() {
	h.mu.Lock()
	chans := make([]chan WorkflowEvent, 0, len(h.clients))
	for ch := range h.clients {
		chans = append(chans, ch)
	}
	h.clients = make(map[chan WorkflowEvent]string)
	h.connectionsByUser = make(map[string]int)
	h.mu.Unlock()
	for _, ch := range chans {
		close(ch)
	}
}

// broadcast sends an event to all connected SSE clients (non-blocking).
func (h *SSEHub) broadcast(event WorkflowEvent) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	for ch := range h.clients {
		select {
		case ch <- event:
		default:
			// Drop event for slow consumers rather than blocking.
		}
	}
}

// MassTransit fanout exchange names (full .NET type name with colon separator).
const (
	exchangeExecutionRunning = "ReelBolt.Shared.IntegrationEvents:WorkflowExecutionRunning"
	exchangeCompleted        = "ReelBolt.Shared.IntegrationEvents:WorkflowExecutionCompleted"
	exchangeFailed           = "ReelBolt.Shared.IntegrationEvents:WorkflowExecutionFailed"
	exchangeStepStarted      = "ReelBolt.Shared.IntegrationEvents:WorkflowStepStarted"
	exchangeStepComplete     = "ReelBolt.Shared.IntegrationEvents:WorkflowStepCompleted"
	exchangeStepToolCalled   = "ReelBolt.Shared.IntegrationEvents:WorkflowStepToolCalled"
	exchangeStepReasoning    = "ReelBolt.Shared.IntegrationEvents:WorkflowStepReasoningCaptured"
	// Ephemeral progress signal for long-running steps (VideoAnalyze/VideoCompile) — see
	// WorkflowStepProgress's doc comment in ReelBolt.Shared.IntegrationEvents. Relayed the same
	// way as every other workflow event here; never persisted, so there is no corresponding DB
	// model or migration on either side.
	exchangeStepProgress = "ReelBolt.Shared.IntegrationEvents:WorkflowStepProgress"
	// Append-only, sequence-numbered chat turn published once per completed turn in a
	// StepType.EditRoom group-chat run — see WorkflowStepChatTurn's doc comment in
	// ReelBolt.Shared.IntegrationEvents.
	exchangeStepChatTurn = "ReelBolt.Shared.IntegrationEvents:WorkflowStepChatTurn"
	// Ephemeral, supersedable progress for one media-derivative production run — see
	// MediaDerivativeProgress's doc comment in ReelBolt.Shared.IntegrationEvents. Deliberately NOT
	// WorkflowStepProgress: a derivative has no execution id, step id or step-result id, so
	// publishing it under that type would render a phantom step card in the workflow monitor. Its
	// payload is PROJECTED before it reaches a browser rather than relayed verbatim — see
	// projectMediaDerivativeProgress.
	exchangeMediaDerivativeProgress = "ReelBolt.Shared.IntegrationEvents:MediaDerivativeProgress"

	// The one exchange this service PUBLISHES to. The Go runner gateway consumes it
	// to drop a revoked device's socket and requeue its in-flight jobs (WP E4b step 5,
	// docs/runner-protocol.md "Connection handshake and authentication"). Named with
	// the MassTransit prefix, so a future .NET consumer needs no new exchange.
	exchangeRunnerDeviceRevoked = "ReelBolt.Shared.IntegrationEvents:RunnerDeviceRevoked"

	// Published by the Inference API when a user adds a project file or edits a
	// thumbnail graphic in the image editor; consumed here to mail the workspace
	// members who opted in (docs/notifications.md).
	exchangeUserActivity = "ReelBolt.Shared.IntegrationEvents:UserActivityNotificationRequested"

	queueName = "go-api-workflow-events"

	// privateQueuePrefix names each process's exclusive auto-delete Hub queue.
	privateQueuePrefix = "go-api-sse"

	// eventTypeMediaDerivativeProgress is the SSE `event:` name this consumer relays the exchange
	// above as. Named once, because the dispatch and the payload projection must agree on it.
	eventTypeMediaDerivativeProgress = "media.derivative.progress"
)

// RunnerDeviceRevokedEvent is the revocation announcement. It carries no secret:
// the device id is what the gateway matches its live sockets on, and the org is for
// the requeue decision.
type RunnerDeviceRevokedEvent struct {
	DeviceID        string    `json:"deviceId"`
	OrganizationID  string    `json:"organizationId"`
	RevokedByUserID string    `json:"revokedByUserId,omitempty"`
	RevokedAt       time.Time `json:"revokedAt"`
}

// massTransitEnvelope is the shape MassTransit publishes and the shape the Go
// consumer in this file already unwraps, kept identical so both sides agree.
type massTransitEnvelope struct {
	MessageID   string   `json:"messageId"`
	MessageType []string `json:"messageType"`
	Message     any      `json:"message"`
}

// PublishRunnerDeviceRevoked announces a revocation to the runner gateway.
//
// Best effort by design: the database write is the authority, and an unreachable
// broker must never fail a revocation. The gateway's 60-second revoked_at re-check
// (docs/runner-protocol.md) is the backstop for anything lost here, which is why
// the caller logs rather than returns this error to the user.
func PublishRunnerDeviceRevoked(ctx context.Context, evt RunnerDeviceRevokedEvent) error {
	url := fmt.Sprintf("amqp://%s:%s@%s:%s/",
		config.Cfg.RabbitMQUsername, config.Cfg.RabbitMQPassword,
		config.Cfg.RabbitMQHost, config.Cfg.RabbitMQPort)

	conn, err := amqp.DialConfig(url, amqp.Config{Dial: amqp.DefaultDial(5 * time.Second)})
	if err != nil {
		return fmt.Errorf("dial: %w", err)
	}
	defer func() { _ = conn.Close() }()

	ch, err := conn.Channel()
	if err != nil {
		return fmt.Errorf("channel: %w", err)
	}
	defer func() { _ = ch.Close() }()

	// Declared here as well as by the consumer, so publishing before the gateway has
	// ever started still lands on a durable exchange.
	if err := ch.ExchangeDeclare(exchangeRunnerDeviceRevoked, "fanout", true, false, false, false, nil); err != nil {
		return fmt.Errorf("exchange declare: %w", err)
	}

	id, err := uuid.NewRandom()
	if err != nil {
		return fmt.Errorf("message id: %w", err)
	}
	body, err := json.Marshal(massTransitEnvelope{
		MessageID:   id.String(),
		MessageType: []string{"urn:message:" + exchangeRunnerDeviceRevoked},
		Message:     evt,
	})
	if err != nil {
		return fmt.Errorf("marshal: %w", err)
	}

	if err := ch.PublishWithContext(ctx, exchangeRunnerDeviceRevoked, "", false, false, amqp.Publishing{
		ContentType:  "application/json",
		DeliveryMode: amqp.Persistent,
		MessageId:    id.String(),
		Body:         body,
	}); err != nil {
		return fmt.Errorf("publish: %w", err)
	}
	return nil
}

// StartRabbitMQConsumer connects to RabbitMQ, binds to the workflow event
// exchanges published by the WorkflowEngine, and dispatches events to Hub.
// It reconnects automatically on connection loss.
func StartRabbitMQConsumer() {
	consumerStarted.Store(true)
	go func() {
		defer close(consumerDone)
		for {
			err := runConsumer()
			select {
			case <-consumerStop:
				return
			default:
			}
			if err != nil {
				log.Printf("[rabbitmq] consumer error: %v – reconnecting in 5s", err)
				select {
				case <-time.After(5 * time.Second):
				case <-consumerStop:
					return
				}
			}
		}
	}()
}

var (
	consumerStop     = make(chan struct{})
	consumerDone     = make(chan struct{})
	consumerStopOnce sync.Once
	consumerStarted  atomic.Bool
	// consumerConnectedBefore is set by the first successful consume (see afterConsumerStart).
	consumerConnectedBefore atomic.Bool
	// notifyWG counts notification goroutines started from shared deliveries, so shutdown can
	// wait for a mail that is in flight instead of losing it at exit.
	notifyWG sync.WaitGroup
)

// afterConsumerStart handles a consumer that is back after a broker reconnect. The private queue
// did not exist during the gap, so events published then are gone, and a browser whose stream
// stayed open would never learn it. Ending the streams makes each browser reconnect, which is
// what triggers the run page's resync (web/lib/hooks/use-resync-tick.ts). The first start has no
// gap and no streams worth ending.
func afterConsumerStart() {
	if consumerConnectedBefore.Swap(true) {
		Hub.CloseAll()
	}
}

// StopRabbitMQConsumer stops consuming and closes the AMQP connection, bounded by ctx. Call it
// before the HTTP drain: the shared queue is consumed with auto-ack, so a delivery handed to a
// dying process is gone from the broker. Deliveries already buffered in the client when the
// consumers are cancelled are still dispatched before it returns. Safe to call when the consumer
// was never started (it then returns at once).
func StopRabbitMQConsumer(ctx context.Context) {
	consumerStopOnce.Do(func() { close(consumerStop) })
	if !consumerStarted.Load() {
		return
	}
	select {
	case <-consumerDone:
	case <-ctx.Done():
		log.Print("[rabbitmq] consumer did not stop before the deadline")
	}
}

// WaitNotifications waits, bounded by ctx, for notification goroutines started from shared
// deliveries. Call it after StopRabbitMQConsumer so no new ones start.
func WaitNotifications(ctx context.Context) {
	done := make(chan struct{})
	go func() { notifyWG.Wait(); close(done) }()
	select {
	case <-done:
	case <-ctx.Done():
		log.Print("[rabbitmq] notifications still in flight at the shutdown deadline")
	}
}

func runConsumer() error {
	url := fmt.Sprintf("amqp://%s:%s@%s:%s/",
		config.Cfg.RabbitMQUsername,
		config.Cfg.RabbitMQPassword,
		config.Cfg.RabbitMQHost,
		config.Cfg.RabbitMQPort,
	)

	conn, err := amqp.Dial(url)
	if err != nil {
		return fmt.Errorf("dial: %w", err)
	}
	defer conn.Close()

	ch, err := conn.Channel()
	if err != nil {
		return fmt.Errorf("channel: %w", err)
	}
	defer ch.Close()

	// Shared durable queue: a COMPETING consumer, so work that must happen once across
	// every live instance (mail, run-terminal notifications) is done by exactly one.
	q, err := ch.QueueDeclare(queueName, true, false, false, false, nil)
	if err != nil {
		return fmt.Errorf("queue declare: %w", err)
	}

	// Private queue: exclusive and auto-delete, one per process and per connection, so every
	// instance sees every event and each SSE client of that instance loses none.
	privateName := newPrivateQueueName()
	pq, err := ch.QueueDeclare(privateName, false, true, true, false, nil)
	if err != nil {
		return fmt.Errorf("private queue declare: %w", err)
	}

	// MassTransit creates these exchanges when the WorkflowEngine publishes the first event;
	// declare them here as well so the bindings are idempotent even if we start before the engine.
	for _, exchange := range allExchanges() {
		if err := ch.ExchangeDeclare(exchange, "fanout", true, false, false, false, nil); err != nil {
			return fmt.Errorf("exchange declare %q: %w", exchange, err)
		}
	}
	for _, exchange := range hubExchanges() {
		if err := ch.QueueBind(pq.Name, "", exchange, false, nil); err != nil {
			return fmt.Errorf("private queue bind %q: %w", exchange, err)
		}
	}
	for _, exchange := range sharedExchanges() {
		if err := ch.QueueBind(q.Name, "", exchange, false, nil); err != nil {
			return fmt.Errorf("queue bind %q: %w", exchange, err)
		}
	}
	// The shared queue used to be bound to the Hub-only exchanges too; stop it accumulating.
	for _, exchange := range hubOnlyExchanges() {
		unbindShared(conn, q.Name, exchange)
	}

	const sharedTag, privateTag = "reelbolt-api-shared", "reelbolt-api-private"
	msgs, err := ch.Consume(q.Name, sharedTag, true, false, false, false, nil)
	if err != nil {
		return fmt.Errorf("consume: %w", err)
	}
	// exclusive: nobody else may consume this process's queue. noLocal is a RabbitMQ no-op and
	// stays false.
	privMsgs, err := ch.Consume(pq.Name, privateTag, true, true, false, false, nil)
	if err != nil {
		return fmt.Errorf("private consume: %w", err)
	}

	log.Printf("[rabbitmq] workflow event consumer started (private queue %s)", pq.Name)
	afterConsumerStart()

	connClose := conn.NotifyClose(make(chan *amqp.Error, 1))

	for {
		select {
		case <-consumerStop:
			// Stop taking deliveries, then dispatch what the client already holds (auto-acked,
			// so it exists nowhere else). The deferred closes drop the connection.
			_ = ch.Cancel(sharedTag, false)
			_ = ch.Cancel(privateTag, false)
			flush := time.After(3 * time.Second)
			for msgs != nil || privMsgs != nil {
				select {
				case m, ok := <-msgs:
					if !ok {
						msgs = nil
						continue
					}
					dispatchMessage(m, false)
				case m, ok := <-privMsgs:
					if !ok {
						privMsgs = nil
						continue
					}
					dispatchMessage(m, true)
				case <-flush:
					return nil
				}
			}
			return nil

		case err := <-connClose:
			if err != nil {
				return fmt.Errorf("connection closed: %w", err)
			}
			return nil

		case msg, ok := <-msgs:
			if !ok {
				return fmt.Errorf("message channel closed")
			}
			dispatchMessage(msg, false)

		case msg, ok := <-privMsgs:
			if !ok {
				return fmt.Errorf("private message channel closed")
			}
			dispatchMessage(msg, true)
		}
	}
}

// newPrivateQueueName names this process's exclusive queue: hostname plus a random suffix.
func newPrivateQueueName() string {
	host, err := os.Hostname()
	if err != nil || host == "" {
		host = "unknown"
	}
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return fmt.Sprintf("%s-%s-%d", privateQueuePrefix, host, time.Now().UnixNano())
	}
	return privateQueuePrefix + "-" + host + "-" + hex.EncodeToString(b)
}

// hubOnlyExchanges are relayed to SSE clients and nothing else: private queue only.
func hubOnlyExchanges() []string {
	return []string{exchangeExecutionRunning, exchangeStepStarted, exchangeStepComplete, exchangeStepToolCalled, exchangeStepReasoning, exchangeStepProgress, exchangeStepChatTurn, exchangeMediaDerivativeProgress}
}

// hubExchanges are every exchange the private queue binds: Hub-only plus the run-terminal pair
// (which the Hub also relays).
func hubExchanges() []string {
	return append(hubOnlyExchanges(), exchangeCompleted, exchangeFailed)
}

// sharedExchanges are the ones with once-only work: run-terminal notifications and mail.
func sharedExchanges() []string {
	return []string{exchangeCompleted, exchangeFailed, exchangeUserActivity}
}

func allExchanges() []string {
	return append(hubExchanges(), exchangeUserActivity)
}

// unbindShared removes a legacy binding of the shared queue on its own channel, because a broker
// error would close a shared one. An already-unbound state is not an error, and anything else is
// only logged: the cost of failure is an accumulating queue, not a lost event.
func unbindShared(conn *amqp.Connection, queue, exchange string) {
	uch, err := conn.Channel()
	if err != nil {
		log.Printf("[rabbitmq] unbind %q: channel: %v", exchange, err)
		return
	}
	defer uch.Close()
	if err := uch.QueueUnbind(queue, "", exchange, nil); err != nil {
		log.Printf("[rabbitmq] unbind %q: %v (ignored)", exchange, err)
	}
}

// dispatchMessage routes one AMQP delivery. A private-queue delivery only feeds the SSE hub. A
// shared-queue delivery only does the once-only work: the notification dispatcher for run-terminal
// events and the email-only user-activity exchange.
func dispatchMessage(msg amqp.Delivery, private bool) {
	if !private {
		dispatchShared(msg)
		return
	}
	dispatchHub(msg)
}

func dispatchShared(msg amqp.Delivery) {
	switch msg.Exchange {
	case exchangeUserActivity:
		evt, ok := decodeUserActivityEvent(unwrapMassTransitMessage(msg.Body))
		if !ok {
			log.Print("[rabbitmq] user-activity event failed to decode; dropped")
			return
		}
		notifyWG.Add(1)
		go func() {
			defer notifyWG.Done()
			Notifications.HandleActivityEvent(context.Background(), evt)
		}()
	case exchangeCompleted, exchangeFailed:
		eventType := "execution.completed"
		if msg.Exchange == exchangeFailed {
			eventType = "execution.failed"
		}
		// The dispatcher resolves the recipient from the execution row and the caller's switches,
		// and never fails the event loop.
		if id, err := uuid.Parse(extractExecutionID(unwrapMassTransitMessage(msg.Body))); err == nil {
			notifyWG.Add(1)
			go func() {
				defer notifyWG.Done()
				Notifications.HandleWorkflowEvent(context.Background(), eventType, id)
			}()
		}
	}
}

func dispatchHub(msg amqp.Delivery) {
	if msg.Exchange == exchangeUserActivity {
		return
	}

	// Determine event type from the exchange name.
	var eventType string
	switch msg.Exchange {
	case exchangeExecutionRunning:
		eventType = "execution.running"
	case exchangeCompleted:
		eventType = "execution.completed"
	case exchangeFailed:
		eventType = "execution.failed"
	case exchangeStepStarted:
		eventType = "step.started"
	case exchangeStepComplete:
		eventType = "step.completed"
	case exchangeStepToolCalled:
		eventType = "step.tool-called"
	case exchangeStepReasoning:
		eventType = "step.reasoning"
	case exchangeStepProgress:
		eventType = "step.progress"
	case exchangeStepChatTurn:
		eventType = "step.chat-turn"
	case exchangeMediaDerivativeProgress:
		eventType = eventTypeMediaDerivativeProgress
	default:
		return
	}

	messagePayload := unwrapMassTransitMessage(msg.Body)

	if eventType == eventTypeMediaDerivativeProgress {
		projected, projectID, ok := projectMediaDerivativeProgress(messagePayload)
		if !ok {
			// No project id means nothing decides who may see it. Drop rather than broadcast a
			// frame that can no longer be scoped to its owner.
			return
		}

		Hub.broadcast(WorkflowEvent{
			Type:      eventType,
			ProjectID: projectID,
			Timestamp: time.Now().UTC(),
			Data:      projected,
		})
		return
	}

	Hub.broadcast(WorkflowEvent{
		Type:        eventType,
		ExecutionID: extractExecutionID(messagePayload),
		Timestamp:   time.Now().UTC(),
		Data:        messagePayload,
	})
}

// decodeUserActivityEvent unwraps the Inference API's activity message. The
// MassTransit envelope is unwrapped first; a payload that is not the expected
// shape is a schema drift and is dropped, not guessed.
func decodeUserActivityEvent(payload json.RawMessage) (UserActivityEvent, bool) {
	var evt UserActivityEvent
	if err := json.Unmarshal(payload, &evt); err != nil {
		return UserActivityEvent{}, false
	}
	if evt.OrganizationID == "" || evt.ProjectID == "" {
		return UserActivityEvent{}, false
	}
	return evt, true
}

// mediaDerivativeProgressPayload is the whitelist of fields relayed to a browser for a
// MediaDerivativeProgress event.
//
// A whitelist rather than "relay verbatim minus the storage key", and that direction is deliberate:
// the bus event carries SourceStorageKey — the half of the derivative's cache identity that names
// the source OBJECT — and no client-facing response may carry a storage key, a bucket or a raw
// object URL. Decoding into a fixed set of fields means a field added to the event later cannot
// silently start reaching browsers; it stays internal until somebody decides it is safe to publish.
//
// CacheKey is absent for the same reason it is absent from MediaDerivativeResponse: it is
// server-side cache identity, and the browser has no use for it. SourceProjectFileId is what the
// media pool keys an item by (its mediaRef is file:<id>), which is all the UI needs to attach
// "Generating proxy 42 %" to the right clip. No client can address the derivative being produced
// before that row exists anyway.
//
// Detail is the one member that is NOT safe by construction, so it is not relayed as authored — see
// sanitizeProgressDetail. Every other field here is either a closed vocabulary (Stage), a typed
// scalar (PercentComplete, OccurredAt) or an opaque id the browser already holds.
type mediaDerivativeProgressPayload struct {
	ProjectID           string          `json:"projectId"`
	SourceProjectFileID string          `json:"sourceProjectFileId,omitempty"`
	Stage               string          `json:"stage"`
	PercentComplete     *int            `json:"percentComplete,omitempty"`
	Detail              string          `json:"detail,omitempty"`
	OccurredAt          json.RawMessage `json:"occurredAt,omitempty"`
}

// progressDetailMaxRunes is the relay's ceiling on the Detail field handed to a browser.
//
// Chosen to sit comfortably above every string the engine writes as human-readable copy today (the
// longest is the omitted-derivatives sentence at roughly 130 runes) and far below the ~2000
// characters of raw ffmpeg/ffprobe stderr that a wrapped tool failure can push through the same
// field. It is a rune count rather than a byte count so a message containing an em dash or a
// non-ASCII project name is not rejected for its encoding.
const progressDetailMaxRunes = 200

// sanitizeProgressDetail decides what a browser may be told, and it fails CLOSED: the relayed value
// is either a short, single-line, separator-free string exactly as the producer wrote it, or the
// field is absent.
//
// Why this exists. Detail is the ONE member of this projection that is a producer-authored free-text
// channel rather than a closed vocabulary or a typed scalar. The engine legitimately interpolates
// things into it that are useful in a log and are not user-facing copy: an ffprobe/ffmpeg failure
// surfaces as ex.Message, and that message embeds the local scratch file path it was handed
// plus a tail of raw process stderr — text that describes the SERVER's filesystem layout and
// internal tooling to whoever is watching the SSE stream. A whitelist over field NAMES cannot help
// here, because the dangerous content is inside an allowed field.
//
// Why all-or-nothing rather than redaction or truncation. A sanitizer that rewrites the bad parts
// out of a string has to be right about every spelling of every thing it is trying to remove; one
// miss is a leak, and the matching is done on attacker-influenced text. Dropping the whole field on
// any doubt inverts that: the rule is "prove this is inert, or say nothing", so the failure mode of
// a rule that is too strict is a missing sentence in a progress toast, never a disclosure. It is the
// same direction as the field-name whitelist above, one level down.
//
// The three rejections, and what each one actually buys:
//
//   - Any '/' or '\\' anywhere — this is the load-bearing rule. Forward and back slash are the
//     canonical separators of every path dialect that could reach here (POSIX absolute, Windows
//     drive-relative, UNC, file:// URLs, object-storage keys), and no escape sequence, encoding
//     trick or relative spelling avoids them. Rejecting the separator rejects the whole family
//     rather than matching known prefixes, so a future producer that adds a path-bearing Detail —
//     say a new outbound-URL message or a storage key — cannot get it through. The deliberate cost
//     is the "No derivatives are defined for MIME type 'video/quicktime' …" sentence, which is
//     dropped for its one slash; its meaning is already carried by the Skipped stage, and a client
//     that genuinely needs the reason wants a typed field, not free text with a hole in it.
//   - Any non-printable rune — raw tool output is multi-line, so a newline, carriage return or tab
//     is by itself the signature of a stderr dump rather than authored copy. Unicode's printable
//     set also rejects the zero-width and format characters that a homoglyph trick could hide in.
//   - More than progressDetailMaxRunes runes — an unbounded string is the second half of the
//     finding. Nothing legitimate here is long, so length is a cheap total-order guard.
//
// An empty return omits the field entirely (json omitempty), so the client sees a stage and a
// percentage rather than a redacted-looking placeholder.
func sanitizeProgressDetail(raw string) string {
	if raw == "" {
		return ""
	}

	if utf8.RuneCountInString(raw) > progressDetailMaxRunes {
		return ""
	}

	if strings.ContainsAny(raw, `/\`) {
		return ""
	}

	for _, r := range raw {
		if !unicode.IsPrint(r) {
			return ""
		}
	}

	return raw
}

// projectMediaDerivativeProgress narrows a MediaDerivativeProgress body to the fields a client may
// see, and returns the project id the event must be scoped by.
//
// ok is false when the body does not carry a usable project id, which is also the answer for a body
// that is not JSON at all — a malformed progress frame is dropped, never broadcast.
//
// Detail is the exception to "relay the whitelisted fields as authored": its value is passed through
// sanitizeProgressDetail, so a producer-authored path or stderr dump never reaches a browser. The
// other five members are relayed verbatim.
func projectMediaDerivativeProgress(payload json.RawMessage) (json.RawMessage, string, bool) {
	fields, err := decodeMessageFields(payload)
	if err != nil {
		return nil, "", false
	}

	projectID := messageString(fields, "projectid")
	if projectID == "" {
		return nil, "", false
	}

	var percentComplete *int
	if raw, ok := fields["percentcomplete"]; ok {
		var parsed int
		if err := json.Unmarshal(raw, &parsed); err == nil {
			percentComplete = &parsed
		}
	}

	projected, err := json.Marshal(mediaDerivativeProgressPayload{
		ProjectID:           projectID,
		SourceProjectFileID: messageString(fields, "sourceprojectfileid"),
		Stage:               messageString(fields, "stage"),
		PercentComplete:     percentComplete,
		Detail:              sanitizeProgressDetail(messageString(fields, "detail")),
		OccurredAt:          fields["occurredat"],
	})
	if err != nil {
		return nil, "", false
	}

	return projected, projectID, true
}

// decodeMessageFields indexes a message body by its LOWERCASED field names, so one lookup serves
// both the camelCase and the PascalCase spelling of a System.Text.Json payload. The two spellings
// genuinely both occur here: extractExecutionID already has to try both, and hard-coding one would
// make this projection silently drop every event the day MassTransit's serializer options change.
func decodeMessageFields(payload json.RawMessage) (map[string]json.RawMessage, error) {
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(payload, &raw); err != nil {
		return nil, err
	}

	fields := make(map[string]json.RawMessage, len(raw))
	for name, value := range raw {
		fields[strings.ToLower(name)] = value
	}
	return fields, nil
}

// messageString reads a string field from a decodeMessageFields index, or "" when it is absent or
// is not a string. An absent optional field is not an error.
func messageString(fields map[string]json.RawMessage, name string) string {
	raw, ok := fields[name]
	if !ok {
		return ""
	}

	var value string
	if err := json.Unmarshal(raw, &value); err != nil {
		return ""
	}
	return value
}

func unwrapMassTransitMessage(body []byte) json.RawMessage {
	var envelope map[string]json.RawMessage
	if err := json.Unmarshal(body, &envelope); err != nil {
		return body
	}

	if message, ok := envelope["message"]; ok && len(message) > 0 {
		return message
	}

	return body
}

func extractExecutionID(payload json.RawMessage) string {
	var message map[string]json.RawMessage
	if err := json.Unmarshal(payload, &message); err != nil {
		return ""
	}

	for _, key := range []string{"executionId", "ExecutionId"} {
		if raw, ok := message[key]; ok {
			var executionID string
			if err := json.Unmarshal(raw, &executionID); err == nil {
				return executionID
			}
		}
	}

	return ""
}
