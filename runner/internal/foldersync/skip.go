package foldersync

import (
	"io/fs"
	"strings"
	"unicode"
	"unicode/utf8"
)

// Reasons a file is not copied, in the words the status shows. A reason is a
// sentence a person can act on, so each one says what it covers.
const (
	ReasonHidden     = "Hidden files and folders are not copied"
	ReasonSystem     = "System files (.DS_Store, Thumbs.db, desktop.ini) are not copied"
	ReasonTemporary  = "Temporary or unfinished files (~$…, .tmp, .part, .crdownload) are not copied"
	ReasonSymlink    = "Links to other places (symlinks) are not followed"
	ReasonOnlineOnly = "Only in iCloud, OneDrive or Dropbox and not downloaded to this computer. Right-click the folder and choose 'Always keep on this device'"
	ReasonUnreadable = "Could not be read on this computer"
	ReasonName       = "The name is not allowed in a project (too long, or it contains \\ or control characters)"
	ReasonNotFile    = "Not a regular file"
	ReasonOccupied   = "Another file in the project already has this name"
	ReasonDuplicate  = "Another file in this folder has the same name in different letter case"
)

// maxPathLen is the server's limit on a relative path.
const maxPathLen = 1024

// serverReason turns the server's skipped reason into a sentence.
func serverReason(code string) string {
	switch code {
	case "occupied":
		return ReasonOccupied
	case "invalid_path":
		return ReasonName
	case "duplicate_path":
		return ReasonDuplicate
	default:
		return "Not accepted by ReelBolt (" + code + ")"
	}
}

// nameReason is the rule for one path segment: hidden, system and temporary
// names, whatever kind of entry carries them. It never touches the disk.
func nameReason(name string) string {
	lower := strings.ToLower(name)
	switch lower {
	case ".ds_store", "thumbs.db", "desktop.ini", "ehthumbs.db":
		return ReasonSystem
	}
	if strings.HasPrefix(name, ".") {
		return ReasonHidden
	}
	if strings.HasPrefix(name, "~$") {
		return ReasonTemporary
	}
	for _, ext := range []string{".tmp", ".part", ".crdownload"} {
		if strings.HasSuffix(lower, ext) {
			return ReasonTemporary
		}
	}
	return ""
}

// validRelPath reports whether a relative, slash-separated path is one the
// server accepts (its GitHubIncludePaths rules: no empty, "." or ".." segment,
// no backslash, no control character, at most 1,024 characters, valid UTF-8).
func validRelPath(p string) bool {
	if p == "" || len(p) > maxPathLen || !utf8.ValidString(p) || strings.ContainsRune(p, '\\') {
		return false
	}
	for _, r := range p {
		if unicode.IsControl(r) {
			return false
		}
	}
	for _, seg := range strings.Split(p, "/") {
		if seg == "" || seg == "." || seg == ".." {
			return false
		}
	}
	return true
}

// entryReason classifies one directory entry from its metadata alone (Lstat
// data — on Windows the FindFirstFile record), so deciding to skip an
// online-only file never opens it. It returns "" for a file or folder to sync.
func entryReason(name string, info fs.FileInfo) string {
	if info.Mode()&fs.ModeSymlink != 0 {
		return ReasonSymlink
	}
	if r := nameReason(name); r != "" {
		return r
	}
	if r := platformReason(info); r != "" {
		return r
	}
	if info.IsDir() {
		return ""
	}
	if !syncableFile(info) {
		return ReasonNotFile
	}
	return ""
}
