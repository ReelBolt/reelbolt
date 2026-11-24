package config

import "strings"

// ServiceOrigin is the one ReelBolt address a build talks to: pairing, the device API, dashboard
// links, the help pages and the crash collector are all paths under it, and the runner gateway is
// handed out by pairing (it is wss://<the same host>/runner/v1/connect on a deployment that serves
// everything from one host).
//
// It is a variable so a build can be stamped for another deployment:
//
//	go build -ldflags '-X github.com/vecchiotom/reelbolt/runner/internal/config.ServiceOrigin=https://...'
//
// The default is the deployment that exists today. Every ReelBolt page, API and the runner
// gateway live under this single host. A release build also accepts its subdomains (for
// example a dedicated runner hostname); it refuses any other domain.
// The product domain is a constant, so a self-hoster on another domain uses a non-release build.
var ServiceOrigin = "https://reelbolt.ai"

// Origin is ServiceOrigin without a trailing slash.
func Origin() string { return strings.TrimRight(strings.TrimSpace(ServiceOrigin), "/") }

// DashboardURL is the signed-in dashboard's home page.
func DashboardURL() string { return Origin() + "/app" }

// HelpURL is the desktop runner's page in the user guide.
func HelpURL() string { return Origin() + "/docs/account/desktop-runner" }

// CollectorEndpoint is where opt-in crash reports are posted.
func CollectorEndpoint() string { return Origin() + "/runner/telemetry/v1/reports" }
