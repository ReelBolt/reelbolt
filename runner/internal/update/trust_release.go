//go:build release

package update

// devTrustAllowed is false in a release build: the shipped runner trusts the
// keys compiled into it and nothing else, whatever the environment says.
const devTrustAllowed = false

// devHostAllowed is false in a release build, so the shipped runner fetches
// manifests and artifacts from the product's download host and nowhere else.
const devHostAllowed = false
