// Command sandbox-executor is the HTTP control plane for the Remotion sandbox:
// a thin, bearer-token-protected layer over the container driver in
// sandbox/pkg/container. It is in-cluster only and never proxied by nginx.
package main

import (
	"context"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/gorilla/mux"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/container"
)

var Version = "dev"

type appConfig struct {
	Port string
	// APIToken is the shared secret every API caller must present. There is no
	// default and an empty value is fatal at startup: this service can start
	// containers and read and write files, so it must never run open.
	APIToken string
	// ContainerCLI is the container CLI binary the driver shells out to
	// (SANDBOX_CONTAINER_CLI, "docker" or "podman"; default "docker").
	ContainerCLI string
	Container    container.Config
	// ArchiveLimits caps workspace archive export and import
	// (SANDBOX_WORKSPACE_*, SANDBOX_MAX_WORKSPACE_ARCHIVE_BYTES).
	ArchiveLimits container.ArchiveLimits
}

func loadConfig() appConfig {
	return appConfig{
		Port:         getEnv("PORT", "8080"),
		APIToken:     os.Getenv("SANDBOX_API_TOKEN"),
		ContainerCLI: getEnv("SANDBOX_CONTAINER_CLI", "docker"),
		Container: container.Config{
			SandboxImage:    getEnv("SANDBOX_IMAGE", "reelbolt-sandbox-runtime:local"),
			SandboxRoot:     getEnv("SANDBOX_ROOT", "/var/lib/reelbolt/sandboxes"),
			SandboxNetwork:  getEnv("SANDBOX_NETWORK", "sandbox-net"),
			NetworkEgress:   getBoolEnv("SANDBOX_NETWORK_EGRESS", false),
			MaxSandboxes:    getIntEnv("MAX_SANDBOXES", 20),
			MaxRenderBytes:  getInt64Env("SANDBOX_MAX_RENDER_BYTES", container.DefaultMaxRenderBytes),
			SandboxTTL:      getDurationEnv("SANDBOX_TTL", 7*24*time.Hour),
			ExecTimeout:     getDurationEnv("SANDBOX_EXEC_TIMEOUT", 5*time.Minute),
			MaxExecTimeout:  getDurationEnv("SANDBOX_EXEC_MAX_TIMEOUT", container.DefaultMaxExecTimeout),
			MemoryLimit:     getEnv("SANDBOX_MEMORY_LIMIT", "2g"),
			ShmSize:         getEnv("SANDBOX_SHM_SIZE", container.DefaultShmSize),
			CPULimit:        getEnv("SANDBOX_CPU_LIMIT", "2"),
			PIDsLimit:       getIntEnv("SANDBOX_PIDS_LIMIT", 256),
			TemplateDir:     getEnv("SANDBOX_TEMPLATE_DIR", container.DefaultTemplateDir),
			ContainerPrefix: strings.TrimSpace(os.Getenv("SANDBOX_CONTAINER_PREFIX")),
			Slot:            strings.TrimSpace(os.Getenv("SANDBOX_SLOT")),
			PeerSlots:       strings.TrimSpace(os.Getenv("SANDBOX_PEER_SLOTS")),
		},
		ArchiveLimits: container.ArchiveLimits{
			MaxEntries:      getIntEnv("SANDBOX_WORKSPACE_MAX_ENTRIES", container.DefaultArchiveMaxEntries),
			MaxTotalBytes:   getInt64Env("SANDBOX_WORKSPACE_MAX_BYTES", container.DefaultArchiveMaxTotalBytes),
			MaxFileBytes:    getInt64Env("SANDBOX_WORKSPACE_MAX_FILE_BYTES", container.DefaultArchiveMaxFileBytes),
			MaxArchiveBytes: getInt64Env("SANDBOX_MAX_WORKSPACE_ARCHIVE_BYTES", container.DefaultArchiveMaxArchiveBytes),
		},
	}
}

func getEnv(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func getIntEnv(key string, fallback int) int {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return fallback
	}
	return n
}

