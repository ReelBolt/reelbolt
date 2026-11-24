//go:build release

package config

// devGatewayAllowed is false in a release build: the shipped runner talks to
// the product domain and nothing else, whatever runner.json says.
const devGatewayAllowed = false

// DevGatewayAllowed reports whether this binary accepts a gateway outside the
// product domain.
func DevGatewayAllowed() bool { return devGatewayAllowed }
