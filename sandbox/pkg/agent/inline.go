package agent

import (
	"encoding/json"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// inlineEnvelopeHeadroom is left unused in a payload for the fields wrapped
// around inline content.
const inlineEnvelopeHeadroom = 1 << 10

// FitsInline reports whether result, serialized as the op.response payload,
// stays within protocol.MaxPayloadBytes. Base64 and JSON escaping both inflate
// content, so the check runs on the encoded JSON, not the raw byte count. A
// frame over the limit makes the receiver close the socket (1009), so callers
// must refuse before building the response.
func FitsInline(result any) bool {
	raw, err := json.Marshal(result)
	return err == nil && len(raw) <= protocol.MaxPayloadBytes-inlineEnvelopeHeadroom
}
