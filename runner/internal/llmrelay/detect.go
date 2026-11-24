package llmrelay

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// Server kinds Detect and the capability document report. They are guesses for
// display: nothing is routed or trusted on them.
const (
	KindOllama       = "ollama"
	KindLMStudio     = "lmstudio"
	KindLlamaCpp     = "llamacpp"
	KindVLLM         = "vllm"
	KindOpenAICompat = "openai-compatible"
)

// DefaultPorts are probed on loopback by Detect.
var DefaultPorts = []int{11434, 1234, 8080, 8000}

const detectTimeout = 1500 * time.Millisecond

// Detected is one local server that answered GET /v1/models.
type Detected struct {
	Kind    string   `json:"kind"`
	BaseURL string   `json:"baseUrl"`
	Models  []string `json:"models"`
}

// Detect probes the well-known loopback ports for an OpenAI-compatible server.
// It only ever contacts 127.0.0.1, follows no redirects and reads at most 1 MiB
// from each. The result is sorted by port.
func Detect(ctx context.Context) []Detected {
	return DetectPorts(ctx, "127.0.0.1", DefaultPorts)
}

// DetectPorts is Detect with explicit ports, for tests and for a UI that wants
// to probe one more.
func DetectPorts(ctx context.Context, host string, ports []int) []Detected {
	client := newClient(false)
	client.Timeout = detectTimeout
	var (
		mu  sync.Mutex
		out []Detected
		wg  sync.WaitGroup
	)
	for _, port := range ports {
		wg.Add(1)
		go func(port int) {
			defer wg.Done()
			base := "http://" + net.JoinHostPort(host, strconv.Itoa(port))
			pctx, cancel := context.WithTimeout(ctx, detectTimeout)
			defer cancel()
			models, owner, err := fetchModels(pctx, client, base+protocol.LLMPathModels)
			if err != nil {
				return
			}
			mu.Lock()
			out = append(out, Detected{Kind: guessKind(strconv.Itoa(port), owner), BaseURL: base, Models: models})
			mu.Unlock()
		}(port)
	}
	wg.Wait()
	// Fixed preference order: the order of ports, not the URL string.
	rank := func(u string) int {
		for i, p := range ports {
			if strings.HasSuffix(u, ":"+strconv.Itoa(p)) {
				return i
			}
		}
		return len(ports)
	}
	sort.SliceStable(out, func(i, j int) bool { return rank(out[i].BaseURL) < rank(out[j].BaseURL) })
	return out
}

// fetchModels reads GET /v1/models and returns the model names (at most 100,
// each at most 200 bytes) and the first entry's owned_by value.
func fetchModels(ctx context.Context, client *http.Client, target string) (models []string, owner string, err error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return nil, "", err
	}
	req.Header.Set("Accept", "application/json")
	resp, err := client.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusOK {
		return nil, "", fmt.Errorf("models: status %d", resp.StatusCode)
	}
	var doc struct {
		Data []struct {
			ID      string `json:"id"`
			OwnedBy string `json:"owned_by"`
		} `json:"data"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, modelsCap)).Decode(&doc); err != nil {
		return nil, "", fmt.Errorf("models: not an OpenAI model list")
	}
	models = []string{}
	for i, m := range doc.Data {
		if i == 0 {
			owner = m.OwnedBy
		}
		id := strings.TrimSpace(m.ID)
		if id == "" || len(id) > protocol.LLMMaxModelNameLen {
			continue
		}
		if len(models) == protocol.LLMMaxModels {
			break
		}
		models = append(models, id)
	}
	return models, owner, nil
}

// guessKind names the server from the port it answered on and what it says
// about itself. Display only.
func guessKind(port, ownedBy string) string {
	owner := strings.ToLower(ownedBy)
	switch {
	case strings.Contains(owner, "vllm"):
		return KindVLLM
	case strings.Contains(owner, "llamacpp"), strings.Contains(owner, "llama.cpp"):
		return KindLlamaCpp
	case owner == "library" || strings.Contains(owner, "ollama"):
		return KindOllama
	}
	switch port {
	case "11434":
		return KindOllama
	case "1234":
		return KindLMStudio
	case "8080":
		return KindLlamaCpp
	case "8000":
		return KindVLLM
	}
	return KindOpenAICompat
}
