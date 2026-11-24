package services

import "strings"

// Bounds the relay applies to an operator_digest body whatever the caller sent, so the template can
// never carry more than a short, one-line-per-value summary.
const (
	digestMaxRuns      = 10
	digestMaxGroups    = 50
	digestMaxFailures  = 100
	digestMaxFeedback  = 100
	digestMaxFieldRune = 200
)

// OperatorDigest is the structured body of the operator_digest mail. It carries counts, closed codes,
// run ids and short redacted excerpts only; the sender (the Inference API) never puts addresses,
// names, file names or prompts in it, and Sanitized bounds whatever arrives anyway.
type OperatorDigest struct {
	WindowHours     int              `json:"windowHours"`
	GeneratedAt     string           `json:"generatedAt"`
	Runs            []DigestCount    `json:"runs"`
	Groups          []DigestGroup    `json:"groups"`
	Failures        []DigestFailure  `json:"failures"`
	FailuresOmitted int              `json:"failuresOmitted"`
	FeedbackCount   int              `json:"feedbackCount"`
	Feedback        []DigestFeedback `json:"feedback"`
}

// DigestCount is one run-status line (status, count).
type DigestCount struct {
	Label string `json:"label"`
	Count int    `json:"count"`
}

// DigestGroup is failed steps grouped by failure code and step type.
type DigestGroup struct {
	Code          string `json:"code"`
	StepType      string `json:"stepType"`
	Count         int    `json:"count"`
	Organizations int    `json:"organizations"`
	First         string `json:"first"`
	Last          string `json:"last"`
}

// DigestFailure is one failed step line.
type DigestFailure struct {
	Time     string `json:"time"`
	StepType string `json:"stepType"`
	Code     string `json:"code"`
	Error    string `json:"error"`
	RunID    string `json:"runId"`
}

// DigestFeedback is one feedback report line.
type DigestFeedback struct {
	Time string `json:"time"`
	Kind string `json:"kind"`
	Text string `json:"text"`
}

// digestLine makes a value safe for one line of the text part and bounded: control characters
// (newlines included) become spaces and the value is cut to digestMaxFieldRune runes. The HTML part
// is escaped by html/template on top of this.
func digestLine(s string) string {
	s = strings.Map(func(r rune) rune {
		if r < 0x20 || r == 0x7f || (r >= 0x80 && r < 0xa0) || r == ' ' || r == ' ' {
			return ' '
		}
		return r
	}, s)
	s = strings.TrimSpace(s)
	if r := []rune(s); len(r) > digestMaxFieldRune {
		s = string(r[:digestMaxFieldRune])
	}
	return s
}

func clampCount(n int) int {
	if n < 0 {
		return 0
	}
	return n
}

// Sanitized returns a bounded, single-line copy of d; the receiver is not modified.
func (d *OperatorDigest) Sanitized() *OperatorDigest {
	out := &OperatorDigest{
		WindowHours:     clampCount(d.WindowHours),
		GeneratedAt:     digestLine(d.GeneratedAt),
		FailuresOmitted: clampCount(d.FailuresOmitted),
		FeedbackCount:   clampCount(d.FeedbackCount),
	}
	for i, r := range d.Runs {
		if i == digestMaxRuns {
			break
		}
		out.Runs = append(out.Runs, DigestCount{Label: digestLine(r.Label), Count: clampCount(r.Count)})
	}
	for i, g := range d.Groups {
		if i == digestMaxGroups {
			break
		}
		out.Groups = append(out.Groups, DigestGroup{
			Code: digestLine(g.Code), StepType: digestLine(g.StepType), Count: clampCount(g.Count),
			Organizations: clampCount(g.Organizations), First: digestLine(g.First), Last: digestLine(g.Last),
		})
	}
	for i, f := range d.Failures {
		if i == digestMaxFailures {
			break
		}
		out.Failures = append(out.Failures, DigestFailure{
			Time: digestLine(f.Time), StepType: digestLine(f.StepType), Code: digestLine(f.Code),
			Error: digestLine(f.Error), RunID: digestLine(f.RunID),
		})
	}
	for i, f := range d.Feedback {
		if i == digestMaxFeedback {
			break
		}
		out.Feedback = append(out.Feedback, DigestFeedback{Time: digestLine(f.Time), Kind: digestLine(f.Kind), Text: digestLine(f.Text)})
	}
	return out
}
