//go:build !windows

package foldersync

import "io/fs"

// caseInsensitiveFS is false: Linux file systems compare names exactly. (macOS
// is case-insensitive by default, but the desktop runner does not ship there —
// decision D26 — so this file only has to keep the package compiling.)
const caseInsensitiveFS = false

// platformReason has nothing to add outside Windows: Linux has no online-only
// placeholder convention, and hidden files are caught by name.
func platformReason(fs.FileInfo) string { return "" }

func syncableFile(info fs.FileInfo) bool { return info.Mode().IsRegular() }
