// Standalone timing oracle (C11 F-3). It reproduces the login path's
// bcrypt.CompareHashAndPassword call against three stored-hash shapes and
// reports how long each answer takes:
//
//   - "empty"  : the pre-fix OAuth-only account (PasswordHash == ""). bcrypt
//     rejects a hash shorter than 60 bytes BEFORE doing any work.
//   - "placeholder" : the fix (services.GeneratePlaceholderPasswordHash), a
//     random value no user can type.
//   - "password" : a normal password account (bcrypt of a real password).
//
// If the fix is correct, "placeholder" and "password" cost ~the same (the full
// bcrypt work), while "empty" returns in nanoseconds.
//
// Run:  go run ./cmd/timingoracle
package main

import (
	"fmt"
	"time"

	"github.com/vecchiotom/reelbolt/services"
	"golang.org/x/crypto/bcrypt"
)

func main() {
	const warmup = 30
	const n = 200

	// A normal password account's hash (what a password user actually stores).
	passwordHash, err := services.HashPassword("correct horse battery staple")
	if err != nil {
		panic(err)
	}
	// The fix: a random placeholder hash for an OAuth-only account.
	placeholderHash, err := services.GeneratePlaceholderPasswordHash()
	if err != nil {
		panic(err)
	}

	cases := map[string]string{
		"empty":       "", // the pre-fix OAuth-only account
		"placeholder": placeholderHash,
		"password":    passwordHash,
	}

	// Warm the CPU/branch predictor with a few real-cost calls so the
	// nanosecond "empty" case is not measured against a cold cache.
	for i := 0; i < warmup; i++ {
		_ = bcrypt.CompareHashAndPassword([]byte(passwordHash), []byte("correct horse battery staple"))
	}

	fmt.Println("=== C11 F-3 timing oracle: one login compare per call, 200 calls each ===")
	for _, name := range []string{"empty", "placeholder", "password"} {
		hash := cases[name]
		var total time.Duration
		for i := 0; i < n; i++ {
			// A wrong password: the login oracle's realistic case (an attacker
			// probing the address) and the one that costs the full work.
			start := time.Now()
			_ = bcrypt.CompareHashAndPassword([]byte(hash), []byte("probe-password-123"))
			total += time.Since(start)
		}
		fmt.Printf("%-12s : mean %10s  (total %s)\n", name, total/n, total)
	}
}
