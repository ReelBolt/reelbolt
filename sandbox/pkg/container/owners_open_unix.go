//go:build unix

package container

import "syscall"

// ownerOpenFlags make an owner-entry open refuse a symlink and never block on a FIFO.
const ownerOpenFlags = syscall.O_NOFOLLOW | syscall.O_NONBLOCK
