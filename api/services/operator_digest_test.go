package services

import (
	"strings"
	"testing"
)

func TestOperatorDigestRendersEscapedAndOneLine(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	evil := "boom <script>alert(1)</script>\r\nBcc: x@evil.test & more"
	r, err := m.Render("ops@example.com", TemplateOperatorDigest, MailData{
		Locale: "it",
		Digest: &OperatorDigest{
			WindowHours: 24, GeneratedAt: "2026-11-16 06:00 UTC",
			Runs:     []DigestCount{{Label: "Failed", Count: 3}, {Label: "Passed", Count: 40}},
			Groups:   []DigestGroup{{Code: "STEP_INTERNAL_ERROR", StepType: "VideoCompile", Count: 2, Organizations: 1, First: "05:00", Last: "05:30"}},
			Failures: []DigestFailure{{Time: "05:00", StepType: "VideoCompile", Code: "STEP_INTERNAL_ERROR", Error: evil, RunID: "11111111-1111-1111-1111-111111111111"}},
			Feedback: []DigestFeedback{{Time: "04:00", Kind: "bug", Text: "<b>x</b>"}}, FeedbackCount: 1,
		},
	})
	if err != nil {
		t.Fatalf("render: %v", err)
	}
	if strings.Contains(r.HTML, "<script>") || strings.Contains(r.HTML, "<b>x</b>") {
		t.Errorf("html not escaped:\n%s", r.HTML)
	}
	if !strings.Contains(r.HTML, "&lt;script&gt;") {
		t.Error("expected escaped markup in html")
	}
	if strings.Contains(r.Text, "\nBcc:") {
		t.Errorf("a newline in a value forged a line:\n%s", r.Text)
	}
	for _, want := range []string{"STEP_INTERNAL_ERROR / VideoCompile: 2 step(s)", "Failed: 3", "FEEDBACK REPORTS: 1", "11111111-1111-1111-1111-111111111111"} {
		if !strings.Contains(r.Text, want) {
			t.Errorf("text lacks %q:\n%s", want, r.Text)
		}
	}
	if !strings.Contains(r.Subject, "1 failure group(s), 1 feedback report(s)") {
		t.Errorf("subject %q", r.Subject)
	}
	if strings.Contains(r.Text, "Ciao") || strings.Contains(r.Text, "<no value>") {
		t.Errorf("not English or unresolved value:\n%s", r.Text)
	}
}

func TestOperatorDigestRequiresBodyAndBoundsIt(t *testing.T) {
	m := NewSMTPMailer(MailerConfig{})
	if _, err := m.Render("ops@example.com", TemplateOperatorDigest, MailData{}); err == nil {
		t.Fatal("rendered without a digest body")
	}
	big := &OperatorDigest{}
	for i := 0; i < 500; i++ {
		big.Failures = append(big.Failures, DigestFailure{Error: strings.Repeat("é", 5000)})
	}
	s := big.Sanitized()
	if len(s.Failures) != digestMaxFailures || len([]rune(s.Failures[0].Error)) != digestMaxFieldRune {
		t.Fatalf("not bounded: %d rows, %d runes", len(s.Failures), len([]rune(s.Failures[0].Error)))
	}
}
