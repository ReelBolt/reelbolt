//go:build !unix

package container

import "os"

// nonBlockingOpenFlag is zero where there are no FIFOs to block on.
const nonBlockingOpenFlag = 0

func setBlocking(*os.File) error { return nil }
