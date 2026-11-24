//go:build windows

package foldersync

import (
	"io/fs"
	"syscall"
)

// caseInsensitiveFS: NTFS compares names without case by default.
const caseInsensitiveFS = true

// attributesOf reads the attribute word from Lstat/ReadDir data, which on
// Windows comes from GetFileAttributesEx/FindFirstFile and never hydrates a
// placeholder.
func attributesOf(info fs.FileInfo) (uint32, bool) {
	if d, ok := info.Sys().(*syscall.Win32FileAttributeData); ok && d != nil {
		return d.FileAttributes, true
	}
	return 0, false
}

func platformReason(info fs.FileInfo) string {
	attrs, ok := attributesOf(info)
	if !ok {
		return ""
	}
	return windowsAttrReason(attrs, info.IsDir())
}

// syncableFile: since Go 1.23 a reparse point that is not a symlink reports
// ModeIrregular, and every OneDrive/Dropbox/iCloud file — including one that
// IS downloaded ("Always keep on this device") — is a cloud reparse point. So
// on Windows irregular is accepted; the cloud attributes above decide whether
// reading is safe. Devices, pipes and sockets are still refused.
func syncableFile(info fs.FileInfo) bool {
	m := info.Mode()
	if m.IsDir() || m&fs.ModeSymlink != 0 {
		return false
	}
	return m&(fs.ModeNamedPipe|fs.ModeSocket|fs.ModeDevice|fs.ModeCharDevice) == 0
}
