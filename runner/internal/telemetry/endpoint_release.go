//go:build release

package telemetry

// devEndpointAllowed is false in a release build: the shipped runner posts to
// the collector compiled into it and nowhere else, whatever the environment
// says.
const devEndpointAllowed = false
