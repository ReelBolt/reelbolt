package services

import (
	"encoding/json"
	"fmt"
	htmltemplate "html/template"
	"io/fs"
	"regexp"
	"strings"
	texttemplate "text/template"
	"time"
)

// localeSet is the parsed template set of one locale.
type localeSet struct {
	html *htmltemplate.Template
	text *texttemplate.Template
}

type subjectsFile struct {
	Subjects     map[string]map[string]string `json:"subjects"`
	FooterNote   map[string]string            `json:"footerNote"`
	FallbackName map[string]string            `json:"fallbackName"`
}

var mailSubjects = map[string]map[string]*texttemplate.Template{}

func defaultNameFor(loc string) string {
	// A present-but-empty entry means "no name": the template then greets without one.
	if n, ok := subjectsData.FallbackName[loc]; ok {
		return n
	}
	return "there"
}

func loadTemplateSets() map[string]*localeSet {
	sets := map[string]*localeSet{}
	for _, loc := range SupportedLocales() {
		// loc comes from the registry allowlist, never from input.
		sets[loc] = &localeSet{
			html: htmltemplate.Must(htmltemplate.ParseFS(emailTemplateFS, "email_templates/"+loc+"/*.html")),
			text: texttemplate.Must(texttemplate.ParseFS(emailTemplateFS, "email_templates/"+loc+"/*.txt")),
		}
	}
	return sets
}

var subjectsData subjectsFile

func init() {
	b, err := fs.ReadFile(emailTemplateFS, "email_templates/subjects.json")
	if err != nil {
		panic(err)
	}
	if err := json.Unmarshal(b, &subjectsData); err != nil {
		panic("email_templates/subjects.json: " + err.Error())
	}
	for loc, m := range subjectsData.Subjects {
		mailSubjects[loc] = map[string]*texttemplate.Template{}
		for name, src := range m {
			mailSubjects[loc][name] = texttemplate.Must(texttemplate.New(loc + "/" + name).Parse(src))
		}
	}
}

// pick returns the html and text templates for a locale, falling back to the default locale
// when the locale or the individual template is missing.
func (r renderer) pick(loc string, tmpl TemplateName) (*htmltemplate.Template, *texttemplate.Template) {
	h, t := r.sets[DefaultLocale].html, r.sets[DefaultLocale].text
	if set := r.sets[loc]; set != nil {
		if set.html.Lookup(string(tmpl)+".html") != nil && set.text.Lookup(string(tmpl)+".txt") != nil {
			h, t = set.html, set.text
		}
	}
	return h, t
}

func renderSubject(loc string, tmpl TemplateName, data MailData) (string, error) {
	t := mailSubjects[loc][string(tmpl)]
	if t == nil {
		t = mailSubjects[DefaultLocale][string(tmpl)]
	}
	if t == nil {
		return "", fmt.Errorf("no subject for email template %q", tmpl)
	}
	var sb strings.Builder
	if err := t.Execute(&sb, data); err != nil {
		return "", fmt.Errorf("subject %s: %w", tmpl, err)
	}
	return sb.String(), nil
}

// footerNoteFor is the alpha version line in the recipient's language. The English line is the
// ProductFooterNote constant; other locales reuse its "ReelBolt <version>" prefix.
func footerNoteFor(loc string) string {
	if loc == DefaultLocale {
		return ProductFooterNote
	}
	tail := subjectsData.FooterNote[loc]
	if tail == "" {
		return ProductFooterNote
	}
	head, _, _ := strings.Cut(ProductFooterNote, " — ")
	return head + " — " + tail
}

var itMonths = [...]string{"gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"}

// FormatMailDate formats a date for a mail in the recipient's language ("2 January 2006" in
// English, "2 gennaio 2006" in Italian).
func FormatMailDate(t time.Time, loc string) string {
	if loc == "it" {
		return fmt.Sprintf("%d %s %d", t.Day(), itMonths[t.Month()-1], t.Year())
	}
	return t.Format("2 January 2006")
}

var durationRE = regexp.MustCompile(`^(\d{1,4}) (minute|minutes|hour|hours|day|days)$`)

// localizeDuration turns the English "24 hours" the callers pass into the recipient's language;
// an unrecognised value is returned unchanged.
func localizeDuration(s, loc string) string {
	if loc != "it" {
		return s
	}
	m := durationRE.FindStringSubmatch(strings.TrimSpace(s))
	if m == nil {
		return s
	}
	one := m[1] == "1"
	var unit string
	switch strings.TrimSuffix(m[2], "s") {
	case "minute":
		unit = map[bool]string{true: "minuto", false: "minuti"}[one]
	case "hour":
		unit = map[bool]string{true: "ora", false: "ore"}[one]
	default:
		unit = map[bool]string{true: "giorno", false: "giorni"}[one]
	}
	return m[1] + " " + unit
}

// itRoles are the organisation role names as an invitee reads them in Italian.
var itRoles = map[string]string{"Owner": "proprietario", "Admin": "amministratore", "Member": "membro", "Billing": "referente per la fatturazione"}

// localizeRole translates an organisation role name for the recipient; an unknown value is
// returned unchanged.
func localizeRole(role, loc string) string {
	if loc != "it" {
		return role
	}
	if r, ok := itRoles[strings.TrimSpace(role)]; ok {
		return r
	}
	return role
}
