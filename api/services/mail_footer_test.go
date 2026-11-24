package services

import (
	"bytes"
	htmltemplate "html/template"
	"io/fs"
	"strings"
	"testing"
	texttemplate "text/template"
)

// embeddedTemplateNames walks the WHOLE embedded template FS (every *.html and
// *.txt the mailer ships), not a sample, so a template that is added later is
// covered by these tests automatically.
func embeddedTemplateNames(t *testing.T, loc string) []string {
	t.Helper()
	var names []string
	err := fs.WalkDir(emailTemplateFS, "email_templates/"+loc, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		names = append(names, d.Name())
		return nil
	})
	if err != nil {
		t.Fatalf("walk embedded templates: %v", err)
	}
	return names
}

// executeEmbedded renders one embedded template by file name, through the same
// html/template and text/template engines production uses.
func executeEmbedded(t *testing.T, name string, htmlTmpl *htmltemplate.Template, textTmpl *texttemplate.Template, data MailData) string {
	t.Helper()
	var buf bytes.Buffer
	var err error
	if strings.HasSuffix(name, ".html") {
		err = htmlTmpl.ExecuteTemplate(&buf, name, data)
	} else {
		err = textTmpl.ExecuteTemplate(&buf, name, data)
	}
	if err != nil {
		t.Fatalf("execute %s: %v", name, err)
	}
	return buf.String()
}

// lineStats returns the total line count and the number of blank lines.
func lineStats(s string) (lines, blanks int) {
	ls := strings.Split(s, "\n")
	for _, l := range ls {
		if strings.TrimSpace(l) == "" {
			blanks++
		}
	}
	return len(ls), blanks
}

func excerpt(s string) string {
	if len(s) > 240 {
		return s[:240] + "…"
	}
	return s
}

// TestMailFooterRendersVersionInEveryTemplate is the whole-FS guard: every
// template the mailer embeds must render the alpha version from .FooterNote,
// with no unexecuted action left behind.
func TestMailFooterRendersVersionInEveryTemplate(t *testing.T) {
	for _, loc := range SupportedLocales() {
		t.Run(loc, func(t *testing.T) {
			htmlTmpl := htmltemplate.Must(htmltemplate.ParseFS(emailTemplateFS, "email_templates/"+loc+"/*.html"))
			textTmpl := texttemplate.Must(texttemplate.ParseFS(emailTemplateFS, "email_templates/"+loc+"/*.txt"))

			names := embeddedTemplateNames(t, loc)
			if len(names) == 0 {
				t.Fatal("no embedded templates found")
			}
			var sawHTML, sawText bool
			for _, name := range names {
				switch {
				case strings.HasSuffix(name, ".html"):
					sawHTML = true
				case strings.HasSuffix(name, ".txt"):
					sawText = true
				default:
					t.Errorf("unexpected file in the embedded template FS: %s", name)
				}
			}
			if !sawHTML || !sawText {
				t.Fatalf("expected both .html and .txt templates in the embedded FS, saw html=%v text=%v", sawHTML, sawText)
			}

			for _, name := range names {
				t.Run(name, func(t *testing.T) {
					data := testData()
					data.FooterNote = ProductFooterNote
					out := executeEmbedded(t, name, htmlTmpl, textTmpl, data)
					if !strings.Contains(out, "0.4.0-alpha") {
						t.Errorf("rendered output does not carry the alpha version from .FooterNote:\n%s", excerpt(out))
					}
					if strings.Contains(out, "{{") {
						t.Errorf("rendered output contains an unexecuted template action:\n%s", excerpt(out))
					}
				})
			}
		})
	}
}

// TestMailFooterEmptyFieldIsSafe is Review Focus 2: a template rendered with an
// empty .FooterNote must still execute, must not leak {{.FooterNote}} or the
// version, and must not leave a stray blank line where the footer line was.
func TestMailFooterEmptyFieldIsSafe(t *testing.T) {
	for _, loc := range SupportedLocales() {
		t.Run(loc, func(t *testing.T) {
			htmlTmpl := htmltemplate.Must(htmltemplate.ParseFS(emailTemplateFS, "email_templates/"+loc+"/*.html"))
			textTmpl := texttemplate.Must(texttemplate.ParseFS(emailTemplateFS, "email_templates/"+loc+"/*.txt"))

			for _, name := range embeddedTemplateNames(t, loc) {
				t.Run(name, func(t *testing.T) {
					full := testData()
					full.FooterNote = ProductFooterNote
					populated := executeEmbedded(t, name, htmlTmpl, textTmpl, full)

					blank := testData()
					blank.FooterNote = ""
					empty := executeEmbedded(t, name, htmlTmpl, textTmpl, blank)

					if strings.Contains(empty, "{{") {
						t.Errorf("empty .FooterNote left a template action in the output:\n%s", excerpt(empty))
					}
					if strings.Contains(empty, "0.4.0-alpha") {
						t.Errorf("empty .FooterNote still rendered the alpha version:\n%s", excerpt(empty))
					}

					populatedLines, populatedBlanks := lineStats(populated)
					emptyLines, emptyBlanks := lineStats(empty)
					if emptyLines != populatedLines || emptyBlanks != populatedBlanks {
						t.Errorf("empty .FooterNote changed the line structure (populated %d lines/%d blank, empty %d lines/%d blank) — a stray blank line where the footer line was",
							populatedLines, populatedBlanks, emptyLines, emptyBlanks)
					}
				})
			}
		})
	}
}
