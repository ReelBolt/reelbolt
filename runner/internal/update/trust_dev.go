//go:build !release

package update

// devTrustAllowed lets a non-release build trust an extra key named by
// REELBOLT_UPDATE_TRUST_KEY. A release build compiles it out, so a shipped
// runner's trust root is exactly what was stamped into it.
const devTrustAllowed = true

// devHostAllowed lets a non-release build talk to an update host outside the
// product domain (a local file server, a staging bucket).
const devHostAllowed = true
