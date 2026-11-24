package agent

import (
	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// LLMSender writes one message on the connection a request arrived on. It
// returns an error once that connection has ended.
type LLMSender func(t protocol.MessageType, payload any) error

// LLMHandler serves the gateway's local-LLM relay messages. It is optional:
// with none configured llm.* messages are ignored like any unknown type, so
// every other flow is untouched.
//
// HandleRequest MUST return promptly (it runs on the connection's read loop);
// the implementation serves the request on its own goroutine and answers
// through send. The payloads never reach the agent's logger.
type LLMHandler interface {
	HandleRequest(req protocol.LLMRequestPayload, send LLMSender)
	// Cancel stops one request the gateway no longer wants.
	Cancel(requestID string)
	// CancelAll stops everything: the connection the requests rode on ended.
	CancelAll()
}

// handleLLMRequest hands an llm.request to the configured handler.
func (a *Agent) handleLLMRequest(c *connection, env protocol.Envelope) {
	if a.cfg.LLM == nil {
		return
	}
	req, err := protocol.DecodePayload[protocol.LLMRequestPayload](env)
	if err != nil || req.Validate() != nil {
		// Unusable: say so if we can tell which request it was.
		if req.RequestID != "" {
			_ = c.send(protocol.TypeLLMError, protocol.LLMErrorPayload{
				RequestID: req.RequestID, Code: protocol.LLMErrBadRequest, Message: "the request was not valid",
			})
		}
		return
	}
	a.cfg.LLM.HandleRequest(req, func(t protocol.MessageType, payload any) error {
		return c.send(t, payload)
	})
}

// handleLLMCancel stops a relayed request.
func (a *Agent) handleLLMCancel(env protocol.Envelope) {
	if a.cfg.LLM == nil {
		return
	}
	if p, err := protocol.DecodePayload[protocol.LLMCancelPayload](env); err == nil && p.RequestID != "" {
		a.cfg.LLM.Cancel(p.RequestID)
	}
}

// helloCapabilities is the capability document for this hello: rebuilt on every
// connect when CapabilitiesFunc is set, so a refreshed model list is advertised
// after a reconnect. A function that returns something the protocol would
// refuse falls back to the static document rather than failing the connection.
func (a *Agent) helloCapabilities() protocol.Capabilities {
	if a.cfg.CapabilitiesFunc != nil {
		if caps := a.cfg.CapabilitiesFunc(); caps.Validate() == nil {
			return caps
		}
	}
	return a.cfg.Capabilities
}
