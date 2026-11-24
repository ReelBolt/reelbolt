package services

import (
	"os"
	"strings"
	"testing"
	"time"

	amqp "github.com/rabbitmq/amqp091-go"
)

func contains(list []string, s string) bool {
	for _, v := range list {
		if v == s {
			return true
		}
	}
	return false
}

func TestExchangeRoutingSets(t *testing.T) {
	for _, ex := range hubOnlyExchanges() {
		if contains(sharedExchanges(), ex) {
			t.Errorf("hub-only exchange %q must not be bound to the shared queue", ex)
		}
		if !contains(hubExchanges(), ex) {
			t.Errorf("hub-only exchange %q must be bound to the private queue", ex)
		}
	}
	if contains(hubExchanges(), exchangeUserActivity) {
		t.Error("user activity must not reach the private queue")
	}
	for _, ex := range []string{exchangeCompleted, exchangeFailed, exchangeUserActivity} {
		if !contains(sharedExchanges(), ex) {
			t.Errorf("%q needs the shared queue (once-only work)", ex)
		}
	}
	if !contains(hubExchanges(), exchangeCompleted) || !contains(hubExchanges(), exchangeFailed) {
		t.Error("run-terminal events must also reach the Hub")
	}
	if len(allExchanges()) != len(hubExchanges())+1 {
		t.Error("allExchanges should be hub exchanges plus user activity")
	}
}

func TestPrivateQueueNameUnique(t *testing.T) {
	a, b := newPrivateQueueName(), newPrivateQueueName()
	if a == b {
		t.Fatal("names must differ")
	}
	host, _ := os.Hostname()
	if !strings.HasPrefix(a, privateQueuePrefix+"-"+host+"-") {
		t.Errorf("unexpected name %q", a)
	}
}

func TestDispatchPrivateFeedsHubOnly(t *testing.T) {
	ch, ok := Hub.Subscribe("route-test")
	if !ok {
		t.Fatal("subscribe")
	}
	defer Hub.Unsubscribe(ch)

	body := []byte(`{"message":{"executionId":"11111111-1111-1111-1111-111111111111"}}`)
	dispatchMessage(amqp.Delivery{Exchange: exchangeStepStarted, Body: body}, true)
	select {
	case ev := <-ch:
		if ev.Type != "step.started" {
			t.Errorf("type %q", ev.Type)
		}
	case <-time.After(time.Second):
		t.Fatal("private delivery did not reach the Hub")
	}

	// Run-terminal on the private queue reaches the Hub (notification is the shared path's job).
	dispatchMessage(amqp.Delivery{Exchange: exchangeCompleted, Body: body}, true)
	select {
	case ev := <-ch:
		if ev.Type != "execution.completed" {
			t.Errorf("type %q", ev.Type)
		}
	case <-time.After(time.Second):
		t.Fatal("completed did not reach the Hub")
	}
}

func TestDispatchSharedNeverFeedsHub(t *testing.T) {
	ch, ok := Hub.Subscribe("route-test-shared")
	if !ok {
		t.Fatal("subscribe")
	}
	defer Hub.Unsubscribe(ch)

	body := []byte(`{"message":{"executionId":"not-a-uuid"}}`)
	for _, ex := range []string{exchangeCompleted, exchangeFailed, exchangeStepStarted, exchangeUserActivity} {
		dispatchMessage(amqp.Delivery{Exchange: ex, Body: body}, false)
	}
	select {
	case ev := <-ch:
		t.Fatalf("shared delivery leaked to the Hub: %+v", ev)
	case <-time.After(50 * time.Millisecond):
	}
}

func TestDispatchPrivateIgnoresUserActivity(t *testing.T) {
	ch, _ := Hub.Subscribe("route-test-activity")
	defer Hub.Unsubscribe(ch)
	dispatchMessage(amqp.Delivery{Exchange: exchangeUserActivity, Body: []byte(`{}`)}, true)
	select {
	case ev := <-ch:
		t.Fatalf("unexpected %+v", ev)
	case <-time.After(50 * time.Millisecond):
	}
}

func TestHubCloseAll(t *testing.T) {
	h := &SSEHub{clients: map[chan WorkflowEvent]string{}, connectionsByUser: map[string]int{}}
	ch, _ := h.Subscribe("k")
	h.CloseAll()
	if _, open := <-ch; open {
		t.Fatal("channel should be closed")
	}
	h.Unsubscribe(ch) // must not double-close
	if _, ok := h.Subscribe("k"); !ok {
		t.Fatal("count should be reset")
	}
}

func TestAfterConsumerStartEndsStreamsOnlyAfterAReconnect(t *testing.T) {
	consumerConnectedBefore.Store(false)
	t.Cleanup(func() { consumerConnectedBefore.Store(false) })
	ch := make(chan WorkflowEvent, 1)
	Hub.mu.Lock()
	Hub.clients[ch] = "user"
	Hub.mu.Unlock()
	t.Cleanup(func() {
		Hub.mu.Lock()
		delete(Hub.clients, ch)
		Hub.mu.Unlock()
	})

	afterConsumerStart()
	select {
	case <-ch:
		t.Fatal("the first start must not end a stream")
	default:
	}

	afterConsumerStart()
	select {
	case _, open := <-ch:
		if open {
			t.Fatal("a reconnect must close the stream, not send on it")
		}
	default:
		t.Fatal("a reconnect must end the open stream")
	}
}
