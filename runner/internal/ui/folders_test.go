package ui

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The Folders screen, tested the way the rest of this package is: a fake host,
// a pure document, and assertions about what a person reads and what a button
// does. No Wails, no desktop, no network.

// fakeFolders is a host that answers with canned state and records what it was
// asked to do.
type fakeFolders struct {
	view      FoldersView
	listErr   error
	path      string
	pathErr   error
	projects  []FolderProject
	projErr   error
	added     []AddFolderRequest
	addErr    error
	paused    []string
	resumed   []resumeCall
	removed   []string
	removeOK  bool
	removeErr error
	opened    []string
}

type resumeCall struct {
	linkID  string
	confirm bool
}

var _ FolderOps = (*fakeFolders)(nil)

func (f *fakeFolders) ListFolders(context.Context) (FoldersView, error) {
	if f.listErr != nil {
		return FoldersView{}, f.listErr
	}
	v := f.view
	if !v.Ready {
		v.Ready = true
	}
	return v, nil
}

func (f *fakeFolders) ChooseFolder(context.Context) (string, error) {
	if f.pathErr != nil {
		return "", f.pathErr
	}
	return f.path, nil
}

func (f *fakeFolders) ListProjects(context.Context) ([]FolderProject, error) {
	if f.projErr != nil {
		return nil, f.projErr
	}
	return f.projects, nil
}

func (f *fakeFolders) AddFolder(_ context.Context, req AddFolderRequest) (FolderLink, error) {
	if f.addErr != nil {
		return FolderLink{}, f.addErr
	}
	f.added = append(f.added, req)
	return FolderLink{LinkID: "l-new", LocalPath: req.LocalPath, ProjectID: req.ProjectID, TargetFolder: req.TargetFolder}, nil
}

func (f *fakeFolders) PauseFolder(_ context.Context, linkID string) error {
	f.paused = append(f.paused, linkID)
	return nil
}

func (f *fakeFolders) ResumeFolder(_ context.Context, linkID string, confirm bool) error {
	f.resumed = append(f.resumed, resumeCall{linkID: linkID, confirm: confirm})
	return nil
}

func (f *fakeFolders) RemoveFolder(_ context.Context, linkID string) (bool, error) {
	f.removed = append(f.removed, linkID)
	if f.removeErr != nil {
		return false, f.removeErr
	}
	return f.removeOK, nil
}

func (f *fakeFolders) OpenProject(_ context.Context, projectID string) error {
	f.opened = append(f.opened, projectID)
	return nil
}

// fakeAsker answers the two questions the window asks.
type fakeAsker struct {
	project    string
	projectErr error
	confirm    bool
	confirmErr error
	asked      []string
	confirmed  []FolderLink
}

func (a *fakeAsker) ChooseProject(_ context.Context, projects []FolderProject) (string, error) {
	a.asked = append(a.asked, fmt.Sprintf("%d projects", len(projects)))
	if a.projectErr != nil {
		return "", a.projectErr
	}
	return a.project, nil
}

func (a *fakeAsker) ConfirmRemove(_ context.Context, link FolderLink) (bool, error) {
	a.confirmed = append(a.confirmed, link)
	if a.confirmErr != nil {
		return false, a.confirmErr
	}
	return a.confirm, nil
}

// foldersApp builds an App over a shell with a folder host and an asker.
func foldersApp(t *testing.T, ops FolderOps, ask FolderAsker, mutate func(*Options)) (*App, func()) {
	t.Helper()
	s, cleanup := newTestShell(t, func(o *Options) {
		o.Folders = ops
		if mutate != nil {
			mutate(o)
		}
	})
	opts := AppOptions{
		OpenURL:    func(string) error { return nil },
		Autostart:  &fakeAutostart{},
		Ask:        ask,
		ShowWindow: func() {},
	}
	return NewApp(s, opts), cleanup
}

func sampleLink() FolderLink {
	return FolderLink{
		LinkID: "l1", LocalPath: "/home/ann/Footage", ProjectID: "p1", ProjectName: "Launch",
		TargetFolder: "Footage", ProjectURL: "https://app.reelbolt.ai/app/projects/p1",
		State: "uptodate", Headline: "Up to date", FileCount: 12,
		LastChangeAt: "2026-10-11T09:00:00Z",
	}
}

