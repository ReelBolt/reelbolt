package foldersync

// Windows file attributes that matter here (winnt.h). The three cloud ones are
// what OneDrive, Dropbox and iCloud for Windows set on an online-only file:
// opening or reading it would make the sync client download it, so the app
// never does — it skips the file and says why.
const (
	fileAttributeHidden             = 0x00000002
	fileAttributeSystem             = 0x00000004
	fileAttributeOffline            = 0x00001000
	fileAttributeRecallOnOpen       = 0x00040000
	fileAttributeRecallOnDataAccess = 0x00400000
)

// windowsAttrReason is the attribute rule, separated so every platform's tests
// can exercise it.
func windowsAttrReason(attrs uint32, isDir bool) string {
	if !isDir && attrs&(fileAttributeOffline|fileAttributeRecallOnOpen|fileAttributeRecallOnDataAccess) != 0 {
		return ReasonOnlineOnly
	}
	if attrs&(fileAttributeHidden|fileAttributeSystem) != 0 {
		return ReasonHidden
	}
	return ""
}