func getInt64Env(key string, fallback int64) int64 {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	n, err := strconv.ParseInt(v, 10, 64)
	if err != nil || n <= 0 {
		return fallback
	}
	return n
}

func getBoolEnv(key string, fallback bool) bool {
	v := strings.TrimSpace(os.Getenv(key))
	if v == "" {
		return fallback
	}
	b, err := strconv.ParseBool(v)
	if err != nil {
		return fallback
	}
	return b
}

func getDurationEnv(key string, fallback time.Duration) time.Duration {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	d, err := time.ParseDuration(v)
	if err != nil || d <= 0 {
		return fallback
	}
	return d
}

type apiHandler struct {
	manager *container.Driver
	// archiveLimits caps workspace archives; zero fields mean the defaults.
	archiveLimits container.ArchiveLimits
}

type createSandboxRequest struct {
	WorkflowExecutionID string `json:"workflowExecutionId"`
}

type writeFileRequest struct {
	ContentBase64 string `json:"contentBase64"`
}

type installPackagesRequest struct {
	Packages []string `json:"packages"`
}

type sandboxStatus struct {
	Exists         bool      `json:"exists"`
	Ready          bool      `json:"ready"`
	HasPackageJson bool      `json:"hasPackageJson"`
	HasNodeModules bool      `json:"hasNodeModules"`
	ContainerName  string    `json:"containerName,omitempty"`
	WorkspacePath  string    `json:"workspacePath,omitempty"`
	CreatedAt      time.Time `json:"createdAt,omitempty"`
	LastActivity   time.Time `json:"lastActivity,omitempty"`
}

func (h *apiHandler) createSandbox(w http.ResponseWriter, r *http.Request) {
	var req createSandboxRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	log.Printf("API create sandbox request for workflow %s", req.WorkflowExecutionID)
	sb, created, err := h.manager.Create(req.WorkflowExecutionID)
	if err != nil {
		log.Printf("error creating sandbox for workflow %s: %v", req.WorkflowExecutionID, err)
		writeManagerError(w, err)
		return
	}
	status := http.StatusCreated
	if !created {
		status = http.StatusOK
	}
	writeJSON(w, status, sb)
}

