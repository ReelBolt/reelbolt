//go:build windows

package update

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"syscall"
	"unsafe"
)

// swapInstalled cannot replace a running Windows image: the executable is
// mapped and the file is not writable while this process lives. The last step
// is therefore handed to a small helper that waits for this process to exit,
// moves the verified file into place, and restarts the runner.
//
// The helper runs from TEMP, takes no arguments a caller can influence, and
// does nothing that was not already verified: the file it moves is the one this
// process verified, and it restores the backup if the move fails.
func (u *Updater) swapInstalled(target, next, backup string, out Outcome) (Outcome, error) {
	if u.Platform.GOOS != OSWindows {
		return out, fmt.Errorf("%w: installing on %s from a windows build", ErrFormatMismatch, u.Platform.GOOS)
	}
	helper, err := writeSwapHelper(os.Getpid(), target, next, backup)
	if err != nil {
		return out, err
	}
	cmd := exec.Command("cmd.exe", "/c", "start", "/min", "", helper)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	if err := cmd.Start(); err != nil {
		return out, fmt.Errorf("update: start the update helper: %w", err)
	}
	// The helper owns the swap from here. It waits for this PID to disappear,
	// so the caller must exit for the update to complete; that is the documented
	// behaviour of an in-place update on Windows.
	out.Applied = true
	return out, nil
}

// writeSwapHelper writes the batch file that performs the swap after this
// process exits.
func writeSwapHelper(pid int, target, next, backup string) (string, error) {
	dir := os.TempDir()
	f, err := os.CreateTemp(dir, "reelbolt-update-*.cmd")
	if err != nil {
		return "", fmt.Errorf("update: create the update helper: %w", err)
	}
	name := f.Name()
	defer func() { _ = f.Close() }()
	script := "@echo off\r\n" +
		"setlocal\r\n" +
		":wait\r\n" +
		"tasklist /FI \"PID eq " + strconv.Itoa(pid) + "\" 2>NUL | find \"" + strconv.Itoa(pid) + "\" >NUL\r\n" +
		"if not errorlevel 1 (\r\n" +
		"  ping -n 2 127.0.0.1 >NUL\r\n" +
		"  goto wait\r\n" +
		")\r\n" +
		"move /Y \"" + target + "\" \"" + backup + "\" >NUL 2>&1\r\n" +
		"move /Y \"" + next + "\" \"" + target + "\" >NUL 2>&1\r\n" +
		"if errorlevel 1 (\r\n" +
		"  move /Y \"" + backup + "\" \"" + target + "\" >NUL 2>&1\r\n" +
		"  exit /b 1\r\n" +
		")\r\n" +
		"start \"\" \"" + target + "\" run --headless\r\n" +
		"del \"%~f0\"\r\n"
	if _, err := f.WriteString(script); err != nil {
		_ = os.Remove(name)
		return "", fmt.Errorf("update: write the update helper: %w", err)
	}
	return name, nil
}

// applyMSI installs a Windows installer package.
//
// The MSI was verified against the signed manifest, the SHA-256 and Authenticode
// before this point. It is run silently and without a restart prompt because the
// runner is a background service and has nowhere to show a dialog.
func (u *Updater) applyMSI(ctx context.Context, staged string, out Outcome) (Outcome, error) {
	cmd := exec.CommandContext(ctx, "msiexec.exe", "/i", staged, "/qn", "/norestart")
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	if err := cmd.Run(); err != nil {
		return out, fmt.Errorf("update: msiexec /i %s: %w", filepath.Base(staged), err)
	}
	out.Applied = true
	return out, nil
}

// verifyPlatformSignature checks the artifact's Authenticode signature through
// WinVerifyTrust — the same API Windows itself uses before it runs a downloaded
// program. A runner that installed a Windows binary without this would accept an
// artifact that our signed manifest describes but Windows would refuse to run.
func (u *Updater) verifyPlatformSignature(path string, a Artifact) error {
	switch a.Format {
	case FormatExe, FormatMSI:
	default:
		return nil
	}
	if err := verifyAuthenticode(path); err != nil {
		return fmt.Errorf("%w: %s: %v", ErrOSSigned, filepath.Base(path), err)
	}
	return nil
}

