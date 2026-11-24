package daemon

import "os"

// getenv is the production environment reader, kept in one place so the prober's
// seat is the only one that needs replacing in a test.
func getenv(key string) string { return os.Getenv(key) }
