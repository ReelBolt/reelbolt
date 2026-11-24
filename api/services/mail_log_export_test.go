package services

import "testing"

// setMailLogRefKeyForTest pins the process log reference key for one test.
func setMailLogRefKeyForTest(t *testing.T, key []byte) {
	t.Helper()
	InitMailLogRef()
	old := mailLogKey.key
	mailLogKey.key = key
	t.Cleanup(func() { mailLogKey.key = old })
}
