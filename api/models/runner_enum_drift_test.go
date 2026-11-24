package models

import (
	"os"
	"sort"
	"strings"
	"testing"
)

// runnerEnumsSource is the C# enum file the runner tables are encoded with. This
// service maps those tables by GORM and never migrates them, so the stored strings
// are duplicated here as constants — and this test is the only thing that keeps the
// two copies honest. The enums are append-only: a rename on the C# side silently
// orphans every existing row, so it must fail here first.
const runnerEnumsSource = "../../inference/src/ReelBolt.Shared/Data/Models/RunnerEnums.cs"

// parseCSharpEnum extracts the member names of one "public enum <name>" block.
// It skips comment lines, the opening brace and blank lines.
func parseCSharpEnum(t *testing.T, source, name string) []string {
	t.Helper()

	lines := strings.Split(source, "\n")
	start := -1
	for i, line := range lines {
		if strings.HasPrefix(strings.TrimSpace(line), "public enum "+name) {
			start = i
			break
		}
	}
	if start < 0 {
		t.Fatalf("enum %s not found in %s", name, runnerEnumsSource)
	}

	var members []string
	for _, line := range lines[start+1:] {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "}") {
			return members
		}
		if trimmed == "" || trimmed == "{" || strings.HasPrefix(trimmed, "//") {
			continue
		}
		member := strings.TrimSuffix(trimmed, ",")
		if member != "" && !strings.ContainsAny(member, " ./(") {
			members = append(members, member)
		}
	}
	return members
}

func sortedCopy(in []string) []string {
	out := append([]string(nil), in...)
	sort.Strings(out)
	return out
}

func TestRunnerEnumsMatchTheInferenceApiSchema(t *testing.T) {
	raw, err := os.ReadFile(runnerEnumsSource)
	if err != nil {
		// The api/ module is often exercised on its own (docker: only api/ mounted),
		// in which case the C# tree simply is not there. A missing file inside a
		// present tree is a different thing entirely — a moved or renamed enum — and
		// that must fail.
		if _, dirErr := os.Stat("../../inference"); dirErr != nil {
			t.Skipf("the Inference API tree is not checked out beside this module: %v", err)
		}
		t.Fatalf("read %s: %v", runnerEnumsSource, err)
	}
	source := string(raw)

	cases := []struct {
		enum    string
		goNames []string
	}{
		{"RunnerKind", []string{RunnerKindDesktop, RunnerKindCloudPool}},
		{
			"RunnerPairingStatus",
			[]string{RunnerPairingPending, RunnerPairingApproved, RunnerPairingDenied, RunnerPairingExpired},
		},
	}

	for _, tc := range cases {
		got := parseCSharpEnum(t, source, tc.enum)
		if len(got) == 0 {
			t.Fatalf("%s parsed to no members", tc.enum)
		}
		want := sortedCopy(tc.goNames)
		sorted := sortedCopy(got)
		if strings.Join(sorted, ",") != strings.Join(want, ",") {
			t.Fatalf("%s = %v in C#, but this service stores %v; the enums are append-only, so update the Go constants",
				tc.enum, sorted, want)
		}
	}
}
