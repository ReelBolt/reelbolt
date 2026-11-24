package services

// ProductFooterNote is the alpha version line every outbound ReelBolt email
// carries in its footer. It is the single source of the version string in mail:
// the templates render {{.FooterNote}}, the mailer fills that field from this
// constant, and infra/scripts/check-version.sh greps the literal below so the
// footer cannot drift from the rest of the release.
const ProductFooterNote = "ReelBolt 0.4.0-alpha — public alpha. Expect rough edges; things may change."
