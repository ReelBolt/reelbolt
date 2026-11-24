package services

import (
	"os"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"testing"
)

// acquisitionVocabularySource is the C# file that states the new-user funnel's closed sets for the
// Inference API, which owns user_onboarding_profiles and user_attribution and puts CHECK constraints
// built from these lists on them. The Go API writes two of those columns (suggested_persona and
// capture) from its own copies of the lists, so a value one side knows and the other does not is
// either a signup-time persona silently dropped here or an insert the database refuses. This test
// is what keeps the copies the same (docs/design/new-user-funnel.md section 4).
const acquisitionVocabularySource = "../../inference/src/ReelBolt.Shared/Acquisition/AcquisitionVocabulary.cs"

var (
	csConstPattern = regexp.MustCompile(`const\s+string\s+(\w+)\s*=\s*"([^"]*)"\s*;`)
	csListItem     = regexp.MustCompile(`"([^"]*)"|\b([A-Za-z_]\w*)\b`)
	csLineComment  = regexp.MustCompile(`(?m)//.*$`)
)

// csStringList reads `public static readonly IReadOnlyList<string> <name> = [ ... ];` from C# source:
// string literals, and identifiers that name a `const string` in the same file.
func csStringList(t *testing.T, source, name string) []string {
	t.Helper()
	source = csLineComment.ReplaceAllString(source, "")
	consts := map[string]string{}
	for _, m := range csConstPattern.FindAllStringSubmatch(source, -1) {
		consts[m[1]] = m[2]
	}
	marker := "IReadOnlyList<string> " + name + " ="
	i := strings.Index(source, marker)
	if i < 0 {
		t.Fatalf("list %s not found in %s", name, acquisitionVocabularySource)
	}
	rest := source[i+len(marker):]
	open, end := strings.Index(rest, "["), strings.Index(rest, "]")
	if open < 0 || end < open {
		t.Fatalf("list %s in %s is not a [ ... ] collection expression", name, acquisitionVocabularySource)
	}
	var out []string
	for _, m := range csListItem.FindAllStringSubmatch(rest[open+1:end], -1) {
		if m[2] == "" {
			out = append(out, m[1])
			continue
		}
		v, ok := consts[m[2]]
		if !ok {
			t.Fatalf("list %s names %s, which is not a const string in %s", name, m[2], acquisitionVocabularySource)
		}
		out = append(out, v)
	}
	sort.Strings(out)
	return out
}

func TestAcquisitionVocabularyMatchesTheInferenceApi(t *testing.T) {
	raw, err := os.ReadFile(acquisitionVocabularySource)
	if err != nil {
		// The api/ module is often exercised on its own (docker: only api/ mounted), in which case
		// the C# tree simply is not there. A missing file inside a present tree is a moved or
		// renamed vocabulary, and that must fail (as models/runner_enum_drift_test.go does).
		if _, dirErr := os.Stat("../../inference"); dirErr != nil {
			t.Skipf("the Inference API tree is not checked out beside this module: %v", err)
		}
		t.Fatalf("read %s: %v", acquisitionVocabularySource, err)
	}
	source := string(raw)

	var goPersonas []string
	for p := range personas {
		goPersonas = append(goPersonas, p)
	}
	sort.Strings(goPersonas)
	if cs := csStringList(t, source, "Personas"); strings.Join(cs, ",") != strings.Join(goPersonas, ",") {
		t.Errorf("personas differ: the Inference API has %v, the Go API has %v", cs, goPersonas)
	}

	goCaptures := []string{AttributionCaptureCookie, AttributionCaptureURL}
	sort.Strings(goCaptures)
	if cs := csStringList(t, source, "AttributionCaptures"); strings.Join(cs, ",") != strings.Join(goCaptures, ",") {
		t.Errorf("user_attribution.capture values differ: the Inference API has %v, the Go API has %v", cs, goCaptures)
	}
}

// attributionSanitizerSource is the Inference API's sanitiser for the same first-touch fields. It
// labels the visitor (product_events.detail of site.first_visit) by the rule the Go API uses for
// the account (user_attribution.source_label), and the per-link report reads the two side by side.
const attributionSanitizerSource = "../../inference/src/ReelBolt.Inference.Api/Services/Telemetry/AttributionSanitizer.cs"

var csMaxLabelLength = regexp.MustCompile(`const\s+int\s+MaxLabelLength\s*=\s*(\d+)\s*;`)

// The widest source label is the same number on both sides. If they differ, a referrer host between
// the two widths is a label for one service and "direct" for the other.
func TestSourceLabelWidthMatchesTheInferenceApi(t *testing.T) {
	raw, err := os.ReadFile(attributionSanitizerSource)
	if err != nil {
		if _, dirErr := os.Stat("../../inference"); dirErr != nil {
			t.Skipf("the Inference API tree is not checked out beside this module: %v", err)
		}
		t.Fatalf("read %s: %v", attributionSanitizerSource, err)
	}
	m := csMaxLabelLength.FindStringSubmatch(string(raw))
	if m == nil {
		t.Fatalf("MaxLabelLength not found in %s", attributionSanitizerSource)
	}
	if got, err := strconv.Atoi(m[1]); err != nil || got != maxSourceLabelLength {
		t.Errorf("the Inference API's MaxLabelLength is %s, the Go API's maxSourceLabelLength is %d", m[1], maxSourceLabelLength)
	}
}