// TestFoldersDocumentCarriesTheEnginesWords: the screen shows what the engine
// said, and the four states the design names each read correctly.
func TestFoldersDocumentCarriesTheEnginesWords(t *testing.T) {
	for _, tc := range []struct {
		name  string
		state string
		head  string
		want  string
	}{
		{"up to date", "uptodate", "Up to date", "Up to date"},
		{"syncing", "syncing", "Syncing 3 files", "Syncing 3 files"},
		{"paused", "paused", "Paused", "Paused in ReelBolt. Resume it there."},
		{"attention", "attention", "Needs attention", "Storage is full (4.2 GB of 15.0 GB used)"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			link := sampleLink()
			link.State, link.Headline, link.Reason = tc.state, tc.head, ""
			if tc.state == "paused" || tc.state == "attention" {
				link.Reason = tc.want
			}
			app, cleanup := foldersApp(t, &fakeFolders{view: FoldersView{Links: []FolderLink{link}}}, nil, nil)
			defer cleanup()
			doc := app.Folders()
			if len(doc.Links) != 1 {
				t.Fatalf("links = %d", len(doc.Links))
			}
			if doc.Links[0].Headline != tc.head {
				t.Fatalf("headline = %q, want %q", doc.Links[0].Headline, tc.head)
			}
			if tc.state == "paused" || tc.state == "attention" {
				if doc.Links[0].Reason != tc.want {
					t.Fatalf("reason = %q, want %q", doc.Links[0].Reason, tc.want)
				}
			}
			if doc.Links[0].LocalPath == "" || doc.Links[0].ProjectName == "" {
				t.Fatalf("the row lost what identifies it: %+v", doc.Links[0])
			}
			if !doc.Ready {
				t.Fatalf("document = %+v", doc)
			}
		})
	}
}

// TestFoldersDocumentSaysWhyItIsOff: a build with no folder support, and a
// computer that needs re-pairing, each say so instead of showing an empty list.
func TestFoldersDocumentSaysWhyItIsOff(t *testing.T) {
	// No host at all.
	app, cleanup := foldersApp(t, nil, nil, nil)
	defer cleanup()
	doc := app.Folders()
	if doc.Problem == "" || !strings.Contains(doc.Problem, "isn't available") {
		t.Fatalf("no-host problem = %q", doc.Problem)
	}
	if doc.Links == nil {
		t.Fatal("a document with no host has a nil link list")
	}

	// A host that cannot resolve the API origin.
	app, cleanup = foldersApp(t, &fakeFolders{listErr: errors.New("Pair this computer again to turn on folder sync")}, nil, nil)
	defer cleanup()
	if got := app.Folders().Problem; !strings.Contains(got, "Pair this computer again") {
		t.Fatalf("problem = %q", got)
	}
}

// TestAddFolderTakesTheChosenPathAndSuggestsTheFolderName walks the Add flow.
func TestAddFolderTakesTheChosenPathAndSuggestsTheFolderName(t *testing.T) {
	dir := t.TempDir()
	picked := filepath.Join(dir, "Footage")
	if err := os.MkdirAll(picked, 0o755); err != nil {
		t.Fatal(err)
	}
	ops := &fakeFolders{
		path:     picked,
		projects: []FolderProject{{ID: "p1", Name: "Launch"}},
	}
	ask := &fakeAsker{project: "p1"}
	app, cleanup := foldersApp(t, ops, ask, nil)
	defer cleanup()

	res, err := app.Do(ActionFoldersAdd, "")
	if err != nil {
		t.Fatalf("folders add: %v", err)
	}
	if len(ops.added) != 1 {
		t.Fatalf("AddFolder calls = %d", len(ops.added))
	}
	got := ops.added[0]
	if got.LocalPath != picked || got.ProjectID != "p1" {
		t.Fatalf("request = %+v", got)
	}
	// The target folder is pre-filled with the local folder's name.
	if got.TargetFolder != "Footage" {
		t.Fatalf("target = %q", got.TargetFolder)
	}
	if res.Screen != "folders" {
		t.Fatalf("screen = %q", res.Screen)
	}
	if !strings.Contains(res.Message, "stay in the project") {
		t.Fatalf("message = %q", res.Message)
	}
}

