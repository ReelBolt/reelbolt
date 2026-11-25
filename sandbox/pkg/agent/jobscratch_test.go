package agent

import (
	"path/filepath"
	"testing"
)

func TestJobScratchDir(t *testing.T) {
	root := t.TempDir()
	got, err := jobScratchDir(root, "3f2b8c1e-aaaa-4bbb-8ccc-0123456789ab")
	if err != nil || got != filepath.Join(root, "3f2b8c1e-aaaa-4bbb-8ccc-0123456789ab") {
		t.Fatalf("valid id: %q, %v", got, err)
	}
	for _, bad := range []string{"", "..", ".", "../x", "a/b", `a\b`, "/abs", "a\x00b", "x.y"} {
		if _, err := jobScratchDir(root, bad); err == nil {
			t.Errorf("id %q was accepted", bad)
		}
	}
}
