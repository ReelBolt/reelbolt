package ui

import (
	"encoding/json"
	"io"
	"net/http"
	"strings"
)

// Handler is the shell's local API, as an http.Handler.
//
// It LISTENS ON NOTHING. The Wails host mounts it on the webview's internal
// asset server (application.ServiceOptions.Route), so a request here never
// crosses a socket - which is the whole reason the desktop shell uses the
// system webview over IPC rather than a loopback server. The acceptance
// criterion "lsof shows no listening sockets" is satisfied by this file never
// calling net.Listen, and by the host never passing -tags server, which WOULD
// start one (see internal/../cmd/reelbolt-runner-desktop/README.md).
//
// The surface is deliberately small and shaped like the tray: read the view,
// perform an action, drive pairing. The window and the tray run the SAME
// App.Do switch, so a fix to one is a fix to both.
func Handler(app *App) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/view", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			methodNotAllowed(w, http.MethodGet)
			return
		}
		writeJSON(w, http.StatusOK, app.View())
	})
	// The Folders screen reads its own document: it is bigger than the tray's
	// view and changes on its own schedule, so shipping it in every /view would
	// make the whole window redraw when one file finishes copying.
	mux.HandleFunc("/folders", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			methodNotAllowed(w, http.MethodGet)
			return
		}
		writeJSON(w, http.StatusOK, app.Folders())
	})
	mux.HandleFunc("/menu", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			methodNotAllowed(w, http.MethodGet)
			return
		}
		writeJSON(w, http.StatusOK, app.Menu())
	})
	mux.HandleFunc("/do", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			methodNotAllowed(w, http.MethodPost)
			return
		}
		var req struct {
			Action string `json:"action"`
			Arg    string `json:"arg"`
		}
		body, err := io.ReadAll(io.LimitReader(r.Body, 64<<10))
		if err != nil {
			writeError(w, http.StatusBadRequest, "could not read the request")
			return
		}
		if err := json.Unmarshal(body, &req); err != nil {
			writeError(w, http.StatusBadRequest, "the request is not valid JSON")
			return
		}
		res, err := app.Do(req.Action, req.Arg)
		if err != nil {
			// An action that FAILED is not a transport failure: the window
			// shows the sentence, and the result still carries the view.
			res.OK = false
			if res.Message == "" {
				res.Message = err.Error()
			}
			res.View = app.View()
			writeJSON(w, http.StatusOK, res)
			return
		}
		writeJSON(w, http.StatusOK, res)
	})
	mux.HandleFunc("/settings", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			methodNotAllowed(w, http.MethodPost)
			return
		}
		var patch SettingsPatch
		body, err := io.ReadAll(io.LimitReader(r.Body, 64<<10))
		if err != nil {
			writeError(w, http.StatusBadRequest, "could not read the request")
			return
		}
		if err := json.Unmarshal(body, &patch); err != nil {
			writeJSON(w, http.StatusOK, ActionResult{OK: false, Message: "Those settings couldn't be read. Check the values and try again.", View: app.View()})
			return
		}
		message, err := app.SaveSettings(patch)
		if err != nil {
			writeJSON(w, http.StatusOK, ActionResult{OK: false, Message: userMessage(err), View: app.View()})
			return
		}
		writeJSON(w, http.StatusOK, ActionResult{OK: true, Message: message, View: app.View()})
	})
	// The payload preview. It is a GET because it changes nothing, and it is
	// fetched on demand rather than shipped in every view: a document that
	// exists to be READ should only be built when someone asks to read it.
	mux.HandleFunc("/telemetry/preview", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			methodNotAllowed(w, http.MethodGet)
			return
		}
		writeJSON(w, http.StatusOK, app.Shell().TelemetryPreview())
	})
	// The Local model screen: read it, look for servers, save, re-check. All
	// local; the only outward traffic is to the model server the person named.
	mux.HandleFunc("/local-model", func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet:
			writeJSON(w, http.StatusOK, app.LocalModel())
		case http.MethodPost:
			var p LocalModelPatch
			body, err := io.ReadAll(io.LimitReader(r.Body, 64<<10))
			if err != nil || json.Unmarshal(body, &p) != nil {
				writeJSON(w, http.StatusOK, LocalModelResult{Message: "Those settings couldn't be read. Check the values and try again.", Doc: app.LocalModel()})
				return
			}
			writeJSON(w, http.StatusOK, app.SaveLocalModel(p))
		default:
			methodNotAllowed(w, http.MethodGet, http.MethodPost)
		}
	})
	mux.HandleFunc("/local-model/detect", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			methodNotAllowed(w, http.MethodPost)
			return
		}
		writeJSON(w, http.StatusOK, app.DetectLocalModels(r.Context()))
	})
	mux.HandleFunc("/local-model/check", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			methodNotAllowed(w, http.MethodPost)
			return
		}
		writeJSON(w, http.StatusOK, app.CheckLocalModel(r.Context()))
	})
	mux.HandleFunc("/pairing", func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet:
			writeJSON(w, http.StatusOK, app.Shell().Pairing())
		case http.MethodDelete, http.MethodPost:
			app.Shell().CancelPairing()
			writeJSON(w, http.StatusOK, app.Shell().Pairing())
		default:
			methodNotAllowed(w, http.MethodGet, http.MethodPost, http.MethodDelete)
		}
	})
	return mux
}

// ActionRequest is the body of POST /do, exposed for the frontend's types.
type ActionRequest struct {
	Action string `json:"action"`
	Arg    string `json:"arg,omitempty"`
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	// The window is a local asset served by the webview, but a cached view is
	// a stale view; there is nothing to cache.
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]any{"ok": false, "error": message})
}

func methodNotAllowed(w http.ResponseWriter, allowed ...string) {
	w.Header().Set("Allow", strings.Join(allowed, ", "))
	writeError(w, http.StatusMethodNotAllowed, "this endpoint accepts "+strings.Join(allowed, ", "))
}