// TestAddFolderSaysWhenThereIsNoChooser: the host may return "not available",
// and the window must say so rather than pretending a folder was chosen.
func TestAddFolderSaysWhenThereIsNoChooser(t *testing.T) {
	ops := &fakeFolders{
		pathErr:  ErrNoFolderPicker,
		projects: []FolderProject{{ID: "p1", Name: "Launch"}},
	}
	app, cleanup := foldersApp(t, ops, &fakeAsker{}, nil)
	defer cleanup()
	_, err := app.Do(ActionFoldersAdd, "")
	if !errors.Is(err, ErrNoFolderPicker) {
		t.Fatalf("error = %v, want ErrNoFolderPicker", err)
	}
	if len(ops.added) != 0 {
		t.Fatal("a folder was added without a chosen path")
	}
}

// TestAddFolderRefusesAFileOrAnEmptyChoice keeps the command line's checks in
// the window too.
func TestAddFolderRefusesAFileOrAnEmptyChoice(t *testing.T) {
	dir := t.TempDir()
	file := filepath.Join(dir, "notes.txt")
	if err := os.WriteFile(file, []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{"", file, filepath.Join(dir, "missing")} {
		ops := &fakeFolders{path: path, projects: []FolderProject{{ID: "p1", Name: "Launch"}}}
		app, cleanup := foldersApp(t, ops, &fakeAsker{}, nil)
		_, err := app.Do(ActionFoldersAdd, "")
		cleanup()
		if err == nil {
			t.Fatalf("path %q was accepted", path)
		}
		if len(ops.added) != 0 {
			t.Fatalf("path %q reached the server", path)
		}
	}
}

// TestAddFolderAsksOnlyWhenThereIsAChoice: one project is not a question.
func TestAddFolderAsksOnlyWhenThereIsAChoice(t *testing.T) {
	dir := t.TempDir()
	ops := &fakeFolders{
		path:     dir,
		projects: []FolderProject{{ID: "only", Name: "The only project"}},
	}
	ask := &fakeAsker{}
	app, cleanup := foldersApp(t, ops, ask, nil)
	defer cleanup()
	if _, err := app.Do(ActionFoldersAdd, ""); err != nil {
		t.Fatal(err)
	}
	if len(ask.asked) != 0 {
		t.Fatalf("the window asked with only one project: %v", ask.asked)
	}
	if ops.added[0].ProjectID != "only" {
		t.Fatalf("project = %q", ops.added[0].ProjectID)
	}

	// Two projects: it asks.
	ops2 := &fakeFolders{
		path:     dir,
		projects: []FolderProject{{ID: "a", Name: "A"}, {ID: "b", Name: "B"}},
	}
	ask2 := &fakeAsker{project: "b"}
	app2, cleanup2 := foldersApp(t, ops2, ask2, nil)
	defer cleanup2()
	if _, err := app2.Do(ActionFoldersAdd, ""); err != nil {
		t.Fatal(err)
	}
	if len(ask2.asked) != 1 || ops2.added[0].ProjectID != "b" {
		t.Fatalf("asked %v, added %+v", ask2.asked, ops2.added)
	}
}

// TestAddFolderWithoutAnAskerSaysSo rather than guessing a project.
func TestAddFolderWithoutAnAskerSaysSo(t *testing.T) {
	dir := t.TempDir()
	ops := &fakeFolders{
		path:     dir,
		projects: []FolderProject{{ID: "a", Name: "A"}, {ID: "b", Name: "B"}},
	}
	app, cleanup := foldersApp(t, ops, nil, nil)
	defer cleanup()
	if _, err := app.Do(ActionFoldersAdd, ""); err == nil {
		t.Fatal("a project was guessed")
	}
	if len(ops.added) != 0 {
		t.Fatal("a folder was added without a chosen project")
	}
}

// TestPauseAndResumeCarryTheLinksId.
func TestPauseAndResumeCarryTheLinksId(t *testing.T) {
	ops := &fakeFolders{view: FoldersView{Links: []FolderLink{sampleLink()}}}
	app, cleanup := foldersApp(t, ops, &fakeAsker{}, nil)
	defer cleanup()

	if _, err := app.Do(ActionFoldersPause, "l1"); err != nil {
		t.Fatal(err)
	}
	if len(ops.paused) != 1 || ops.paused[0] != "l1" {
		t.Fatalf("paused = %v", ops.paused)
	}
	res, err := app.Do(ActionFoldersResume, "l1")
	if err != nil {
		t.Fatal(err)
	}
	if len(ops.resumed) != 1 || ops.resumed[0].linkID != "l1" {
		t.Fatalf("resumed = %v", ops.resumed)
	}
	if ops.resumed[0].confirm {
		t.Fatal("a plain resume confirmed a mass deletion")
	}
	if !strings.Contains(res.Message, "copied again") {
		t.Fatalf("message = %q", res.Message)
	}
}

// TestResumeAfterTheGuardConfirmsTheDeletion: the guard's pause is the one case
// where Resume must send the confirmation, or the button would do nothing.
func TestResumeAfterTheGuardConfirmsTheDeletion(t *testing.T) {
	link := sampleLink()
	link.State, link.Headline = "paused", "Paused"
	link.PausedBy, link.CanConfirmDelete = "guard", true
	link.Reason = "The folder looks empty or mostly gone — sync is paused so nothing is deleted."

	ops := &fakeFolders{view: FoldersView{Links: []FolderLink{link}}}
	app, cleanup := foldersApp(t, ops, &fakeAsker{}, nil)
	defer cleanup()
	res, err := app.Do(ActionFoldersResume, "l1")
	if err != nil {
		t.Fatal(err)
	}
	if len(ops.resumed) != 1 || !ops.resumed[0].confirm {
		t.Fatalf("resumed = %+v, want confirmDelete", ops.resumed)
	}
	if !strings.Contains(res.Message, "removed from the project") {
		t.Fatalf("message = %q", res.Message)
	}

	// A DASHBOARD pause offers no local resume: the dashboard owns it.
	dash := sampleLink()
	dash.State, dash.Headline = "paused", "Paused"
	dash.PausedBy = "dashboard"
	dash.Reason = "Paused in ReelBolt. Resume it there."
	ops2 := &fakeFolders{view: FoldersView{Links: []FolderLink{dash}}}
	app2, cleanup2 := foldersApp(t, ops2, &fakeAsker{}, nil)
	defer cleanup2()
	if _, err := app2.Do(ActionFoldersResume, "l1"); err != nil {
		t.Fatal(err)
	}
	if ops2.resumed[0].confirm {
		t.Fatal("a dashboard pause was resumed with a deletion confirmation")
	}
}

// TestRemoveAsksAndKeepsTheFiles: the confirmation names the promise, and a
// person who says no changes nothing.
func TestRemoveAsksAndKeepsTheFiles(t *testing.T) {
	ops := &fakeFolders{view: FoldersView{Links: []FolderLink{sampleLink()}}, removeOK: true}
	ask := &fakeAsker{confirm: false}
	app, cleanup := foldersApp(t, ops, ask, nil)
	defer cleanup()

	res, err := app.Do(ActionFoldersRemove, "l1")
	if err != nil {
		t.Fatal(err)
	}
	if len(ask.confirmed) != 1 {
		t.Fatalf("the window did not confirm: %v", ask.confirmed)
	}
	if len(ops.removed) != 0 {
		t.Fatal("a folder was removed without confirmation")
	}
	if res.Message != "Nothing was removed." {
		t.Fatalf("message = %q", res.Message)
	}

	// Now say yes.
	ask.confirm = true
	res, err = app.Do(ActionFoldersRemove, "l1")
	if err != nil {
		t.Fatal(err)
	}
	if len(ops.removed) != 1 {
		t.Fatalf("removed = %v", ops.removed)
	}
	if !strings.Contains(res.Message, "stay in the project") {
		t.Fatalf("message = %q", res.Message)
	}
}

// TestFolderActionsRefuseAnUnknownLink rather than silently doing nothing.
func TestFolderActionsRefuseAnUnknownLink(t *testing.T) {
	ops := &fakeFolders{view: FoldersView{Links: []FolderLink{sampleLink()}}, removeOK: true}
	app, cleanup := foldersApp(t, ops, &fakeAsker{confirm: true}, nil)
	defer cleanup()
	for _, action := range []string{ActionFoldersRemove, ActionFoldersOpenProject} {
		if _, err := app.Do(action, "nope"); err == nil {
			t.Fatalf("%s accepted an unknown link", action)
		}
	}
	if len(ops.removed) != 0 {
		t.Fatal("an unknown link was removed")
	}
}

// TestOpenProjectInBrowserUsesTheLinksProject.
func TestOpenProjectInBrowserUsesTheLinksProject(t *testing.T) {
	ops := &fakeFolders{view: FoldersView{Links: []FolderLink{sampleLink()}}}
	app, cleanup := foldersApp(t, ops, &fakeAsker{}, nil)
	defer cleanup()
	res, err := app.Do(ActionFoldersOpenProject, "l1")
	if err != nil {
		t.Fatal(err)
	}
	if len(ops.opened) != 1 || ops.opened[0] != "p1" {
		t.Fatalf("opened = %v", ops.opened)
	}
	if !strings.Contains(res.Message, "browser") {
		t.Fatalf("message = %q", res.Message)
	}
}

// TestOpenFoldersIsARegisteredAction keeps the window's tab from being a dead
// button: every action a view offers must be handled by Do.
func TestOpenFoldersIsARegisteredAction(t *testing.T) {
	registered := map[string]bool{}
	for _, a := range Actions() {
		registered[a] = true
	}
	for _, a := range []string{ActionOpenFolders, ActionFoldersAdd, ActionFoldersPause, ActionFoldersResume, ActionFoldersRemove, ActionFoldersOpenProject} {
		if !registered[a] {
			t.Fatalf("%s is not in Actions()", a)
		}
	}
	if got := screenFor(ActionOpenFolders); got != "folders" {
		t.Fatalf("screenFor(open-folders) = %q", got)
	}
}

// TestTrayShowsTheFolderSummaryLine: the tray's one line about folder sync, and
// no row at all when there is nothing to say.
func TestTrayShowsTheFolderSummaryLine(t *testing.T) {
	in := base()
	v := Build(in)
	v.FolderSummary = "Folder sync: syncing 2 files"
	items := Menu(v)
	found := false
	for _, it := range items {
		if strings.Contains(it.Label, "Folder sync:") {
			found = true
			if it.Enabled {
				t.Fatal("the folder status line is a button")
			}
			if it.ID != ActionOpenFolders {
				t.Fatalf("folder line ID = %q", it.ID)
			}
		}
	}
	if !found {
		t.Fatalf("the tray has no folder line: %+v", items)
	}
	if !strings.Contains(Tooltip(v), "Folder sync: syncing 2 files") {
		t.Fatalf("tooltip = %q", Tooltip(v))
	}

	// Nothing to say: no row.
	plain := Build(in)
	for _, it := range Menu(plain) {
		if strings.Contains(it.Label, "Folder sync:") {
			t.Fatal("a machine with no folder sync got a folder row")
		}
	}
}

// TestFolderSummaryIsReadFromTheHostAndDrivesTheView: the shell's one line comes
// from the host's document, and a change to it redraws the tray.
func TestFolderSummaryIsReadFromTheHostAndDrivesTheView(t *testing.T) {
	ops := &fakeFolders{view: FoldersView{
		Summary: "Folder sync: syncing 1 file",
		Links:   []FolderLink{sampleLink()},
	}}
	app, cleanup := foldersApp(t, ops, &fakeAsker{}, nil)
	defer cleanup()
	if got := app.View().FolderSummary; got != "Folder sync: syncing 1 file" {
		t.Fatalf("folderSummary = %q", got)
	}
	// A host with nothing to say contributes nothing.
	ops.view.Summary = ""
	ops.view.Links = nil
	if got := app.View().FolderSummary; got != "" {
		t.Fatalf("folderSummary = %q, want empty", got)
	}
}

// TestFoldersHandlerIsReadOnly: the local API's folders route answers GET and
// refuses anything else, like every other read route.
func TestFoldersHandlerIsReadOnly(t *testing.T) {
	app, cleanup := foldersApp(t, &fakeFolders{view: FoldersView{Links: []FolderLink{sampleLink()}}}, nil, nil)
	defer cleanup()
	h := Handler(app)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodGet, "/folders", nil))
	if rec.Code != http.StatusOK || !strings.Contains(rec.Body.String(), "l1") {
		t.Fatalf("GET /folders = %d %s", rec.Code, rec.Body.String())
	}
	rec = httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequestWithContext(context.Background(), http.MethodPost, "/folders", nil))
	if rec.Code != http.StatusMethodNotAllowed {
		t.Fatalf("POST /folders = %d", rec.Code)
	}
}
