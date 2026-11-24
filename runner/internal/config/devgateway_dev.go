//go:build !release

package config

// devGatewayAllowed is true for every build that is not a release build. The
// release pipeline builds with -tags release, so a shipped binary refuses a
// development gateway even if one is written into runner.json by hand.
const devGatewayAllowed = true

// DevGatewayAllowed reports whether this binary accepts a gateway outside the
// product domain.
func DevGatewayAllowed() bool { return devGatewayAllowed }
