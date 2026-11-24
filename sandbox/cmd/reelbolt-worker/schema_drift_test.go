package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// TestSmokeSchemaMatchesTheGateways keeps sandbox/scripts/runner-smoke-schema.sql
// honest. That file is what the D13 smoke test (sandbox/scripts/worker-smoke.sh)
// loads into a throwaway Postgres, and it is a COPY of the schema the gateway's
// own integration tests create — the one description of "the tables the gateway
// really talks to" that runs in CI without a database.
//
// Two copies of a schema diverge the moment one of them is edited, and the
// failure mode is that the smoke test stops exercising the real tables while
// still printing all its PASS lines. Hence this test: it reads the gateway's own
// DDL constant out of its source (the gateway is package main, so there is
// nothing to import) and fails on any difference at all.
func TestSmokeSchemaMatchesTheGateways(t *testing.T) {
	gatewayPath := filepath.Join("..", "runner-gateway", "queue_integration_test.go")
	source, err := os.ReadFile(gatewayPath)
	if err != nil {
		t.Fatalf("read the gateway's integration DDL: %v", err)
	}
	schemaPath := filepath.Join("..", "..", "scripts", "runner-smoke-schema.sql")
	smoke, err := os.ReadFile(schemaPath)
	if err != nil {
		t.Fatalf("read the smoke schema: %v", err)
	}

	want := normaliseDDL(extractRawString(t, string(source), "const queueTestDDL = "))
	got := normaliseDDL(stripSQLComments(string(smoke)))
	if want != got {
		t.Fatalf("the smoke schema and the gateway's test schema have diverged.\n"+
			"sandbox/scripts/runner-smoke-schema.sql must be updated to match %s.\n%s",
			gatewayPath, firstDifference(want, got))
	}
}

// extractRawString returns the body of the Go raw string literal that follows
// marker, which is how the gateway declares its DDL.
func extractRawString(t *testing.T, source, marker string) string {
	t.Helper()
	at := strings.Index(source, marker)
	if at < 0 {
		t.Fatalf("the gateway no longer declares %q: the smoke schema has no source of truth", marker)
	}
	rest := source[at+len(marker):]
	if !strings.HasPrefix(rest, "`") {
		t.Fatalf("%s is no longer followed by a raw string literal", marker)
	}
	rest = rest[1:]
	end := strings.Index(rest, "`")
	if end < 0 {
		t.Fatalf("the raw string after %q is not terminated", marker)
	}
	return rest[:end]
}

// stripSQLComments removes -- comments and blank lines, so a comment may be
// reworded in either copy without failing the test. The DDL itself may not
// change without changing both.
func stripSQLComments(sql string) string {
	var out []string
	for _, line := range strings.Split(sql, "\n") {
		if i := strings.Index(line, "--"); i >= 0 {
			line = line[:i]
		}
		line = strings.TrimSpace(line)
		if line != "" {
			out = append(out, line)
		}
	}
	return strings.Join(out, "\n")
}

// normaliseDDL collapses whitespace so a re-indentation is not a failure.
func normaliseDDL(sql string) string {
	var out []string
	for _, line := range strings.Split(stripSQLComments(sql), "\n") {
		out = append(out, strings.Join(strings.Fields(line), " "))
	}
	return strings.Join(out, "\n")
}

// firstDifference names the first line that differs, so a failure points at the
// change rather than at a wall of SQL.
func firstDifference(want, got string) string {
	wantLines := strings.Split(want, "\n")
	gotLines := strings.Split(got, "\n")
	for i := 0; i < len(wantLines) || i < len(gotLines); i++ {
		var w, g string
		if i < len(wantLines) {
			w = wantLines[i]
		}
		if i < len(gotLines) {
			g = gotLines[i]
		}
		if w != g {
			return "line " + itoa(i+1) + ":\n  gateway: " + w + "\n  smoke:   " + g
		}
	}
	return ""
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	var digits []byte
	for n > 0 {
		digits = append([]byte{byte('0' + n%10)}, digits...)
		n /= 10
	}
	return string(digits)
}