// WinVerifyTrust is reached through wintrust.dll directly rather than through a
// wrapper module: it is the only Windows API this package needs, and a
// dependency that exists to call one function is a dependency to keep patched
// for nothing.
var (
	wintrustDLL      = syscall.NewLazyDLL("wintrust.dll")
	winVerifyTrustEx = wintrustDLL.NewProc("WinVerifyTrust")
)

// wintrustActionGenericVerifyV2 is WINTRUST_ACTION_GENERIC_VERIFY_V2.
var wintrustActionGenericVerifyV2 = syscall.GUID{
	Data1: 0x00AAC56B,
	Data2: 0xCD44,
	Data3: 0x11D0,
	Data4: [8]byte{0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE},
}

const (
	wtdUINone                = 2
	wtdRevokeNone            = 0
	wtdChoiceFile            = 1
	wtdStateActionVerify     = 1
	wtdStateActionClose      = 2
	wtdSaferFlag             = 0x00000100
	wtdDisableMD2AndMD4      = 0x00002000
	wtdCacheOnlyURLRetrieval = 0x00001000
	// trusTEUnknownSubject is TRUST_E_NOSIGNATURE, which is what an unsigned
	// file reports. It is called out so the error reads as "unsigned" rather
	// than as an opaque HRESULT.
	trustENoSignature = 0x800B0100
)

type winTrustFileInfo struct {
	cbStruct       uint32
	pcwszFilePath  *uint16
	hFile          syscall.Handle
	pgKnownSubject *syscall.GUID
}

type winTrustData struct {
	cbStruct            uint32
	pPolicyCallbackData uintptr
	pSIPClientData      uintptr
	dwUIChoice          uint32
	fdwRevocationChecks uint32
	dwUnionChoice       uint32
	pFile               uintptr
	dwStateAction       uint32
	hWVTStateData       syscall.Handle
	pwszURLReference    *uint16
	dwProvFlags         uint32
	dwUIContext         uint32
	pSignatureSettings  uintptr
}

// verifyAuthenticode returns nil when the file carries a signature Windows
// trusts.
func verifyAuthenticode(path string) error {
	wide, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return err
	}
	fileInfo := winTrustFileInfo{
		cbStruct:      uint32(unsafe.Sizeof(winTrustFileInfo{})),
		pcwszFilePath: wide,
	}
	data := winTrustData{
		cbStruct:      uint32(unsafe.Sizeof(winTrustData{})),
		dwUIChoice:    wtdUINone,
		dwUnionChoice: wtdChoiceFile,
		pFile:         uintptr(unsafe.Pointer(&fileInfo)),
		dwStateAction: wtdStateActionVerify,
		// Offline policy: revocation checking would need the network, which a
		// runner may not have, and a missing CRL would then block an update
		// that is otherwise correct. The signature itself is still enforced.
		fdwRevocationChecks: wtdRevokeNone,
		dwProvFlags:         wtdSaferFlag | wtdDisableMD2AndMD4 | wtdCacheOnlyURLRetrieval,
	}
	action := wintrustActionGenericVerifyV2
	ret, _, _ := winVerifyTrustEx.Call(
		0,
		uintptr(unsafe.Pointer(&action)),
		uintptr(unsafe.Pointer(&data)),
	)
	// Always close the state handle, whatever the verdict was.
	closeData := data
	closeData.dwStateAction = wtdStateActionClose
	_, _, _ = winVerifyTrustEx.Call(
		0,
		uintptr(unsafe.Pointer(&action)),
		uintptr(unsafe.Pointer(&closeData)),
	)
	runtime.KeepAlive(fileInfo)
	runtime.KeepAlive(wide)
	if ret == 0 {
		return nil
	}
	if uint32(ret) == trustENoSignature {
		return fmt.Errorf("the file is not digitally signed")
	}
	return fmt.Errorf("WinVerifyTrust returned 0x%08X", uint32(ret))
}
