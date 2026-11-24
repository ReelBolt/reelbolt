//go:build !release

package telemetry

// devEndpointAllowed lets a non-release build post to a loopback collector
// named by REELBOLT_TELEMETRY_ENDPOINT, which is how the local fake this WP was
// built against is reached. A release build compiles it out, so a shipped
// runner's collector is exactly the one stamped into it.
const devEndpointAllowed = true
