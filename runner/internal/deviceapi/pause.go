package deviceapi

// The two pauses the server can report, in the client's own vocabulary (the
// wire values are types.go's PausedByGuard/PausedByUser).
//
// The engine has to tell them apart because they have different answers:
// PausedByGuard is released from the desktop app (Resume, confirming the
// deletions the guard held back), and PausedByUser is released only in the
// dashboard. Carrying the reason on the client is what lets the window offer
// the right button for each.

// GuardPaused reports whether the last pause the server reported for linkID was
// the mass-delete guard's.
//
// It is true only after a manifest answer that said so. An unknown link answers
// false, so a caller that has never seen a reason gets the dashboard wording and
// offers no local button — the safe default, because a Resume this computer
// cannot perform would be a dead button.
func (c *Client) GuardPaused(linkID string) bool {
	c.pauseMu.Lock()
	defer c.pauseMu.Unlock()
	return c.pauseWhy[linkID] == PausedByGuard
}

// recordPause remembers why a link is paused.
func (c *Client) recordPause(linkID, reason string) {
	if linkID == "" {
		return
	}
	c.pauseMu.Lock()
	defer c.pauseMu.Unlock()
	if c.pauseWhy == nil {
		c.pauseWhy = map[string]string{}
	}
	if reason == PausedByGuard {
		c.pauseWhy[linkID] = PausedByGuard
		return
	}
	// Anything else — PausedByUser, an empty reason, a reason this build does
	// not know — is the dashboard's.
	c.pauseWhy[linkID] = PausedByUser
}
