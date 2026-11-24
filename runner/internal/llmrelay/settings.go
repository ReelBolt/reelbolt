package llmrelay

import "github.com/vecchiotom/reelbolt/runner/internal/config"

// GetSettings reads the sharing setting from runner.json.
func GetSettings() (Settings, error) {
	c, err := config.Load()
	if err != nil {
		return Settings{}, err
	}
	return c.LocalLLMSettings(), nil
}

// SaveSettings validates and writes the sharing setting to runner.json,
// leaving every other setting as it was. A running daemon picks it up through
// Daemon.ApplyLocalLLM.
func SaveSettings(s Settings) error {
	c, err := config.Load()
	if err != nil {
		return err
	}
	if err := c.SetLocalLLM(s); err != nil {
		return err
	}
	return c.Save()
}