func (h *apiHandler) adopt(w http.ResponseWriter, r *http.Request) {
	// Without a slot there is no handoff to run: the endpoint does not exist.
	if h.manager.Config().Slot == "" {
		writeError(w, http.StatusNotFound, "not found")
		return
	}
	var req struct {
		PeerSlots *[]string `json:"peerSlots"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	// Absent or null is refused: an adopt pass with an unstated peer list would treat every live
	// peer's containers as its own. An explicit [] is the operator saying no peer is live.
	if req.PeerSlots == nil {
		writeError(w, http.StatusBadRequest, "peerSlots is required (use [] when no peer is live)")
		return
	}
	for _, p := range *req.PeerSlots {
		if strings.TrimSpace(p) == "" {
			writeError(w, http.StatusBadRequest, "peerSlots must not contain blank names")
			return
		}
	}
	log.Printf("API adopt request, peer slots %v", *req.PeerSlots)
	h.manager.AdoptExistingWithPeers(*req.PeerSlots)
	writeJSON(w, http.StatusOK, map[string]any{"sandboxes": len(h.manager.List())})
}

func (h *apiHandler) listSandboxes(w http.ResponseWriter, r *http.Request) {
	log.Printf("API list sandboxes request")
	writeJSON(w, http.StatusOK, h.manager.List())
}

func (h *apiHandler) getSandbox(w http.ResponseWriter, r *http.Request) {
	workflowExecutionID := mux.Vars(r)["workflowExecutionId"]
	log.Printf("API get sandbox request for workflow %s", workflowExecutionID)
	sb, err := h.manager.GetByExecution(workflowExecutionID)
	if err != nil {
		writeManagerError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, sb)
}

func (h *apiHandler) deleteSandbox(w http.ResponseWriter, r *http.Request) {
	workflowExecutionID := mux.Vars(r)["workflowExecutionId"]
	log.Printf("API delete sandbox request for workflow %s", workflowExecutionID)
	if err := h.manager.StopByExecution(workflowExecutionID); err != nil {
		log.Printf("API delete sandbox (stop) error for workflow %s: %v", workflowExecutionID, err)
		writeManagerError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (h *apiHandler) completeWorkflow(w http.ResponseWriter, r *http.Request) {
	workflowExecutionID := mux.Vars(r)["workflowExecutionId"]
	log.Printf("API complete workflow (sandbox kept for TTL) request for workflow %s", workflowExecutionID)
	if err := h.manager.StopByExecution(workflowExecutionID); err != nil {
		log.Printf("API complete workflow (stop) error for workflow %s: %v", workflowExecutionID, err)
		writeManagerError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (h *apiHandler) execSandbox(w http.ResponseWriter, r *http.Request) {
	var req container.ExecRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	output, err := h.manager.RunExec(mux.Vars(r)["workflowExecutionId"], req)
	if err != nil {
		status := http.StatusInternalServerError
		if errors.Is(err, container.ErrBadExec) || errors.Is(err, container.ErrNotFound) || errors.Is(err, container.ErrBadExecution) {
			status = statusFromErr(err)
		}
		body := map[string]string{
			"error":  err.Error(),
			"output": output,
		}
		if errors.Is(err, container.ErrSandboxNotRunning) {
			// Additive: lets the engine tell "the sandbox went down under this
			// command, retry it" from a command that ran and failed.
			body["code"] = "SANDBOX_NOT_RUNNING"
		}
		writeJSON(w, status, body)
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"output": output})
}

func (h *apiHandler) listFiles(w http.ResponseWriter, r *http.Request) {
	path := r.URL.Query().Get("path")
	entries, err := h.manager.ListFiles(mux.Vars(r)["workflowExecutionId"], path)
	if err != nil {
		writeManagerError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, entries)
}

func (h *apiHandler) getFileContent(w http.ResponseWriter, r *http.Request) {
	path := r.URL.Query().Get("path")
	if path == "" {
		writeError(w, http.StatusBadRequest, "path is required")
		return
	}
	data, err := h.manager.ReadFile(mux.Vars(r)["workflowExecutionId"], path)
	if err != nil {
		writeManagerError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{
		"path":          path,
		"contentBase64": base64.StdEncoding.EncodeToString(data),
	})
}

// getFileRaw streams a workspace file as application/octet-stream with an exact
// Content-Length: no base64, no buffering. Used for rendered videos, which are
// far beyond the /files/content cap.
func (h *apiHandler) getFileRaw(w http.ResponseWriter, r *http.Request) {
	path := r.URL.Query().Get("path")
	if path == "" {
		writeError(w, http.StatusBadRequest, "path is required")
		return
	}
	f, size, err := h.manager.OpenRawFile(mux.Vars(r)["workflowExecutionId"], path)
	if err != nil {
		writeManagerError(w, err)
		return
	}
	defer func() {
		// Best-effort cleanup: the stream above already logged a failure if it
		// could not finish (a closed connection or an aborted response), and
		// nothing after this can do more with a file that no longer streams.
		_ = f.Close()
	}()
	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Length", strconv.FormatInt(size, 10))
	w.WriteHeader(http.StatusOK)
	// Never send more than the advertised length, even if the file grows.
	if _, err := io.Copy(w, io.LimitReader(f, size)); err != nil {
		log.Printf("raw file stream for %s aborted: %v", path, err)
	}
}

func (h *apiHandler) putFileContent(w http.ResponseWriter, r *http.Request) {
	path := r.URL.Query().Get("path")
	if path == "" {
		writeError(w, http.StatusBadRequest, "path is required")
		return
	}
	var req writeFileRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	content, err := base64.StdEncoding.DecodeString(req.ContentBase64)
	if err != nil {
		writeError(w, http.StatusBadRequest, "contentBase64 must be valid base64")
		return
	}
	if err := h.manager.WriteFile(mux.Vars(r)["workflowExecutionId"], path, content); err != nil {
		writeManagerError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (h *apiHandler) deleteFilePath(w http.ResponseWriter, r *http.Request) {
	path := r.URL.Query().Get("path")
	if path == "" {
		writeError(w, http.StatusBadRequest, "path is required")
		return
	}
	if err := h.manager.DeletePath(mux.Vars(r)["workflowExecutionId"], path); err != nil {
		writeManagerError(w, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// workspaceHas reports whether the workspace itself holds name as a regular
// file (or, with dir, as a directory). It looks through the workspace's os.Root
// and does not follow a link: sandboxed code owns the workspace, and a
// package.json or node_modules that is a symlink would otherwise make this
// answer whether a path in the control plane's own namespace exists.
func workspaceHas(workspace, name string, dir bool) bool {
	root, err := os.OpenRoot(workspace)
	if err != nil {
		return false
	}
	defer func() { _ = root.Close() }()
	info, err := root.Lstat(name)
	if err != nil {
		return false
	}
	if dir {
		return info.IsDir()
	}
	return info.Mode().IsRegular()
}

func (h *apiHandler) getSandboxStatus(w http.ResponseWriter, r *http.Request) {
	workflowExecutionID := mux.Vars(r)["workflowExecutionId"]
	// treating a status check as activity so polls won't trigger the janitor
	h.manager.Touch(workflowExecutionID)

	sb, err := h.manager.GetByExecution(workflowExecutionID)
	if err != nil {
		if errors.Is(err, container.ErrNotFound) {
			writeJSON(w, http.StatusOK, sandboxStatus{Exists: false})
			return
		}
		writeManagerError(w, err)
		return
	}

	hasPackageJson := workspaceHas(sb.WorkspacePath, "package.json", false)
	hasNodeModules := workspaceHas(sb.WorkspacePath, "node_modules", true)
	ready := hasPackageJson && hasNodeModules

	writeJSON(w, http.StatusOK, sandboxStatus{
		Exists:         true,
		Ready:          ready,
		HasPackageJson: hasPackageJson,
		HasNodeModules: hasNodeModules,
		ContainerName:  sb.ContainerName,
		WorkspacePath:  sb.WorkspacePath,
		CreatedAt:      sb.CreatedAt,
		LastActivity:   sb.LastActivity,
	})
}

func (h *apiHandler) installPackages(w http.ResponseWriter, r *http.Request) {
	var req installPackagesRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "invalid json body")
		return
	}
	if len(req.Packages) == 0 {
		writeError(w, http.StatusBadRequest, "packages list is empty")
		return
	}
	if !h.manager.Config().NetworkEgress {
		// Fail with an explanation rather than letting npm spend the whole exec
		// timeout failing to resolve registry.npmjs.org on an --internal network.
		writeError(w, http.StatusConflict,
			"sandbox containers have no network egress, so packages cannot be installed at runtime. "+
				"Add the dependency to the baked-in Remotion template image instead, or set "+
				"SANDBOX_NETWORK_EGRESS=true to allow sandboxed code to reach the network.")
		return
	}
	for _, pkg := range req.Packages {
		if !container.ValidPackageName(pkg) {
			writeError(w, http.StatusBadRequest, fmt.Sprintf("invalid package name: %q", pkg))
			return
		}
	}

	workflowExecutionID := mux.Vars(r)["workflowExecutionId"]
	sb, err := h.manager.GetByExecution(workflowExecutionID)
	if err != nil {
		writeManagerError(w, err)
		return
	}

	output, execErr := h.manager.InstallPackages(workflowExecutionID, sb, req.Packages)
	if execErr != nil {
		log.Printf("install packages error for workflow %s: %v output: %s", workflowExecutionID, execErr, string(output))
		writeJSON(w, http.StatusInternalServerError, map[string]string{
			"error":  execErr.Error(),
			"output": string(output),
		})
		return
	}
	log.Printf("installed packages %v for workflow %s", req.Packages, workflowExecutionID)
	writeJSON(w, http.StatusOK, map[string]string{"output": string(output)})
}

// getWorkspaceArchive streams the workspace as a deterministic tar.gz. The
// driver's pre-pass runs (and enforces every cap) before the status line is
// written, so 404/400/413 still carry the usual JSON body. A failure after
// streaming began can only abort the response: the handler panics with
// http.ErrAbortHandler, which resets the connection, and the gzip trailer is
// never written, so the client cannot mistake a partial archive for a whole one.
func (h *apiHandler) getWorkspaceArchive(w http.ResponseWriter, r *http.Request) {
	workflowExecutionID := mux.Vars(r)["workflowExecutionId"]
	started := false
	stats, err := h.manager.ExportWorkspace(workflowExecutionID, h.archiveLimits, func(stats container.ArchiveStats) (io.Writer, error) {
		w.Header().Set("Content-Type", "application/gzip")
		w.Header().Set("X-Workspace-Entries", strconv.Itoa(stats.Entries))
		w.Header().Set("X-Workspace-Bytes", strconv.FormatInt(stats.Bytes, 10))
		w.Header().Set("X-Workspace-Skipped", strconv.Itoa(stats.Skipped))
		w.WriteHeader(http.StatusOK)
		started = true
		return w, nil
	})
	if err != nil {
		if !started {
			log.Printf("workspace export for workflow %s refused: %v", workflowExecutionID, err)
			writeArchiveError(w, err)
			return
		}
		log.Printf("workspace export for workflow %s aborted mid-stream: %v", workflowExecutionID, err)
		panic(http.ErrAbortHandler)
	}
	log.Printf("exported workspace for workflow %s: %d entries, %d bytes, %d skipped",
		workflowExecutionID, stats.Entries, stats.Bytes, stats.Skipped)
}

// putWorkspaceArchive restores a tar.gz (as produced by getWorkspaceArchive)
// into the workspace. replace defaults to true; replace=false merges.
func (h *apiHandler) putWorkspaceArchive(w http.ResponseWriter, r *http.Request) {
	workflowExecutionID := mux.Vars(r)["workflowExecutionId"]
	replace := true
	if v := r.URL.Query().Get("replace"); v != "" {
		b, err := strconv.ParseBool(v)
		if err != nil {
			writeError(w, http.StatusBadRequest, "replace must be true or false")
			return
		}
		replace = b
	}
	result, err := h.manager.ImportWorkspace(workflowExecutionID, r.Body, h.archiveLimits, container.ImportOptions{Replace: replace})
	if err != nil {
		log.Printf("workspace import for workflow %s failed: %v", workflowExecutionID, err)
		writeArchiveError(w, err)
		return
	}
	log.Printf("imported workspace for workflow %s: %d files, %d dirs, %d bytes, dependencies match template: %v (compared: %v)",
		workflowExecutionID, result.Files, result.Directories, result.Bytes, result.DependenciesMatchTemplate, result.TemplateCompared)
	writeJSON(w, http.StatusOK, result)
}

// writeArchiveError answers an archive failure with the usual JSON error body
// plus a machine-readable code for the two archive-specific failures.
func writeArchiveError(w http.ResponseWriter, err error) {
	var maxBytes *http.MaxBytesError
	switch {
	case errors.As(err, &maxBytes):
		writeJSON(w, http.StatusRequestEntityTooLarge, map[string]string{
			"error": fmt.Sprintf("%v: request body exceeds %d bytes", container.ErrArchiveTooLarge, maxBytes.Limit),
			"code":  "ARCHIVE_TOO_LARGE",
		})
	case errors.Is(err, container.ErrArchiveTooLarge):
		writeJSON(w, http.StatusRequestEntityTooLarge, map[string]string{"error": err.Error(), "code": "ARCHIVE_TOO_LARGE"})
	case errors.Is(err, container.ErrInvalidArchive):
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": err.Error(), "code": "ARCHIVE_INVALID"})
	default:
		writeManagerError(w, err)
	}
}

func writeManagerError(w http.ResponseWriter, err error) {
	writeError(w, statusFromErr(err), err.Error())
}

func statusFromErr(err error) int {
	switch {
	case errors.Is(err, container.ErrArchiveTooLarge):
		return http.StatusRequestEntityTooLarge
	case errors.Is(err, container.ErrInvalidArchive):
		return http.StatusBadRequest
	case errors.Is(err, container.ErrNotFound), errors.Is(err, fs.ErrNotExist):
		return http.StatusNotFound
	case errors.Is(err, container.ErrInvalidPath), errors.Is(err, container.ErrBadExec), errors.Is(err, container.ErrBadExecution):
		return http.StatusBadRequest
	case errors.Is(err, container.ErrTooManySandboxes):
		return http.StatusTooManyRequests
	case errors.Is(err, container.ErrPeerSandboxExists):
		return http.StatusConflict
	case errors.Is(err, container.ErrFileTooLarge):
		return http.StatusRequestEntityTooLarge
	default:
		return http.StatusInternalServerError
	}
}

// maxRequestBodyBytes caps any JSON request body. writeFile bodies are base64,
// so this bounds a written file at roughly three quarters of the limit.
const maxRequestBodyBytes = 96 << 20 // 96 MiB

// workspaceArchiveSuffix is the path suffix of the workspace archive routes.
const workspaceArchiveSuffix = "/workspace/archive"

// bodyLimitFor chooses the request body cap per route: the workspace archive
// import (PUT .../workspace/archive) is a raw tar.gz stream capped at
// archiveMax; every other route keeps maxRequestBodyBytes.
func bodyLimitFor(archiveMax int64) func(*http.Request) int64 {
	return func(r *http.Request) int64 {
		if r.Method == http.MethodPut && strings.HasSuffix(r.URL.Path, workspaceArchiveSuffix) {
			return archiveMax
		}
		return maxRequestBodyBytes
	}
}

// requireAPIToken rejects any request that does not carry the shared secret in
// `Authorization: Bearer <token>`, and caps every body at maxRequestBodyBytes.
func requireAPIToken(token string, next http.Handler) http.Handler {
	return requireAPITokenWithLimits(token, func(*http.Request) int64 { return maxRequestBodyBytes }, next)
}

// requireAPITokenWithLimits is requireAPIToken with the body cap chosen per
// request by bodyLimit.
//
// This service has no notion of a user: every caller can start containers and
// read and write files under SANDBOX_ROOT, so the only access control available
// is "does the caller hold the secret". The comparison is constant-time so a
// caller cannot recover the token a byte at a time from response timing.
func requireAPITokenWithLimits(token string, bodyLimit func(*http.Request) int64, next http.Handler) http.Handler {
	expected := []byte(token)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		presented := []byte(strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")))
		if subtle.ConstantTimeCompare(presented, expected) != 1 {
			log.Printf("rejected unauthenticated %s %s from %s", r.Method, r.URL.Path, r.RemoteAddr)
			writeError(w, http.StatusUnauthorized, "unauthorized")
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, bodyLimit(r))
		next.ServeHTTP(w, r)
	})
}

func writeJSON(w http.ResponseWriter, status int, body interface{}) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(body)
}

func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}

func main() {
	cfg := loadConfig()
	if cfg.APIToken == "" {
		log.Fatal("SANDBOX_API_TOKEN is required and must not be empty: this service starts containers " +
			"and reads and writes files on behalf of its callers, so it refuses to run unauthenticated. " +
			"Set it to a long random value shared with the workflow engine (Sandbox__ApiToken).")
	}
	if err := os.MkdirAll(cfg.Container.SandboxRoot, 0o755); err != nil {
		log.Fatalf("failed to create sandbox root: %v", err)
	}
	if p := cfg.Container.ContainerPrefix; p != "" {
		if err := container.ValidateContainerPrefix(p); err != nil {
			log.Fatal(err)
		}
	}
	manager := container.New(cfg.Container, cfg.ContainerCLI)
	if kind, err := manager.DetectKind(context.Background()); err != nil {
		log.Printf("warning: could not detect container CLI kind for %q: %v", cfg.ContainerCLI, err)
	} else {
		log.Printf("container CLI %q detected as %s", cfg.ContainerCLI, kind)
	}
	// re-adopt sandboxes that survived a restart (a deploy must not end their TTL); purge the rest
	manager.AdoptExisting()

	if cfg.Container.SandboxNetwork != "" {
		if err := manager.EnsureNetwork(); err != nil {
			log.Fatal(err)
		}
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	manager.StartJanitor(ctx)

	handler := &apiHandler{manager: manager, archiveLimits: cfg.ArchiveLimits}
	router := mux.NewRouter()
	router.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
	}).Methods(http.MethodGet)

	api := router.PathPrefix("/api/v1/sandboxes").Subrouter()
	archiveBodyLimit := bodyLimitFor(cfg.ArchiveLimits.MaxArchiveBytes)
	api.Use(func(next http.Handler) http.Handler {
		return requireAPITokenWithLimits(cfg.APIToken, archiveBodyLimit, next)
	})
	api.HandleFunc("", handler.createSandbox).Methods(http.MethodPost)
	api.HandleFunc("", handler.listSandboxes).Methods(http.MethodGet)
	api.HandleFunc("/{workflowExecutionId}", handler.getSandbox).Methods(http.MethodGet)
	api.HandleFunc("/{workflowExecutionId}", handler.deleteSandbox).Methods(http.MethodDelete)
	api.HandleFunc("/{workflowExecutionId}/status", handler.getSandboxStatus).Methods(http.MethodGet)
	api.HandleFunc("/{workflowExecutionId}/complete", handler.completeWorkflow).Methods(http.MethodPost)
	api.HandleFunc("/{workflowExecutionId}/exec", handler.execSandbox).Methods(http.MethodPost)
	api.HandleFunc("/{workflowExecutionId}/packages", handler.installPackages).Methods(http.MethodPost)
	api.HandleFunc("/{workflowExecutionId}/files", handler.listFiles).Methods(http.MethodGet)
	api.HandleFunc("/{workflowExecutionId}/files", handler.deleteFilePath).Methods(http.MethodDelete)
	api.HandleFunc("/{workflowExecutionId}/files/content", handler.getFileContent).Methods(http.MethodGet)
	api.HandleFunc("/{workflowExecutionId}/files/raw", handler.getFileRaw).Methods(http.MethodGet)
	api.HandleFunc("/{workflowExecutionId}/files/content", handler.putFileContent).Methods(http.MethodPut)
	api.HandleFunc("/{workflowExecutionId}"+workspaceArchiveSuffix, handler.getWorkspaceArchive).Methods(http.MethodGet)
	api.HandleFunc("/{workflowExecutionId}"+workspaceArchiveSuffix, handler.putWorkspaceArchive).Methods(http.MethodPut)

	// Authenticated like the API: re-runs the adoption pass with the given peer list, so this
	// executor takes over a retired executor's kept sandboxes.
	adminToken := func(next http.Handler) http.Handler { return requireAPIToken(cfg.APIToken, next) }
	router.Handle("/admin/adopt", adminToken(http.HandlerFunc(handler.adopt))).Methods(http.MethodPost)

	addr := ":" + cfg.Port
	log.Printf("ReelBolt Sandbox Executor %s listening on %s", Version, addr)
	if err := http.ListenAndServe(addr, router); err != nil {
		// Unwind the deferred cancel() ourselves: log.Fatal calls os.Exit,
		// which skips defers.
		cancel()
		log.Fatal(err) //nolint:gocritic // exitAfterDefer: cancel() is invoked explicitly above
	}
}
