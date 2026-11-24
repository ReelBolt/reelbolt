package daemon

import (
	"testing"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"

	"github.com/vecchiotom/reelbolt/runner/internal/config"
	"github.com/vecchiotom/reelbolt/runner/internal/llmrelay"
)

func TestHelloCapabilitiesCarryLocalLLMOnlyWhenEnabled(t *testing.T) {
	d := &Daemon{
		probe: ProbeResult{Capabilities: protocol.Capabilities{ProtocolVersion: protocol.ProtocolVersion, Slots: 1, Container: protocol.ContainerNone}},
		llm:   llmrelay.New(llmrelay.Options{}),
	}
	if d.helloCapabilities().LocalLLM != nil {
		t.Fatal("default runner advertises localLlm")
	}
	if err := d.llm.Apply(config.LocalLLM{Enabled: true}); err != nil {
		t.Fatal(err)
	}
	doc := d.helloCapabilities()
	if doc.LocalLLM == nil || !doc.LocalLLM.Enabled || doc.LocalLLM.MaxConcurrent != config.DefaultLocalLLMConcurrent {
		t.Fatalf("localLlm = %+v", doc.LocalLLM)
	}
	if err := doc.Validate(); err != nil {
		t.Fatalf("advertised document invalid: %v", err)
	}
	if d.probe.Capabilities.LocalLLM != nil {
		t.Fatal("the probe's stored document was mutated")
	}
}
