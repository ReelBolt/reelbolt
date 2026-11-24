// Package foldersync keeps folders on this computer mirrored into ReelBolt
// projects, one way, live (docs/design/local-folder-sync-and-storage.md,
// "Desktop app").
//
// Each link watches its folder with fsnotify, waits for a file to stop
// changing, hashes it (reusing a local index so an unchanged file is never
// hashed twice) and uploads it through the device API's upload ticket. A full
// rescan and a manifest catch up whatever happened while the app was off, after
// a wake or a lost connection, and every 15 minutes as a safety net.
//
// Nothing is ever written to the folder, and nothing that would make the
// operating system download an online-only file is ever done: such files are
// skipped and named in the status.
package foldersync

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// LinksFileName is the link list, under os.UserConfigDir()/ReelBolt.
const LinksFileName = "folders.json"

// Link is one synced folder, as this computer records it. The local path is
// never sent to the server except as the link's display string.
type Link struct {
	LinkID       string `json:"linkId"`
	LocalPath    string `json:"localPath"`
	ProjectID    string `json:"projectId"`
	ProjectName  string `json:"projectName,omitempty"`
	TargetFolder string `json:"targetFolder"`
	// Paused is a pause made on this computer. A pause made from the dashboard
	// or by the mass-delete guard lives on the server.
	Paused bool `json:"paused"`
	// ConfirmLargeDelete is set by "Resume" after the mass-delete guard paused
	// the link: the next manifest carries confirmLargeDelete:true, once.
	ConfirmLargeDelete bool   `json:"confirmLargeDelete,omitempty"`
	AddedAt            string `json:"addedAt,omitempty"`
}

type linksFile struct {
	Version int    `json:"version"`
	Links   []Link `json:"links"`
}

// LinkStore is folders.json. Both the daemon and the command line write it, so
// every change is a read-modify-write of the whole file through an atomic
// rename; the daemon notices a change by re-reading it.
type LinkStore struct {
	path string
	mu   sync.Mutex
}

// DefaultLinksPath is os.UserConfigDir()/ReelBolt/folders.json.
func DefaultLinksPath() (string, error) {
	base, err := os.UserConfigDir()
	if err != nil {
		return "", fmt.Errorf("foldersync: user config directory: %w", err)
	}
	return filepath.Join(base, "ReelBolt", LinksFileName), nil
}

// OpenStore opens the store at path (the file need not exist).
func OpenStore(path string) *LinkStore { return &LinkStore{path: path} }

// Path is where the store lives.
func (s *LinkStore) Path() string { return s.path }

// Load reads every link. A missing file is no links.
func (s *LinkStore) Load() ([]Link, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.load()
}

func (s *LinkStore) load() ([]Link, error) {
	raw, err := os.ReadFile(s.path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("foldersync: read %s: %w", s.path, err)
	}
	var f linksFile
	if err := json.Unmarshal(raw, &f); err != nil {
		return nil, fmt.Errorf("foldersync: parse %s: %w", s.path, err)
	}
	out := f.Links[:0]
	for _, l := range f.Links {
		if l.LinkID == "" || l.LocalPath == "" {
			continue
		}
		out = append(out, l)
	}
	return out, nil
}

func (s *LinkStore) save(links []Link) error {
	dir := filepath.Dir(s.path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return fmt.Errorf("foldersync: create %s: %w", dir, err)
	}
	if links == nil {
		links = []Link{}
	}
	raw, err := json.MarshalIndent(linksFile{Version: 1, Links: links}, "", "  ")
	if err != nil {
		return err
	}
	return writeFileAtomic(s.path, append(raw, '\n'))
}

// Update applies fn to the link list and writes it back.
func (s *LinkStore) Update(fn func([]Link) ([]Link, error)) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	links, err := s.load()
	if err != nil {
		return err
	}
	next, err := fn(links)
	if err != nil {
		return err
	}
	return s.save(next)
}

// Add records a new link. A second link for the same folder is refused: two
// links watching one folder would race each other into the project.
func (s *LinkStore) Add(l Link) error {
	return s.Update(func(links []Link) ([]Link, error) {
		for _, x := range links {
			if x.LinkID == l.LinkID {
				return nil, fmt.Errorf("link %s is already on this computer", l.LinkID)
			}
			if samePath(x.LocalPath, l.LocalPath) {
				return nil, fmt.Errorf("%s is already synced (link %s)", l.LocalPath, x.LinkID)
			}
		}
		if l.AddedAt == "" {
			l.AddedAt = time.Now().UTC().Format(time.RFC3339)
		}
		return append(links, l), nil
	})
}

// Remove forgets a link. It reports whether the link was there.
func (s *LinkStore) Remove(linkID string) (bool, error) {
	found := false
	err := s.Update(func(links []Link) ([]Link, error) {
		out := links[:0]
		for _, l := range links {
			if l.LinkID == linkID {
				found = true
				continue
			}
			out = append(out, l)
		}
		return out, nil
	})
	return found, err
}

// Modify changes one link in place.
func (s *LinkStore) Modify(linkID string, fn func(*Link)) error {
	return s.Update(func(links []Link) ([]Link, error) {
		for i := range links {
			if links[i].LinkID == linkID {
				fn(&links[i])
				return links, nil
			}
		}
		return nil, fmt.Errorf("no synced folder with id %s on this computer", linkID)
	})
}

// Find resolves a link by its id or by a unique prefix of it, which is what a
// person types from "folders list".
func Find(links []Link, idOrPrefix string) (Link, error) {
	idOrPrefix = strings.TrimSpace(idOrPrefix)
	if idOrPrefix == "" {
		return Link{}, errors.New("name a synced folder by its id (see \"folders list\")")
	}
	var match []Link
	for _, l := range links {
		if l.LinkID == idOrPrefix {
			return l, nil
		}
		if strings.HasPrefix(l.LinkID, idOrPrefix) {
			match = append(match, l)
		}
	}
	switch len(match) {
	case 1:
		return match[0], nil
	case 0:
		return Link{}, fmt.Errorf("no synced folder with id %s on this computer", idOrPrefix)
	default:
		return Link{}, fmt.Errorf("%s matches %d synced folders; type more of the id", idOrPrefix, len(match))
	}
}

func samePath(a, b string) bool {
	a, b = filepath.Clean(a), filepath.Clean(b)
	if a == b {
		return true
	}
	return caseInsensitiveFS && strings.EqualFold(a, b)
}

// writeFileAtomic writes a 0600 file through a temporary file and a rename.
func writeFileAtomic(path string, data []byte) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, filepath.Base(path)+".tmp*")
	if err != nil {
		return err
	}
	name := tmp.Name()
	defer func() { _ = os.Remove(name) }()
	if err := tmp.Chmod(0o600); err != nil {
		_ = tmp.Close()
		return err
	}
	if _, err := tmp.Write(data); err != nil {
		_ = tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(name, path)
}
