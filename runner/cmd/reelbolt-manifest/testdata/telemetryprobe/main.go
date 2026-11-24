// Command telemetryprobe prints the collector address compiled into it. It
// exists for ldflags_build_test.go only: the headless runner does not link the
// telemetry package (only the desktop app, which needs a GUI toolchain, does),
// so this is the smallest real build that proves the -X for
// telemetry.CollectorEndpoint lands.
package main

import (
	"fmt"

	"github.com/vecchiotom/reelbolt/runner/internal/telemetry"
)

func main() { fmt.Println(telemetry.CollectorEndpoint) }
